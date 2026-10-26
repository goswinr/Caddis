# AGENTS.md

Caddis is an MCP server that hosts one persistent F# Interactive session inside Rhino or Revit,
so that AI agents can run RhinoCommon or Revit API code in the running app.
F# on .NET 8, .NET 10 and .NET Framework 4.8; Windows, and Rhino also on Mac.

Before a change that isn't trivial, read the relevant part of [Architecture.md](Architecture.md):
it covers the design decisions, every file, the request flow, threads, cancellation, and the known risks.
[README.md](README.md) documents the MCP tools, the REST endpoints and the settings for users.

## Commands

You need Windows with the .NET 8 and .NET 10 SDKs, as in CI. The net48 builds and tests run only on Windows.

```sh
dotnet build Caddis.slnx                         # all projects, Debug
dotnet build Caddis.slnx -c Release              # as CI does
dotnet test Caddis.slnx                          # all tests, on every target framework
dotnet test Tests/Caddis.Core.Tests -f net10.0   # one project on one framework, much faster
dotnet test Tests/Caddis.Core.Tests -f net10.0 --filter "FullyQualifiedName~keeps definitions between runs"
dotnet test Tests/Caddis.UI.Tests -f net10.0 --filter "FullyQualifiedName~PanelTests"
```

- Test names are F# backtick sentences, so filter by part of the sentence or by module name, e.g. `FullyQualifiedName~HttpTests`.
- Core tests run on net8.0, net10.0 and net48. UI tests run on net8.0 and net10.0 only, so on net48 only the build checks the panel (Avalonia 11).
- There is no formatter or linter config. Warning 1182 (unused variables) is on.

### In Rhino or Revit

Rhino and Revit themselves have no automated tests. CI tests the core with `HeadlessHost` and the panel with Avalonia's headless platform.
So changes to `RhinoHost`, `RevitHost`, the panel's hosting or anything else app-specific need a test in the app.

- `Dev\Rhino8.cmd`, `Rhino8NetFx.cmd`, `Rhino9.cmd` and `Revit2024.cmd` to `Revit2027.cmd` build Debug and start that app with only this build. The installed Caddis stays installed. They take `-Configuration Release` and `-NoBuild`, see [Dev/README.md](Dev/README.md).
  The build is copied first, so you can build again while the app runs, but the app loads a new build only after a restart.
- With `"autoStart": true` in `%AppData%\Caddis\settings-rhino.json` or `settings-revit.json`, the server starts with the app.
  Once it listens, it writes `%AppData%\Caddis\instances\<pid>.json`. Then use REST: `curl http://127.0.0.1:7810/eval -d "{\"code\": \"1 + 1\"}"`, with port 7820 for Revit.
- In Revit, a modal dialog blocks every run from starting. `GET /dialogs` lists it and `POST /dialog {"button": "..."}` presses one of its buttons.
- In Git Bash, set `MSYS_NO_PATHCONV=1` before passing `/switch` arguments to Windows programs. Otherwise Git Bash rewrites them as paths.

## Architecture

| Project | |
|---|---|
| `Caddis.Core` | F# session, runs, `HttpListener` server, REST, MCP, API docs. No Rhino or Revit dependency |
| `Caddis.UI` | Avalonia panel shared by both apps |
| `Caddis.Rhino` | Rhino plugin (`.rhp`): `RhinoHost`, the `Caddis` command, the panel in an Eto `NativeControlHost` |
| `Caddis.Revit` | Revit add-in: `RevitHost`, the ribbon, the panel in a WPF `HwndHost` |
| `Caddis.Revit.Loader` | C# add-in for Revit 2025 and later. It loads the net8.0 or net10.0 build of Caddis.Revit at start |
| `Caddis.Revit.Bootstrapper` | Velopack app of the Revit installer: registers the add-in and installs updates |

