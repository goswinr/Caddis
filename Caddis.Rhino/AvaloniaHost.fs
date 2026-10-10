namespace Caddis.Rhino

open System
open System.IO
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Embedding
open Avalonia.Themes.Fluent

/// The Avalonia application of the panel. Rhino owns the process and its main loop, Avalonia only adds its platform services.
/// The Fluent theme styles what PanelStyles.axaml doesn't, e.g. scroll bars and the context menu of text boxes.
type PanelApp() =
    inherit Application()

    override this.Initialize() = this.Styles.Add(FluentTheme())


/// Starts Avalonia in Rhino's process, once, on Rhino's UI thread.
module AvaloniaSetup =

    /// The folder of this platform's native libraries, as NuGet lays them out next to the .rhp.
    let private nativeDir =
        let rid =
            if OperatingSystem.IsMacOS() then "osx"
            elif RuntimeInformation.ProcessArchitecture = Architecture.Arm64 then "win-arm64"
            else "win-x64"
        Path.Combine(Path.GetDirectoryName typeof<PanelApp>.Assembly.Location, "runtimes", rid, "native")

    /// Rhino doesn't read the plugin's deps.json, so .NET doesn't look for native libraries in its 'runtimes' folder.
    let private resolveNativeLibraries (assembly: Reflection.Assembly) =
        try
            NativeLibrary.SetDllImportResolver(
                assembly,
                DllImportResolver(fun name _ _ ->
                    let file = if Path.HasExtension name then name elif OperatingSystem.IsMacOS() then name + ".dylib" else name + ".dll"
                    let path = Path.Combine(nativeDir, file)
                    let mutable handle = 0n
                    if File.Exists path && NativeLibrary.TryLoad(path, &handle) then handle else 0n)
            )
        with :? InvalidOperationException ->
            () // another plugin set a resolver already, then it also finds the library

    let private setup =
        lazy
            (resolveNativeLibraries typeof<SkiaSharp.SKObject>.Assembly
             resolveNativeLibraries typeof<HarfBuzzSharp.Blob>.Assembly
             // Another plugin may have started Avalonia already. Then the panel uses its platform, and PanelView adds the Fluent theme.
             if isNull Application.Current then
                 let builder = AppBuilder.Configure<PanelApp>()
                 // Software rendering: no GPU context next to Rhino's viewports, and no ANGLE libraries on Windows.
                 let platform =
                     if OperatingSystem.IsMacOS() then
                         builder
                             .UseAvaloniaNative()
                             .With(
                                 AvaloniaNativePlatformOptions(
                                     RenderingMode = [| AvaloniaNativeRenderingMode.Software |],
                                     AvaloniaNativeLibraryPath = Path.Combine(nativeDir, "libAvaloniaNative.dylib")
                                 )
                             )
                             // Rhino owns the app delegate, the menu bar and the process name.
                             .With(
                                 MacOSPlatformOptions(
                                     DisableAvaloniaAppDelegate = true,
                                     DisableNativeMenus = true,
                                     DisableDefaultApplicationMenuItems = true,
                                     DisableSetProcessName = true
                                 )
                             )
                     else
                         builder.UseWin32().With(Win32PlatformOptions(RenderingMode = [| Win32RenderingMode.Software |]))
                 platform.UseSkia().UseHarfBuzz().SetupWithoutStarting() |> ignore)

    /// Starts Avalonia if it isn't yet. Throws if it can't, every time it is called.
    let ensure () = setup.Force()


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


/// An Eto control that shows Avalonia content, so that Rhino can dock it like any Eto panel.
/// Avalonia's embeddable top level is a child window on Windows and an NSView on Mac, Eto's NativeControlHost hosts both.
type AvaloniaHost(content: Control) =
    inherit Eto.Forms.NativeControlHost()

    let mutable root: EmbeddableControlRoot = null
    let mutable handle = 0n

    /// Called by Eto when it creates the native control.
    override _.OnCreateNativeControl(e) =
        AvaloniaSetup.ensure ()
        root <- new EmbeddableControlRoot(Content = content)
        // The root is black where the content doesn't cover it, e.g. for a moment while Rhino resizes the panel.
        match content with
        | :? Primitives.TemplatedControl as c ->
            root.Bind(Primitives.TemplatedControl.BackgroundProperty, c.GetObservable Primitives.TemplatedControl.BackgroundProperty) |> ignore
        | _ -> ()
        root.Prepare()
        root.StartRendering()
        handle <- root.TryGetPlatformHandle().Handle
        e.NativeControl <- box handle

    /// Windows sends WM_DPICHANGED only to top level windows, so the embedded window keeps the scaling of the monitor it was created on.
    /// This sends it the DPI of its current monitor, e.g. after the panel moved to another monitor.
    member _.SyncDpi() =
        if OperatingSystem.IsWindows() && handle <> 0n then
            let dpi = Win32.GetDpiForWindow handle
            if dpi > 0u && abs (float dpi / 96.0 - root.RenderScaling) > 0.001 then
                // Avalonia moves the window to the given rectangle, keep it where it is, in its parent's coordinates.
                let mutable rect = Win32.Rect()
                if Win32.GetWindowRect(handle, &rect) then
                    Win32.MapWindowPoints(0n, Win32.GetParent handle, &rect, 2u) |> ignore
                    Win32.SendMessage(handle, Win32.WM_DPICHANGED, nativeint ((dpi <<< 16) ||| dpi), &rect) |> ignore

    override _.Dispose(disposing) =
        if disposing && not (isNull root) then
            root.Dispose()
            root <- null
            handle <- 0n
        base.Dispose(disposing)
