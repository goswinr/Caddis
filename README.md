![Logo](https://raw.githubusercontent.com/goswinr/Caddis/main/Docs/img/logo128.png)

# Caddis

[![build](https://github.com/goswinr/Caddis/actions/workflows/build.yml/badge.svg?event=push)](https://github.com/goswinr/Caddis/actions/workflows/build.yml)
[![release rhino](https://github.com/goswinr/Caddis/actions/workflows/release-rhino.yml/badge.svg?event=push)](https://github.com/goswinr/Caddis/actions/workflows/release-rhino.yml)
[![release revit](https://github.com/goswinr/Caddis/actions/workflows/release-revit.yml/badge.svg?event=push)](https://github.com/goswinr/Caddis/actions/workflows/release-revit.yml)

![latest tag](https://img.shields.io/github/v/tag/goswinr/Caddis?label=Latest%20Tag)
![yak version](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fyak.rhino3d.com%2Fpackages%2Fcaddis&query=%24.version&logo=rhinoceros&label=Yak%20Package%20Manager&color=%23a3d6ff)

[![license](https://img.shields.io/github/license/goswinr/Caddis)](LICENSE)
![code size](https://img.shields.io/github/languages/code-size/goswinr/Caddis.svg)

An MCP server to host F# interactive in Rhino or Revit.
This gives your AI agent the full power to do anything that RhinoCommon or the RevitAPI can do.
Your LLM already knows how to use these APIs, but can't execute them directly, unless hosted inside Rhino or Revit.

**Individual Rhino/Revit methods are not registered as MCP tools.** Agents discover Caddis's tools through MCP,
then use `caddis_describe` to explore the API loaded in the running app: search names, list the types in a namespace,
inspect a type's members, or look up a method's overloads. It returns signatures in F# syntax from reflection,
with documentation from XML files when available, so agents can check the installed API version before using it.
They then write F# code that calls those methods, check it with `caddis_check`, and execute it with `caddis_eval`.


Caddis runs F# scripts directly from inside the Rhino or Revit process and is controlled via HTTP:
AI agents like Claude or Codex generate the F# code and send it for evaluation and execution inside the host application via it's [MCP](https://modelcontextprotocol.io) endpoint.


Plus, you get the F# code it ran too, so you could make a permanent plugin form from it.
This makes it ideal for AI-driven automation and rapid prototyping within Rhino and Revit.

> Early development. Tested in Revit 2024 and 2026.5 and Rhino 8.35 and 9.0-beta on Windows, not yet on Mac.


| App | Runs in | Install | Read more |
|---|---|---|---|
| Rhino | Rhino 8.19 or later on Windows and Mac, Rhino 9 Beta | Rhino's `PackageManager`, search for `Caddis` (after the first Yak release) | [Caddis for Rhino](Caddis.Rhino/README.md) |
| Revit | Revit 2024 to 2027 on Windows | `Caddis.Revit-win-Setup.exe` from the [latest release](https://github.com/goswinr/Caddis/releases/latest) | [Caddis for Revit](Caddis.Revit/README.md) |

Also install the [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) or newer, so that scripts can use NuGet packages via `#r "nuget: Name"`.
It works for every version of Rhino and Revit. Caddis runs without it, only NuGet packages need it, see [NuGet packages](#nuget-packages).

## What it does
- Hosts one persistent F# Interactive session inside the app.
  Values, functions and types defined in one run stay available in the next.
- Serves an MCP endpoint at `/mcp`, so AI agents can run F# in the app, and plain REST endpoints for curl and scripts.
- Returns results: status, the value of the last expression, stdout, stderr, compiler diagnostics, exceptions with the script line, notes of the app, e.g. Revit's warnings, and files.
  Via MCP as compact text for the model with images inline, long output shortened and saved to a file. Via REST as JSON.
- Runs scripts either on the app's UI thread (safest, the default) or on a background thread (cancellable, with timeouts).
- Wraps every run in one undo step, captures viewports as PNG, and exports to the file formats of the app.
- Looks up the signatures and docs of the loaded APIs, e.g. RhinoCommon or the Revit API, so agents don't have to guess API names and overloads.
- Shows the server state, the recent runs, and the output and code of the selected run in a panel docked in the app, in the app's light or dark theme.
- In Rhino it also runs Rhino commands like typed at the command line, in Revit it reports Revit's blocking dialogs and presses their buttons.

What is specific to each app, its installation, commands, panel and API, is in [Caddis for Rhino](Caddis.Rhino/README.md) and [Caddis for Revit](Caddis.Revit/README.md).

## Connect an AI agent
Start the server in the app and open the Caddis panel. While the server runs, it shows for Claude Code, Codex and Claude Desktop whether they connect to Caddis in this app:

- **active**: the agent connects to this app.
- **not set up**: `Set up` adds Caddis to the agent for all your projects, with the agent's own program, e.g. `claude mcp add ...`. For Claude Desktop it opens the extension `caddis-rhino.mcpb` or `caddis-revit.mcpb`, and Claude Desktop asks to install it.
- **inactive**: the agent connects to another port, e.g. that of a second Revit with Caddis. `Activate` asks the app on that port to hand it over once it is idle, with no run in progress, and waits for that. Then this app listens on that port, and agents connected to it go on with this app. If the port can't be taken, e.g. another program uses it, `Activate` sets the agent up for this app instead.

The tooltips of the buttons show the commands they run, a right-click on an agent copies them.
Sessions of an agent that were open before it was set up see Caddis after a restart.

Without the panel, run the commands yourself in a terminal. Rhino prints them to its command line when the server starts,
Revit shows them in the `Info` dialog of the `Caddis` ribbon tab, where Ctrl+C copies its text. For Claude Code:
```
claude mcp add --transport http --scope user caddis-rhino http://127.0.0.1:7810/mcp
```

`--scope user` makes Caddis available across your Claude Code projects.

For Codex:
```
codex mcp add caddis-rhino --url http://127.0.0.1:7810/mcp
```

### Claude Desktop
Claude Desktop can't use the URL of Caddis directly: it runs local MCP servers as programs only, and reaches the URLs of connectors
from Anthropic's servers, which can't reach your computer. So Caddis comes as a Claude Desktop extension, a `.mcpb` file,
whose small relay passes Claude Desktop's messages to Caddis on this computer. Claude Desktop brings the Node.js that runs it.

The panel's `Set up` makes the extension for the running app, with its port and its tools. Each [release](https://github.com/goswinr/Caddis/releases)
also has `caddis-rhino.mcpb` and `caddis-revit.mcpb` for the default ports: drag one into the window of Claude Desktop, or open it.
On Windows, Claude Desktop isn't the app for `.mcpb` files after it is installed. If Windows asks which app to open it with, choose
`More apps` > `Look for another app on this PC` and `%LOCALAPPDATA%\AnthropicClaude\claude.exe`, with `Always`.
Claude Desktop from the Microsoft Store or an MSIX file isn't in that folder: drag the file into it, or choose it under Settings > Extensions.
Its setting `Port` in Claude Desktop under Settings > Extensions must match the port of the Caddis panel.
While the app isn't running, the extension still lists its tools, and their results say to start the app.

Rhino and Revit have their own port and MCP name, so that both can run and stay registered in Claude Code at the same time:

| App | Default port | MCP name |
|---|---|---|
| Rhino | 7810 | `caddis-rhino` |
| Revit | 7820 | `caddis-revit` |

If the port is taken, the next free one is used, and the panel shows the agents set up for the port as inactive.

## MCP tools

| Tool | |
|---|---|
| `caddis_eval` | Run F# code or a script file in the session |
| `caddis_get_run` | A run, waiting for it if it is still running |
| `caddis_check` | Type-check F# code against the current session without running it |
| `caddis_describe` | Signatures and docs of namespaces, types and members of the loaded assemblies |
| `caddis_status` | State of the server, the F# session and the document |
| `caddis_cancel` | Cancel the current run, in background mode |
| `caddis_reset` | Start a new F# session |
| `caddis_set_mode` | Run scripts on the UI thread or in the background |
| `caddis_undo` | Undo a run in the app |
| `caddis_capture_viewport` | PNG of a viewport |
| `caddis_export` | Export to a file |
| `caddis_command` | Rhino only: run Rhino command macros, see [Rhino commands](Caddis.Rhino/README.md#rhino-commands) |
| `caddis_dialog` | Revit only: list Revit's open dialogs or press one of their buttons, see [Differences to Rhino](Caddis.Revit/README.md#differences-to-rhino) |

`caddis_eval` returns after `waitMs` (default 50 s) with status `running` if the run has not finished, it keeps running and
`caddis_get_run` waits for it. `timeoutMs` cancels a run, only in background mode.
Cancellation skips queued `Script.OnUi` callbacks. A callback already executing in the app must finish first;
the run stays busy until it returns, then cancellation stops the script before it continues.

`caddis_check` type-checks against the current session, so definitions from earlier runs are known,
but not the definitions in files loaded with `#load`.

`caddis_describe` looks up types and members of the loaded assemblies and returns their signatures in F# syntax
with the summaries from their XML doc files:
a namespace (`Rhino.Geometry`) lists its types, a type (`Brep`) its members, a member (`Brep.CreateFromBox`) all overloads with docs.
`search` finds names, e.g. `fillet`. Misspelled names get suggestions.
F# modules are found only by their compiled names, e.g. `Microsoft.FSharp.Collections.ListModule.Map` for `List.map`.

`caddis_export` is a run like `caddis_eval`: it returns `running` after `waitMs`, and `caddis_get_run` returns its files.
An explicit `objectIds` array must be nonempty and contain only valid ids of the app.

MCP results are text written for the model, without `structuredContent`:
Claude Code shows the model only the `structuredContent` of a result that has one ([#55677](https://github.com/anthropics/claude-code/issues/55677)).

### NuGet packages
Scripts can reference NuGet packages via `#r "nuget: Name"`. F# Interactive restores them with `dotnet msbuild`, so they need a .NET SDK of the app's .NET version or newer,
e.g. the .NET SDK 8 in Rhino 8 or Revit 2025, and any SDK on .NET Framework. Everything else works without an SDK.
Caddis finds `dotnet` where F# Interactive does: `DOTNET_HOST_PATH`, next to the running .NET runtime, on `PATH`, then in `Program Files\dotnet`.
Without a suitable SDK
- `caddis_status` and `/status` report it under `nuget`, with the reason and the download page,
- the description of `caddis_eval` and the instructions on `initialize` tell the agent, which can pass the advice on to the person, and in Rhino the instructions leave out the suggestion of [Rhino.Scripting](Caddis.Rhino/README.md#rhinoscripting),
- a failed run with `#r "nuget:"` gets a note explaining it: F# Interactive's own error doesn't mention the SDK,
- the panel recommends installing the SDK, with a button to its download page.

An SDK installed while the app runs is found within 10 s.

## REST
```
curl http://127.0.0.1:7810/eval -d "{\"code\": \"1 + 1\"}"
```

| Endpoint | |
|---|---|
| `GET /status` | State of the server and the F# session |
| `POST /eval` | `{code?, path?, waitMs?, timeoutMs?, recordUndo?}`, returns the run, status 202 if still running after `waitMs`. Without `waitMs` it waits until the run finished |
| `GET /runs`, `GET /runs/{id}?waitMs=` | Recent runs, one run, optionally waiting for it |
| `POST /cancel` | `{runId?}`, background mode only |
| `POST /reset` | New F# session |
| `POST /mode` | `{mode: "ui" \| "background"}` |
| `POST /check` | `{code?, path?}`, type-check against the current session without running |
| `GET /describe?name=&filter=&search=` | Signatures and docs of a namespace, type or member, or a search of names |
| `POST /undo` | `{runId?}`, undo a run in the app |
| `GET /viewport?view=&width=&height=&displayMode=` | PNG of a viewport |
| `POST /export` | `{format, name?, objects?: "all" \| "selected", objectIds?, waitMs?}`, an export is a run like `/eval` |
| `GET /files/{id}/{name}` | A file of a run, capture or export |
| `POST /command` | Rhino only: `{commands, select?, inputTimeoutMs?, waitMs?}`, runs Rhino commands, returns the run like `/eval` |
| `GET /dialogs`, `POST /dialog` | Revit only: Revit's open dialogs, `{button, dialog?}` presses a button of one |
| `GET /help` | A short description of all endpoints |
| `POST /mcp` | MCP endpoint |

## In scripts
Caddis adds to every session:
```fsharp
Caddis.Script.OutDir   // a folder for files of this run, they are returned with the result
Caddis.Script.RunId    // e.g. "r12"
Caddis.Script.OnUi(fun () -> ...)  // run code on the app's UI thread, needed in background mode for calls into the app
```
Each app references its API in every session and adds its own helpers,
see [scripts in Rhino](Caddis.Rhino/README.md#in-scripts) and [scripts in Revit](Caddis.Revit/README.md#in-scripts).

Scripts can check these conditional compilation symbols with `#if`, e.g. to run in several apps or runtimes:

| Symbol | Defined |
|---|---|
| `CADDIS` | in every Caddis session |
| `RHINO`, `REVIT` | in the app of that name |
| `NETFRAMEWORK` | on .NET Framework, in Rhino 8 on Windows and Revit 2024 |
| `NET`, `NETCOREAPP` | on .NET 8 and later |
| `INTERACTIVE` | by F# Interactive itself, in every session |

`extraFsiArgs` in the settings file of the app can add more, e.g. `"--define:MYSYMBOL"`.

## Files
Caddis keeps its files in `%AppData%\Caddis` on Windows and in `~/.config/Caddis` on Mac:

| File | |
|---|---|
| `settings-rhino.json` | The settings of Caddis in Rhino |
| `settings-revit.json` | The settings of Caddis in Revit |
| `panel-rhino8.json`, `panel-rhino9.json` | Whether the panel is shown in Rhino and where, restored when Rhino starts |
| `instances\<process id>.json` | The port of each running server |
| `revit.log` | Caddis' own messages in Revit |
| `revit-update.json` | The result of the latest update check in Revit, which the panel and `Info` show |

## Security
Caddis has no authentication: anyone who can send requests to it can run any code on your computer, with your rights.
- The server only listens on localhost, so other computers can't reach it.
- Every program on this computer can reach it, also programs of other users signed in to it.
- Requests from web pages, with an `Origin` header, are rejected.

## Development
```
dotnet build Caddis.slnx
dotnet test Caddis.slnx
```

| Project | |
|---|---|
| `Caddis.Core` | The F# session, HTTP server, REST and MCP, without a Rhino or Revit dependency, tested with a headless host |
| `Caddis.UI` | The shared Avalonia panel, styles, output formatting and code viewer |
| `Caddis.Rhino` | The Rhino plugin, see [Caddis for Rhino](Caddis.Rhino/README.md) |
| `Caddis.Revit` | The Revit add-in, see [Caddis for Revit](Caddis.Revit/README.md) |
| `Caddis.Revit.Loader` | Loads the build of `Caddis.Revit` for the .NET of Revit 2025 and later |
| `Caddis.Revit.Bootstrapper` | The app of the Revit installer, it registers the add-in and installs updates |
| `Tests` | Tests of Core, and of the shared panel with Avalonia's headless platform on both runtimes |

`Caddis.Core` and `Caddis.UI` target `net8.0;net10.0` with Avalonia 12, and `net48` for Rhino and Revit 2024 on .NET Framework, with Avalonia 11.
Each app supplies the panel's actions, theme colors and docking wrapper: Eto in Rhino, WPF `HwndHost` in Revit.
What differs between them, the texts of the MCP tools, the id format of objects and whether there are commands, comes from `ICaddisHost.Info`.
See [Architecture.md](Architecture.md) for the design, the API and what is not there yet.

### Run a local build in Rhino or Revit
To start Rhino or Revit with a local build instead of the installed Caddis, double-click e.g. `Dev\Rhino8.cmd` or `Dev\Revit2026.cmd`, see [Dev/README.md](Dev/README.md).

### Releases
The package version and assembly versions come from the latest versioned entry in [CHANGELOG.md](CHANGELOG.md),
using `Ionide.KeepAChangelog.Tasks`, as in Fesh.Rhino. To publish a release:

1. Add a versioned entry to `CHANGELOG.md`, for example `## [0.1.0] - 2026-10-10`, and commit it.
2. Push a matching tag, for example `0.1.0` (without a `v` prefix).

The tag starts two release workflows, each needs a one-time setup:
[Yak packages for Rhino](Caddis.Rhino/README.md#yak-releases) and [the Revit installer](Caddis.Revit/README.md#releases).

## License
[MIT](LICENSE)
