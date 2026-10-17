namespace Caddis.Revit

open System
open System.Windows
open System.Windows.Media
open System.Windows.Media.Imaging
open Autodesk.Revit.UI

/// Revit-style vector artwork, rasterized at the ribbon's size and the screen's scale.
module internal RibbonIcons =

    type Icon =
        | ServerStart
        | ServerStop
        | CopyMcp
        | NewToken
        | ThreadMode
        | ResetSession
        | CancelRun
        | CancelRunUnavailable
        | Panel
        | Info
        | InfoError

    let start = ServerStart
    let stop = ServerStop
    let copy = CopyMcp
    let newToken = NewToken
    let mode = ThreadMode
    let reset = ResetSession
    let cancel = CancelRun
    let cancelUnavailable = CancelRunUnavailable
    let panel = Panel
    let info = Info
    let infoError = InfoError

    type private Palette = {
        Face: Brush
        Top: Brush
        Side: Brush
        Edge: Brush
        Ink: Brush
        Blue: Brush
        BlueShade: Brush
        Green: Brush
        GreenShade: Brush
        Red: Brush
        RedShade: Brush
        Gold: Brush
        GoldShade: Brush
    }

    let private brush color : Brush =
        let b = SolidColorBrush(ColorConverter.ConvertFromString color :?> Color)
        b.Freeze()
        b

    // Pale faces and darker side planes follow Revit's model/document icons in both themes.
    // Only the accents and fine details change contrast; the artwork has no baked-in background.
    let private palette dark = {
        Face = brush (if dark then "#D8DCE1" else "#D5DAE0")
        Top = brush "#F4F5F6"
        Side = brush (if dark then "#A3ADB8" else "#98A3AF")
        Edge = brush (if dark then "#7E8C9B" else "#758391")
        Ink = brush "#627486"
        Blue = brush (if dark then "#8BCBF3" else "#65B4E5")
        BlueShade = brush (if dark then "#459ACD" else "#337FAC")
        Green = brush (if dark then "#68C98C" else "#4DB77A")
        GreenShade = brush "#2D8755"
        Red = brush (if dark then "#EE8990" else "#DE6D77")
        RedShade = brush "#B34350"
        Gold = brush "#F0CF8B"
        GoldShade = brush "#BB914A"
    }

    let private lightPalette = palette false
    let private darkPalette = palette true

    let private grayscalePalette (p: Palette) =
        let gray (source: Brush) =
            let c = (source :?> SolidColorBrush).Color
            let value = byte (Math.Round(0.299 * float c.R + 0.587 * float c.G + 0.114 * float c.B))
            let b = SolidColorBrush(Color.FromArgb(c.A, value, value, value))
            b.Freeze()
            b :> Brush
        { Face = gray p.Face; Top = gray p.Top; Side = gray p.Side; Edge = gray p.Edge
          Ink = gray p.Ink; Blue = gray p.Blue; BlueShade = gray p.BlueShade
          Green = gray p.Green; GreenShade = gray p.GreenShade
          Red = gray p.Red; RedShade = gray p.RedShade; Gold = gray p.Gold; GoldShade = gray p.GoldShade }

    let private lightGrayscalePalette = grayscalePalette lightPalette
    let private darkGrayscalePalette = grayscalePalette darkPalette

    // All shapes use a 32-unit canvas. Major edges land on even coordinates so they
    // remain crisp at 16 px; the small version also omits secondary surface details.
    let private artwork (dc: DrawingContext) (p: Palette) compact icon =
        let rect fill x y width height =
            dc.DrawRectangle(fill, null, Rect(x, y, width, height))
        let path fill data =
            dc.DrawGeometry(fill, null, Geometry.Parse data)
        let line fill width x1 y1 x2 y2 =
            dc.DrawLine(Pen(fill, width), Point(x1, y1), Point(x2, y2))
        let ellipse fill x y radius =
            dc.DrawEllipse(fill, null, Point(x, y), radius, radius)

        let sheet x y =
            dc.PushTransform(TranslateTransform(x, y))
            path p.Edge "M0,0 H12 L20,8 V26 H0 Z"
            path p.Face "M0,0 H12 V8 H18 V24 H0 Z"
            path p.Top "M0,0 H12 V2 H0 Z"
            path p.Top "M12,0 L20,8 H12 Z"
            dc.Pop()

        let code () =
            path p.BlueShade "M10,9 L6,13 L10,17 L12,15 L10,13 L12,11 Z"
            if not compact then
                rect p.Ink 14.0 11.0 5.0 2.0
                rect p.Side 14.0 15.0 3.0 2.0

        let server () =
            path p.Top "M4,6 L10,2 H26 L20,6 Z"
            path p.Side "M20,6 L26,2 V24 L20,28 Z"
            rect p.Face 4.0 6.0 16.0 22.0
            rect p.Edge 4.0 26.0 16.0 2.0
            for y in [10.0; 18.0] do
                rect p.Ink 6.0 y 12.0 4.0
                rect p.Blue 8.0 y 2.0 2.0
                if not compact then rect p.Side 12.0 (y + 1.0) 4.0 1.0

        match icon with
        | ServerStart ->
            server ()
            path p.GreenShade "M18,16 L31,23 L18,30 Z"
            path p.Green "M18,16 L29,22 L18,28 Z"
        | ServerStop ->
            server ()
            rect p.RedShade 18.0 18.0 12.0 12.0
            rect p.Red 18.0 18.0 10.0 10.0
        | CopyMcp ->
            sheet 2.0 2.0
            // The offset blue sheet makes the copy silhouette legible without a text glyph.
            rect p.Side 8.0 4.0 2.0 22.0
            sheet 10.0 4.0
            rect p.Blue 10.0 14.0 18.0 14.0
            rect p.BlueShade 10.0 26.0 18.0 2.0
            rect p.Top 14.0 18.0 10.0 2.0
            if not compact then rect p.Top 14.0 22.0 6.0 2.0
        | NewToken ->
            // A straight diagonal key with two teeth, an open bow and a separate new-token glint.
            // Both gold layers share the cutout so the bow stays transparent.
            path p.GoldShade "F0 M6,4 H12 L16,8 V14 L28,26 L24,30 L22,28 L20,30 L18,28 L20,26 L18,24 L16,26 L14,24 L16,22 L12,18 H6 L2,14 V8 Z M6,6 H12 V12 H6 Z"
            path p.Gold "F0 M6,2 H12 L16,6 V12 L28,24 L24,28 L22,26 L20,28 L18,26 L20,24 L18,22 L16,24 L14,22 L16,20 L12,16 H6 L2,12 V6 Z M6,6 H12 V12 H6 Z"
            path p.Top "M2,6 L6,2 H12 L14,4 H6 L4,6 Z"
            path p.Blue "M26,2 L28,6 L30,8 L28,10 L26,14 L24,10 L22,8 L24,6 Z"
        | ThreadMode ->
            rect p.Edge 2.0 4.0 16.0 14.0
            rect p.Face 2.0 4.0 14.0 12.0
            rect p.Top 2.0 4.0 14.0 2.0
            rect p.Side 4.0 8.0 4.0 6.0
            rect p.Edge 16.0 16.0 14.0 14.0
            rect p.Face 16.0 16.0 12.0 12.0
            rect p.Top 16.0 16.0 12.0 2.0
            if not compact then
                rect p.Ink 20.0 20.0 6.0 2.0
                rect p.Side 20.0 24.0 4.0 2.0
            path p.Blue "M20,4 H24 V10 H28 L22,16 L16,10 H20 Z"
            path p.BlueShade "M12,28 H8 V22 H4 L10,16 L16,22 H12 Z"
        | ResetSession ->
            sheet 4.0 2.0
            code ()
            // A broad open arc survives downsampling better than a thin refresh-font glyph.
            // Extend both arcs into the arrowhead so they meet its long diagonal edge.
            path p.GreenShade "F1 M30,23 A8,8 0 1 1 26.8,16.6 L24.4,19.8 A4,4 0 1 0 26,23 Z M30,11 V21 H20 Z"
            path p.Green "F1 M30,22 A8,8 0 1 1 26.8,15.6 L24.4,18.8 A4,4 0 1 0 26,22 Z M30,10 V20 H20 Z"
        | CancelRun
        | CancelRunUnavailable ->
            sheet 4.0 2.0
            code ()
            path p.RedShade "M18,17 L23,22 L28,17 L30,19 L25,24 L30,29 L28,31 L23,26 L18,31 L16,29 L21,24 L16,19 Z"
            path p.Red "M18,16 L23,21 L28,16 L30,18 L25,23 L30,28 L28,30 L23,25 L18,30 L16,28 L21,23 L16,18 Z"
        | Panel ->
            rect p.Edge 2.0 4.0 28.0 24.0
            rect p.Face 2.0 4.0 26.0 22.0
            rect p.Top 2.0 4.0 26.0 4.0
            rect p.Side 4.0 10.0 12.0 14.0
            rect p.BlueShade 18.0 8.0 10.0 18.0
            rect p.Blue 18.0 8.0 8.0 16.0
            if not compact then
                rect p.Top 20.0 12.0 4.0 2.0
                rect p.Top 20.0 16.0 4.0 2.0
                line p.Ink 1.0 6.0 20.5 14.0 12.5
        | Info ->
            sheet 4.0 2.0
            rect p.Ink 8.0 10.0 10.0 2.0
            if not compact then rect p.Side 8.0 14.0 6.0 2.0
            ellipse p.BlueShade 22.0 22.0 9.0
            ellipse p.Blue 21.0 21.0 8.0
            rect p.Top 20.0 15.0 2.0 2.0
            rect p.Top 20.0 19.0 2.0 8.0
            if not compact then rect p.Top 18.0 25.0 6.0 2.0
        | InfoError ->
            // The sheet of Info behind a larger red disc with a broad exclamation mark, to stand out on the ribbon.
            sheet 4.0 2.0
            rect p.Ink 8.0 10.0 10.0 2.0
            ellipse p.RedShade 21.0 21.0 10.0
            ellipse p.Red 20.0 20.0 10.0
            rect p.Top 18.0 12.0 4.0 10.0
            rect p.Top 18.0 24.0 4.0 4.0

    /// Draws a transparent 16 or 32 DIP bitmap in Revit's current theme, at the screen's DPI.
    let draw (icon: Icon) (size: int) : ImageSource =
        let dpi = VisualTreeHelper.GetDpi(DrawingVisual())
        let visual = DrawingVisual()
        use dc = visual.RenderOpen()
        let scale = float size / 32.0
        dc.PushTransform(ScaleTransform(scale, scale))
        let dark = UIThemeManager.CurrentTheme = UITheme.Dark
        let colors =
            if icon = CancelRunUnavailable then
                if dark then darkGrayscalePalette else lightGrayscalePalette
            elif dark then darkPalette else lightPalette
        artwork dc colors (size <= 16) icon
        dc.Pop()
        dc.Close()
        let pixels scale = max 1 (int (Math.Round(float size * scale)))
        let bitmap = RenderTargetBitmap(pixels dpi.DpiScaleX, pixels dpi.DpiScaleY, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32)
        bitmap.Render visual
        bitmap.Freeze()
        bitmap
