namespace Caddis.UI

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Themes.Fluent
open Avalonia.Threading
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

    /// Returns a check for the host's refresh timer. A lost Win32 wake-up leaves Avalonia waiting
    /// for that message even when more work is posted. Only one callback needs to be pending.
    /// The first wake-up sent again for a waiting callback goes to log, so that a freeze shows in the app's log.
    let internal createDispatcherCheck (log: string -> unit) (logError: string -> unit) : unit -> unit =
        if not (OperatingSystem.IsWindows()) then ignore
        else
            try
                let dispatcher = Dispatcher.UIThread
                // Avalonia 11 and 12 hide the platform dispatcher's Signal method from add-ins.
                let field = typeof<Dispatcher>.GetField("_impl", BindingFlags.Instance ||| BindingFlags.NonPublic)
                let impl = field.GetValue(dispatcher)
                if impl.GetType().Assembly.GetName().Name <> "Avalonia.Win32" then ignore
                else
                    let wake = Delegate.CreateDelegate(typeof<Action>, impl, "Signal") :?> Action
                    let pending = Stopwatch()
                    let resent = ref false
                    let acknowledge =
                        Action(fun () ->
                            pending.Reset()
                            resent.Value <- false)
                    fun () ->
                        if not pending.IsRunning then
                            pending.Start()
                            dispatcher.Post(acknowledge, DispatcherPriority.Normal)
                        elif pending.ElapsedMilliseconds >= 1000L then
                            if not resent.Value then
                                resent.Value <- true
                                log "Caddis: the panel missed a wake-up of its Avalonia dispatcher and stopped repainting, the wake-up was sent again."
                            pending.Restart()
                            wake.Invoke()
            with e ->
                logError $"Caddis: could not set up recovery for the panel's Avalonia dispatcher: {e.Message}"
                ignore
