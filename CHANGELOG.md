# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Rhino: a dark yellow border around all viewports while a run is in progress, an F# run, a command run or an export, so that the person in Rhino sees that a script is running. It is not drawn in view captures, prints or the details of layouts.
- Revit: native ribbon checkboxes for starting with Revit and listening on the network, synchronized with the panel's settings even when the panel is hidden.
- Caddis logo in the panel header.
- Draggable divider between the panel run list and details tabs to resize their heights.
- `waitMs` for `caddis_eval`, `caddis_get_run`, `POST /eval` and `GET /runs/{id}`: return a still running run after this time. MCP calls return after 50 s by default.
- `stdoutOffset` for `caddis_get_run`, to poll only new output of a running run.
- Images saved by a run are returned inline via MCP.
- `caddis_status` shows the document's units, tolerances, current layer, layers and selection count.
- MCP: `Mcp-Session-Id` on initialize, `idempotentHint` annotations.
- `caddis_describe` tool and `GET /describe`: signatures in F# syntax and XML doc summaries of namespaces, types and members of the loaded assemblies, e.g. RhinoCommon, a search of their names, and suggestions for misspelled names.
- `caddis_command` tool and `POST /command`: run Rhino command macros like typed at the command line, e.g. `-_Line 0,0,0 10,0,0`. Returns each command's result, the ids of the created, changed and deleted objects, the selection if it changed, and what the commands printed to the command line. `select` selects objects by id first. A command that waits for input its macro doesn't give is cancelled after `inputTimeoutMs`, or via `caddis_cancel`. `caddis_undo` undoes all undo steps of a call.
- Runs return what was printed to the Rhino command line, e.g. by `RhinoApp.WriteLine` or commands run via `RhinoApp.RunScript`, also in background mode: `commandLine` in REST, a 'Rhino command line' section in MCP. Caddis' own echo of the output is left out.
- `CancelWaitingCommands` setting, a checkbox in the panel and an option of the `Caddis` command, off by default: cancels Rhino commands that an F# script starts, e.g. via `RhinoApp.RunScript`, when they wait 3 s for input. The run gets a note with the prompt. In background mode only commands started via `Caddis.Script.OnUi`.
- Runs have `notes` from the host, shown in MCP results and the panel.
- Tooltips for all buttons, checkboxes and the thread mode in the panel, also on disabled buttons, e.g. why `Cancel run` is disabled.
- The logo in the panel opens the GitHub repository.
- `Caddis.Revit`, an add-in for Revit 2026.5 on .NET 10, tested in Revit 2026.5: the same F# session, REST and MCP tools without `caddis_command`. Runs on the UI thread run in Revit's API context via an external event, each in a transaction group: one undo step if it succeeds, rolled back if it fails.
- Revit: warnings are removed and listed as notes, errors roll back their transaction. Results list the created, changed and deleted element ids. `Caddis.Revit.Script` has `Doc`, `UiDoc`, `UiApp`, `Transact` and `OnUi`.
- Revit: its own settings file, port 7820 and MCP name `caddis-revit`, a ribbon tab, and `install-dev.ps1` to register a build.
- Revit: an optional dockable Caddis pane, shown and hidden with the ribbon button `Panel`, like the panel in Rhino: server state and settings, the recent runs, and the output and code of the selected run.
- Revit: changed elements are counted per category in MCP results, e.g. '95 <Path of Travel Lines>, 3 Schedules', their ids are saved to a file. A script that leaves a transaction open gets its run rolled back with a note.
- `caddis_dialog` tool, `GET /dialogs` and `POST /dialog` (Revit only): list the host app's open modal dialog with its text and buttons, or press a button. `caddis_status` and results of runs still running name an open dialog, since it blocks all calls into Revit.
- Runs have `started`: a run still waiting for the host app says in MCP that it has not started yet.
- Rhino 8 on .NET Framework, started with `/netfx`: Caddis.Core and Caddis.Rhino also target net48, with FSharp.Compiler.Service 43.12.401 and FSharp.Core 10.1.401 like Fesh.Rhino. Runs, values of earlier runs, cancellation, timeouts, type-checks and script line numbers in stack traces work like on .NET 8. The panel too: Caddis.UI also targets net48, with Avalonia 11.3 and AvaloniaEdit 11.4, since Avalonia 12 needs .NET 8.
- Scripts can use the conditional compilation symbols `CADDIS`, the host app (`RHINO`, `REVIT`), and `NETFRAMEWORK` or `NET` and `NETCOREAPP`. F# Interactive only defines `INTERACTIVE`.
- Rhino 9 Beta on .NET 10: Caddis.Rhino also targets net10.0, with RhinoCommon 9.0.26209.18303-beta, FSharp.Compiler.Service 43.12.401 and FSharp.Core 10.1.401 like Fesh.Rhino, and the panel. Tested in Rhino 9.0.26272.
- A second Yak package for Rhino 9, `caddis-<version>-rh9_0-any.yak` with the net10.0 build, next to `caddis-<version>-rh8_19-any.yak` with net8.0 and net48. The release workflow pushes both.

