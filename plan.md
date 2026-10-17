# Caddis: plan

Caddis runs F# Interactive (FSI) inside the Rhino 8 process on .NET 8, and is controlled over HTTP.

## Decisions

| Topic | Decision |
|---|---|
| Protocol | MCP (Streamable HTTP, JSON-RPC) at `/mcp` plus plain REST, both built on one shared core |
| Threading | Switchable for the whole session, like Fesh: **UI thread** or **background thread**. Switching resets FSI. Default: UI thread, same as Fesh.Rhino. |
| Sessions | One shared, persistent FSI session. One run at a time; a request while busy gets `409 Busy`, not a queue. |
| Security | Bearer token required. Listens on 127.0.0.1 by default; LAN is an opt-in setting. Requests with an `Origin` header are rejected. |
| Fesh coexistence | Independent, pinned to FCS 43.8.403 and FSharp.Core 8.0.403 (the same versions as Fesh 0.33.5-net8), so both plugins load together. Each has its own FSI session. The `net10.0` build of Caddis.Core, for Revit, uses FCS 43.12.401 and FSharp.Core 10.1.401, like Fesh 0.34.0-net10. |
| Lifecycle | `Caddis` command to start and stop, an opt-in autostart setting, and one discovery file per Rhino instance |
| v1 extras | Cancel + timeout, viewport capture, type-check only, undo last eval, returning exported/saved files |
| Platform / UI | One Avalonia panel in `Caddis.UI` (`net8.0;net10.0` with Avalonia 12, and since 2026-10-10 `net48` with Avalonia 11 for Rhino on .NET Framework), shared by Rhino and Revit (2026-10-10). Rhino docks it through Eto on Windows and Mac; Revit docks it through a WPF `HwndHost`. Each host supplies actions, capabilities and theme colors. |

## Constraints

- **No Kestrel.** Rhino 8's `dotnetstart.8.runtimeconfig.json` loads only `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App`, not ASP.NET Core. The server is `System.Net.HttpListener`, which is part of the base runtime.
- **MCP is hand-written**, not built on the official C# SDK. The SDK pulls in newer `Microsoft.Extensions.*` and `System.Diagnostics.DiagnosticSource` packages, which is the same conflict that keeps Fesh on FCS 43.8.x for net8. A minimal JSON-RPC subset needs about 200 lines.
- **JSON** uses the in-box `System.Text.Json` (`Utf8JsonWriter` / `JsonNode`), with no extra packages.
- **Rhino.Scripting** finds its host through Fesh.Rhino's plugin GUID (`Fesh.Rhino.Sync` via reflection). Caddis gets its own GUID and sets the public `Rhino.RhinoSync.SyncContext` property by reflection, either when that assembly loads or once at startup if it is already loaded.

## Project layout

Four projects keep the core and shared UI independent of the host APIs:

