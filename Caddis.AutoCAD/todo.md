# Caddis for AutoCAD: what is left to do

For agents who continue the AutoCAD port. Read first: [AGENTS.md](../AGENTS.md), [README.md](README.md) in this folder,
and section 12 of [Architecture.md](../Architecture.md#12-autocad-caddisautocad). Update this file when you finish or change an item.

## State, as of 2026-11-07

Steps 1 and 2 of 4 are done.

Step 1: `AutoCADHost`, the command `CADDIS`, `Dev\AutoCAD2025.cmd`. Tested by hand in AutoCAD 2025 with its update 25.0.189, on .NET 10:
eval, check, describe, status, capture, DWG and DXF export, undo, background mode with `Script.OnUi`, timeout, rejection while a command waits, MCP `initialize` and `tools/list`.

Step 2: `caddis_command`, see "AutoCAD commands" in [README.md](README.md) and section 12 of Architecture.md. `ICaddisHost.RunCommands` returns a `Task<RunOutcome>` now.
Tested over REST and MCP in AutoCAD 2025 25.0.189: macros with points, quoted inputs and `""`, `_.-LAYER`, PLINE, ID and LIST with their command line output,
`select` with ERASE (pickfirst) and MOVE, COPY, LIST with `_P`, a handle that doesn't exist, the input timeout and `caddis_cancel` at a prompt,
UNITS (runs its command line version), OPTIONS (a dialog: no `^C^C`, a note, a cancel stops the next macro), an unknown command, a command defined in AutoLISP,
`caddis_undo` of a command run and of an F# run before and after one, command runs in background mode.

Not tested yet:
- the `net8.0-windows` build, i.e. AutoCAD 2025 or 2026 before their update to .NET 10
- AutoCAD 2026, and AutoCAD 2027 (R26.0, package AutoCAD.NET 26.0.0): does the build against the 2025 API load there?
- that zooming or panning with the mouse after a run makes `caddis_undo` refuse (it relies on `Document.ViewChanged`)
- `caddis_command` with more than 400 new command line lines, with several drawings open, with a command that shows a progress bar for longer than the input timeout,
  and `caddis_cancel` before AutoCAD started the commands (the code path exists, it's hard to hit by hand)

## How to test in AutoCAD

- `Dev\AutoCAD2025.cmd`, or `pwsh -File Dev/Start-Dev.ps1 AutoCAD2025 -NoBuild`. It starts `acad.exe /b load.scr`, the script NETLOADs the build from `%LocalAppData%\Caddis.Dev\AutoCAD2025`.
- `"autoStart": true` in `%AppData%\Caddis\settings-autocad.json`, then REST on port 7830, e.g. `curl http://127.0.0.1:7830/eval -d "{\"code\": \"1 + 1\"}"`. Messages also go to `%AppData%\Caddis\autocad.log`.
- AutoCAD loads a new build only after a restart. Close only an AutoCAD you started yourself, with only test drawings in it.
  An AutoCAD that the person started may run at the same time: `Start-Dev.ps1` starts a second one, and only that one loads Caddis. Tell them apart by the command line `/b ...load.scr`.
- **Never queue a command without all its inputs** with `SendStringToExecute`, e.g. `"_.LINE 0,0 "`. Every run is then rejected, and nothing from outside cancels the command:
  AutoCAD ignores an Esc posted to its main window, and refuses COM calls while the command waits (`RPC_E_CALL_REJECTED`). Only Esc in AutoCAD, or killing it, ends it.
  A backslash in the string is a pause for input too, e.g. in `(princ "\n")`; use `(chr 10)`. Test such things with `caddis_command`, which cancels its waiting commands.
- To experiment with the command context, queue a callback with `ExecuteInCommandContextAsync` in a run, and read what it recorded in the next run.
  Arm a watchdog first that queues `^C^C` from a dispatcher call, e.g. `SynchronizationContext.Current.Post` after a `Task.Delay`: a command left waiting rejects every run.
- `SECURELOAD` is 0 in the AutoCAD 2025 profile on this computer, so it doesn't ask before loading the unsigned build.
- To see which undo step `UNDO` reverted, read the system variable `LASTPROMPT` in a run: it ends with e.g. `<1>:  Caddis r2`.
- Undo checks to repeat after changes to the undo tracking: draw, run `1 + 1`, `caddis_undo` of the drawing run must undo it.
  Draw, run a script that only zooms (`Editor.SetCurrentView`), `caddis_undo` of the drawing run must refuse.

## Step 2: caddis_command, done

What was found out, beyond what the docs say:
- `ExecuteInCommandContextAsync` never runs its callback at once from a dispatcher call, with or without a document lock. So `RunCommands` became asynchronous.
- `CommandAsync` doesn't help with the selection: a command asks at `Select objects:` also when only its name was given with `CommandAsync`. Its `CommandResult` has no `ICriticalNotifyCompletion`, so F#'s `task` can't await it; chain `OnCompleted` by hand.
- `LASTPROMPT` is the last line, not the prompt: for UNITS it was a line of its help text, after SELECT `Select objects:   1 found`. The message of the `PromptingFor*` event is the prompt.
- The members of an `internal` F# type are internal in IL: reflection on them needs `BindingFlags.NonPublic`.
- The setting `cancelWaitingCommands` does nothing in AutoCAD: a script can't start a command that waits, `SendStringToExecute` runs it after the run.

Left to do:
- The panel's tooltip for `CancelWaitingCommands` (step 3) should say that it is for Rhino only.
- F# runs could show the command line too, from `AutoCADCommandLine.history` in `BeforeRun` and `AfterRun`, e.g. what `Editor.WriteMessage` printed.

## Step 3: panel, ribbon, messages

- Reference Caddis.UI. Host the Avalonia panel in a `PaletteSet` (AcMgd) with `PaletteSet.AddVisual(name, visual)`,
  through a WPF `HwndHost` like [AvaloniaHost.fs](../Caddis.Revit/AvaloniaHost.fs) in Revit, which is internal there: copy it first, share it later.
- Keyboard: AutoCAD sends key presses to its command line. Try `PaletteSet.KeepFocus`, and check typing in the panel's search box.
- Theme: the system variable `COLORTHEME` (0 dark, 1 light), refresh on `SystemVariableChanged`. See PanelTheme.fs of Revit and Rhino.
- [PanelView.fs](../Caddis.UI/PanelView.fs) has tooltips that name Rhino, around lines 529 to 540, for `EchoOutput` and `CancelWaitingCommands`. Word them per app, e.g. through `PanelHost`.
- Ribbon: `Autodesk.Windows.ComponentManager.Ribbon` from AdWindows, the library of Revit's ribbon checkboxes. When Caddis loads at AutoCAD's start, the ribbon may not exist yet: wait for `ComponentManager.ItemInitialized`.
  A tab `Caddis` with Start/Stop, Mode, Cancel, Reset, Panel and Info, like Revit's.
- Messages: `Autodesk.Windows.TaskDialog` from AdWindows, for an Info dialog like Revit's `App.showInfo`.
- FSI doesn't reference `Caddis.AutoCAD.dll`. If a script helper type like `Caddis.Revit.Script` is added and the dll referenced, its public types must not expose WPF or Avalonia types, as AGENTS.md explains for Revit.

## Step 4: installer, updates, CI

- Loader: a C# DLL like [Caddis.Revit.Loader](../Caddis.Revit.Loader/CaddisLoader.cs) that picks the net8.0 or net10.0 build by `Environment.Version`.
  AutoCAD 2025 moved from .NET 8 to .NET 10 with an update, as Revit did. The loader probably has to load the build with `Autodesk.AutoCAD.Runtime.ExtensionLoader.Load(path)`,
  not `Assembly.LoadFrom`, so that AutoCAD registers the commands and calls `Initialize`. Verify.
- Bundle: `%AppData%\Autodesk\ApplicationPlugins\Caddis.bundle` with a `PackageContents.xml` for series R25.0 and later, loaded at AutoCAD's start.
  Find out whether AutoCAD asks before loading an unsigned DLL from there (`SECURELOAD`); Revit's releases are signed with Azure Trusted Signing.
- Updates: a Velopack bootstrapper like [Caddis.Revit.Bootstrapper](../Caddis.Revit.Bootstrapper), or one installer for Revit and AutoCAD.
- Dev start: once an installed Caddis exists, `Start-Dev.ps1` must keep it from loading too, e.g. a `CADDIS_AUTOCAD_DEV` variable read by the loader, as in Revit.
- CI: a packaging action and a release workflow, and a Claude Desktop extension `caddis-autocad.mcpb` in [bundles.fsx](../.github/actions/package-claude-desktop/bundles.fsx).
- CHANGELOG.md: add an entry under `## [Unreleased]` once Caddis for AutoCAD can be installed. Single-line bullets, CRLF line endings, see AGENTS.md.

## Smaller items

- Capture: other views, layouts and visual styles; now only the active view with `Document.CapturePreviewImage`.
- Export: PDF, STL, SAT and others, e.g. through the commands `_.-EXPORT`, `_.STLOUT`, `_.ACISOUT` with `RunCommands`; now only DWG and DXF with `Database.Wblock`.
  `ICaddisHost.Export` is synchronous, so that would need it to return a task too, as `RunCommands` does.
- `caddis_dialog`: the Win32 and UI Automation code of [RevitDialogs.fs](../Caddis.Revit/RevitDialogs.fs) could find AutoCAD's modal dialogs too, e.g. the one of a command in `caddis_command`.
  Posting `WM_CLOSE` to the Options dialog closed it in a test, and OPTIONS then ended with the result `Success`.
- AutoCAD 2024 is installed on this computer and runs on .NET Framework: a `net48` build would need AutoCAD.NET 24.x and Avalonia 11 for the panel. Not planned yet.

## Open decisions for the user

- Ship the XML docs of the AutoCAD API, so that `caddis_describe` shows summaries? They are in the packages AutoCAD.NET, .Core and .Model.
  Autodesk's ObjectARX license allows passing copies on only with the license, for developing AutoCAD apps.
- When to add the CHANGELOG entry: the user wasn't asked yet.
