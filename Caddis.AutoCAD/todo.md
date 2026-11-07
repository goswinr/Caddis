# Caddis for AutoCAD: what is left to do

For agents who continue the AutoCAD port. Read first: [AGENTS.md](../AGENTS.md), [README.md](README.md) in this folder,
and section 12 of [Architecture.md](../Architecture.md#12-autocad-caddisautocad). Update this file when you finish or change an item.

## State, as of 2026-11-06

Step 1 of 4 is done: `AutoCADHost`, the command `CADDIS`, `Dev\AutoCAD2025.cmd`. Tested by hand in AutoCAD 2025 with its update 25.0.189, on .NET 10:
eval, check, describe, status, capture, DWG and DXF export, undo, background mode with `Script.OnUi`, timeout, rejection while a command waits, MCP `initialize` and `tools/list`.

Not tested yet:
- the `net8.0-windows` build, i.e. AutoCAD 2025 or 2026 before their update to .NET 10
- AutoCAD 2026, and AutoCAD 2027 (R26.0, package AutoCAD.NET 26.0.0): does the build against the 2025 API load there?
- that zooming or panning with the mouse after a run makes `caddis_undo` refuse (it relies on `Document.ViewChanged`)

## How to test in AutoCAD

- `Dev\AutoCAD2025.cmd`, or `pwsh -File Dev/Start-Dev.ps1 AutoCAD2025 -NoBuild`. It starts `acad.exe /b load.scr`, the script NETLOADs the build from `%LocalAppData%\Caddis.Dev\AutoCAD2025`.
- `"autoStart": true` in `%AppData%\Caddis\settings-autocad.json`, then REST on port 7830, e.g. `curl http://127.0.0.1:7830/eval -d "{\"code\": \"1 + 1\"}"`. Messages also go to `%AppData%\Caddis\autocad.log`.
- AutoCAD loads a new build only after a restart. Close only an AutoCAD you started yourself, with only test drawings in it.
- **Never queue a command without all its inputs**, e.g. `SendStringToExecute("_.LINE 0,0 ", ...)`. Every run is then rejected, and nothing from outside cancels the command:
  AutoCAD ignores an Esc posted to its main window, and refuses COM calls while the command waits (`RPC_E_CALL_REJECTED`). Only Esc in AutoCAD, or killing it, ends it.
- To see which undo step `UNDO` reverted, read the system variable `LASTPROMPT` in a run: it ends with e.g. `<1>:  Caddis r2`.
- Undo checks to repeat after changes to the undo tracking: draw, run `1 + 1`, `caddis_undo` of the drawing run must undo it.
  Draw, run a script that only zooms (`Editor.SetCurrentView`), `caddis_undo` of the drawing run must refuse.

## Step 2: caddis_command

Model it on `runCommands` in [RhinoHost.fs](../Caddis.Rhino/RhinoHost.fs). Set `McpTexts.command` in [AutoCADInfo.fs](AutoCADInfo.fs), implement `RunCommands` in [AutoCADHost.fs](AutoCADHost.fs).

- **The hard part, settle it first:** `ICaddisHost.RunCommands` is called on the UI thread and returns the outcome synchronously, but in application context AutoCAD runs commands only later:
  `Editor.Command` fails with `eInvalidInput`, `SendStringToExecute` runs after the current call returns.
  Try `DocumentCollection.ExecuteInCommandContextAsync` with `Editor.Command` inside: find out whether its callback runs at once when called from a dispatcher call, and whether it works while Caddis holds a lock of the drawing.
  If it doesn't run at once, `RunCommands` has to become asynchronous (`Task<RunOutcome>`), which changes Caddis.Core, `RhinoHost`, `RevitHost`, `HeadlessHost` and the tests.
- Results: `Document.CommandWillStart`, `CommandEnded`, `CommandCancelled`, `CommandFailed` give the English command names (`GlobalCommandName`) and results.
  The database events that `AutoCADHost` already watches give created, changed and deleted objects; report their handles.
- `select`: `Editor.SetImpliedSelection(ObjectId[])` before the first command; report the selection after it with `Editor.SelectImplied` if it changed.
- Input watchdog: Caddis' dispatcher calls still run while a command waits for input, that is how the run that hit `LINE` was rejected.
  So from such a call: read the prompt from `LASTPROMPT`, and cancel by queuing `^C^C` with `doc.SendStringToExecute("\x03\x03", true, false, false)`. Untested.
  The `Editor.PromptingFor*` events tell when a command starts to wait.
- Undo: each command is its own undo step. Count them, and let `caddis_undo` send `UNDO <n>`.
- The setting `cancelWaitingCommands` exists already, for commands that a script starts.
- Docs: the MCP tools table in the main [README.md](../README.md) says `caddis_command` is "Rhino only", the table in section 7.2 of Architecture.md, section 12 there, and [README.md](README.md) here.

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
- Export: PDF, STL, SAT and others, through commands once step 2 works; now only DWG and DXF with `Database.Wblock`.
- `caddis_dialog`: the Win32 and UI Automation code of [RevitDialogs.fs](../Caddis.Revit/RevitDialogs.fs) could find AutoCAD's modal dialogs too.
- Command line output in results (`run.CommandLine`): AutoCAD has no API to read it. Maybe the command log file (`LOGFILEON`, `LOGFILEPATH`), reading what was added during the run.
- AutoCAD 2024 is installed on this computer and runs on .NET Framework: a `net48` build would need AutoCAD.NET 24.x and Avalonia 11 for the panel. Not planned yet.

## Open decisions for the user

- Ship the XML docs of the AutoCAD API, so that `caddis_describe` shows summaries? They are in the packages AutoCAD.NET, .Core and .Model.
  Autodesk's ObjectARX license allows passing copies on only with the license, for developing AutoCAD apps.
- When to add the CHANGELOG entry: the user wasn't asked yet.
