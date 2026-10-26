# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Rhino: while NuGet packages can be used, the MCP instructions suggest the package Rhino.Scripting.FSharp to the agent, with the 900+ functions of rhinoscriptsyntax as in Python, for common tasks like adding objects, layers, names, colors and user text. Models know these functions well, so they guess fewer RhinoCommon names and overloads.

### Changed
- The extension of Claude Desktop gets the same instructions as Caddis while Caddis isn't running, with the NuGet packages of the app or the advice to install the .NET SDK.

### Fixed
- Rhino: an Esc pressed in Rhino before a run, e.g. to deselect objects, no longer makes `rs.EscapeTest()` of Rhino.Scripting raise right at the start of the run. Caddis clears Rhino.Scripting's Esc state before each run, as Fesh.Rhino does.

## [0.3.2] - 2026-10-26

### Added
- While a newer Caddis is available, or installed and waiting for a restart, the panel shows a notice at its top, in Rhino and Revit.
- Rhino: when Rhino starts, Caddis asks Yak for a newer release for this Rhino. The panel's settings and the `Caddis` command show whether this is the latest version, or that a newer one is available in the `PackageManager` or installed and loaded when Rhino restarts.
- Rhino: Caddis saves whether its panel is shown and where when Rhino closes, in `panel-rhino8.json` or `panel-rhino9.json`, and moves it back at the next start if Rhino shows it elsewhere, e.g. after an update of Caddis. Docked, it goes back to the tabs of a panel it was docked with; floating, to its place on the screen, on Windows.

## [0.3.1] - 2026-10-26

### Changed
- The window of the Revit installer closes after 30 seconds. Its button counts down the seconds, clicking it keeps the window open.

### Fixed
- The link to the README in the messages of the Revit installer opens the README of Caddis for Revit, it pointed to a heading that doesn't exist.
- In the panel's list of runs, the time of a running run counts up every second. It stayed at the time when the run was first listed.
- On Windows, `Set up` for Claude Desktop starts Claude Desktop with the extension, from its installer or its MSIX package of the Microsoft Store. Windows asked which app to open the `.mcpb` file with, since Claude Desktop isn't the app for `.mcpb` files.
- The panel shows whether Claude Desktop from its MSIX package has the extension installed. It looked for its settings in `%APPDATA%\Claude`, the MSIX package has them in its own folder.

## [0.3.0] - 2026-10-17

### Added
- Internal errors of Caddis, e.g. when the F# session can't be created, are kept apart from its other messages, which can't push them out anymore. While there are any, the panel shows an `Errors` button with a red exclamation mark, in Rhino and Revit; it lists them newest first and can clear them.
- The panel's code view has `Run again` to run the code of the selected F# run again, as a new run in the current F# session.
- When the server starts, Rhino prints the commands to add Caddis to Claude Code (`claude mcp add ...`) and Codex (`codex mcp add ...`) to its command line.
- In Revit, unseen errors also turn the ribbon's `Info` button red with an exclamation mark and their number, and the Info dialog lists the latest ones above the other messages. In `revit.log` they are marked with `ERROR`.
- While the server runs, the panel shows whether Claude Code and Codex connect to this app: `active`, `not set up` with a `Set up` button that runs `claude mcp add ...` or `codex mcp add ...`, or `inactive` with an `Activate` button. The commands are in the buttons' tooltips, a right-click on an agent copies them.
- For development, the files in `Dev`, e.g. `Rhino8.cmd` or `Revit2026.cmd`, start Rhino or Revit with a local build of Caddis, while the installed Caddis stays installed for every other start. In Revit 2025 and later the loader loads Caddis.Revit from the folder in the environment variable `CADDIS_REVIT_DEV` if it is set.
- With two instances of an app, e.g. two Revits, `Activate` in the second one takes over the port where the agents connect, once the first one is idle with no run in progress. The first one moves to another port and logs that. Agents connected to the port go on with the second one, and their next tool result says that another Caddis answers now. `POST /release` asks for the port.
- Claude Desktop: Caddis as a Claude Desktop extension, `caddis-rhino.mcpb` and `caddis-revit.mcpb`, whose relay passes Claude Desktop's messages to Caddis on this computer. The panel shows Claude Desktop too, its `Set up` makes the extension for the running app and opens it. Each GitHub release has both extensions for the default ports.
- The panel's settings show the version of Caddis. In Revit also whether it is the latest, or whether a newer one is available or downloaded, from the update check at Revit's start.
- Revit's `Info` button shows the same update status, and the `Caddis` command in Rhino shows the version.
- Caddis checks whether a .NET SDK is installed that can restore NuGet packages for `#r "nuget: ..."`. `caddis_status` reports it under `nuget`, and the description of `caddis_eval` and the MCP instructions tell the agent.
- Without that SDK the panel recommends installing it, with a button to its download page, and a failed run with `#r "nuget:"` gets a note explaining why.

