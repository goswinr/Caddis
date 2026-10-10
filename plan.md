# Caddis: plan

Caddis runs F# Interactive (FSI) inside the Rhino 8 process on .NET 8, and is controlled over HTTP.

## Decisions

| Topic | Decision |
|---|---|
| Protocol | MCP (Streamable HTTP, JSON-RPC) at `/mcp` plus plain REST, both built on one shared core |
| Threading | Switchable for the whole session, like Fesh: **UI thread** or **background thread**. Switching resets FSI. Default: UI thread, same as Fesh.Rhino. |
| Sessions | One shared, persistent FSI session. One run at a time; a request while busy gets `409 Busy`, not a queue. |
| Security | Bearer token required. Listens on 127.0.0.1 by default; LAN is an opt-in setting. Requests with an `Origin` header are rejected. |
| Fesh coexistence | Independent, pinned to FCS 43.8.403 and FSharp.Core 8.0.403 (the same versions as Fesh 0.33.5-net8), so both plugins load together. Each has its own FSI session. |
| Lifecycle | `Caddis` command to start and stop, an opt-in autostart setting, and one discovery file per Rhino instance |
| v1 extras | Cancel + timeout, viewport capture, type-check only, undo last eval, returning exported/saved files |
| Platform / UI | Plain `net8.0` running on Rhino 8 for Windows and Mac, with an Eto dockable panel |

## Constraints

- **No Kestrel.** Rhino 8's `dotnetstart.8.runtimeconfig.json` loads only `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App`, not ASP.NET Core. The server is `System.Net.HttpListener`, which is part of the base runtime.
- **MCP is hand-written**, not built on the official C# SDK. The SDK pulls in newer `Microsoft.Extensions.*` and `System.Diagnostics.DiagnosticSource` packages, which is the same conflict that keeps Fesh on FCS 43.8.x for net8. A minimal JSON-RPC subset needs about 200 lines.
- **JSON** uses the in-box `System.Text.Json` (`Utf8JsonWriter` / `JsonNode`), with no extra packages.
- **Rhino.Scripting** finds its host through Fesh.Rhino's plugin GUID (`Fesh.Rhino.Sync` via reflection). Caddis gets its own GUID and sets the public `Rhino.RhinoSync.SyncContext` property by reflection, either when that assembly loads or once at startup if it is already loaded.

## Project layout

Two projects keep the core testable without Rhino:

```
Caddis.Core/   net8.0, no RhinoCommon dependency
  Settings.fs      port, listenOnLan, autostart, threadMode, token → AppData/Caddis/settings.json
  Output.fs        TextWriter that captures output per run; swaps Console.Out/Err during an eval
  FsiHost.fs       session create/reset, eval, modes, cancel (ported from Fesh Src/Fsi.fs)
  (type-check)     FsiEvaluationSession.ParseAndCheckInteraction, against the live session
  Runs.fs          run registry: id, state, output, diagnostics, files, timings, per-run out dir
  Json.fs          output types and serialization
  Http/Server.fs   HttpListener loop, auth, Origin check, routing, discovery file
  Http/Rest.fs     REST handlers
  Http/Mcp.fs      initialize, tools/list, tools/call, ping
Caddis.Rhino/  net8.0, TargetExt .rhp
  AssemblyInfo.fs  new plugin GUID (not Fesh's)
  PlugIn.fs        load, UI SynchronizationContext, autostart, Rhino.Scripting hookup
  RhinoOps.fs      undo record wrapping, redraw, InCommand guard, viewport capture, export, undo
  Panel.fs         Eto panel: server toggle, URL/token copy, mode, run log, cancel button
  Commands.fs      Caddis command: Start / Stop / Status / Token / Mode
  yak.yml
Tests/         Expecto or xUnit tests for Caddis.Core (MCP routing, auth, eval, cancel)
```

`Caddis.Core` declares interfaces for the Rhino hooks (`beforeEval`, `afterEval`, `capture`, `export`), and `Caddis.Rhino` implements them.

Like Fesh.Rhino, use `DisableImplicitFSharpCoreReference` and reference `FSharp.Core` 8.0.403 and `FSharp.Compiler.Service` 43.8.403 explicitly. RhinoCommon 8.x is referenced with `PrivateAssets="all" ExcludeAssets="runtime"`.