### Changed
- Revit: ribbon icons use custom vector artwork in Revit's style, with gray surfaces, blue accents and colored action markers, instead of Windows font glyphs. Both 16 and 32 pixel icons follow the screen's scale and Revit's light or dark theme; the Panel button shows a docked pane.
- Rhino: the panel is drawn with Avalonia 12 inside Rhino's dockable Eto panel, instead of Eto controls. It takes its colors from Rhino's theme, light, dark or custom, and follows a change immediately, including dropdowns, tooltips, menus, scrollbars and the run selection.
- Rhino: the panel shows the code of a run read-only in an AvaloniaEdit editor, with F# syntax highlighting in light and dark colors, line numbers, and search with Ctrl+F.
- Rhino: the panel's code view formats F# code with Fantomas 7 off the UI thread. Its `Format` button switches back to the code as it was run, whose line numbers match the diagnostics.
- Rhino: the Yak package includes Avalonia, AvaloniaEdit, SkiaSharp and HarfBuzz with their native libraries for 64 bit Windows (x64, Arm64) and Mac, about 20 MB more, and Fantomas.Core with Fantomas.FCS, about 9 MB. The panel renders in software, so ANGLE isn't included.
- Revit: the dockable pane follows Revit's light and dark themes, with native-style compact controls, readable run details, themed dropdowns and scrollbars, and visible selection and keyboard focus.
- Revit: all ribbon buttons are large, side by side, each with its own icon in the colors of Revit's light or dark theme, a green Start and a red Stop. They are grouped in the panels `Server`, `F# Session`, `Status` and `Settings` instead of one panel titled `Caddis`.
- Caddis.Core is independent of the host app: `ICaddisHost.Info` gives the app name, the MCP server name, the texts of the MCP tools and the format of object ids. Object ids are strings, `AfterRun` gets the status of the run, `SettingsStore` takes a settings file name and defaults per host.
- Caddis.Core targets net8.0 for Rhino 8 (FSharp.Compiler.Service 43.8.403, FSharp.Core 8.0.403) and net10.0 for Revit (FSharp.Compiler.Service 43.12.401, FSharp.Core 10.1.401). The resolver for values of earlier runs also matches the `FSI-ASSEMBLY-MULTI` assemblies of FCS 43.12.
- `caddis_export` and `POST /export` start a run of kind `export`, like `caddis_eval`: it returns `running` after `waitMs` (MCP default 50 s) and `caddis_get_run` returns its files. It takes the one run slot, so other runs get 409 meanwhile, and it can't be cancelled.
- Copied Claude Code MCP setup command uses user scope so Caddis is available across projects.
- Panel run details show executed code in its own read-only tab, separate from output and diagnostics.
- Rhino: the panel header is compact: the logo beside a one-line status and one row of buttons, with the thread mode and the checkboxes folded under a `Settings` button. The status shows the thread mode. On a narrow panel the button groups wrap.
- MCP results are compact text for the model instead of JSON, without `structuredContent`: empty fields left out, long output shortened at line ends and saved to a file, compiler messages on one line, stack traces trimmed to the frames leading into the script.
- `timeoutMs` only cancels background runs, it no longer means "return early" on the UI thread, that is `waitMs` now.
- `caddis_check` and `POST /check` type-check against the current F# session, so definitions from earlier runs are known. The separate `FSharpChecker` is gone, which saves memory.
- Captured output keeps its start and its end when it exceeds `maxOutputChars`, not only the start.
- Viewport captures without a size are scaled down to at most 1280 pixels on the long edge, and a single given dimension keeps the view's aspect ratio.
- System.Drawing.Common is referenced in every session, RhinoCommon uses its `Bitmap`.
- PDF export is refused: it opened Rhino's print dialog and blocked until someone clicked.
- F# Interactive in background mode starts without `--exec`, so FCS itself makes runs cancellable, instead of Caddis setting a private field via reflection. `--gui+` and `--debug:full` are no longer passed, they have no effect in a hosted session.
- `extraFsiArgs` that make FCS close the host app, e.g. `--help`, `--version`, `--langversion:?` or `--abortonerror`, are ignored with a message.

