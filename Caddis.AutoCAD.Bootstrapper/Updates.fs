namespace Caddis.AutoCAD.Bootstrapper

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Velopack
open Velopack.Locators
open Velopack.Sources

/// Updates from the releases on GitHub, in the channel 'autocad' of Velopack, next to the installer for Revit in the same release.
/// Caddis.AutoCAD starts this app for them, see Caddis.AutoCAD/Updates.fs, so that Velopack runs in this process and not in AutoCAD,
/// where it could conflict with the Velopack of another plugin. A copy of Caddis.Revit.Bootstrapper/Updates.fs for AutoCAD.
module Updates =

    let private source () = GithubSource("https://github.com/goswinr/Caddis", "", false)

    /// %AppData%/Caddis, with the settings and the log of Caddis in AutoCAD.
    let private folder = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "Caddis")

    /// Appends to the log of Caddis in AutoCAD, see Caddis.AutoCAD/Log.fs. The Info button of Caddis in AutoCAD shows only its own messages.
    let private log (msg: string) =
        try
            let path = Path.Combine(folder, "autocad.log")
            File.AppendAllText(path, DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss" + " Caddis.AutoCAD.Bootstrapper: " + msg + Environment.NewLine)
        with _ ->
            ()

    /// A JSON string, or null. By hand, .NET Framework has no System.Text.Json.
    let private json (value: string option) =
        match value with
        | None -> "null"
        | Some s ->
            let sb = StringBuilder "\""
            for c in s do
                match c with
                | '"' -> sb.Append "\\\"" |> ignore
                | '\\' -> sb.Append "\\\\" |> ignore
                | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
                | c -> sb.Append c |> ignore
            sb.Append('"').ToString()

    /// The result of the latest check, for the panel and the Info button of Caddis in AutoCAD, see Caddis.AutoCAD/Updates.fs:
    /// the newest release, the one downloaded to be installed, and why the check failed, each null if not known.
    let private writeStatus (latest: string option) (downloaded: string option) (error: string option) =
        let path = Path.Combine(folder, "autocad-update.json")
        try
            let fields =
                [
                    "checked", Some(DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss")
                    "latest", latest
                    "downloaded", downloaded
                    "error", error
                ]
                |> List.map (fun (name, value) -> $"  \"{name}\": {json value}")
            Directory.CreateDirectory folder |> ignore
            File.WriteAllLines(path, [ "{"; String.concat ("," + Environment.NewLine) fields; "}" ])
        with e ->
            log $"writing {path} failed: {e.Message}"

    /// Checks for a newer release and asks whether to download it. Only one check at a time, e.g. when several AutoCADs start.
    let check () =
        use running = new Mutex(false, "Caddis.AutoCAD.Bootstrapper.CheckForUpdate")
        if running.WaitOne 0 then
            try
                try
                    let um = UpdateManager(source ())
                    match um.CheckForUpdates() with
                    | null -> writeStatus (Some(string um.CurrentVersion)) None None
                    | info ->
                        let v = info.TargetFullRelease.Version
                        let pending = um.UpdatePendingRestart
                        if isNull pending || pending.Version <> v then
                            writeStatus (Some(string v)) None None
                            let question =
                                $"Caddis {v} is available, this is {um.CurrentVersion}.{Environment.NewLine}{Environment.NewLine}Download it now? It is installed when the last AutoCAD has closed."
                            if Dialog.ask question then
                                um.DownloadUpdates info
                                writeStatus (Some(string v)) (Some(string v)) None
                                log $"downloaded Caddis {v}, it is installed when the last AutoCAD has closed."
                                Dialog.info $"Caddis {v} is downloaded. It is installed when the last AutoCAD has closed."
                            else
                                log $"the update to Caddis {v} was skipped."
                        else
                            writeStatus (Some(string v)) (Some(string v)) None
                with e ->
                    writeStatus None None (Some e.Message)
                    log $"checking for updates failed: {e.Message}"
            finally
                running.ReleaseMutex()

    /// Quotes an argument for the command line, like ArgumentList of .NET does.
    let private quote (a: string) =
        if a <> "" && a.IndexOfAny [| ' '; '\t'; '"' |] < 0 then
            a
        else
            let sb = StringBuilder "\""
            let mutable backslashes = 0
            for c in a do
                if c = '\\' then
                    backslashes <- backslashes + 1
                else
                    // backslashes before a quote are escaped, others are literal
                    sb.Append('\\', (if c = '"' then 2 * backslashes + 1 else backslashes)).Append(c) |> ignore
                    backslashes <- 0
            sb.Append('\\', 2 * backslashes).Append('"').ToString()

    /// Velopack's default for this process, but with the id of the AutoCAD that Update.exe waits for.
    type private WaitForAutoCAD(autocadId: int) =
        interface IProcessImpl with
            member _.GetCurrentProcessPath() = Process.GetCurrentProcess().MainModule.FileName
            member _.GetCurrentProcessId() = uint32 autocadId

            member _.StartProcess(exe, args, workDir, showWindow) = // starts Update.exe
                let psi = ProcessStartInfo(exe, args |> Seq.map quote |> String.concat " ")
                psi.WorkingDirectory <- workDir
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- not showWindow
                Process.Start psi |> ignore

            member _.Exit(exitCode) = exit exitCode // only called by ApplyUpdatesAndExit and ApplyUpdatesAndRestart

    /// Lets Update.exe install a downloaded release once the AutoCAD process has exited, and then start this app again,
    /// which shows the new version. Update.exe waits for at most 60 seconds.
    let installOnExit (autocadId: int) =
        try
            let um = UpdateManager(source (), locator = WindowsVelopackLocator(WaitForAutoCAD autocadId, null))
            match um.UpdatePendingRestart with
            | null -> ()
            | asset ->
                log $"installing Caddis {asset.Version} once AutoCAD has exited."
                um.WaitExitThenApplyUpdates(asset, false, true, null)
        with e ->
            log $"installing the update failed: {e.Message}"