```
Caddis.Core/   net8.0;net10.0;net48, no Rhino or Revit dependency
  Settings.fs      port, listenOnLan, autostart, threadMode, token → AppData/Caddis/settings-rhino.json
  Output.fs        TextWriter that captures output per run; swaps Console.Out/Err during an eval
  FsiHost.fs       session create/reset, eval, modes, cancel (ported from Fesh Src/Fsi.fs)
  (type-check)     FsiEvaluationSession.ParseAndCheckInteraction, against the live session
  Runs.fs          run registry: id, state, output, diagnostics, files, timings, per-run out dir
  Json.fs          output types and serialization
  Http/Server.fs   HttpListener loop, auth, Origin check, routing, discovery file
  Http/Rest.fs     REST handlers
  Http/Mcp.fs      initialize, tools/list, tools/call, ping
Caddis.UI/     net8.0;net10.0 with Avalonia 12, net48 with Avalonia 11, shared Avalonia content, no Rhino or Revit dependency
  PanelHost.fs     host actions, optional settings, messages and theme palette
  AvaloniaSetup.fs starts Avalonia without taking over the host's main loop; resolves native libraries
  EmbeddedView.fs  lifetime and DPI handling for the embedded native view
  PanelView.fs     shared view (PanelView.axaml, PanelStyles.axaml): state, settings, runs, output and code
  RunDetails.fs    output formatting, including Rhino commands and Revit grouped element changes
  FSharpHighlighting.fs  light/dark F# highlighting (FSharp.xshd); code formatting uses Fantomas 7
Caddis.Rhino/  net8.0 (Rhino 8), net10.0 (Rhino 9), net48 (Rhino 8 on .NET Framework, Windows only), TargetExt .rhp
  AssemblyInfo.fs  new plugin GUID (not Fesh's)
  PlugIn.fs        load, UI SynchronizationContext, autostart, Rhino.Scripting hookup
  RhinoOps.fs      undo record wrapping, redraw, InCommand guard, viewport capture, export, undo
  AvaloniaHost.fs  starts Avalonia in Rhino's process, hosts its embeddable top level in an Eto NativeControlHost
  PanelTheme.fs    the panel's brushes from Rhino.UI.ThemeSettings, follows ThemeChanged
  Panel.fs         the Eto docking shell and Rhino services for the shared PanelView
  Commands.fs      Caddis command: Start / Stop, Panel, CopyMcpCommand, Mode, Cancel, Reset, AutoStart, EchoOutput, ListenOnLan, NewToken
  yak.yml
Caddis.Revit/  net48 (Revit 2024), net8.0-windows (Revit 2025 and 2026 up to .4), net10.0-windows (Revit 2025.5, 2026.5 and later, 2027)
  AvaloniaHost.fs  WPF HwndHost, native parenting, keyboard input and DPI changes
  PanelTheme.fs    Revit light/dark colors and ThemeChanged subscription
  Panel.fs         pane registration (unchanged ID), WPF shell and Revit services for PanelView
  Updates.fs       starts the bootstrapper to check for updates, and to install them when the last Revit has closed
Caddis.Revit.Loader/        net8.0, C#: the add-in in Revit 2025 and later, loads the net8.0 or net10.0 build for Revit's .NET
Caddis.Revit.Bootstrapper/  net48: the Velopack app of the Revit installer, writes and deletes the .addin files, runs the updates
                            (Velopack is not loaded into Revit, where it could conflict with the Velopack of an older Fesh.Revit)
Tests/         xUnit tests for Core and the shared UI, both on net8.0 and net10.0, Core also on net48
```