### Changed
- Rhino's settings file is now `settings-rhino.json`, like `settings-revit.json` for Revit. Settings saved in `settings.json` by an earlier version are not read anymore, Rhino starts with the default settings once.

### Removed
- The token: requests need no `Authorization` header anymore, and the copied `claude mcp add` command has no `--header`. MCP clients registered with the token keep working, the header is ignored. `token.txt` is not used anymore and can be deleted.
- `NewToken` in Rhino's `Caddis` command, `New token` on Revit's ribbon and `Copy token` in the panel.
- `Copy MCP command` in the panel, `CopyMcpCommand` in Rhino's `Caddis` command and `Copy MCP` on Revit's ribbon: the panel's agents replace them. Without the panel, Rhino prints the commands for Claude Code and Codex when the server starts, and Revit's `Info` dialog shows them.
- Listening on the network: the setting `listenOnLan`, the option `ListenOnLan` in Rhino and the checkbox `Listen on the network (LAN)` in the panel and on Revit's ribbon. Caddis listens on localhost only.
- `listenOnLan` and `tokenFile` in the status and in the discovery files.

## [0.2.1] - 2026-10-11

### Fixed
- The changes of 0.2.0 reach Rhino 9 too: its upload to Yak failed. Each Rhino version is now pushed to Yak in its own release job.

## [0.2.0] - 2026-10-11

### Added
- The panel's code view has `Export` to save the code of the selected run as an `.fsx` file, and `Export all` to save all successful F# runs to one `.fsx` file, each after a comment with its id and time.

### Changed
- The `Format` button of the panel's code view is now the check box `Formatted`, set apart from the `Export` buttons.

## [0.1.0] - 2026-10-11

### Added
- An F# Interactive session hosted inside Rhino and Revit, with an HTTP server for AI agents and other clients: an MCP endpoint at `/mcp` and REST endpoints.
- Rhino 8 on .NET 8 and on .NET Framework (`/netfx`), and Rhino 9 Beta on .NET 10, as two Yak packages for Windows and Mac: `rh8_19` and `rh9_0`.
- Revit 2024 to 2027 on Windows, with the installer `Caddis.Revit-win-Setup.exe`. It registers Caddis with every installed Revit, and Caddis offers updates when Revit starts.
- Runs on the app's UI thread or in the background, with `Caddis.Script.OnUi` for calls into the app. Background runs can be cancelled and time out.
- MCP tools `caddis_eval`, `caddis_get_run`, `caddis_check`, `caddis_describe`, `caddis_status`, `caddis_cancel`, `caddis_reset`, `caddis_set_mode`, `caddis_undo`, `caddis_capture_viewport` and `caddis_export`, plus `caddis_command` in Rhino and `caddis_dialog` in Revit.
- Long runs return `running` after `waitMs` and keep going; `caddis_get_run` waits for them, and `stdoutOffset` polls only new output.
- Results are compact text for the model: output, what was printed to the app's command line, notes, compiler messages, stack traces trimmed to the script, the ids of created, changed and deleted objects, and images saved by the run.
- Type-checks against the current session without running, and signatures with XML docs of the loaded assemblies, with a search of names and suggestions for misspelled ones.
- Undo of a run, viewport captures as PNG, and export to the file formats the app can write.
- Rhino: `caddis_command` runs command macros like typed at the command line, and cancels a command that waits for input its macro doesn't give. The setting `CancelWaitingCommands` does the same for commands started by scripts.
- Rhino: a dark yellow border around the viewports while a run is in progress.
- Revit: runs on the UI thread run in Revit's API context, each in a transaction group: one undo step if it succeeds, rolled back if it fails. Warnings become notes. `Caddis.Revit.Script` has `Doc`, `UiDoc`, `UiApp`, `Transact` and `OnUi`.
- Revit: `caddis_dialog` lists Revit's open modal dialogs and presses their buttons, since a dialog blocks all calls into Revit.
- A panel in Rhino and a dockable pane in Revit, in the app's light or dark theme: server state, settings, recent runs, and the output and code of a run, with F# highlighting and formatting.
- The `Caddis` command in Rhino and the `Caddis` ribbon tab in Revit.
- Scripts can check the conditional compilation symbols `CADDIS`, `RHINO`, `REVIT`, `NETFRAMEWORK`, `NET` and `NETCOREAPP`.
- Rhino and Revit have their own port, 7810 and 7820, and MCP name, `caddis-rhino` and `caddis-revit`, so that both can run at the same time.
- Security: a bearer token shared by both apps, localhost by default with LAN opt-in, and requests from web pages are rejected.
