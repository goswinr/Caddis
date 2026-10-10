namespace Caddis.UI

open System
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Embedding

/// Windows functions to tell the embedded Avalonia window about DPI changes.
module private Win32 =

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Rect =
        val mutable Left: int
        val mutable Top: int
        val mutable Right: int
        val mutable Bottom: int

    [<Literal>]
    let WM_DPICHANGED = 0x02E0u

    [<DllImport("user32.dll")>]
    extern uint32 GetDpiForWindow(nativeint hwnd)

    [<DllImport("user32.dll")>]
    extern nativeint GetParent(nativeint hwnd)

    [<DllImport("user32.dll")>]
    extern bool GetWindowRect(nativeint hwnd, Rect& rect)

    [<DllImport("user32.dll")>]
    extern int MapWindowPoints(nativeint hwndFrom, nativeint hwndTo, Rect& points, uint32 count)

    [<DllImport("user32.dll")>]
    extern nativeint SendMessage(nativeint hwnd, uint32 msg, nativeint wParam, Rect& lParam)


/// Owns one native Avalonia child window (or NSView on Mac). Both docking wrappers use this lifetime and DPI handling.
/// Detaching content before disposal allows a docking host to recreate its native window with the same view.
type EmbeddedView(content: Control) =
    let root = new EmbeddableControlRoot(Content = content)
    let mutable disposed = false

    do
        match content with
        | :? Primitives.TemplatedControl as c ->
            root.Bind(Primitives.TemplatedControl.BackgroundProperty, c.GetObservable Primitives.TemplatedControl.BackgroundProperty) |> ignore
        | _ -> ()
        root.Prepare()
        root.StartRendering()

    member _.Handle = root.TryGetPlatformHandle().Handle

    /// Embedded windows do not receive the top-level window's WM_DPICHANGED automatically.
    member this.SyncDpi() =
        if OperatingSystem.IsWindows() && not disposed then
            let handle = this.Handle
            let dpi = Win32.GetDpiForWindow handle
            if dpi > 0u && abs (float dpi / 96.0 - root.RenderScaling) > 0.001 then
                let mutable rect = Win32.Rect()
                if Win32.GetWindowRect(handle, &rect) then
                    Win32.MapWindowPoints(0n, Win32.GetParent handle, &rect, 2u) |> ignore
                    Win32.SendMessage(handle, Win32.WM_DPICHANGED, nativeint ((dpi <<< 16) ||| dpi), &rect) |> ignore

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                root.Content <- null
                root.Dispose()