`Caddis.Core` declares interfaces for the Rhino hooks (`beforeEval`, `afterEval`, `capture`, `export`), and `Caddis.Rhino` implements them.
Since 2026-10-10 `Caddis.Revit/` (net10.0-windows) implements them for Revit 2026, see [Revit](#revit).
Since 2026-10-10 it also targets net48 and net8.0-windows, for Revit 2024 to 2027, with the Revit API from `Revit_All_Main_Versions_API_x64`, and has a Velopack installer like Fesh.Revit.

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
POST /export  {format, objects: all|selected, objectIds?, waitMs?}  → RunResult with files (a run of kind export)  [caddis_export]
GET  /dialogs                 the host app's open modal dialogs (Revit only)            [caddis_dialog]
POST /dialog  {button, dialog?}  press a button of a dialog (Revit only)                [caddis_dialog]
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

## Revit

`Caddis.Revit` is an add-in for Revit 2026.5 on .NET 10, implemented on 2026-10-10, tested headless and live in Revit 2026.5, see [Revit live test](#revit-live-test-2026-10-10).
Revit 2026.5 (26.5.0.55) is installed on the development machine, its RevitAPI targets net10.0.

| Topic | Decision |
|---|---|
| Versions | Only Revit 2026 on .NET 10 for now (2026.5 moved from .NET 8 to .NET 10): `net10.0-windows`, built against `Nice3point.Revit.Api.RevitAPI`/`RevitAPIUI` 2026.4.10, since there is no 2026.5 package and the API is the same within 2026. Caddis.Core targets `net8.0` for Rhino 8 (FCS 43.8.403, FSharp.Core 8.0.403) and `net10.0` for Revit (FCS 43.12.401, FSharp.Core 10.1.401). FCS pins the exact FSharp.Core, so each project that references Caddis.Core uses the FSharp.Core of its framework. The Core tests run on both. |
| API context | `InvokeOnUi` is one `ExternalEvent` with a queue (`ApiContext.fs`), inline when already in it. A run on the `ui` thread runs entirely inside the event's `Execute`, so `Script.OnUi` runs inline and can't deadlock. |
| Transactions | Each `ui`-thread run with `recordUndo` is wrapped in a `TransactionGroup`. Scripts open their own transactions or use `Caddis.Revit.Script.Transact`. Success: `Assimilate`, one undo step. Error or cancel: `RollBack` of all changes. Background mode: no group, each transaction is an undo step. |
| Commands | No `caddis_command`: Revit has no command line. `McpTexts.command = None` leaves the tool out, `/command` returns 400. |
| Coexistence | Own `settings-revit.json`, default port 7820, MCP name `caddis-revit`. The token and the discovery folder are shared, discovery files have `app`. |
| UI | Ribbon tab with the panels Server (Start/Stop, Copy MCP, New token), F# Session (Mode, Reset F#, Cancel run), Status (Panel, Info) and Settings (AdWindows checkboxes), large buttons with Revit-style vector icons drawn per theme and screen scale, with simplified 16 px variants (`RibbonIcons.fs`, redrawn on `ThemeChanged`). Revit titles every panel and can hide titles only for the whole ribbon. Messages go to `%AppData%/Caddis/revit.log`. Optional dockable pane `CaddisPanel` (`Panel.fs`, added 2026-10-10); since 2026-10-10 a WPF shell embeds the same Avalonia `Caddis.UI.PanelView` used in Rhino: state, Start/Stop, Copy MCP, Copy token, thread mode, Start with Revit, LAN, Cancel, Reset, the latest message, the run list and the Output and Code of the selected run. Registered in `OnStartup` with `VisibleByDefault = false`, tabbed behind the Project Browser; the ribbon button `Panel` toggles it, Revit remembers its state. Its buttons call the `App` actions outside API context, so `App.show` opens a TaskDialog only in API context and otherwise only logs; the pane shows the latest log line. A 1 s `DispatcherTimer` refreshes it and the ribbon texts (`App.stateChanged`). It must not call `Dialogs()`: UI Automation from the UI thread would wait on itself. |
| Install | Dev only: `Caddis.Revit/install-dev.ps1` writes `%AppData%/Autodesk/Revit/Addins/2026/Caddis.addin`. Packaging later. |
| Element ids | `ElementId.Value` numbers as strings. `HostInfo.isObjectId` validates them. |
| Load context | Revit's default context (no `UseRevitContext=false`), so Caddis.Core is loaded once and scripts share `Caddis.Script`. In that context only one FSharp.Core and one FCS can load: Caddis.Revit can run next to a Fesh.Revit built on Fesh 0.34.0-net10, not next to a .NET 8 build of Fesh.Revit. The add-in that starts first wins. Decision 2026-10-10: Fesh.Revit moves to the same latest net10 versions. |

Changes in Caddis.Core for it: `ICaddisHost.Info` (`HostInfo`: app name, MCP server name, MCP texts, object id format), object ids as strings,
`AfterRun` gets the run's status, `SettingsStore(folder, fileName, defaults)`.
The Rhino texts moved unchanged to `Caddis.Rhino/RhinoInfo.fs`.

How `RevitHost` maps the host interface:
- `BeforeRun` / `AfterRun`: the transaction group, a note if Revit started the run more than 2 s late.
- `Application.DocumentChanged`: counts the script's commits, collects added, modified and deleted element ids into `run.Commands` (without steps),
  and tracks whether a run is the latest undo step. Only changes made in API context by Caddis count.
  Changed elements are also counted per category (`CommandReport.changedGroups`): MCP shows the counts, the ids go to a file.
- `Application.FailuresProcessing`: during a run, deletes warnings and lists them in the notes, rolls back a transaction with an error instead of Revit's dialog.
- `Status`: fresh via the external event if Revit answers within 1 s, otherwise the previous one.
- `CaptureView`: `Document.ExportImage` into a temp folder, display style set in a rolled-back transaction group.
- `Export`: a run of kind `export` (Core: `FsiHost.StartExport`, the same slot as other runs, `waitMs`). By extension via `Document.Export`, checking its result.
  Selections are isolated in the view (`IsolateElementsTemporary` + `ConvertTemporaryHideIsolateToPermanent`) in a rolled-back group, not possible on sheets.
  IFC, DWF and DWFX in a rolled-back transaction, DGN with Revit's V8 seed file from `ACADInterop`, SAT from a 3D view.
- `Undo`: `UIApplication.PostCommand(PostableCommand.Undo)`, runs after the call.
- `Dialogs` / `PressDialogButton` (`RevitDialogs.fs`, tool `caddis_dialog`, `GET /dialogs`, `POST /dialog`): Revit's main window (from `UIControlledApplication.MainWindowHandle`) is disabled while a modal dialog shows,
  and its `GetLastActivePopup` is the dialog on top; neither sends messages to the busy UI thread. Then UI Automation reads the dialog's texts and enabled buttons
  (not those of title and scroll bars) and presses one via `InvokePattern`, each with a 5 s timeout. Core lists open dialogs in the status and in results of runs still running.

FCS 43.12.401 on .NET 10: all Core tests pass headless on `net10.0`, including cancellation, which uses reflection into FCS.
FCS 43.12 names the assemblies of runs `FSI-ASSEMBLY-MULTI` instead of `FSI-ASSEMBLY`. The resolver for values of earlier runs matched only the old name, so on .NET 10 the test with a resolver like Rhino's failed with a MissingMethodException. It now matches both. The net10 build of Fesh probably has the same issue.
On .NET 10 the `DiagnosticSource` limit that pins FCS for Rhino 8 does not apply.

### Revit live test (2026-10-10)

Revit 2026.5 (26.5.0.55) on Windows, with a copy of the sample `Snowdon Towers Sample Architectural.rvt`, via REST and MCP, without Fesh.Revit.
Session locked for most of the test: external events still ran, about 0.3 s per call (60 ms with the person present).

Confirmed:
- Loading, `ExternalEvent.Create` in `OnStartup`, auto start, the discovery file (deleted on exit). FSI 43.12.401 with FSharp.Core 10.1.401 on .NET 10.0.12.
- RevitAPI references, `Caddis.Revit.Script` (`Doc`, `UiDoc`, `UiApp`, `Transact`, `OnUi`) shared between add-in and scripts, values of earlier runs (the `FSI-ASSEMBLY-MULTI` resolver), reset.
- Transaction group: a wall is one undo step with its id in `created`; a failing run is rolled back with a note and the right script line; `caddis_undo` via the posted Undo command.
- `FailuresProcessing`: the 'walls overlap' warning deleted and listed in the notes; 'top of the wall lower than its base' rolled back its transaction (`Commit` returns `RolledBack`) with a note, no dialogs.
- `DocumentChanged` inside the group; deleted ids; changed elements per category, e.g. 'changed (106): 95 <Path of Travel Lines>, 8 without category, 3 Schedules' for one new wall.
- Capture of a sheet and of `{3D}` with display styles and `width`, unknown view names list the views.
- Exports: dwg, dxf, dgn, pdf, dwf, dwfx of the active sheet (with its views and images); sat, obj, stl, step, fbx of `{3D}`; ifc of the model; obj of three walls by id with the view restored; nwc reports the missing Navisworks exporter.
  Large: ifc 159 MB in 47 s, obj 505 MB in 31 s. As a run: `running` after `waitMs`, 409 for other runs, not cancellable, files via `caddis_get_run`. The document stays unmodified.
- Background mode: `Script.OnUi`, `Script.Transact` (undoable), `Script.Doc` outside API context gives a clear error, cancel of a busy loop.
- MCP: server name `caddis-revit`, Revit instructions, no `caddis_command`, `caddis_check` with Revit types, `caddis_describe` with RevitAPI.xml.
- Dialogs: 'Unresolved References' while opening, 'Project Not Saved Recently' after 30 min, Revit's error dialog after a failed command, 'Save File' on exit and Caddis' own dialogs.
  A run waits ('has not started yet', then the note 'Revit started the run only after 195 s'), its result names the dialog with text and buttons, `caddis_dialog` presses a button, the run continues.
- Ribbon: the tab with state texts (Stop, Mode: UI, Auto start: on), Info, Copy MCP.

Found and fixed during the test:
- Scripts didn't get `Caddis.Revit.dll`: `typeof<Script>` in `RevitHost` meant `Caddis.Script` because of `open Caddis`.
- A transaction the script left open: `Assimilate` failed silently and Revit rolled back everything without a note. Now Caddis rolls the group back on purpose, clears the changes and says why.
- Exports: dwf and dwfx need an open transaction, sat a 3D view, dgn a seed file; `Export`'s `false` result is now an error; selections on a sheet are refused with a hint.
- 'Copy MCP' failed while the clipboard can't be opened (locked session): retries, then shows the command in a dialog for Ctrl+C.
- Dialog titles 'Caddis - Caddis' (Revit prefixes the add-in name), now e.g. 'Caddis - Info'.

Pane (2026-10-10, Revit 2026.5, session locked, so checked by rendering it to a PNG from a script via reflection and with UI Automation): registration, hidden by default, the ribbon button toggles it (`IsShown` true and false), shown again after a restart.
Run list with export runs, the details of a selected run with value, elements per category, stdout and F# output.
'Reset FSI session', 'Copy token' (clipboard locked: message in the pane), the thread mode list (saved, and the ribbon text follows within a second). No dialogs from the pane.
Note: the WPF `IsVisible` of the pane's content stays true when Revit hides the pane.

Not tested: a run that closes its document, runs waiting for a Revit command or edit mode (the same external-event queue as for dialogs), the ribbon buttons 'Mode', 'Reset F#', 'Cancel run', 'Auto start' and 'New token', the pane's Start/Stop, LAN and Cancel, the pane in Revit's dark theme, loading next to a Fesh.Revit on .NET 10.

Scripts can't use WPF types: FCS type-checks against the SDK's reference pack (`packs/Microsoft.NETCore.App.Ref/10.0.x/ref/net10.0`), whose `WindowsBase.dll` 4.0.0.0 is a stub,
and it wins over `WindowsBase` 10.0.0.0 of Microsoft.WindowsDesktop.App, also with `-r:` and with `--noframework` (tried in a console with FCS 43.12.401). Fesh has the same limitation. Reflection works.

Notes: Revit asks again about the unsigned add-in after every rebuild. `Path.GetTempPath()` in Revit is a folder per Revit session, so `RunRegistry.DeleteStaleFolders` doesn't see the output folders of earlier Revit sessions.

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
- **Panel on .NET Framework (2026-10-10), tested in Rhino 8.34 started with `/netfx`:** `Caddis.UI` also targets `net48`, with Avalonia 11.3.22 and AvaloniaEdit 11.4.1, since Avalonia 12 needs .NET 8. net8.0 and net10.0 keep Avalonia 12.
  - Confirmed: the panel shows the server state, the run list and the formatted, highlighted code of a selected run. SkiaSharp and HarfBuzz load from the plugin's `x64` folder, Fantomas formats, F# runs work as before.
  - Found and fixed (the build, the unit tests and a net48 console test had passed before):
    - **Two copies of System.Memory:** a .NET Framework plugin has no binding redirects. The loader binds Avalonia's request for System.Memory 4.0.1.2 to Rhino 8's copy in its `System` folder without asking any resolver. AvaloniaEdit asks for 4.0.1.1, and `FsiAssemblyResolver` returned Caddis' 4.0.5.0. So `ReadOnlyMemory<char>` was two types, and the panel failed with a MissingMethodException for `TextCharacters..ctor`. The resolver now prefers the host's copy if it has the requested version or a higher one; FCS (4.0.2.0) and Caddis.Core (4.0.5.0) still get Caddis' copy. The console test missed it because an exe gets binding redirects.
    - **Empty Output and Code tabs:** Avalonia 12's `TabControl` sets the content of `PART_SelectedContentHost` itself, Avalonia 11's needs `Content="{TemplateBinding SelectedContent}"`. `PanelStyles.axaml` has the binding, which works on both.
    - **Native libraries:** on .NET Framework SkiaSharp's and HarfBuzzSharp's own build targets copy the natives for every OS and architecture into folders like `x64`, not into `runtimes/<rid>/native`: 142 MB. `Directory.Build.targets` keeps only the `x64` and `arm64` DLLs: 61 MB. `AvaloniaSetup` loads them with `LoadLibrary`, .NET Framework has no `NativeLibrary`.
  - Keep the panel's code and XAML within what both Avalonia 11 and 12 support. `Caddis.UI.Tests` run only on Avalonia 12, so check changes for net48 in a net48 console that shows `PanelView` in a window with a running dispatcher loop, or in Rhino with `/netfx`.
  - Notes for testing: McNeel's `DataExchangeRhinoConnector.rhp` targets .NET 8 only, so Rhino with `/netfx` shows a 'Rhino Plug-in Error' for it at startup, unrelated to Caddis; press OK and leave 'Never load this plug-in again' unticked. A newly opened panel floats. Until a document is open, `Panels.GetPanel(id, RhinoDoc.ActiveDoc)` fails. The panel was checked from Caddis scripts (reflection into `AvaloniaHost`, `RenderTargetBitmap`) and with `PrintWindow` screenshots of its floating window.
  - Not tested on .NET Framework: theme and DPI changes, Windows on ARM64.
- **Revit 2024 on .NET Framework (2026-10-10), tested in Revit 2024.3 next to Fesh.Revit 0.32.3-net48:** F# runs on the UI thread with the Revit API and script line numbers in stack traces; FSharp.Core 10.1 of Caddis loads next to Fesh's 9.0. The pane shows the server state, the run list and the highlighted, Fantomas-formatted code of a selected run, in Revit's dark theme.
  - Found and fixed: the panel failed with a `MissingMethodException` for `TextCharacters(ReadOnlyMemory<char>, ...)`. Revit 2024's folder has System.Memory 4.0.1.1, exactly the version AvaloniaEdit references, so the loader binds AvaloniaEdit to it without raising `AssemblyResolve`, while Avalonia's request for 4.0.1.2 got Caddis' own 4.0.5.0. `FsiAssemblyResolver.resolveOwn` now also takes the host's copy if only its revision is lower.
  - Notes for testing: Revit creates dockable panes only once a project is open, `GetDockablePane` fails on the Home screen. On this PC closed Revits of all versions, also without Caddis, sometimes stay listed with one thread stuck after they have exited, and keep their dlls locked until a reboot: build into another folder meanwhile. `Updates.installOnExit` ignores them.
- **Open:**
  - Revit: the untested parts in [Revit live test](#revit-live-test-2026-10-10), packaging, and Rhino with the export run and `caddis_dialog` (not offered in Rhino yet).
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
5. **Panel**, first in Eto, since 2026-10-10 shared Avalonia content in `Caddis.UI`, embedded by Eto in Rhino and WPF in Revit. Shared UI tests cover host options, theme lifetimes, code formatting and output on .NET 8 and .NET 10. An isolated WPF smoke check covers native embedding, keyboard input, tab focus, resizing, reparenting and root recreation. Live checks through Caddis in Revit 2026.5 verified script execution, run selection/output, highlighting, formatting/original code, search event handling, theme changes, autostart and hide/reopen without changing the model. Keep the Revit `AvaloniaHost` internal: its public F# signature otherwise makes `Script.Doc` and `Script.OnUi` depend on Avalonia/WPF types unavailable to FSI. The WPF shell has no outer margin, which otherwise exposes black strips from its native container. Manual mouse/keyboard and multi-monitor checks remain.
6. **Packaging**: a yak package for Rhino 8 (Windows and Mac), GitHub Actions modelled on Fesh.Rhino, README, and a CHANGELOG versioned by Ionide.KeepAChangelog.

## Risks

- **FCS internals:** the cancel hack and the script folder of `caddis_check` reach into private FCS fields by reflection. This is safe only because the version is pinned; any FCS upgrade needs a re-test.
- **Assembly resolution:** Caddis puts its resolver for `FSI-ASSEMBLY` first by setting the private `AssemblyLoadContext.AssemblyResolve` field. If a .NET update renames it, Caddis logs that and values of earlier runs fail again in Rhino. A plugin that inserts its own handler at the front later would break it too.
- **Assembly versions on .NET Framework:** which copy of System.Memory and the other helper assemblies FCS and the panel get depends on the versions in Rhino's `System` folder or Revit 2024's folder, see [Panel on .NET Framework](#progress-2026-10-10). A Rhino or Revit 2024 update that changes them needs a re-test.
- **Modal dialogs:** a script, or an export format, that opens a modal dialog blocks Rhino until someone clicks. PDF export is refused for this reason; the other formats are only known to be fine from the test above.
- **Mac is unverified:** ControlledExecution, `System.Drawing` for viewport capture (the fallback is `-_ViewCaptureToFile`), and whether a UI `SynchronizationContext` exists (the fallback is `RhinoApp.InvokeOnUiThread`). The panel too: Avalonia.Native as an NSView in Eto's `NativeControlHost`, with Avalonia's app delegate, menus and process name disabled. If Avalonia fails to start, the panel shows the error and the `Caddis` command still works.
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
