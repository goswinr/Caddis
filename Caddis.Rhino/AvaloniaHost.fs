namespace Caddis.Rhino

open System
open System.IO
open Avalonia
open Caddis.UI

/// Rhino owns its main loop, app delegate and menus. Only start Avalonia's platform services.
module RhinoAvaloniaSetup =
    let ensure () =
        AvaloniaSetup.ensure (fun builder ->
            if OperatingSystem.IsMacOS() then
                builder
                    .UseAvaloniaNative()
                    .With(
                        AvaloniaNativePlatformOptions(
                            RenderingMode = [| AvaloniaNativeRenderingMode.Software |],
                            AvaloniaNativeLibraryPath = Path.Combine(AvaloniaSetup.nativeDir, "libAvaloniaNative.dylib")
                        )
                    )
                    .With(
                        MacOSPlatformOptions(
                            DisableAvaloniaAppDelegate = true,
                            DisableNativeMenus = true,
                            DisableDefaultApplicationMenuItems = true,
                            DisableSetProcessName = true
                        )
                    )
            else
                builder.UseWin32().With(Win32PlatformOptions(RenderingMode = [| Win32RenderingMode.Software |])))

/// The Eto shell Rhino docks on Windows and Mac; all visible controls belong to Caddis.UI.
type AvaloniaHost(content: Avalonia.Controls.Control) =
    inherit Eto.Forms.NativeControlHost()

    let mutable embedded: EmbeddedView option = None

    override _.OnCreateNativeControl(e) =
        RhinoAvaloniaSetup.ensure ()
        embedded |> Option.iter (fun view -> (view :> IDisposable).Dispose())
        let view = new EmbeddedView(content)
        embedded <- Some view
        e.NativeControl <- box view.Handle

    member _.SyncDpi() = embedded |> Option.iter _.SyncDpi()

    override _.Dispose(disposing) =
        if disposing then
            embedded |> Option.iter (fun view -> (view :> IDisposable).Dispose())
            embedded <- None
        base.Dispose(disposing)
