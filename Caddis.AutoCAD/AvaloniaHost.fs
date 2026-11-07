namespace Caddis.AutoCAD

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Windows.Input
open System.Windows.Interop
open Avalonia
open Caddis.UI

module private NativePanel =
    [<DllImport("user32.dll", SetLastError = true)>]
    extern nativeint SetParent(nativeint child, nativeint parent)

    [<DllImport("user32.dll")>]
    extern nativeint GetFocus()

    [<DllImport("user32.dll")>]
    extern nativeint SetFocus(nativeint hwnd)

    [<DllImport("user32.dll")>]
    extern bool IsChild(nativeint parent, nativeint child)

    [<DllImport("user32.dll")>]
    extern bool TranslateMessage(MSG& msg)

    [<DllImport("user32.dll")>]
    extern nativeint SendMessage(nativeint hwnd, int msg, nativeint wParam, nativeint lParam)

    [<DllImport("user32.dll")>]
    extern nativeint DispatchMessage(MSG& msg)

    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern int GetClassName(nativeint hwnd, System.Text.StringBuilder name, int maxCount)

/// A WPF element for AutoCAD's palette, containing the shared Avalonia view's native child window.
/// AutoCAD owns the UI thread and message loop. No second Application.Run or UI thread is started.
/// A copy of Caddis.Revit/AvaloniaHost.fs without its code for Avalonia 11, which AutoCAD doesn't need: it runs on .NET 8 or 10.
/// Internal, so that the public signature of Caddis.AutoCAD exposes no Avalonia or WPF types, see AGENTS.md.
type internal AvaloniaHost(content: Avalonia.Controls.Control) =
    inherit HwndHost()

    let mutable embedded: EmbeddedView option = None

    static member EnsureInitialized() =
        AvaloniaSetup.ensure (fun builder ->
            builder.UseWin32().With(Win32PlatformOptions(RenderingMode = [| Win32RenderingMode.Software |])))

    override this.BuildWindowCore(parent) =
        AvaloniaHost.EnsureInitialized()
        let view = new EmbeddedView(content)
        try
            // Avalonia creates an embeddable root with WS_CHILD and an off-screen parent.
            if NativePanel.SetParent(view.Handle, parent.Handle) = 0n then
                raise (Win32Exception(Marshal.GetLastWin32Error()))
            embedded <- Some view
            view.SyncDpi()
            HandleRef(this, view.Handle)
        with _ ->
            (view :> IDisposable).Dispose()
            reraise ()

    override _.DestroyWindowCore(_) =
        embedded |> Option.iter (fun view -> (view :> IDisposable).Dispose())
        embedded <- None

    member _.SyncDpi() = embedded |> Option.iter _.SyncDpi()

    override this.OnDpiChanged(oldDpi, newDpi) =
        base.OnDpiChanged(oldDpi, newDpi)
        this.SyncDpi()

    override this.HasFocusWithinCore() =
        let focused = NativePanel.GetFocus()
        this.Handle <> 0n && (focused = this.Handle || NativePanel.IsChild(this.Handle, focused))

    override this.TabIntoCore(request) =
        if this.Handle = 0n then false
        else
            NativePanel.SetFocus this.Handle |> ignore
            let last = request.FocusNavigationDirection = FocusNavigationDirection.Last || request.FocusNavigationDirection = FocusNavigationDirection.Previous
            let manager = Avalonia.Controls.TopLevel.GetTopLevel(content).FocusManager
            let target = if last then manager.FindLastFocusableElement() else manager.FindFirstFocusableElement()
            if isNull target then false
            else target.Focus(Avalonia.Input.NavigationMethod.Tab)

    override this.TranslateAcceleratorCore(msg: MSG byref, _modifiers) =
        // Deliver keyboard input to Avalonia before WPF or AutoCAD can treat it as a shortcut.
        // TranslateMessage generates WM_CHAR for text input, e.g. the code search box.
        if this.HasFocusWithinCore() && msg.message >= 0x0100 && msg.message <= 0x0105 then
            NativePanel.TranslateMessage(&msg) |> ignore
            NativePanel.SendMessage(msg.hwnd, msg.message, msg.wParam, msg.lParam) |> ignore
            true
        else false


/// AutoCAD sends the keys typed in a window it doesn't know to its command line, before the window gets them,
/// also in the panel's windows, e.g. its search box. So Caddis gives them to Avalonia itself, in AutoCAD's PreTranslateMessage.
module internal KeyboardFilter =

    /// True for the windows of Avalonia, the embedded panel and its popups, e.g. a context menu. Their window class starts with 'Avalonia'.
    let private isAvalonia (hwnd: nativeint) =
        let name = Text.StringBuilder 64
        hwnd <> 0n && NativePanel.GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString().StartsWith "Avalonia"

    let private onMessage =
        Autodesk.AutoCAD.ApplicationServices.PreTranslateMessageEventHandler(fun _ e ->
            let m = e.Message
            // WM_KEYDOWN to WM_UNICHAR, also with Alt and dead keys
            if m.message >= 0x0100 && m.message <= 0x0109 && isAvalonia m.hwnd then
                let mutable msg = m
                // WM_KEYDOWN and WM_SYSKEYDOWN: posts WM_CHAR for the text, which comes here again
                if m.message = 0x0100 || m.message = 0x0104 then NativePanel.TranslateMessage(&msg) |> ignore
                NativePanel.DispatchMessage(&msg) |> ignore
                e.Handled <- true)

    let mutable private installed = false

    /// Called on AutoCAD's main thread when the panel is created.
    let install () =
        if not installed then
            installed <- true
            Autodesk.AutoCAD.ApplicationServices.Core.Application.add_PreTranslateMessage onMessage
