namespace Caddis.Revit

open System
open System.Globalization
open System.Reflection
open System.Windows
open System.Windows.Media
open System.Windows.Media.Imaging
open Autodesk.Revit.UI

/// The icons of the ribbon buttons, drawn sharp for the screen's scale and in the colors of Revit's light or dark theme.
module internal RibbonIcons =

    type Icon =
        | Logo
        /// A glyph of Windows' icon font in the theme's text color.
        | Glyph of char
        /// A glyph in a signal color, for the light and the dark theme.
        | Signal of char * light: string * dark: string

    // Code points of Segoe Fluent Icons, the same in Segoe MDL2 Assets on Windows 10.
    let start = Signal('', "#107C10", "#6CCB5F") // Play
    let stop = Signal('', "#C42B1C", "#F1707A")
    let copy = Glyph ''
    let newToken = Glyph '' // Permissions, a key
    let mode = Glyph '' // Switch
    let reset = Glyph '' // Refresh
    let cancel = Glyph ''
    let panel = Logo
    let info = Glyph ''

    let private typeface = Typeface "Segoe Fluent Icons, Segoe MDL2 Assets"

    let private logo =
        lazy
            use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream "Caddis.Revit.Resources.Caddis.png"
            let img = BitmapImage()
            img.BeginInit()
            img.StreamSource <- stream
            img.CacheOption <- BitmapCacheOption.OnLoad
            img.EndInit()
            img.Freeze()
            img

    /// A square bitmap of the given size in device independent pixels, with as many pixels as the screen shows.
    let private render (size: int) (draw: DrawingContext -> float -> unit) : ImageSource =
        let scale = VisualTreeHelper.GetDpi(DrawingVisual()).DpiScaleX
        let visual = DrawingVisual()
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality)
        let dc = visual.RenderOpen()
        draw dc scale
        dc.Close()
        let pixels = int (Math.Round(float size * scale))
        let bitmap = RenderTargetBitmap(pixels, pixels, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32)
        bitmap.Render visual
        bitmap.Freeze()
        bitmap

    let private glyph (c: char) (color: string) (size: int) =
        render size (fun dc scale ->
            let brush = SolidColorBrush(ColorConverter.ConvertFromString color :?> Color)
            let text = FormattedText(string c, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, float size, brush, scale)
            // The font's em box is the image. Center the ink by whole pixels, so that it keeps the font's pixel grid.
            let ink = text.BuildGeometry(Point()).Bounds
            let shift center = Math.Round((float size / 2.0 - center) * scale) / scale
            dc.DrawText(text, Point(shift (ink.Left + ink.Width / 2.0), shift (ink.Top + ink.Height / 2.0))))

    /// Draws the icon at 16 or 32 device independent pixels, in the colors of Revit's current theme.
    let draw (icon: Icon) (size: int) : ImageSource =
        let dark = UIThemeManager.CurrentTheme = UITheme.Dark
        match icon with
        | Logo -> render size (fun dc _ -> dc.DrawImage(logo.Value, Rect(0.0, 0.0, float size, float size)))
        | Glyph c -> glyph c (if dark then "#E6E6E6" else "#333333") size
        | Signal(c, light, darkColor) -> glyph c (if dark then darkColor else light) size
