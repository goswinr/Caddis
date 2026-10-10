# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Caddis logo in the Eto control panel header.
- Draggable divider between the panel run list and details tabs to resize their heights.

### Changed
- Copied Claude Code MCP setup command uses user scope so Caddis is available across projects.
- Panel run details show executed code in its own read-only tab, separate from output and diagnostics.
- Panel checkboxes align in one column beside the button and thread-mode rows, leaving more vertical space for runs and details.

### Fixed
- Panel Output and Code views fill their tabs without extra padding; removed the panel's outer margin.

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
