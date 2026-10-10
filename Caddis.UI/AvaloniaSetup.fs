namespace Caddis.UI

open System
open System.IO
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Themes.Fluent
open Caddis

/// The Avalonia application of the panel. The host owns the process and its main loop, Avalonia only adds its platform services.
/// The Fluent theme styles what PanelStyles.axaml doesn't, e.g. scroll bars and the context menu of text boxes.
type PanelApp() =
    inherit Application()

    override this.Initialize() = this.Styles.Add(FluentTheme())


/// Starts Avalonia on the host UI thread, without taking over the process or its message loop.
module AvaloniaSetup =

    /// The folder of this platform's native libraries, as NuGet lays them out next to the plugin.
    /// On .NET Framework SkiaSharp's and HarfBuzzSharp's own build targets copy them to a folder per architecture, e.g. x64.
    let nativeDir =
        let dir = Path.GetDirectoryName typeof<PanelApp>.Assembly.Location
        let arm64 = RuntimeInformation.ProcessArchitecture = Architecture.Arm64
#if NETFRAMEWORK
        Path.Combine(dir, (if arm64 then "arm64" else "x64"))
#else
        let rid = if OperatingSystem.IsMacOS() then "osx" elif arm64 then "win-arm64" else "win-x64"
        Path.Combine(dir, "runtimes", rid, "native")
#endif

#if NETFRAMEWORK
    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode)>]
    extern nativeint private LoadLibrary(string path)

    /// .NET Framework has no NativeLibrary and only looks in the folder of Rhino.exe and the system folders.
    /// A library that is already loaded is found by its name, so it is loaded from the plugin's folder first.
    let private resolveNativeLibraries (assembly: Reflection.Assembly) =
        let name = if assembly = typeof<SkiaSharp.SKObject>.Assembly then "libSkiaSharp.dll" else "libHarfBuzzSharp.dll"
        let path = Path.Combine(nativeDir, name)
        if File.Exists path then LoadLibrary path |> ignore
#else
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
#endif

    let private nativeLibraries =
        lazy
            (resolveNativeLibraries typeof<SkiaSharp.SKObject>.Assembly
             resolveNativeLibraries typeof<HarfBuzzSharp.Blob>.Assembly)

    /// The host supplies its platform backend. Another plugin may already own Avalonia's application.
    let ensure (platform: AppBuilder -> AppBuilder) =
        nativeLibraries.Force()
        if isNull Application.Current then
            let builder = AppBuilder.Configure<PanelApp>()
#if NETFRAMEWORK
            (platform builder).UseSkia().SetupWithoutStarting() |> ignore // Avalonia 11 has HarfBuzz in its Skia backend
#else
            (platform builder).UseSkia().UseHarfBuzz().SetupWithoutStarting() |> ignore
#endif
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess()
