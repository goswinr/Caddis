# Caddis for AutoCAD

The AutoCAD plugin of Caddis, an F# interactive host for AutoCAD 2025 and later, controlled through HTTP and MCP.
It has the same F# session, HTTP server, REST endpoints and MCP tools as in Rhino,
except `caddis_command` and `caddis_dialog`, which are not there yet.
See the [main README](../README.md) for what Caddis does, connecting an agent, the MCP tools and the REST endpoints.

> A first version, not packaged yet. Tested in AutoCAD 2025 with its update 25.0.189, on .NET 10.

## Installation

There is no installer yet. Build Caddis and load `Caddis.AutoCAD.dll` with AutoCAD's command `NETLOAD`,
from `Caddis.AutoCAD\bin\Debug\net10.0-windows` or `net8.0-windows`, for the .NET that AutoCAD runs on.
Or double-click [Dev\AutoCAD2025.cmd](../Dev/README.md), which builds Caddis and starts AutoCAD 2025 with it.
AutoCAD asks whether to load the unsigned `Caddis.AutoCAD.dll`, unless its folder is in AutoCAD's trusted locations.

There is a build of Caddis for each .NET that AutoCAD runs on:

| AutoCAD | .NET | Build | FSharp.Compiler.Service, FSharp.Core |
|---|---|---|---|
| 2025 and 2026 as released | .NET 8 | `net8.0` | 43.8.403, 8.0.403 |
| 2025 from its update 25.0.189 on | .NET 10 | `net10.0` | 43.12.401, 10.1.401 |

AutoCAD's folder has the file `acdbmgd.runtimeconfig.json`, its `tfm` tells which .NET it runs on.
AutoCAD 2024 and older run on .NET Framework, Caddis doesn't support them. AutoCAD LT and AutoCAD for Mac have no .NET API.

The builds reference the API of AutoCAD 2025, from the package [AutoCAD.NET](https://www.nuget.org/packages/AutoCAD.NET) 25.0.1.
The versions of FSharp.Compiler.Service and FSharp.Core are those of the build of [Fesh](https://github.com/goswinr/Fesh) for the same .NET, as in Rhino and Revit.

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

It is scriptable, e.g. `(command "CADDIS" "_Start")`.
Caddis writes its messages to the command line and to `%AppData%\Caddis\autocad.log`, which also has the messages from when no drawing was open.

## How runs work in AutoCAD

- **Application context.** Caddis runs code from AutoCAD's message loop, like the code of a modeless window, not inside a command.
  `Editor.Command` fails there with `eInvalidInput`, and the `Editor.Get*` prompts wait for the person.
  To run a command from a script, queue it with `doc.SendStringToExecute("_.ZOOM _E ", true, false, false)`: AutoCAD runs it after the run has ended.
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

## Status, capture and export

- `caddis_status` shows AutoCAD's version and build, the command in progress, the open drawings,
  and the active drawing: its file, whether it is modified, units (`INSUNITS`), measurement, layout and space, extents, the number of objects in model space and of selected objects, the current layer and the layers.
- `caddis_capture_viewport` captures the active view of the active drawing with `Document.CapturePreviewImage`. Other views and visual styles are not supported yet.
- `caddis_export` writes a copy of the drawing, or of some objects in model space, to a new DWG or DXF file with `Database.Wblock`. The drawing and its file name stay as they are.
- Objects are identified by their handle as a hexadecimal string, e.g. `2A3F`.

## Not there yet

| Missing | Notes |
|---|---|
| `caddis_command` | A command that waits for input can't be cancelled from outside AutoCAD: it ignores an Esc posted to its window, and refuses COM calls while it waits. Caddis' own calls on the main thread still run then, so the plan is to queue `^C^C` with `SendStringToExecute` from one of them, not tested yet. |
| `caddis_dialog` | The Win32 and UI Automation code of Revit's dialogs could be shared. |
| The panel, a ribbon tab | AutoCAD's `PaletteSet` can host the Avalonia panel through WPF, as Revit's dockable pane does. |
| An installer and updates | A bundle in `ApplicationPlugins`, with a loader that picks the net8.0 or net10.0 build, as in Revit. |
| Summaries in `caddis_describe` | AutoCAD doesn't install the XML docs of its API. They are in the AutoCAD.NET packages, whose license allows passing them on only with the license. |
| The command line in results | AutoCAD has no API to read its command line. Output of `Editor.WriteMessage` is not in the result, use `printfn`. |
