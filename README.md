![Logo](https://raw.githubusercontent.com/goswinr/Caddis/main/Docs/img/logo128.png)

# Caddis

[![build](https://github.com/goswinr/Caddis/actions/workflows/build.yml/badge.svg?event=push)](https://github.com/goswinr/Caddis/actions/workflows/build.yml)
[![publish yak](https://github.com/goswinr/Caddis/actions/workflows/release.yml/badge.svg?event=push)](https://github.com/goswinr/Caddis/actions/workflows/release.yml)

![latest tag](https://img.shields.io/github/v/tag/goswinr/Caddis?label=Latest%20Tag)
![yak version](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fyak.rhino3d.com%2Fpackages%2Fcaddis&query=%24.version&logo=rhinoceros&label=Yak%20Package%20Manager&color=%23a3d6ff)

[![license](https://img.shields.io/github/license/goswinr/Caddis)](LICENSE)
![code size](https://img.shields.io/github/languages/code-size/goswinr/Caddis.svg)

An F# interactive host for Rhino,
runs F# scripts within the Rhino process.
Controlled via http, for AI agents and remote script execution and automation within Rhino.
There is also an add-in for Revit 2026, see [Revit](#revit).

> Early development. Tested in Rhino 8.35 on Windows, not yet on Mac, and in Revit 2026.5.

## What it does
- Hosts one persistent F# Interactive session inside Rhino 8 (.NET 8, Windows and Mac).
  Values, functions and types defined in one run stay available in the next.
- Serves an [MCP](https://modelcontextprotocol.io) endpoint at `/mcp`, so AI agents like Claude Code can run F# in Rhino,
  and plain REST endpoints for curl and scripts.
- Returns results: status, the value of the last expression, stdout, what was printed to the Rhino command line (e.g. by `RhinoApp.WriteLine` or commands run via `RhinoApp.RunScript`), stderr, compiler diagnostics, exceptions with the script line, and files.
  Via MCP as compact text for the model with images inline, long output shortened and saved to a file. Via REST as JSON.
- Runs scripts either on the Rhino UI thread (safest, the default) or on a background thread (cancellable, with timeouts).
- Runs Rhino commands like typed at the command line, e.g. `-_Line 0,0,0 10,0,0`, and returns what they printed and the objects they created.
- Wraps every run in one Rhino undo record, captures viewports as PNG, and exports to any file format Rhino can write, except PDF.

## Installation

After the first Yak release, install via Rhino's `PackageManager` command and search for `Caddis`.
The package supports Rhino 8.19 or later on Windows and Mac, running on .NET 8.

### Manual installation

Build it with `dotnet build Caddis.Rhino/Caddis.Rhino.fsproj`,
then drag `Caddis.Rhino/bin/Debug/net8.0/Caddis.Rhino.rhp` into Rhino 8.
Rhino must run on .NET 8, see the Rhino command `SetDotNetRuntime`.

Caddis can be loaded together with [Fesh.Rhino](https://github.com/goswinr/Fesh.Rhino),
both use the same versions of FSharp.Core and FSharp.Compiler.Service.

## Usage in Rhino
Run the command `Caddis`. Its options:
- `Start` / `Stop` the HTTP server, by default on `http://127.0.0.1:7810`, or the next free port.
- `Panel` opens a panel with the recent runs and their output.
- `CopyMcpCommand` copies the command to add Caddis to Claude Code, including the token.
- `Mode` runs scripts on the `UIThread` or in the `Background`. Changing it resets the F# session.
- `Cancel` the current run, `Reset` the F# session.
- `AutoStart` starts the server when Rhino starts.
- `EchoOutput` also writes the output of runs to the Rhino command line.
- `CancelWaitingCommands` (off by default, also a checkbox in the panel) cancels Rhino commands that an F# script starts, e.g. via `RhinoApp.RunScript`,
  when they wait 3 s for input that the macro doesn't give. The script goes on, the result has a note with the prompt.
  Without it such a command waits until someone gives the input in Rhino. In background mode only commands started via `Caddis.Script.OnUi` are watched,
  so commands you type in Rhino while a script runs are never cancelled.
- `ListenOnLan` makes the server reachable from other machines.
- `NewToken` replaces the token.

It is scriptable, e.g. `-Caddis Start`.

## Connect an AI agent
In Rhino run `Caddis` then `CopyMcpCommand`, and paste the command in a terminal. It looks like:
```
claude mcp add --transport http --scope user caddis http://127.0.0.1:7810/mcp --header "Authorization: Bearer <token>"
```

`--scope user` makes Caddis available across your Claude Code projects.

The MCP tools are `caddis_eval`, `caddis_command`, `caddis_get_run`, `caddis_check`, `caddis_describe`, `caddis_status`, `caddis_cancel`, `caddis_reset`,
`caddis_set_mode`, `caddis_undo`, `caddis_capture_viewport` and `caddis_export`, in Revit also `caddis_dialog` and not `caddis_command`.

`caddis_describe` looks up types and members of the loaded assemblies, e.g. RhinoCommon, and returns their signatures in F# syntax
with the summaries from their XML doc files, so agents don't have to guess API names and overloads:
a namespace (`Rhino.Geometry`) lists its types, a type (`Brep`) its members, a member (`Brep.CreateFromBox`) all overloads with docs.
`search` finds names, e.g. `fillet`. Misspelled names get suggestions.
F# modules are found only by their compiled names, e.g. `Microsoft.FSharp.Collections.ListModule.Map` for `List.map`.

`caddis_command` runs Rhino command macros, as typed at the command line or in a button macro, e.g. `["-_Line 0,0,0 10,0,0", "_Zoom _All _Extents"]`,
one after the other until one does not succeed. It always runs on the UI thread.
It returns the result of each command, the ids of the objects they created, changed and deleted, the selection if it changed,
and the text they printed to the command line, e.g. of `_What`, `_Length` or `_Area`.
`select` selects objects by id before the first command. A command that waits for input that its macro doesn't give is cancelled
after `inputTimeoutMs` (default 3 s), the result shows its prompt; 0 waits for the person in Rhino, e.g. for `_Pause`.
A leading `!` is left out. Each command that changes the document is one undo step, `caddis_undo` undoes all of a call.
A command that opens a dialog blocks Rhino until someone closes it, so use the scripted `-` version of a command.

`caddis_eval` returns after `waitMs` (default 50 s) with status `running` if the run has not finished, it keeps running and
`caddis_get_run` waits for it. `timeoutMs` cancels a run, only in background mode.
`caddis_check` type-checks against the current session, so definitions from earlier runs are known,
but not the definitions in files loaded with `#load`.

MCP results are text written for the model, without `structuredContent`:
Claude Code shows the model only the `structuredContent` of a result that has one ([#55677](https://github.com/anthropics/claude-code/issues/55677)).

## REST
Every request needs the header `Authorization: Bearer <token>`.
The token is in `%AppData%\Caddis\token.txt` on Windows and `~/.config/Caddis/token.txt` on Mac.
```
curl -H "Authorization: Bearer <token>" http://127.0.0.1:7810/eval -d "{\"code\": \"1 + 1\"}"
```

| Endpoint | |
|---|---|
| `GET /status` | State of the server and the F# session |
| `POST /eval` | `{code?, path?, waitMs?, timeoutMs?, recordUndo?}`, returns the run, status 202 if still running after `waitMs`. Without `waitMs` it waits until the run finished |
| `POST /command` | `{commands, select?, inputTimeoutMs?, waitMs?}`, runs Rhino commands, returns the run like `/eval` |
| `GET /runs`, `GET /runs/{id}?waitMs=` | Recent runs, one run, optionally waiting for it |
| `POST /cancel` | `{runId?}`, background mode only |
| `POST /reset` | New F# session |
| `POST /mode` | `{mode: "ui" \| "background"}` |
| `POST /check` | `{code?, path?}`, type-check against the current session without running |
| `GET /describe?name=&filter=&search=` | Signatures and docs of a namespace, type or member, or a search of names |
| `POST /undo` | `{runId?}`, undo a run in Rhino |
| `GET /viewport?view=&width=&height=&displayMode=` | PNG of a viewport |
| `POST /export` | `{format, name?, objects?: "all" \| "selected", objectIds?, waitMs?}`, an export is a run like `/eval` |
| `GET /files/{id}/{name}` | A file of a run, capture or export |
| `POST /mcp` | MCP endpoint |

Each Rhino instance writes its port to `%AppData%\Caddis\instances\<process id>.json`.

## In scripts
RhinoCommon and System.Drawing.Common are referenced in every session.
Other assemblies of Rhino can be referenced by file name, e.g. `#r "Eto.dll"` or `#r "Rhino.UI.dll"`,
and NuGet packages via `#r "nuget: Rhino.Scripting"`, which needs the .NET SDK. Caddis adds:
```fsharp
Caddis.Script.OutDir   // a folder for files of this run, they are returned with the result
Caddis.Script.RunId    // e.g. "r12"
Caddis.Script.OnUi(fun () -> ...)  // run code on the Rhino UI thread, needed in background mode for UI calls
```

## Revit
> Tested in Revit 2026.5 on Windows.

`Caddis.Revit` is an add-in for Revit 2026.5 on .NET 10. It has the same F# session, HTTP server, REST endpoints and MCP tools as in Rhino,
except `caddis_command`: Revit has no command line. It adds `caddis_dialog` for Revit's dialogs.

It uses FSharp.Compiler.Service 43.12.401 and FSharp.Core 10.1.401, like the .NET 10 build of [Fesh](https://github.com/goswinr/Fesh).
Revit loads only one version of them, so Caddis.Revit can run next to a Fesh.Revit built for .NET 10, not next to one built for .NET 8.

### Installation for development
```
dotnet build Caddis.Revit -c Release
./Caddis.Revit/install-dev.ps1 -Configuration Release
```
The script writes `Caddis.addin` to `%AppData%\Autodesk\Revit\Addins\2026`, pointing at the build output.
On its first start, and after every rebuild, Revit asks whether to load the unsigned add-in, choose 'Always Load'.
Revit locks the dll while it runs, close Revit before building again. `install-dev.ps1 -Uninstall` removes the add-in.

### Usage in Revit
The `Caddis` ribbon tab has the buttons `Start` / `Stop`, `Copy MCP`, `Panel`, `Mode`, `Reset F#`, `Cancel run`, `Info` and `New token`,
plus native checkboxes for `Start with Revit` and `Listen on the network (LAN)`, synchronized with the panel even when it is hidden.
Caddis' own messages go to `%AppData%\Caddis\revit.log`, `Info` shows the latest ones.

`Panel` shows or hides the optional Caddis pane, like the panel in Rhino: the server state and settings,
the recent runs, and the output and code of the selected run. It docks next to the Project Browser,
can be moved like any Revit pane, and Revit remembers where it is and whether it is shown.
Its buttons show their messages in the pane instead of in dialogs.
The pane follows Revit's light or dark UI theme immediately, including dropdowns, run selection,
output, scrollbars and keyboard focus. Its WPF styles are scoped to Caddis, leaving Revit and other add-ins' resources untouched.
See the [Revit panel previews](Caddis.Revit/README.md#dockable-panel) for both themes.

Revit has its own settings file `settings-revit.json`, the default port 7820 and the MCP name `caddis-revit`,
so that Caddis in Rhino and in Revit can run and stay registered in Claude Code at the same time. The token is shared.
```
claude mcp add --transport http --scope user caddis-revit http://127.0.0.1:7820/mcp --header "Authorization: Bearer <token>"
```

### Differences to Rhino
- The Revit API can only be used in Revit's API context, being on the UI thread is not enough.
  Runs on the `ui` thread (the default) run inside an external event, which Revit runs only while no command, edit mode or modal dialog is active.
  Until then a run stays `running` and its result says it has not started yet.
- A Revit dialog, e.g. 'Project Not Saved Recently' or 'Unresolved References', blocks every call until it is closed.
  `caddis_status` and the results of runs still running name an open dialog with its text and buttons.
  `caddis_dialog` lists it or presses one of its buttons (REST: `GET /dialogs`, `POST /dialog`).
- Each run on the `ui` thread is wrapped in a transaction group. Scripts open their own transactions,
  or use `Caddis.Revit.Script.Transact("name", fun doc -> ...)`. If the run succeeds, its transactions are one undo step `Caddis r12`, which `caddis_undo` reverts.
  If it fails, Caddis rolls back all its changes.
- Revit's warnings about the changes of a run, e.g. overlapping walls, are removed and listed in the notes of the result.
  An error rolls back its transaction instead of showing Revit's dialog.
- The result lists the ids of the elements a run created and deleted. Changed elements are counted per category,
  because Revit reports many it updates itself, e.g. schedules; their ids are saved to a file.
- In `background` mode every Revit API call must be inside `Caddis.Revit.Script.OnUi(fun () -> ...)` or `Script.Transact`.
  Each transaction is an undo step of its own, and a failed run is not rolled back.
- `caddis_undo` posts Revit's Undo command, which runs right after the call.
- `caddis_capture_viewport` uses `Document.ExportImage`: it shows the whole view or its crop region, not the zoom of the window.
  `displayMode` is a display style, e.g. `HiddenLine`, `Shading` or `Realistic`.
- `caddis_export` writes dwg, dxf, dgn, pdf, dwf and dwfx from the active view, fbx, obj, stl, step and sat from a 3D view,
  and ifc and nwc from the whole model. With `selected` or `objectIds`, element ids as numbers, Caddis isolates those elements in the view during the export.
  Exports of a large model take a while, e.g. 47 s for a 159 MB ifc of the Snowdon Towers sample. Like `caddis_eval` an export is a run:
  it returns `running` after `waitMs`, and `caddis_get_run` returns its files.

In scripts RevitAPI, RevitAPIUI and `Caddis.Revit.dll` are referenced:
```fsharp
Caddis.Revit.Script.Doc      // the active Document
Caddis.Revit.Script.UiDoc    // the active UIDocument, e.g. for the selection
Caddis.Revit.Script.UiApp    // the UIApplication
Caddis.Revit.Script.Transact("name", fun doc -> ...)  // a committed transaction, also in background mode
Caddis.Revit.Script.OnUi(fun () -> ...)  // in Revit's API context, needed in background mode
```
`Caddis.Revit.Script` also has `OutDir` and `RunId` of `Caddis.Script`.

Scripts can't use WPF types, e.g. `System.Windows.Window`: F# Interactive type-checks against the .NET SDK's reference assemblies,
whose `WindowsBase.dll` is an empty stub that takes precedence over the real one, also with `#r`. Reflection works.

## Security
Anyone who can send requests to Caddis can run any code on your computer.
- Every request needs the token.
- By default the server only listens on localhost.
- Requests from web pages, with an `Origin` header, are rejected.
- With `ListenOnLan` the token is sent unencrypted over the network.
  On Windows this needs a one-time `netsh http add urlacl ...` as administrator, Caddis prints the exact command.

## Development
```
dotnet build Caddis.slnx
dotnet test Caddis.slnx
```
`Caddis.Core` has no Rhino or Revit dependency and is tested with a headless host. `Caddis.Rhino` is the Rhino plugin, `Caddis.Revit` the Revit add-in.
What differs between them, the texts of the MCP tools, the id format of objects and whether there are commands, comes from `ICaddisHost.Info`.
See [plan.md](plan.md) for the design and the open tasks.

### Yak releases

Build and release CI use the same [packaging action](.github/actions/package/action.yml).
It publishes the plugin and its dependencies into `bin/Release/rh8/net8.0`, includes the Yak manifest,
logo and license, then builds `bin/Release/caddis-<version>-rh8_19-any.yak`.
The package version and assembly versions come from the latest versioned entry in [CHANGELOG.md](CHANGELOG.md),
using `Ionide.KeepAChangelog.Tasks`, as in Fesh.Rhino.

To publish a release:

1. Generate a Yak CI token with `yak.exe login --ci` and save it as the repository Actions secret `YAK_TOKEN`.
2. Add a versioned entry to `CHANGELOG.md`, for example `## [0.1.0] - 2026-10-10`, and commit it.
3. Push a matching tag, for example `0.1.0` (without a `v` prefix).

The [release workflow](.github/workflows/release.yml) builds, tests, packages, checks the tag against the changelog,
and pushes to Yak. Build CI also creates the package on every push and pull request to `main`.

## License
[MIT](LICENSE)
