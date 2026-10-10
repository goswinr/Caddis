# Caddis for Revit

An F# interactive host for Revit 2026.5, controlled through HTTP and MCP.
See the [main README](../README.md#revit) for installation and usage.

## Ribbon

The Caddis tab groups its buttons in the panels **Server**, **F# Session** and **Status**.
Each button has its own icon, a glyph of Windows' icon font (Segoe Fluent Icons, or Segoe MDL2 Assets
on Windows 10) drawn for the screen's scale and in the colors of Revit's light or dark theme.
`Start` and `Stop` show the server's state in their text and icon, `Mode` the thread mode.

The **Settings** panel has native checkboxes for **Start with Revit** and
**Listen on the network (LAN)**. They share the panel's settings and stay in sync
even while the panel is hidden. Changing the LAN setting takes effect after
stopping and starting the server.

## Dockable panel

Open the panel with **Caddis → Panel** on Revit's ribbon. It shows the server
state, settings, recent runs, and the selected run's output and code.

Rhino and Revit use the same Avalonia view, styles and code viewer from `Caddis.UI`.
Revit supplies its actions and light or dark theme colors, and embeds the view in
a WPF `HwndHost` within its dockable pane. Styles stay scoped to Caddis.
The pane keeps its existing ID, so Revit retains the saved docking position.

The code viewer is read-only, with F# highlighting, line numbers and Ctrl+F search.
`Format` switches between Fantomas formatting and the original script, whose line
numbers match the diagnostics. `Settings` unfolds the thread mode, **Start with Revit**
and **Listen on the network (LAN)**. Revit's latest status message and grouped
element changes are preserved; Rhino's command-line options are hidden.

The shared view is covered by headless tests on .NET 8 and .NET 10. The WPF wrapper
has been checked in an isolated Windows host for native embedding, keyboard input,
tab focus, resizing, moving between windows and recreating its native content.
Live checks in Revit 2026.5 through Caddis verified script execution, run selection,
output, highlighting, formatting/original code, search event handling, automatic
light/dark themes, the autostart setting and hiding/reopening the pane. The model
was unchanged. The WPF shell fills its container without an outer margin, avoiding
black strips at the edges. Mouse/keyboard interaction and moving between monitors
still need manual verification.

<details>
<summary>Previous WPF panel previews</summary>

These previews show the earlier WPF implementation in Revit 2026.5, before the
shared Avalonia panel replaced it.

| Dark theme | Light theme |
| --- | --- |
| ![Caddis panel in Revit's dark theme](Docs/panel-dark.png) | ![Caddis panel in Revit's light theme](Docs/panel-light.png) |

</details>