## Porting from Fesh `Src/Fsi.fs`

**Keep:**
- `createSession`: FSI args and `fsi` printers, the current-directory workaround for dotnet/fsharp#9064 (create the session while the current directory is FSharp.Core's folder), and `--lib:` with the RhinoCommon folder (found via `typeof<RhinoApp>.Assembly.Location` on both OSes).
- The .NET Core cancellation path: `setAControlledExecutionCancellationToken` and `getFrameworkAgnosticAborter`, which use reflection into `fsiInterruptController` / `controlledExecution`.
- `includeScriptFolder` (`#I`), and `EvalInteractionNonThrowing(code, scriptPath)` so line numbers and `__SOURCE_DIRECTORY__` work.
- The state machine (`NotLoaded | Initializing | Ready | Compiling | Evaluating`) and the rule that switching mode resets the session.

**Drop:** the editor, `GoTo`, MessageBoxes, AvalonLog, every net48 / `Thread.Abort` branch, and `SyncWpf`.

**Change:**
- **Background thread:** no WPF `Dispatcher` exists here, so it becomes a plain thread running a small single-thread `SynchronizationContext` work loop. It is STA only when `OperatingSystem.IsWindows()`.
- **FSI args:** Fesh's defaults (`--langversion:preview --exec --debug+ --debug:full --optimize+ --nologo --multiemit+`), with `--gui+` only on Windows.
- **Results:** instead of printing to a log, the eval builds a structured `RunResult`.
- **Script context:** every session references `Caddis.Core.dll` (`-r:`), so scripts can use the static `Caddis.Script.OutDir`, `Caddis.Script.RunId` and `Caddis.Script.OnUi(fun () -> ...)`. Files the script saves to `OutDir` come back in the response. (Not `session.AddBoundValue`: it emits a new FSI fragment on every call.)
- **References:** the loaded `RhinoCommon.dll` is referenced in every session.

## API

REST (MCP tool names in brackets):

```
GET  /status                  state, mode, versions, Rhino doc, current run, memory     [caddis_status]
POST /eval    {code|path, waitMs?, timeoutMs?, recordUndo?=true}  → RunResult (202 if still running after waitMs)  [caddis_eval]
GET  /runs/{id}?waitMs=       poll or wait for a running run                             [caddis_get_run]
POST /cancel  {runId?}        background mode only                                       [caddis_cancel]
POST /reset                                                                              [caddis_reset]
POST /mode    {ui|background} resets the session                                         [caddis_set_mode]
POST /check   {code|path}     → diagnostics against the current session, no run          [caddis_check]
GET  /describe?name=&filter=&search=  signatures and docs from the loaded assemblies     [caddis_describe]
POST /undo                    undo the last eval if its record is still on top           [caddis_undo]
GET  /viewport?view=&w=&h=&displayMode=  → PNG (MCP: inline image content)               [caddis_capture_viewport]
POST /export  {ext, objects: all|selected|[ids]}  → files                                [caddis_export]
GET  /files/{runId}/{name}    download any returned file
POST /mcp                     JSON-RPC; replies with application/json (no SSE in v1)
```

**RunResult fields:** `id`, `status` (ok, compileError, runtimeError, cancelled, timeout), `stdout`, `stderr`, `fsiOutput`, `value{type,text}`, `diagnostics[{severity, message, file, range}]`, `exception{type, message, stackTrace, scriptLine}`, `files[{name, size, mime, path, url}]`, `durationMs`. Output is capped and flagged as truncated when cut.

**Files:** each one is returned as a URL and a local path. An agent on the same machine reads the path; a client on the LAN uses the URL. In MCP, images are returned inline and all files are listed with their local path (and URL for LAN clients).

**Exports:** `RhinoDoc.Export` / `ExportSelected`, which pick the format from the extension, so any format Rhino can write works. `.3dm` uses `WriteFile`.

**MCP:** handle `initialize` (negotiate the protocol version the client asks for), `notifications/initialized` (reply 202), `tools/list`, `tools/call`, and `ping`. `GET /mcp` returns 405 because v1 has no server-initiated stream.

**Connecting Claude Code:**

```
claude mcp add --transport http --scope user caddis http://127.0.0.1:<port>/mcp --header "Authorization: Bearer <token>"
```

