namespace Caddis.Rhino

open System
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Threading
open Rhino.UI

/// The colors of the panel, taken from Rhino's theme: its light or dark default colors, or the user's own.
/// PanelStyles.axaml uses them as dynamic resources, so an open panel follows a change of the theme.
module internal PanelTheme =

    let private toAvalonia (c: Eto.Drawing.Color) = Color.FromArgb(byte c.Ab, byte c.Rb, byte c.Gb, byte c.Bb)

    /// The keys of the brushes in PanelStyles.axaml, without their 'Caddis.' prefix, and their colors in Rhino's theme.
    let private colors () =
        let z = ThemeSettings.Content
        [
            "Panel", z.Background
            "Surface", z.List.Enabled.Background // lists, the output and code
            "Input", z.Entry.Enabled.Background // check boxes and the dropdown, like Rhino's own
            "Text", z.Text.Enabled
            "Disabled", z.Text.Disabled
            "Border", z.Entry.Enabled.Border
            "DisabledBorder", z.Button.Disabled.Border
            "Divider", z.Divider
            "Button", z.Button.Enabled.Background
            "ButtonText", z.Button.Enabled.Text
            "Hover", z.Button.EnabledHover.Background
            "Pressed", z.Button.Checked.Background
            "Accent", z.Highlight
            "ListHover", z.List.EnabledHover.Background
            "Selection", z.List.Checked.Background
            "SelectionText", z.List.Checked.Text
        ]

    /// Rhino has a dark mode setting on Windows and follows the system on Mac. A custom theme can be dark too, so decide by the panel's color.
    let private isDark () =
        let c = ThemeSettings.Content.Background
        0.2126 * float c.Rb + 0.7152 * float c.Gb + 0.0722 * float c.Bb < 128.0

    /// Sets the panel's brushes, and the theme variant of the Fluent styles, e.g. for scroll bars and menus.
    let apply (resources: IResourceDictionary) (scope: ThemeVariantScope) =
        for key, color in colors () do
            resources["Caddis." + key] <- SolidColorBrush(toAvalonia color)
        scope.RequestedThemeVariant <- if isDark () then ThemeVariant.Dark else ThemeVariant.Light

    /// Calls the function on the UI thread whenever Rhino's theme changes, until disposed.
    /// ThemeChanged is a field, not an event, in RhinoCommon 8.
    let subscribe (onChange: unit -> unit) : IDisposable =
        let handler = EventHandler(fun _ _ -> Dispatcher.UIThread.Post onChange)
        ThemeSettings.ThemeChanged <- Delegate.Combine(ThemeSettings.ThemeChanged, handler) :?> EventHandler
        { new IDisposable with
            member _.Dispose() = ThemeSettings.ThemeChanged <- Delegate.Remove(ThemeSettings.ThemeChanged, handler) :?> EventHandler
        }
