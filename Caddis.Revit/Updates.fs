namespace Caddis.Revit

open System
open System.Diagnostics
open System.IO
open System.Reflection
open Caddis

/// Updates of Caddis installed by its installer, from the releases on GitHub. Velopack runs in the app of the installer,
/// Caddis.Revit.Bootstrapper.exe in the folder above the folder of this build, not in Revit: Revit loads only one version of an assembly,
/// so Velopack in Revit could conflict with the one of another add-in, e.g. of an older Fesh.Revit.
module internal Updates =

    let private bootstrapper =
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)), "Caddis.Revit.Bootstrapper.exe")

    /// Not for a build registered by install-dev.ps1.
    let private installed = File.Exists bootstrapper

    /// The version of this build, e.g. 0.2.1, from the changelog.
    let version =
        let a = Assembly.GetExecutingAssembly()
        match a.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> a.GetName().Version.ToString 3
        | v -> v.InformationalVersion.Split('+')[0] // without the git commit hash

    /// With the shell, so that the bootstrapper inherits no handles of Revit.
    let private start (args: string) =
        try Process.Start(ProcessStartInfo(bootstrapper, args, UseShellExecute = true)) |> ignore
        with e -> RevitLog.error $"Caddis: starting {bootstrapper} failed: {e.Message}"

    /// At Revit's start: the bootstrapper checks for a newer release and asks whether to download it.
    let check () =
        if installed then start "--check-for-update"
        else RevitLog.write "Caddis: updates are off, Caddis was not installed by its installer."

    /// When the last Revit closes: the bootstrapper lets Velopack install a downloaded release once this Revit has exited.
    /// While another Revit is running it may use Caddis, then the next one to close does it. Called in OnShutdown.
    /// A Revit that has exited may still be listed for minutes, e.g. while a thread of it is stuck in a driver, it doesn't count.
    let installOnExit () =
        let running (p: Process) = try not p.HasExited with _ -> false
        if installed && (Process.GetProcessesByName "Revit" |> Array.filter running).Length = 1 then
            start $"--install-update {Process.GetCurrentProcess().Id}"

    /// The result of the bootstrapper's latest check, see Caddis.Revit.Bootstrapper/Updates.fs. Each None if not known.
    type private CheckResult = {
        Latest: string option
        Downloaded: string option
        Error: string option
        }

    let private statusPath = Path.Combine(SettingsStore.DefaultFolder, "revit-update.json")

    /// The last read check and the time its file was written. Read again when the file changes.
    let mutable private lastCheck: (DateTime * CheckResult) option = None

    /// None before the first check. While the bootstrapper writes the file it may not parse, then the last one is kept and it is read again next time.
    let private readCheck () =
        try
            let written = File.GetLastWriteTimeUtc statusPath
            match lastCheck with
            | Some(time, check) when time = written -> Some check
            | _ when not (File.Exists statusPath) -> None
            | _ ->
                let node =
                    match Json.parse (File.ReadAllText statusPath) with
                    | null -> failwith "empty"
                    | node -> node
                let check = {
                    Latest = Json.tryString "latest" node
                    Downloaded = Json.tryString "downloaded" node
                    Error = Json.tryString "error" node
                }
                lastCheck <- Some(written, check)
                Some check
        with _ ->
            lastCheck |> Option.map snd

    /// Whether a newer Caddis is downloaded or available, compared to this build: the file of the check may be older than this build,
    /// e.g. at the first start after an update, until the bootstrapper checked again. None if not, or if Caddis was not installed by its installer.
    let notice () =
        if not installed then
            None
        else
            match readCheck () with
            | Some { Downloaded = Some d } when ReleaseVersion.isNewer d version ->
                Some $"Caddis {d} is downloaded, it is installed when the last Revit has closed. You're using {version}."
            | Some { Latest = Some l } when ReleaseVersion.isNewer l version ->
                Some $"Caddis {l} is available, you're using {version}. Caddis offers to download it when Revit starts."
            | _ -> None

    /// The version of Caddis and whether a newer one is available or downloaded.
    let status () =
        if not installed then
            $"You're using Caddis {version}. Updates are off, Caddis was not installed by its installer."
        else
            match notice (), readCheck () with
            | Some n, _ -> n
            | None, Some { Latest = Some _ } -> $"You're using the latest version of Caddis, {version}."
            | None, Some { Error = Some e } -> $"You're using Caddis {version}. Checking for updates failed: {e}"
            | _ -> $"You're using Caddis {version}."
