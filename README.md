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

> Early development. It builds and the core is tested outside of Rhino, but it has not been tested inside Rhino yet.

## What it does
- Hosts one persistent F# Interactive session inside Rhino 8 (.NET 8, Windows and Mac).
  Values, functions and types defined in one run stay available in the next.
- Serves an [MCP](https://modelcontextprotocol.io) endpoint at `/mcp`, so AI agents like Claude Code can run F# in Rhino,
  and plain REST endpoints for curl and scripts.
- Returns results: status, the value of the last expression, stdout, stderr, compiler diagnostics, exceptions with the script line, and files.
  Via MCP as compact text for the model with images inline, long output shortened and saved to a file. Via REST as JSON.
- Runs scripts either on the Rhino UI thread (safest, the default) or on a background thread (cancellable, with timeouts).
- Wraps every run in one Rhino undo record, captures viewports as PNG, and exports to any file format Rhino can write.

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
- `ListenOnLan` makes the server reachable from other machines.
- `NewToken` replaces the token.

It is scriptable, e.g. `-Caddis Start`.

## Connect an AI agent
In Rhino run `Caddis` then `CopyMcpCommand`, and paste the command in a terminal. It looks like:
```
claude mcp add --transport http --scope user caddis http://127.0.0.1:7810/mcp --header "Authorization: Bearer <token>"
```

`--scope user` makes Caddis available across your Claude Code projects.

The MCP tools are `caddis_eval`, `caddis_get_run`, `caddis_check`, `caddis_describe`, `caddis_status`, `caddis_cancel`, `caddis_reset`,
`caddis_set_mode`, `caddis_undo`, `caddis_capture_viewport` and `caddis_export`.

`caddis_describe` looks up types and members of the loaded assemblies, e.g. RhinoCommon, and returns their signatures in F# syntax
with the summaries from their XML doc files, so agents don't have to guess API names and overloads:
a namespace (`Rhino.Geometry`) lists its types, a type (`Brep`) its members, a member (`Brep.CreateFromBox`) all overloads with docs.
`search` finds names, e.g. `fillet`. Misspelled names get suggestions.

`caddis_eval` returns after `waitMs` (default 50 s) with status `running` if the run has not finished, it keeps running and
`caddis_get_run` waits for it. `timeoutMs` cancels a run, only in background mode.
`caddis_check` type-checks against the current session, so definitions from earlier runs are known.

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
| `POST /eval` | `{code?, path?, waitMs?, timeoutMs?, recordUndo?}`, returns the run, status 202 if still running after `waitMs` |
| `GET /runs`, `GET /runs/{id}?waitMs=` | Recent runs, one run, optionally waiting for it |
| `POST /cancel` | `{runId?}`, background mode only |
| `POST /reset` | New F# session |
| `POST /mode` | `{mode: "ui" \| "background"}` |
| `POST /check` | `{code?, path?}`, type-check against the current session without running |
| `GET /describe?name=&filter=&search=` | Signatures and docs of a namespace, type or member, or a search of names |
| `POST /undo` | `{runId?}`, undo a run in Rhino |
| `GET /viewport?view=&width=&height=&displayMode=` | PNG of a viewport |
| `POST /export` | `{format, name?, objects?: "all" \| "selected", objectIds?}` |
| `GET /files/{id}/{name}` | A file of a run, capture or export |
| `POST /mcp` | MCP endpoint |

Each Rhino instance writes its port to `%AppData%\Caddis\instances\<process id>.json`.

## In scripts
RhinoCommon is referenced in every session. Caddis adds:
```fsharp
Caddis.Script.OutDir   // a folder for files of this run, they are returned with the result
Caddis.Script.RunId    // e.g. "r12"
Caddis.Script.OnUi(fun () -> ...)  // run code on the Rhino UI thread, needed in background mode for UI calls
```

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
`Caddis.Core` has no Rhino dependency and is tested with a headless host. `Caddis.Rhino` is the Rhino plugin.
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