- An app plugs in by implementing `ICaddisHost` and supplying a `HostInfo`, both in [Caddis.Core/Model.fs](Caddis.Core/Model.fs). The interface carries the UI thread, undo, capture, export, commands and dialogs. `HeadlessHost` implements it for the tests.
- How a request runs: `CaddisServer` checks `Origin` and the body size. It passes the request to `Rest.handle` or `McpEndpoint`, which call `CaddisService`. `CaddisService` calls `FsiHost`, which runs the code on the app's UI thread via `host.InvokeOnUi`, or on the `Caddis FSI` background `WorkerThread`.
- There is one F# session per app process, shared by all clients. Only one run runs at a time: a second request gets 409 or an MCP tool error and is never queued. Changing the thread mode resets the session.
- MCP is hand-written JSON-RPC over Streamable HTTP, with one JSON response per request and no SSE. [McpView](Caddis.Core/Http/McpView.fs) writes results as text for the model plus images, without `structuredContent`, because Claude Code would then hide the text.
- The tool descriptions are written for the model and differ per app. They are the `McpTexts` in [RhinoInfo.fs](Caddis.Rhino/RhinoInfo.fs), [RevitInfo.fs](Caddis.Revit/RevitInfo.fs) and [HeadlessHost.fs](Caddis.Core/HeadlessHost.fs). `None` for `command` or `dialog` leaves out that tool.
  A new or changed tool or endpoint touches `Http/Mcp.fs`, `Http/Rest.fs` with its `/help`, those texts, the tables in the README and section 8 of Architecture.md.
- F# compiles files in the order of the `<Compile>` items in each `.fsproj`. Add a new file there, after the files it uses.

## Constraints you can't see in the code

Rhino and Revit load one copy of each assembly into their process, shared with every other plugin, e.g. Fesh. That causes these rules:
- Each project references FSharp.Core explicitly per target framework (`DisableImplicitFSharpCoreReference`). It must be the exact version that FSharp.Compiler.Service pins for that framework, the same pair as in Fesh. See the table in section 4 of Architecture.md. Change a pair in all projects at once, or not at all.
- net8.0 stays on FCS 43.8.403. Newer FCS versions need System.Diagnostics.DiagnosticSource 9 or later, which doesn't load in Rhino 8.
- Avoid new packages. Don't use ASP.NET Core or Kestrel, which Rhino doesn't load, the MCP C# SDK or Newtonsoft.Json. Write JSON with the in-box `System.Text.Json` `JsonNode`, via [Json.fs](Caddis.Core/Json.fs).
- net48 builds use Avalonia 11, the other builds Avalonia 12. Panel code and XAML must work with both.
- Revit builds compile against the oldest Revit they run in: 2024 for net48, 2025 for net8.0 and net10.0.

Some code depends on internals or on what the apps do at runtime:
- Cancellation, and the script folder of `caddis_check`, read private FCS fields by reflection. `FsiAssemblyResolver` puts itself first by setting a private field of the runtime. After any FCS or .NET upgrade, test again in Rhino, with values defined in a second run and after a reset.
- Public types of Caddis.Revit must not expose Avalonia or WPF types. FSI reads its public signature when a script uses `Caddis.Revit.Script`, and FSI on .NET can't resolve WPF types: the SDK's stub of WindowsBase wins. For the same reason, don't add WPF assemblies to the host's `References`.
- Don't rename `Caddis.Revit.Loader.CaddisLoader`: installed `.addin` files contain its full name. The loader and the bootstrapper find Caddis.Revit by its path, not by a project reference.
- The HTTP accept loop is a `backgroundTask`, not a `task`. `Start` runs on the app's UI thread, and a `task` would continue there, so it couldn't accept requests while a run blocks that thread.
- During a run, output capture redirects `Console.Out` for the whole process. That is why both test projects set `DisableTestParallelization`.

## Conventions

- The version comes from the latest versioned entry in [CHANGELOG.md](CHANGELOG.md), via Ionide.KeepAChangelog.Tasks. Add user-visible changes under `## [Unreleased]`.
  Write each bullet on one line, and keep one kind of line ending in the file. Otherwise the changelog task fails the build of every project.
- Update Architecture.md and the READMEs when the code changes. They describe the current behavior, file by file.
- Write docs, changelog entries and commit messages as plain, concrete sentences about what the user sees and why, like the existing ones. The subject of a commit names the change in behavior, e.g. "Show a notice in the panel when a newer Caddis is available". The body explains why.
- Give types and members `///` doc comments, as in the existing code.
- A release is a commit "Release x.y.z" that moves the Unreleased entries under `## [x.y.z] - <date>`, then a tag `x.y.z` without a `v`. The tag starts the workflows that publish to Yak and GitHub, so make a release only when asked.

## Git

- Commit on the branch that is checked out, also on `main`. Don't create a new branch to commit.
- Push only when asked.