### Fixed
- Cancelling a background run no longer blocks on a queued `Script.OnUi` call. Cancelled callbacks are skipped and retain their original run context; already-started callbacks finish before the run completes.
- Empty or malformed export `objectIds` arrays are rejected instead of silently exporting all objects. Command and selection arrays no longer discard invalid entries.
- Rotating the shared token updates authentication and copied connection commands in every running instance; token files are replaced atomically.
- Revit transaction finalization failures now produce failed runs and MCP error results instead of success with a rollback note.
- Panel Output and Code views fill their tabs without extra padding; removed the panel's outer margin.
- Viewport capture, export and undo waited for a running UI-thread run to finish; now they report the run as busy.
- `notifications/cancelled` from one MCP client could cancel another client's run with the same request id.
- MCP: unknown tools are a JSON-RPC `-32602` error, an unsupported `MCP-Protocol-Version` header gets 400.
- Chunked request bodies were not limited to 20 MB.
- A warning in Rhino when the configured port is taken and Caddis listens on another one, which MCP clients don't know about.
- A clear error for invalid ids in `objectIds` of `caddis_export`.
- In Rhino, values defined in earlier runs failed with a `TypeLoadException`, and after a reset could silently be those of an old session: Rhino's assembly resolver returned the wrong FSI assembly. Caddis now resolves them first.
- In UI-thread mode no request was answered while a run was in progress, so busy errors and the `waitMs` of `caddis_get_run` didn't work: the HTTP accept loop continued on the UI thread.
- `caddis_undo` refused to undo a run if a later run made no changes.
- `caddis_check` with `path` did not find files loaded with a relative `#load`.
- Exporting to an extension without exporter wrote a 3dm file, now it is an error.
- `caddis_describe` cut summaries at "e.g." in member lists.
- Loading the .rhp again while Caddis is loaded, e.g. by dragging it into Rhino, started a second server on the next port before Rhino refused the plugin, and the panel no longer opened.
- An error while writing an HTTP response was swallowed and the client got an empty `200`. It is logged to the host app now, e.g. a `MissingMethodException` when another plugin loaded a different FSharp.Core first.

## [0.1.0] - 2026-10-10

### Added
- F# Interactive session hosted in the Rhino 8 process (.NET 8), on the UI thread or a background thread.
- HTTP server with REST endpoints and an MCP endpoint at `/mcp`.
- Bearer token auth, localhost by default, LAN opt-in.
- Cancel and timeout for background runs, type-check only, undo of the last run.
- Viewport capture and export to any file format Rhino can write.
- `Caddis` Rhino command.
- Yak packaging for Rhino 8.19 and later on .NET 8, Windows and Mac.
- Build-and-test CI and tag-triggered Yak publishing, with versions from this changelog.
