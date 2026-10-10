namespace Caddis.Revit

open Avalonia.Media
open Autodesk.Revit.UI
open Caddis.UI

/// Revit supplies its palette; the shared view applies it only within the Caddis panel.
module internal PanelTheme =
    let private changed = Event<unit>()

    /// Called by Revit's ThemeChanged event, including when Revit follows the system theme.
    let refresh () = changed.Trigger()

    let subscribe onChange = changed.Publish.Subscribe(fun () -> onChange ())

    let palette () : PanelPalette =
        let dark = UIThemeManager.CurrentTheme = UITheme.Dark
        let colors =
            if dark then
                [ "Panel", "#3B4453"; "Surface", "#2E3440"; "Input", "#222933"
                  "Text", "#F5F5F5"; "Border", "#687386"; "Divider", "#687386"
                  "Button", "#485263"; "ButtonText", "#F5F5F5"; "Hover", "#54647A"; "Pressed", "#263E59"
                  "Accent", "#6CB8F6"; "ListHover", "#485263"; "Selection", "#315879"; "SelectionText", "#FFFFFF"
                  "Disabled", "#A2A6B0"; "DisabledBorder", "#566174" ]
            else
                [ "Panel", "#F0F0F0"; "Surface", "#FFFFFF"; "Input", "#FFFFFF"
                  "Text", "#1F1F1F"; "Border", "#ACACAC"; "Divider", "#ACACAC"
                  "Button", "#E6E6E6"; "ButtonText", "#1F1F1F"; "Hover", "#DCEAF7"; "Pressed", "#C5DDF3"
                  "Accent", "#0078D4"; "ListHover", "#DCEAF7"; "Selection", "#CDE6F7"; "SelectionText", "#162C40"
                  "Disabled", "#707070"; "DisabledBorder", "#CCCCCC" ]
        { IsDark = dark; Colors = colors |> List.map (fun (key, color) -> key, Color.Parse color) }
