namespace Caddis.UI

open System
open System.IO
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Themes.Fluent

/// The Avalonia application of the panel. The host owns the process and its main loop, Avalonia only adds its platform services.
/// The Fluent theme styles what PanelStyles.axaml doesn't, e.g. scroll bars and the context menu of text boxes.
type PanelApp() =
    inherit Application()

    override this.Initialize() = this.Styles.Add(FluentTheme())


/// Starts Avalonia on the host UI thread, without taking over the process or its message loop.
module AvaloniaSetup =

    /// The folder of this platform's native libraries, as NuGet lays them out next to the plugin.
    let nativeDir =
        let rid =
            if OperatingSystem.IsMacOS() then "osx"
            elif RuntimeInformation.ProcessArchitecture = Architecture.Arm64 then "win-arm64"
            else "win-x64"
        Path.Combine(Path.GetDirectoryName typeof<PanelApp>.Assembly.Location, "runtimes", rid, "native")

    /// Hosts don't necessarily read the plugin's deps.json, so .NET doesn't look for native libraries in its 'runtimes' folder.
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

    let private nativeLibraries =
        lazy
            (resolveNativeLibraries typeof<SkiaSharp.SKObject>.Assembly
             resolveNativeLibraries typeof<HarfBuzzSharp.Blob>.Assembly)

    /// The host supplies its platform backend. Another plugin may already own Avalonia's application.
    let ensure (platform: AppBuilder -> AppBuilder) =
        nativeLibraries.Force()
        if isNull Application.Current then
            let builder = AppBuilder.Configure<PanelApp>()
            (platform builder).UseSkia().UseHarfBuzz().SetupWithoutStarting() |> ignore
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess()
