# Caddis for AutoCAD

The AutoCAD plugin of Caddis, an F# interactive host for AutoCAD 2025 and later, controlled through HTTP and MCP.
It has the same F# session, HTTP server, REST endpoints, MCP tools and panel as in Rhino and Revit,
also `caddis_command` as in Rhino and `caddis_dialog` as in Revit.
See the [main README](../README.md) for what Caddis does, connecting an agent, the MCP tools and the REST endpoints.

> Tested in AutoCAD 2025 with its update 25.0.189, on .NET 10. The installer is new and not tested in a release yet.

## Installation

Download `Caddis.AutoCAD-autocad-Setup.exe` from the [latest release](https://github.com/goswinr/Caddis/releases/latest) and run it.
It needs no admin rights. It installs Caddis to `%LocalAppData%\Caddis.AutoCAD\current`
and registers it with the bundle `%AppData%\Autodesk\ApplicationPlugins\Caddis.bundle`, which every AutoCAD from 2025 on loads when it starts,
also the products based on AutoCAD, e.g. Civil 3D. An AutoCAD that is running loads it right away.
If AutoCAD asks whether to load `Caddis.AutoCAD.Loader.dll`, choose 'Always Load'.

When AutoCAD starts, Caddis checks for a newer release on GitHub and offers to download it. A downloaded release is installed when the last AutoCAD has closed.
To uninstall, use 'Installed apps' in Windows' settings, it removes the bundle too.

Without the installer, build Caddis and load `Caddis.AutoCAD.dll` with AutoCAD's command `NETLOAD`,
from `Caddis.AutoCAD\bin\Debug\net10.0-windows` or `net8.0-windows`, for the .NET that AutoCAD runs on.
Or double-click [Dev\AutoCAD2025.cmd](../Dev/README.md), which builds Caddis and starts AutoCAD 2025 with it, also when Caddis is installed.
AutoCAD asks whether to load an unsigned dll, unless its folder is in AutoCAD's trusted locations.

There is a build of Caddis for each .NET that AutoCAD runs on:

| AutoCAD | .NET | Build | FSharp.Compiler.Service, FSharp.Core |
|---|---|---|---|
| 2025 and 2026 as released | .NET 8 | `net8.0` | 43.8.403, 8.0.403 |
| 2025 from its update 25.0.189 on | .NET 10 | `net10.0` | 43.12.401, 10.1.401 |

AutoCAD's folder has the file `acdbmgd.runtimeconfig.json`, its `tfm` tells which .NET it runs on.
The bundle loads a small [loader](../Caddis.AutoCAD.Loader/README.md), which loads the build for the .NET of the AutoCAD it runs in. So Caddis keeps working when an update of AutoCAD changes its .NET.
AutoCAD 2024 and older run on .NET Framework, Caddis doesn't support them. AutoCAD LT and AutoCAD for Mac have no .NET API.

The builds reference the API of AutoCAD 2025, from the package [AutoCAD.NET](https://www.nuget.org/packages/AutoCAD.NET) 25.0.1.
The versions of FSharp.Compiler.Service and FSharp.Core are those of the build of [Fesh](https://github.com/goswinr/Fesh) for the same .NET, as in Rhino and Revit.

## The ribbon tab and the panel

The tab `Caddis` on AutoCAD's ribbon has:

| Button | Does |
|---|---|
| `Start listening`, `Stop listening` | starts or stops the server on port 7830 |
| `Mode` | runs scripts on AutoCAD's main thread or on a background thread. Starts a new F# session |
| `Reset F# Session` | starts a new F# session, all values are lost |
| `Cancel run` | cancels a run in background mode, or the commands of `caddis_command` while they wait for input |
| `Caddis Details Panel` | shows or hides the panel |
| `Info` | the state, the commands to add Caddis to Claude Code and Codex, the latest messages, and errors. It turns red after an error |
| `Start with AutoCAD`, `Echo output` | the settings `autoStart` and `echoOutput` |

The panel is the same as in Rhino and Revit, in an AutoCAD palette: the server, set up of the AI agents, the settings, the recent runs with their output and code.
The first time, it docks on the right of AutoCAD's window. AutoCAD keeps its place and size from then on, and shows it again at the next start if it was open.
It follows AutoCAD's dark or light theme. While it has the focus, it gets all keys, also AutoCAD's shortcuts: click into the drawing to give them back to AutoCAD.

## The CADDIS command

`CADDIS` prints the state of the server and the session, then offers:

| Option | Does |
|---|---|
| `Start`, `Stop` | starts or stops the server on port 7830 |
| `Mode` | runs scripts on AutoCAD's main thread (`UIThread`, the default) or on a `Background` thread. Starts a new session |
| `Cancel` | cancels a run in background mode |
| `Reset` | starts a new F# session, all values are lost |
| `AutoStart` | starts the server when Caddis loads, toggles and keeps the prompt open |
| `EchoOutput` | also prints the output of runs to the command line, toggles and keeps the prompt open |
| `Panel` | shows the panel |

It is scriptable, e.g. `(command "CADDIS" "_Start")`. `CADDISPANEL` shows the panel too.
Caddis writes its messages to the command line and to `%AppData%\Caddis\autocad.log`, which also has the messages from when no drawing was open.

## How runs work in AutoCAD

- **Application context.** Caddis runs code from AutoCAD's message loop, like the code of a modeless window, not inside a command.
  `Editor.Command` fails there with `eInvalidInput`, and the `Editor.Get*` prompts wait for the person.
  To run commands, use `caddis_command`. A script can only queue a command with `doc.SendStringToExecute("_.ZOOM _E ", true, false, false)`: AutoCAD runs it after the run has ended.
- **Document lock.** In UI-thread mode Caddis locks the active drawing for the whole run, so the script can change it without locking it itself.
  AutoCAD counts each lock as a command. A run that changed something is one undo step, named after the run, e.g. `Caddis r12`.
  AutoCAD keeps no undo step for a run that changed nothing.
- **Undo.** `caddis_undo` sends AutoCAD's `UNDO 1`, which runs right after the call. It only does so if the run is the latest undo step:
  any command or change of the drawing in AutoCAD after the run makes it refuse, and so does a later run that changed the view or a system variable.
  Zooming or panning with the mouse after the run should make it refuse too, through `Document.ViewChanged`, but that is not tested yet.
- **Commands in progress.** A run is rejected while a command is active, e.g. one that waits for a point. The person has to finish or cancel it.
- **Background mode.** Every call of the AutoCAD API must be inside `Caddis.Script.OnUi(fun () -> ...)`. Caddis locks the drawing for each call,
  so each call that changes something is its own undo step. `caddis_undo` works only for a run with exactly one of them.
- **Redraw.** The views are redrawn after each run.

## AutoCAD commands

`caddis_command` runs commands as if typed at the command line, e.g. `["_.LINE 0,0 10,0 10,5 \"\"", "_.ZOOM _E"]`, one after the other until one does not succeed.

- **Inputs** are separated by spaces. Unlike in scripts a space is no Enter: an empty input `""` is Enter, e.g. to end LINE.
  An input that starts with a quote ends at the next quote, e.g. `_.-LAYER _M "Wall 1" ""`. Other quotes are part of the input, e.g. `2'6"`. A leading `^C^C` of menu macros is left out.
- **How**: Caddis calls `Editor.Command` with the inputs of each macro, all in one call of `DocumentCollection.ExecuteInCommandContextAsync`.
  AutoCAD runs that call as its command `EXECUTEFUNCTION`, once Caddis' call on the main thread has returned. So all commands of a call are one undo step, which `caddis_undo` reverts with `UNDO 1`.
  As with `(command ...)` in AutoLISP, commands use their command line version, e.g. UNITS. Commands defined in AutoLISP can't be run this way.
- **Selection**: `select` takes handles, otherwise the commands get the objects that are selected in AutoCAD. `EXECUTEFUNCTION` clears the selection when it starts, so Caddis selects them again.
  A command uses the selected objects only if no inputs follow its name, e.g. `_.ERASE`. Otherwise it asks at `Select objects:`, where `_P ""` gives it the selected objects, e.g. `_.MOVE _P "" 0,0 10,0`:
  Caddis runs SELECT with them first, which makes them the previous selection set.
- **Waiting for input**: a command that waits at a prompt for input that the macro does not give is cancelled after `inputTimeoutMs` (3 s), and `caddis_cancel` cancels it at once.
  Caddis' calls on the main thread still run while a command waits. One of them queues `^C^C` with `SendStringToExecute`, which the prompt takes as its input.
  The `PromptingFor*` and `PromptedFor*` events of the editor tell whether a prompt waits, and its message.
- **Dialogs**: a command that opens a modal dialog, e.g. OPTIONS, waits until the dialog is closed, and the result says so. The input timeout doesn't cancel it:
  the dialog doesn't take the `^C^C`, AutoCAD would run it later, e.g. at a prompt of the next command. A `caddis_cancel` meanwhile keeps the next macros from running.
  `caddis_dialog` lists the open dialog with its text and buttons, and presses one of them, e.g. `Cancel`. It finds the dialog on top of AutoCAD's main window, which a modal dialog disables,
  and reads and presses its buttons with UI Automation, as in Revit. `GET /dialogs` and `POST /dialog {"button": "Cancel"}` do the same over REST.
- **Drawings**: commands that open or close drawings, e.g. `QUIT`, `CLOSE`, `QNEW` or `OPEN`, are refused: AutoCAD can't run them from the command context of a drawing, `QUIT` froze it in a test.
  Scripts open and create drawings with `Application.DocumentManager` in `caddis_eval`.
- **Results**: the English name and result of each command, the handles of the entities in layouts that the commands created, changed and erased, and the selection if it changed.
  Also the command line from AutoCAD's history, e.g. what LIST or DIST print, read with the internal API `Autodesk.AutoCAD.Internal.Utils.GetLastCommandLines`. AutoCAD keeps its last 400 lines.

## Status, capture and export

- `caddis_status` shows AutoCAD's version and build, the command in progress, the open drawings,
  and the active drawing: its file, whether it is modified, units (`INSUNITS`), measurement, layout and space, extents, the number of objects in model space and of selected objects, the current layer and the layers.
- `caddis_capture_viewport` captures the active view of the active drawing with `Document.CapturePreviewImage`. Other views and visual styles are not supported yet.
- `caddis_export` writes a copy of the drawing, or of some objects in model space, to a new DWG or DXF file with `Database.Wblock`. The drawing and its file name stay as they are.
- Objects are identified by their handle as a hexadecimal string, e.g. `2A3F`.

## Not there yet

| Missing | Notes |
|---|---|

| Summaries in `caddis_describe` | AutoCAD doesn't install the XML docs of its API. They are in the AutoCAD.NET packages, whose license allows passing them on only with the license. |
| The command line in results of `caddis_eval` | Output of `Editor.WriteMessage` is not in the result, use `printfn`. Command runs show the command line from AutoCAD's history, F# runs could too. |
