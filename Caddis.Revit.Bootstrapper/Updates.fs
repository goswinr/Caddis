namespace Caddis.Revit.Bootstrapper

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Windows
open Velopack
open Velopack.Locators
open Velopack.Sources

/// Updates from the releases on GitHub. Caddis.Revit starts this app for them, see Caddis.Revit/Updates.fs,
/// so that Velopack runs in this process and not in Revit, where it could conflict with the Velopack of another add-in.
module Updates =

    let private source () = GithubSource("https://github.com/goswinr/Caddis", "", false)

    /// Appends to the log of Caddis in Revit, see Caddis.Revit/Log.fs. The Info button of Caddis in Revit shows only its own messages.
    let private log (msg: string) =
        try
            let path = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "Caddis", "revit.log")
            File.AppendAllText(path, DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss" + " Caddis.Revit.Bootstrapper: " + msg + Environment.NewLine)
        with _ ->
            ()

    let private show (text: string) (buttons: MessageBoxButton) (icon: MessageBoxImage) = MessageBox.Show(text, "Caddis for Revit", buttons, icon)

    /// Checks for a newer release and asks whether to download it. Only one check at a time, e.g. when several Revits start.
    let check () =
        use running = new Mutex(false, "Caddis.Revit.Bootstrapper.CheckForUpdate")
        if running.WaitOne 0 then
            try
                try
                    let um = UpdateManager(source ())
                    match um.CheckForUpdates() with
                    | null -> ()
                    | info ->
                        let v = info.TargetFullRelease.Version
                        let pending = um.UpdatePendingRestart
                        if isNull pending || pending.Version <> v then
                            let question =
                                $"Caddis {v} is available, this is {um.CurrentVersion}.{Environment.NewLine}{Environment.NewLine}Download it now? It is installed when the last Revit has closed."
                            if show question MessageBoxButton.YesNo MessageBoxImage.Question = MessageBoxResult.Yes then
                                um.DownloadUpdates info
                                log $"downloaded Caddis {v}, it is installed when the last Revit has closed."
                                show $"Caddis {v} is downloaded. It is installed when the last Revit has closed." MessageBoxButton.OK MessageBoxImage.Information |> ignore
                            else
                                log $"the update to Caddis {v} was skipped."
                with e ->
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

    /// Velopack's default for this process, but with the id of the Revit that Update.exe waits for, like in Fesh.Revit.
    type private WaitForRevit(revitId: int) =
        interface IProcessImpl with
            member _.GetCurrentProcessPath() = Process.GetCurrentProcess().MainModule.FileName
            member _.GetCurrentProcessId() = uint32 revitId

            member _.StartProcess(exe, args, workDir, showWindow) = // starts Update.exe
                let psi = ProcessStartInfo(exe, args |> Seq.map quote |> String.concat " ")
                psi.WorkingDirectory <- workDir
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- not showWindow
                Process.Start psi |> ignore

            member _.Exit(exitCode) = exit exitCode // only called by ApplyUpdatesAndExit and ApplyUpdatesAndRestart

    /// Lets Update.exe install a downloaded release once the Revit process has exited, and then start this app again,
    /// which shows the new version. Update.exe waits for at most 60 seconds.
    let installOnExit (revitId: int) =
        try
            let um = UpdateManager(source (), locator = WindowsVelopackLocator(WaitForRevit revitId, null))
            match um.UpdatePendingRestart with
            | null -> ()
            | asset ->
                log $"installing Caddis {asset.Version} once Revit has exited."
                um.WaitExitThenApplyUpdates(asset, false, true, null)
        with e ->
            log $"installing the update failed: {e.Message}"
