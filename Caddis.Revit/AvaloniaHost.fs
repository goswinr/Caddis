namespace Caddis.Revit

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Windows.Input
open System.Windows.Interop
open Avalonia
#if NETFRAMEWORK
open Avalonia.VisualTree
#endif
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

/// A WPF element for Revit's dockable pane, containing the shared Avalonia view's native child window.
/// Revit owns the UI thread and message loop. No second Application.Run or UI thread is started.
/// Keep this internal: FSI inspects the add-in's public F# signature when resolving Script.Doc/OnUi.
/// Exposing Avalonia controls or the WPF DpiScale override makes ordinary Revit scripts require those UI assemblies.
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
#if NETFRAMEWORK
            // Avalonia 11's focus manager can't find the first or last focusable element, so take the tab stops in the order of the visual tree.
            let tabStops =
                content.GetSelfAndVisualDescendants()
                |> Seq.choose (function
                    | :? Avalonia.Input.InputElement as e when
                        e.Focusable && e.IsEffectivelyVisible && e.IsEffectivelyEnabled && Avalonia.Input.KeyboardNavigation.GetIsTabStop e
                        ->
                        Some e
                    | _ -> None)
                |> Array.ofSeq
            match (if last then Array.tryLast tabStops else Array.tryHead tabStops) with
            | Some target -> target.Focus(Avalonia.Input.NavigationMethod.Tab)
            | None -> false
#else
            let manager = Avalonia.Controls.TopLevel.GetTopLevel(content).FocusManager
            let target = if last then manager.FindLastFocusableElement() else manager.FindFirstFocusableElement()
            if isNull target then false
            else target.Focus(Avalonia.Input.NavigationMethod.Tab)
#endif

    override this.TranslateAcceleratorCore(msg: MSG byref, _modifiers) =
        // Deliver keyboard input to Avalonia before WPF can treat it as a host shortcut.
        // TranslateMessage generates WM_CHAR for text input, e.g. the code search box.
        if this.HasFocusWithinCore() && msg.message >= 0x0100 && msg.message <= 0x0105 then
            NativePanel.TranslateMessage(&msg) |> ignore
            NativePanel.SendMessage(msg.hwnd, msg.message, msg.wParam, msg.lParam) |> ignore
            true
        else false
