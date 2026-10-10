namespace Caddis.Rhino

open System
open System.Drawing
open System.Reflection
open Rhino
open Rhino.Display

/// Draws a dark yellow border along the edges of every viewport, see RunBorder.
type RunBorderConduit() =
    inherit DisplayConduit()

    static let color = Color.FromArgb(210, 160, 0)

    /// In logical pixels, scaled on high DPI monitors.
    static let thickness = 4.0

    static let pointerField = typeof<DisplayPipeline>.GetField("m_ptr", BindingFlags.NonPublic ||| BindingFlags.Instance)

    /// True if the pipeline draws the view on the screen. A capture, e.g. RhinoView.CaptureToBitmap by caddis_capture_viewport
    /// or by a script, draws a copy of the viewport with another pipeline, and DisplayPipeline.IsInViewCapture is false then.
    /// RhinoCommon can only tell them apart by their native pointers. If its internals changed, the border shows in captures too.
    static let isOnScreen (e: DrawEventArgs) =
        match e.Viewport.ParentView with
        | null -> false
        | view ->
            try
                isNull pointerField
                || (pointerField.GetValue e.Display :?> IntPtr) = (pointerField.GetValue view.DisplayPipeline :?> IntPtr)
            with _ ->
                true

    override _.DrawForeground(e: DrawEventArgs) =
        // not in captures, prints, or around the details of a layout
        if
            not e.Display.IsInViewCapture
            && not e.Display.IsPrinting
            && e.Viewport.ViewportType <> ViewportType.DetailViewport
            && isOnScreen e
        then
            let t = max 1 (int (round (thickness * float e.Display.DpiScale)))
            let size = e.Viewport.Size
            let w, h = size.Width, size.Height
            // four filled bars, so that the border ends at the edges whatever the stroke alignment of Draw2dRectangle
            for bar in [ Rectangle(0, 0, w, t); Rectangle(0, h - t, w, t); Rectangle(0, t, t, h - 2 * t); Rectangle(w - t, t, t, h - 2 * t) ] do
                e.Display.Draw2dRectangle(bar, Color.Transparent, 0, color)


/// The border around all viewports while a run is in progress, so that the person working in Rhino
/// sees that an agent's script or commands are running. In UI-thread mode it explains why Rhino is blocked,
/// in background mode it warns that the document might change while they work.
module RunBorder =

    let private conduit = lazy (new RunBorderConduit())

    /// Shows or hides the border and redraws the views of all open documents. Call on the UI thread.
    /// The redraw happens at once, so in UI-thread mode the border is visible while the run blocks Rhino.
    let show (visible: bool) =
        conduit.Value.Enabled <- visible
        for doc in RhinoDoc.OpenDocuments() do
            doc.Views.Redraw()
