namespace Caddis.Revit

open System
open System.Reflection
open System.Windows
open System.Windows.Controls
open System.Windows.Markup
open System.Windows.Media
open Autodesk.Revit.UI

/// Scoped WPF resources: never change Application.Current.Resources, which belongs to Revit.
module internal PanelTheme =

    let private changed = Event<unit>()

    /// Called by Revit's ThemeChanged event, including when Revit follows the system theme.
    let refresh () = changed.Trigger()

    let initialize (panel: Control) =
        use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream "Caddis.Revit.Resources.PanelStyles.xaml"
        let resources = XamlReader.Load stream :?> ResourceDictionary
        panel.Resources.MergedDictionaries.Add resources
        panel.SetResourceReference(Control.BackgroundProperty, "Caddis.Panel")
        panel.SetResourceReference(Control.ForegroundProperty, "Caddis.Text")
        panel.FontFamily <- FontFamily "Segoe UI"
        panel.FontSize <- 12.0
        panel.UseLayoutRounding <- true
        panel.SnapsToDevicePixels <- true

        let mutable appliedTheme = None
        let apply () =
            let theme = UIThemeManager.CurrentTheme
            if appliedTheme <> Some theme then
                // Match Revit's neutral blue-grey docked panes and flat, compact controls.
                // Use our own keys rather than depending on undocumented UIFramework resources.
                let colors =
                    if theme = UITheme.Dark then
                        [ "Panel", "#3B4453"; "Surface", "#2E3440"; "Input", "#222933"
                          "Text", "#F5F5F5"; "Muted", "#C3C7CF"; "Border", "#687386"
                          "Button", "#485263"; "Hover", "#54647A"; "Pressed", "#263E59"
                          "Accent", "#6CB8F6"; "Selection", "#315879"; "SelectionText", "#FFFFFF"
                          "Disabled", "#A2A6B0"; "ScrollThumb", "#7C8798" ]
                    else
                        [ "Panel", "#F0F0F0"; "Surface", "#FFFFFF"; "Input", "#FFFFFF"
                          "Text", "#1F1F1F"; "Muted", "#555555"; "Border", "#ACACAC"
                          "Button", "#E6E6E6"; "Hover", "#DCEAF7"; "Pressed", "#C5DDF3"
                          "Accent", "#0078D4"; "Selection", "#CDE6F7"; "SelectionText", "#162C40"
                          "Disabled", "#707070"; "ScrollThumb", "#8C8C8C" ]
                for key, color in colors do
                    let brush = SolidColorBrush(ColorConverter.ConvertFromString color :?> Color)
                    brush.Freeze()
                    resources["Caddis." + key] <- brush
                // Stock WPF templates still use these for scroll-viewer corners and text menus.
                // Override them in this pane only, including inactive selection and disabled text.
                for key, color in
                    [ SystemColors.ControlBrushKey, "Panel"; SystemColors.ControlTextBrushKey, "Text"
                      SystemColors.WindowBrushKey, "Input"; SystemColors.WindowTextBrushKey, "Text"
                      SystemColors.GrayTextBrushKey, "Disabled"; SystemColors.HighlightBrushKey, "Selection"
                      SystemColors.HighlightTextBrushKey, "SelectionText"
                      SystemColors.InactiveSelectionHighlightBrushKey, "Selection"
                      SystemColors.InactiveSelectionHighlightTextBrushKey, "SelectionText" ] do
                    resources[key] <- resources["Caddis." + color]
                appliedTheme <- Some theme

        let mutable subscription: IDisposable option = None
        apply ()
        panel.Loaded.Add(fun _ ->
            apply ()
            if subscription.IsNone then subscription <- Some(changed.Publish.Subscribe(fun () -> apply ())))
        panel.Unloaded.Add(fun _ ->
            subscription |> Option.iter _.Dispose()
            subscription <- None)
