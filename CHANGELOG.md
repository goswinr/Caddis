# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Caddis logo in the Eto control panel header.
- Draggable divider between the panel run list and details tabs to resize their heights.
- `waitMs` for `caddis_eval`, `caddis_get_run`, `POST /eval` and `GET /runs/{id}`: return a still running run after this time. MCP calls return after 50 s by default.
- `stdoutOffset` for `caddis_get_run`, to poll only new output of a running run.
- Images saved by a run are returned inline via MCP.
- `caddis_status` shows the document's units, tolerances, current layer, layers and selection count.
- MCP: `Mcp-Session-Id` on initialize, `idempotentHint` annotations.
- `caddis_describe` tool and `GET /describe`: signatures in F# syntax and XML doc summaries of namespaces, types and members of the loaded assemblies, e.g. RhinoCommon, a search of their names, and suggestions for misspelled names.

### Changed
- Copied Claude Code MCP setup command uses user scope so Caddis is available across projects.
- Panel run details show executed code in its own read-only tab, separate from output and diagnostics.
- Panel checkboxes align in one column beside the button and thread-mode rows, leaving more vertical space for runs and details.
- MCP results are compact text for the model instead of JSON, without `structuredContent`: empty fields left out, long output shortened at line ends and saved to a file, compiler messages on one line, stack traces trimmed to the frames leading into the script.
- `timeoutMs` only cancels background runs, it no longer means "return early" on the UI thread, that is `waitMs` now.
- `caddis_check` and `POST /check` type-check against the current F# session, so definitions from earlier runs are known. The separate `FSharpChecker` is gone, which saves memory.
- Captured output keeps its start and its end when it exceeds `maxOutputChars`, not only the start.
- Viewport captures without a size are scaled down to at most 1280 pixels on the long edge, and a single given dimension keeps the view's aspect ratio.

### Fixed
- Panel Output and Code views fill their tabs without extra padding; removed the panel's outer margin.
- Viewport capture, export and undo waited for a running UI-thread run to finish; now they report the run as busy.
- `notifications/cancelled` from one MCP client could cancel another client's run with the same request id.
- MCP: unknown tools are a JSON-RPC `-32602` error, an unsupported `MCP-Protocol-Version` header gets 400.
- Chunked request bodies were not limited to 20 MB.
- A warning in Rhino when the configured port is taken and Caddis listens on another one, which MCP clients don't know about.
- A clear error for invalid ids in `objectIds` of `caddis_export`.

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
