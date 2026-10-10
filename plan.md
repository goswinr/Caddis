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
  Commands.fs      Caddis command: Start / Stop, Panel, CopyMcpCommand, Mode, Cancel, Reset, AutoStart, EchoOutput, ListenOnLan, NewToken
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
POST /command {commands, select?, inputTimeoutMs?, waitMs?}  → RunResult of Rhino commands  [caddis_command]
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
- The token is a random value, created on first start. `Caddis CopyMcpCommand` and the panel copy it, `Caddis NewToken` replaces it.
- **Port:** default 7810, configurable, with fallback to the next free port.
- **Discovery file:** `AppData/Caddis/instances/<pid>.json` holds `{pid, port, url, mcpUrl, listenOnLan, host, startedAt, tokenFile}`. It contains the path of the token file, not the token. It is deleted on stop and on Rhino close.
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
  - A runtime error in a top-level statement, e.g. an array index, is reported at line 1 (FSI's debug info for top-level code, also with `--optimize-`). Inside functions, and for exceptions thrown by a called function, the line is right.
- **Fixed after the first Rhino test:** export via `-_Export "<path>" _EnterEnd` (`RhinoDoc.Export` and `RhinoDoc.WriteFile` show the options dialog of e.g. STL, even with `SuppressDialogBoxes`), undo via the `_Undo` command (`RhinoDoc.Undo()` outside a command leaves an undo record open), `objectCount` without deleted objects, 400 for `failwith` messages, and the Rhino.Scripting hookup via `Rhino.Scripting.RhinoSync` (the name since Rhino.Scripting 0.7; Fesh.Rhino looked for the stale `Rhino.RhinoSync` too, fixed there on 2026-10-10). All re-tested in the second Rhino test.
- **Second Rhino test (2026-10-10), Rhino 8.35 on Windows without Fesh.Rhino, via MCP and REST:**
  - Confirmed: export without dialogs to stl, obj, step, iges, dwg, dxf, 3mf, fbx, gltf, svg and 3dm, with `selected` and `objectIds`, restoring the user's selection. Undo of the latest run. Status with units, tolerance, layers and an `objectCount` without deleted objects. Capture with display mode and the 1280 px cap. The Rhino.Scripting hookup without Fesh.Rhino, `#r "nuget: Rhino.Scripting"` 0.14 calling RhinoCommon from the background thread. Auth, `Origin` check, path traversal in `/files` blocked, MCP version negotiation and errors. Background mode: `Script.OnUi`, timeout, cancel, 409 for eval, check and export, status and capture during a run. Runs rejected during a Rhino command. Eval by `path` with relative `#load` and `__SOURCE_DIRECTORY__`. `caddis_describe` with RhinoCommon and NuGet types. `-Caddis Panel` and `-Caddis CopyMcpCommand`.
  - Found and fixed, with headless regression tests where possible (65 tests), re-tested in Rhino 8.35 on 2026-10-10 unless noted:
    - **Values of earlier runs:** with `--multiemit+` each run is an assembly named `FSI-ASSEMBLY`, versions restart in each session. Rhino's `AssemblyResolver.CurrentDomain_AssemblyResolve` comes before FSI's handler and returns the first `FSI-ASSEMBLY` of the process. So a value defined in any run but the very first failed later with a `TypeLoadException`, and after a reset a value silently came from an old session. `FsiAssemblyResolver` in `FsiHost.fs` now goes first in `AssemblyLoadContext.AssemblyResolve` (set via reflection) and resolves by full name in the session's `DynamicAssemblies`. The test puts a resolver like Rhino's first. Re-tested in background and UI mode and after a reset. Fesh.Rhino on net8 probably has the same bug.
    - **Requests during a UI-thread run:** the HTTP accept loop was a `task` started on the UI thread, so it continued there and no request was accepted until the run ended: no 409, no refused capture, `get_run` ignored `waitMs`. Now a `backgroundTask`. Re-tested: status, 409 for capture, eval, check, export and undo, and `get_run` with `waitMs` all answer at once during a UI-thread run.
    - **Undo:** `caddis_undo` refused after a later run without changes. Rhino raises `BeginRecording`, `EndRecording` and then `Purge` for the empty record of such a run, and the handler set `lastUndoRecord` to 0 on any purge. Now a purge of the record on top makes the record below it the top again. Rhino-only, no test. Re-tested: undo after runs without changes works, a Rhino command after the run still blocks it (the first attempt, restoring in `AfterRun`, had failed).
    - **`caddis_check` by `path`:** relative `#load` was resolved against the folder the session was created in, so it reported a missing file. `implicitIncludeDir` of the session's `tcConfigB` is now set to the script's folder during the check, via reflection. Only partly fixed: FCS's `ParseAndCheckInteraction` doesn't type-check the content of loaded files, so their definitions are still reported as not defined.
    - **Export:** PDF opened Rhino's print dialog and blocked until someone clicked, now it is refused. An extension without exporter silently wrote a 3dm file, now it is an error. Rhino-only, no test.
    - `caddis_describe` cut summaries at "e.g.". System.Drawing.Common is now referenced in every session, `#r "System.Drawing.Common"` was not found because the WindowsDesktop framework folder is not searched.
  - Known limitations: `caddis_check` doesn't know the definitions of files loaded with `#load`. `caddis_describe` finds F# modules only by compiled name (`ListModule.Map`, not `List.map`), and its `search` doesn't cover FSharp.Core. Printing 30,000 lines on the UI thread took 20 s, probably the echo to the command line. A width-only capture of a narrow view can exceed 1280 px in height. Memory grew from 792 to 1253 MB in about 90 runs including a NuGet restore.
- **`caddis_command` (2026-10-10), tested in Rhino 8.35 on Windows via REST and MCP** (74 headless tests): runs Rhino command macros via `RhinoApp.RunScript(docSerial, macro, echo = true)` on the UI thread, one `RunScript` per macro, stopping at the first that doesn't succeed. It is a run of kind `commands` (ids `c1`, `c2`, ...) that takes the same one-run-at-a-time slot in `FsiHost` as F# runs, but needs no F# session. `RhinoHost` collects:
  - The result of each command from `Command.EndCommand`. A macro that ran no command, e.g. an unknown name, is an error.
  - The command line output via `RhinoApp.CommandWindowCaptureEnabled` and `CapturedCommandWindowStrings`, into the run's stdout.
  - Created, changed and deleted objects from the add, delete and attribute events (a replace raises a delete and an add with the same id), and the selection if it changed.
  - The commands' own undo records: `UndoSerial` is the latest, `UndoSteps` how many; `caddis_undo` runs `_Undo` that often. No Caddis undo record is opened.
  - A waiting command: a background loop awaits a check on the UI thread every 200 ms. During a macro the UI thread only runs it while a command waits in a message loop. After `inputTimeoutMs` (default 3 s), or on `caddis_cancel`, the check reads `RhinoApp.CommandPrompt` and calls `RhinoApp.PostCancelEvent`.
  - Confirmed in Rhino: `RunScript` runs synchronously from `InvokeOnUiThread`. The check runs while a command waits for input, and `PostCancelEvent` cancels it (`-_Line 0,0,0`, `-_What`, `-_Loft`; the result shows e.g. 'Select curves to loft ( Point )'). Output of native commands is captured with newlines (`_Length`, `_Area`, `-_What _Enter`, 'Unknown command: ...'). Undo of a call with two lines, of `_Delete`, and of an F# run after command runs. `select` with `-_Move` reports the object as changed. With `inputTimeoutMs: 0` other runs get 409 and `/cancel` cancels the waiting command.
  - A modal dialog (`_Options`): the check runs inside the dialog's message loop too, but `PostCancelEvent` doesn't close the dialog, so the run stays running until someone closes it, then finishes, and the next command runs normally. A command still running 2 s after the cancel counts as held by a dialog or window: the message says so and suggests the `-` version, also if the command then succeeds. Its prompt is meaningless then ('Command').
- **Command line output of F# runs (2026-10-10), tested in Rhino 8.35:** `BeforeRun` starts capturing, `AfterRun` writes the captured text to the run's `CommandLine` buffer (separate from stdout, also used by command runs). Findings: switching `CommandWindowCaptureEnabled` off keeps what was captured and leaves out what is written meanwhile; `WriteLine` strings end with '
'. `RhinoLog` switches the capture off around Caddis' own writes, so the echo of a run's output isn't captured. But a write on the UI thread first prints what other threads queued, e.g. `RhinoApp.WriteLine` of a background run, so `RhinoLog` writes an empty string before switching the capture off; without it that text was lost after a `printfn`. Tested in UI and background mode.
  - An F# run that calls `RhinoApp.RunScript` with a macro that lacks an input waits for the user, e.g. `-_Length` with nothing selected; Esc in Rhino ends it.
- **`CancelWaitingCommands` setting (2026-10-10), tested in Rhino 8.35:** opt-in watchdog for F# runs, the same `checkWaiting` loop as for command runs, started in `BeforeRun` when `run.InputTimeout` is set. Only commands the script started are watched: `invokeOnUi` counts its calls in progress on the UI thread (`uiCallDepth`), and `Command.BeginCommand` during an F# run records whether one was in progress. In UI-thread mode the whole run is such a call; in background mode `Caddis.Script.OnUi` is, so commands typed in Rhino during a background run are left alone. Commands that Rhino.Scripting starts in background mode via its own SynchronizationContext are not watched. A cancelled command adds a note to the run and `RunScript` returns false, the script goes on. Tested: UI mode (`-_Length` cancelled after 3.8 s, note), background mode via `OnUi` (cancelled) and via a raw `InvokeOnUiThread` like a typed command (not cancelled), the `Caddis` command option and the panel checkbox.
- **Open:**
  - Test on Mac.
  - Verify the new embedded panel icon in Rhino. `Docs/img/makeAllSizes.ps1` generates the logo variants and `favicon.ico` from `logo.png`; the favicon is embedded in the plugin. Build verified.
  - Not yet tested in Rhino: the port-change warning, `ListenOnLan`.
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

- **FCS internals:** the cancel hack and the script folder of `caddis_check` reach into private FCS fields by reflection. This is safe only because the version is pinned; any FCS upgrade needs a re-test.
- **Assembly resolution:** Caddis puts its resolver for `FSI-ASSEMBLY` first by setting the private `AssemblyLoadContext.AssemblyResolve` field. If a .NET update renames it, Caddis logs that and values of earlier runs fail again in Rhino. A plugin that inserts its own handler at the front later would break it too.
- **Modal dialogs:** a script, or an export format, that opens a modal dialog blocks Rhino until someone clicks. PDF export is refused for this reason; the other formats are only known to be fine from the test above.
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
