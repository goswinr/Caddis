# Caddis for Revit

An F# interactive host for Revit 2026.5, controlled through HTTP and MCP.
See the [main README](../README.md#revit) for installation and usage.

## Ribbon settings

The Caddis ribbon includes native checkboxes for **Start with Revit** and
**Listen on the network (LAN)**. They share the panel's settings and stay in sync
even while the panel is hidden. Changing the LAN setting takes effect after
stopping and starting the server.

## Dockable panel

Open the panel with **Caddis → Panel** on Revit's ribbon. It shows the server
state, settings, recent runs, and the selected run's output and code.

The panel follows Revit's light or dark UI theme as it changes. Compact WPF
controls use styles scoped to Caddis, including dropdowns, selection, keyboard
focus, tabs, and scrollbars.

These previews were rendered from the updated panel in a running Revit 2026.5
session. The run history contains the scripts used to verify the panel.

| Dark theme | Light theme |
| --- | --- |
| ![Caddis panel in Revit's dark theme](Docs/panel-dark.png) | ![Caddis panel in Revit's light theme](Docs/panel-light.png) |

Verified in both themes: run selection, output and code, dropdowns, column
resizing, text selection, horizontal and vertical scrolling, narrow layouts,
and reopening the panel after a theme change.
