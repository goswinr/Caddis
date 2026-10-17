namespace Caddis.Revit

open System.Diagnostics
open System.IO
open System.Reflection

/// Updates of Caddis installed by its installer, from the releases on GitHub. Velopack runs in the app of the installer,
/// Caddis.Revit.Bootstrapper.exe in the folder above the folder of this build, not in Revit: Revit loads only one version of an assembly,
/// so Velopack in Revit could conflict with the one of another add-in, e.g. of an older Fesh.Revit.
module internal Updates =

    let private bootstrapper =
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)), "Caddis.Revit.Bootstrapper.exe")

    /// With the shell, so that the bootstrapper inherits no handles of Revit.
    let private start (args: string) =
        try Process.Start(ProcessStartInfo(bootstrapper, args, UseShellExecute = true)) |> ignore
        with e -> RevitLog.error $"Caddis: starting {bootstrapper} failed: {e.Message}"

    /// At Revit's start: the bootstrapper checks for a newer release and asks whether to download it.
    let check () =
        if File.Exists bootstrapper then start "--check-for-update"
        else RevitLog.write "Caddis: updates are off, Caddis was not installed by its installer."

    /// When the last Revit closes: the bootstrapper lets Velopack install a downloaded release once this Revit has exited.
    /// While another Revit is running it may use Caddis, then the next one to close does it. Called in OnShutdown.
    /// A Revit that has exited may still be listed for minutes, e.g. while a thread of it is stuck in a driver, it doesn't count.
    let installOnExit () =
        let running (p: Process) = try not p.HasExited with _ -> false
        if File.Exists bootstrapper && (Process.GetProcessesByName "Revit" |> Array.filter running).Length = 1 then
            start $"--install-update {Process.GetCurrentProcess().Id}"
