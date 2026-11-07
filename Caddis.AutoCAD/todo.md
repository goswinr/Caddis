# Caddis for AutoCAD: what is left to do

For agents who continue the AutoCAD port. Read first: [AGENTS.md](../AGENTS.md), [README.md](README.md) in this folder,
and section 12 of [Architecture.md](../Architecture.md#12-autocad-caddisautocad). Update this file when you finish or change an item.

## State, as of 2026-11-07

Steps 1 to 4 are done. Tested by hand in AutoCAD 2025 with its update 25.0.189, on .NET 10:

- Step 1, the host: eval, check, describe, status, capture, DWG and DXF export, undo, background mode with `Script.OnUi`, timeout, rejection while a command waits, MCP `initialize` and `tools/list`.
- Step 2, `caddis_command`: macros with points, quoted inputs and `""`, `_.-LAYER`, PLINE, ID and LIST with their command line output,
  `select` with ERASE (pickfirst) and MOVE, COPY, LIST with `_P`, a handle that doesn't exist, the input timeout and `caddis_cancel` at a prompt,
  UNITS, OPTIONS (a dialog: no `^C^C`, a note, a cancel stops the next macro), an unknown command, a command defined in AutoLISP,
  `caddis_undo` of a command run and of an F# run before and after one, command runs in background mode. QUIT, CLOSE, QNEW are refused.
- Step 3, panel and ribbon: the ribbon tab with its texts, icons and checkboxes, in the dark and light theme; the panel in a palette set, its colors in both themes,
  typing in its search box (with key messages posted into AutoCAD's queue, not a keyboard), the Info dialog, the command CADDIS with `Panel`.
- `caddis_dialog`: the dialog of OPTIONS run with `caddis_command`, listed with its buttons, and closed with `Cancel`, over REST; the tool over MCP.
- Step 4, the loader: loaded with NETLOAD from `loader\` with `CADDIS_AUTOCAD_DEV`, and from a folder like the bundle's `Contents` with `install-folder.txt`;
  AutoCAD registers the commands of the build it loads and calls its `Initialize`. The packaging steps publish locally; `PackageContents.xml` parses.

Not tested yet:
- **The installer and the bundle.** Writing a bundle to `%AppData%\Autodesk\ApplicationPlugins` was not tried: an AutoCAD of the person was running,
  and AutoCAD loads a bundle that appears there also into a running AutoCAD (`APPAUTOLOAD` 14). Test it with no other AutoCAD running:
  build `Caddis.AutoCAD.Bootstrapper`, copy `bin/publish/autocad` of the [packaging action](../.github/actions/package-autocad/action.yml) somewhere, run the bootstrapper from there.
  It writes the bundle; start AutoCAD, Caddis should load from that folder. Remove the bundle folder afterwards.
- `SECURELOAD` 1, the default: does AutoCAD ask before it loads the loader from the bundle in `%AppData%`? It is 0 in the profile on this computer.
- The Velopack release in the channel `autocad`, uploaded with `--merge` into Revit's release, and updates from it. Only the next tag runs it.
- The panel's first show without a saved place: docked right at 420 by 760. Tested only that `Load` doesn't fire for a palette set with a new id.
- the `net8.0-windows` build, i.e. AutoCAD 2025 or 2026 before their update to .NET 10
- AutoCAD 2026, and AutoCAD 2027 (R26.0, package AutoCAD.NET 26.0.0): does the build against the 2025 API load there?
- that zooming or panning with the mouse after a run makes `caddis_undo` refuse (it relies on `Document.ViewChanged`)
- `caddis_command` with more than 400 new command line lines, with several drawings open, with a command that shows a progress bar for longer than the input timeout,
  and `caddis_cancel` before AutoCAD started the commands (the code path exists, it's hard to hit by hand)

## How to test in AutoCAD

- `Dev\AutoCAD2025.cmd`, or `pwsh -File Dev/Start-Dev.ps1 AutoCAD2025 -NoBuild`. It starts `acad.exe /b load.scr` with `CADDIS_AUTOCAD_DEV`, the script NETLOADs the local loader from `%LocalAppData%\Caddis.Dev\AutoCAD2025\loader`.
- `"autoStart": true` in `%AppData%\Caddis\settings-autocad.json`, then REST on port 7830, e.g. `curl http://127.0.0.1:7830/eval -d "{\"code\": \"1 + 1\"}"`. Messages also go to `%AppData%\Caddis\autocad.log`.
- AutoCAD loads a new build only after a restart. Close only an AutoCAD you started yourself, with only test drawings in it.
  An AutoCAD that the person started may run at the same time: `Start-Dev.ps1` starts a second one, and only that one loads Caddis. Tell them apart by the command line `/b ...load.scr`.
- **AutoCAD 2025 may hang when it exits**, also without Caddis: one thread stays, it uses no CPU, and `taskkill` says there is no such process. It still locks the dev build, so Start-Dev refuses to start.
  Killing AutoCAD with `taskkill /F` before it exits works and frees the files, but AutoCAD then saves nothing, e.g. not the place of the panel.
  To test with a stuck one around, stage the build into another folder under `%LocalAppData%\Caddis.Dev` and start `acad.exe /b` with a script there yourself.
- **Never queue a command without all its inputs** with `SendStringToExecute`, e.g. `"_.LINE 0,0 "`. Every run is then rejected, and nothing from outside cancels the command:
  AutoCAD ignores an Esc posted to its main window, and refuses COM calls while the command waits (`RPC_E_CALL_REJECTED`). Only Esc in AutoCAD, or killing it, ends it.
  A backslash in the string is a pause for input too, e.g. in `(princ "\n")`; use `(chr 10)`. Test such things with `caddis_command`, which cancels its waiting commands.
- To experiment with the command context, queue a callback with `ExecuteInCommandContextAsync` in a run, and read what it recorded in the next run.
  Arm a watchdog first that queues `^C^C` from a dispatcher call, e.g. `SynchronizationContext.Current.Post` after a `Task.Delay`: a command left waiting rejects every run.
- `SECURELOAD` is 0 in the AutoCAD 2025 profile on this computer, so it doesn't ask before loading unsigned dlls.
- To see which undo step `UNDO` reverted, read the system variable `LASTPROMPT` in a run: it ends with e.g. `<1>:  Caddis r2`.
- Undo checks to repeat after changes to the undo tracking: draw, run `1 + 1`, `caddis_undo` of the drawing run must undo it.
  Draw, run a script that only zooms (`Editor.SetCurrentView`), `caddis_undo` of the drawing run must refuse.
- The ribbon and the panel from a run: FSI can't reference AdWindows, WPF or Avalonia, use reflection. Click a ribbon button by its id, e.g. `Caddis.Panel`,
  through its `CommandHandler`, posted with `SynchronizationContext.Current.Post` so that it runs after the run. The panel's view is `Panel.content` (internal) `.Value.View`.
- Screenshots: `PrintWindow` with `PW_RENDERFULLCONTENT` of AutoCAD's main window, or of the palette set's own window when it floats; close a dialog with `WM_CLOSE`.
- Switching `COLORTHEME` changes the profile in the registry, which the person's AutoCAD shares: set it back to what it was, 0 here.

## Step 2: caddis_command, done

What was found out, beyond what the docs say:
- `ExecuteInCommandContextAsync` never runs its callback at once from a dispatcher call, with or without a document lock. So `RunCommands` became asynchronous.
- `CommandAsync` doesn't help with the selection: a command asks at `Select objects:` also when only its name was given with `CommandAsync`. Its `CommandResult` has no `ICriticalNotifyCompletion`, so F#'s `task` can't await it; chain `OnCompleted` by hand.
- `LASTPROMPT` is the last line, not the prompt: for UNITS it was a line of its help text, after SELECT `Select objects:   1 found`. The message of the `PromptingFor*` event is the prompt.
- The members of an `internal` F# type are internal in IL: reflection on them needs `BindingFlags.NonPublic`.
- The setting `cancelWaitingCommands` does nothing in AutoCAD: a script can't start a command that waits, `SendStringToExecute` runs it after the run. The panel doesn't show it in AutoCAD.
- QUIT through `Editor.Command` froze AutoCAD; QNEW succeeded without a new drawing; CLOSE closed the only drawing. So commands that open or close drawings are refused.

Left to do:
- F# runs could show the command line too, from `AutoCADCommandLine.history` in `BeforeRun` and `AfterRun`, e.g. what `Editor.WriteMessage` printed.
  With `echoOutput` on, the command line also has Caddis' own echo of the output, which would have to be left out.

## Step 3: panel, ribbon, messages, done

What was found out:
- `PaletteSet.AddVisual` hosts the WPF `CaddisPanel`, and in it `AvaloniaHost`, a copy of Revit's without its code for Avalonia 11. Share it later, e.g. as a linked file.
- AutoCAD takes the keys typed in Avalonia's window for its command line before the window gets them, also `WM_CHAR`. `Application.PreTranslateMessage` gives them to Avalonia first, see `KeyboardFilter`.
- The palette set ignores `Size` before it is first shown. Without a saved state, AutoCAD shows it at the top left in its minimum size; `Load` fires only when it applies a saved state.
- Switching `COLORTHEME` makes AutoCAD rebuild its ribbon, the timer of `Ribbon` adds the tab again if it is gone. The ribbon's own state, e.g. the active tab, is reset then.
- `Application.Quit()` from a dispatcher call does nothing; the command QUIT quits.

## Step 4: installer, updates, CI, done

What was decided:
- A bundle names its modules relative to its folder, so the bootstrapper copies the loader into `Caddis.bundle\Contents` with `install-folder.txt`. An absolute `ModuleName` would avoid the copy, but couldn't be tested.
- The loader uses `Assembly.LoadFrom`: AutoCAD registers commands and calls `Initialize` for every loaded assembly, `ExtensionLoader.Load` isn't needed.
- A separate Velopack package `Caddis.AutoCAD`, in the channel `autocad` of the same GitHub release as Revit's, packed and uploaded with `--merge` after Revit's in release-revit.yml,
  so that it runs in the environment `releaseRevit` whose federated credential signs with Azure Trusted Signing.
- The bootstrapper's Dialog, Updates and Main are copies of Revit's with AutoCAD in their texts. Share them later.

## Smaller items

- Capture: other views, layouts and visual styles; now only the active view with `Document.CapturePreviewImage`.
- Export: PDF, STL, SAT and others, e.g. through the commands `_.-EXPORT`, `_.STLOUT`, `_.ACISOUT` with `RunCommands`; now only DWG and DXF with `Database.Wblock`.
  `ICaddisHost.Export` is synchronous, so that would need it to return a task too, as `RunCommands` does.
- `caddis_dialog` is done, with [Dialogs.fs](Dialogs.fs), a copy of [RevitDialogs.fs](../Caddis.Revit/RevitDialogs.fs). Share the two later.
- AutoCAD 2024 is installed on this computer and runs on .NET Framework: a `net48` build would need AutoCAD.NET 24.x and Avalonia 11 for the panel. Not planned yet.

## Open decisions for the user

- Ship the XML docs of the AutoCAD API, so that `caddis_describe` shows summaries? They are in the packages AutoCAD.NET, .Core and .Model.
  Autodesk's ObjectARX license allows passing copies on only with the license, for developing AutoCAD apps.
