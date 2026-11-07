namespace Caddis.AutoCAD

open Avalonia.Media
open Autodesk.AutoCAD.ApplicationServices.Core
open Caddis.UI

/// AutoCAD's theme, from the system variable COLORTHEME: 0 dark, 1 light. The shared view applies it only within the Caddis panel,
/// the ribbon icons are drawn in it too.
module internal PanelTheme =
    let private changed = Event<unit>()

    /// True for AutoCAD's dark theme, its default.
    let isDark () =
        try
            match Application.GetSystemVariable "COLORTHEME" with
            | :? int16 as v -> v = 0s
            | v -> System.Convert.ToInt32 v = 0
        with _ ->
            true

    /// Called when COLORTHEME changes, see Extension.fs.
    let refresh () = changed.Trigger()

    let subscribe onChange = changed.Publish.Subscribe(fun () -> onChange ())

    /// The colors of AutoCAD's palettes, e.g. of the Properties palette. Its dark theme has the same blue gray as Revit's.
    let palette () : PanelPalette =
        let dark = isDark ()
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