## Settings, token and discovery

- Settings and the token live in `Environment.SpecialFolder.ApplicationData/Caddis/`, which works on Windows and Mac.
- The token is a random value, created on first start and shown by `Caddis Token` and in the panel.
- **Port:** default 7810, configurable, with fallback to the next free port.
- **Discovery file:** `AppData/Caddis/instances/<pid>.json` holds `{port, pid, rhinoVersion, docPath, startedAt}`. It contains no token. It is deleted on stop and on Rhino close.
- **LAN:** the prefix becomes `http://+:<port>/`. On Windows this needs a one-time admin `netsh http add urlacl url=http://+:<port>/ user=Everyone`; the command prints that line when access is denied.

## Rhino integration (same as Fesh.Rhino)

- Every eval is wrapped in `BeginUndoRecord` / `EndUndoRecord` on the UI thread.
- Redraw is re-enabled and forced after every run, including failed ones.
- An eval is refused while `Command.InCommand()`.
- On Rhino close, the running eval is cancelled, the server stops and the discovery file is deleted.

## Progress (2026-10-10)

- **Done, tested outside Rhino** (28 tests in `Tests/Caddis.Core.Tests`, headless host): FsiHost core (phase 1), HTTP server, auth and REST (phase 2), MCP endpoint (phase 3), type-check.
- **Tested in Rhino 8.35 on Windows, next to Fesh.Rhino 0.33.5** (via REST and MCP):
  - Eval, persistent values, stdout, compile and runtime errors, files in `Caddis.Script.OutDir`, type-check with RhinoCommon, viewport capture with display mode.
  - Undo record per run. Runs are rejected while a Rhino command is active.
  - Background mode: `Script.OnUi`, timeout and cancel of a busy loop with Rhino staying responsive, 409 for a second run.
  - `#r "nuget: Rhino.Scripting"` in background mode.
  - Fesh.Rhino and Caddis share one FSharp.Core and FCS. Caddis.Core is loaded once into the default load context, so scripts and the plugin share `Caddis.Script`.
  - A runtime error in a top-level expression is reported at line 1 (FSI's debug info for top-level code, also with `--optimize-`). Inside functions the line is right.
- **Fixed after the Rhino test, not yet re-tested in Rhino** (needs a Rhino restart to load the new build):
  - **Export:** `RhinoDoc.Export` and `RhinoDoc.WriteFile` (even with `SuppressDialogBoxes`) show the options dialog of e.g. STL and block until someone clicks. Now `-_Export "<path>" _EnterEnd` on the selected objects, restoring the user's selection. Tested from a script for stl, obj, step, 3mf, dwg, igs: no dialogs.
  - **Undo:** `RhinoDoc.Undo()` outside a command leaves an undo record open, so later runs get no undo record. Now the `_Undo` command via `RhinoApp.RunScript`, tested from a script.
  - **Status:** `objectCount` counted deleted objects kept for undo.
  - **Errors:** messages from `failwith` (e.g. unknown view) are now 400 instead of 500.
  - **Rhino.Scripting:** the hookup looked for `Rhino.RhinoSync`, but since Rhino.Scripting 0.12 it is `Rhino.Scripting.RhinoSync`. (Fesh.Rhino's `PlugIn.fs` uses the same stale name.)
- **Open:**
  - Re-test the fixes above in Rhino. Test the Rhino.Scripting hookup without Fesh.Rhino loaded. Test on Mac.
  - Verify the new embedded panel icon in Rhino. `Docs/img/makeAllSizes.ps1` generates the logo variants and `favicon.ico` from `logo.png`; the favicon is embedded in the plugin. Build verified.
  - Run the first Yak release after configuring the repository's `YAK_TOKEN` secret.
- **MCP review (2026-10-10), tested outside Rhino** (49 tests):
  - MCP results are plain text written for the model, plus inline images; no `structuredContent`, because Claude Code then shows the model only that JSON ([claude-code#55677](https://github.com/anthropics/claude-code/issues/55677)). REST keeps the full `RunResult` JSON.
  - Empty fields are left out; stdout, stderr, FSI output, values and exceptions have size budgets and keep start and end, cut at line ends. Shortened output is saved to `<output folder>/logs/<run>-<stream>.txt`. Stack traces keep only the frames that lead into the script.
  - The capture buffer keeps the first and the latest half of `maxOutputChars` instead of only the start.
  - `waitMs` (return with status `running`, default 50 s in MCP) is separate from `timeoutMs` (cancel, background only). `caddis_get_run` waits too and takes `stdoutOffset` to poll only new output.
  - `caddis_check` uses `ParseAndCheckInteraction`, so definitions of earlier runs are known; the separate `FSharpChecker` is gone.
  - Capture is refused while a UI-thread run is in progress, export and undo while any run is.
  - `Mcp-Session-Id` is handed out on initialize and only used to keep request ids of different clients apart for `notifications/cancelled`. Unsupported `MCP-Protocol-Version` gets 400, unknown tools `-32602`.
  - `caddis_describe` (`ApiDocs.fs`): reflection over the loaded assemblies plus their XML doc files (RhinoCommon.xml is next to RhinoCommon.dll in `System/netcore`), signatures in F# syntax (curried F# functions, `[<Out>]` arguments, `option`/`list`/function types). Large types are shown in less detail until they fit 16k characters. Misspelled names get suggestions by contained words and edit distance. Checked against RhinoCommon 8.35 loaded by reflection outside Rhino, e.g. `Brep` fits with one overload per member including inherited ones.
  - Not yet tested in Rhino: capture size cap (1280 px long edge by default), document units, tolerance and layers in `caddis_status`, the port-change warning.
- **Packaging implemented and verified locally** (phase 6): shared build/release packaging action, a `rh8_19-any` Yak package with a `net8.0` folder, tag/changelog consistency checks, versioning via Ionide.KeepAChangelog, and README logo and badges matching Fesh.Rhino. Release build, all 28 tests, workflow linting and the actual Yak package build pass. Publishing to Yak is pending the first release.

## Phases

0. **Spike (de-risk first).** A net8.0 .rhp creates the pinned FSI session and evaluates `1+1`. Check each of these:
   - Loading next to Fesh.Rhino (Windows).
   - Loading on Mac Rhino 8.
   - ControlledExecution cancel on Windows and Mac.
   - `#r "nuget:..."`.
   - `AddBoundValue`.
1. **FsiHost core**: output capture, runs, both modes, cancel and timeout, Rhino hooks, Rhino.Scripting SyncContext.
2. **HTTP**: server, auth, REST, settings, token, discovery file, and the `Caddis` command, with tests on the core.
3. **MCP endpoint**, verified against Claude Code and MCP Inspector.
4. **Rhino operations**: viewport capture, export and files, undo, type-check.
5. **Eto panel.**
6. **Packaging**: a yak package for Rhino 8 (Windows and Mac), GitHub Actions modelled on Fesh.Rhino, README, and a CHANGELOG versioned by Ionide.KeepAChangelog.

## Risks

- **FCS internals:** the cancel hack reaches into private FCS fields by reflection. This is safe only because the version is pinned; any FCS upgrade needs a re-test.
- **Mac is unverified:** ControlledExecution, `System.Drawing` for viewport capture (the fallback is `-_ViewCaptureToFile`), and whether a UI `SynchronizationContext` exists (the fallback is `RhinoApp.InvokeOnUiThread`).
- **Background thread:** raw RhinoCommon UI calls from that thread can crash Rhino. That is why the default is the UI thread.
- **Memory:** FSI's emitted assemblies are never unloaded, so memory grows over a long Rhino session and a reset does not free it. `/status` reports it.
- **LAN:** the token travels in plain text over HTTP. TLS is out of scope for v1.
- **NuGet references:** `#r "nuget:"` needs the .NET SDK installed on the machine.
- **Shared console:** swapping `Console.Out` during an eval also captures other plugins' output written in that window.
- **Other plugins:** another plugin that loads an older FSharp.Core first can block Caddis from loading (the same known issue as Fesh.Rhino).

## Defaults to confirm

These were chosen without an explicit decision and may change:

1. The default mode is the UI thread.
2. A request while a run is active gets `409 Busy` rather than being queued.
3. The project is split into `Caddis.Core` and `Caddis.Rhino`.
4. Scripts use `Caddis.Script.OutDir` (changed from a bound value, see above).
5. The default port is 7810, falls back to the next free port, and is published in the discovery file.
