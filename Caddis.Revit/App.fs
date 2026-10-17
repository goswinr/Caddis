namespace Caddis.Revit

open System
open System.Threading.Tasks
open Autodesk.Revit.UI
open Caddis

/// The Caddis server of this Revit process, and the actions of the ribbon buttons.
/// Messages of actions that finish later go to the log, see the Info button.
module App =

    let mutable server: CaddisServer option = None

    let private log (s: string) = RevitLog.write s

    /// Called on the UI thread when the panel refreshes, so that the ribbon shows changes made in the panel. Set at startup.
    let mutable stateChanged: unit -> unit = ignore

    /// Logs a message and shows it in a dialog, for the ribbon buttons. Revit puts the add-in name before the title: 'Caddis - Message'.
    /// Only in Revit's API context: the buttons of the panel call these actions outside of it, and the panel shows the latest message itself.
    let private show (msg: string) =
        log msg
        if ApiContext.isActive () then TaskDialog.Show("Message", msg) |> ignore

    let private withServer (f: CaddisServer -> unit) =
        match server with
        | Some s -> f s
        | None -> show "Caddis is not loaded."

    /// A few lines about the server and session.
    let info (s: CaddisServer) =
        let autoStart = if s.Service.Settings.autoStart then "on" else "off"
        String.concat
            Environment.NewLine
            [
                $"Caddis {Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString 3} on {Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"
                if s.IsRunning then $"Caddis is listening on {s.Url} , MCP endpoint: {s.McpUrl}" else "Caddis is stopped."
                $"F# session: {FsiState.toString s.Service.Fsi.State}, runs on: {ThreadMode.toString s.Service.Fsi.Mode} thread"
                $"Auto start: {autoStart}"
                $"Settings file: {s.Service.Store.SettingsPath}"
                $"Log file: {RevitLog.path}"
                // for when the panel can't be shown, where Set up runs them
                if s.IsRunning then
                    ""
                    "To add Caddis to Claude Code or Codex, run one of these in a terminal. Ctrl+C copies the text of this dialog."
                    s.ClaudeMcpCommand
                    s.CodexMcpCommand
            ]

    /// Starts the server. quiet: only log, e.g. at Revit's start.
    let start (quiet: bool) =
        withServer (fun s ->
            match s.Start() with
            | Ok url -> (if quiet then log else show) $"Caddis is listening on {url} , MCP endpoint: {url}/mcp"
            | Error msg ->
                let msg = $"Caddis could not start:{Environment.NewLine}{msg}"
                RevitLog.error msg
                if not quiet && ApiContext.isActive () then TaskDialog.Show("Message", msg) |> ignore)

    let stop () =
        withServer (fun s ->
            s.Stop()
            log "Caddis stopped.")

    /// Runs an action of the F# session in the background and logs how it ended.
    let private runAction (doneMessage: string) (action: unit -> Task) =
        task {
            try
                do! action ()
                log $"Caddis: {doneMessage}"
            with
            | BusyException runId -> log $"Caddis: run '{runId}' is still in progress, cancel it first (only possible in background mode)."
            | e -> RevitLog.error $"Caddis: {e.Message}"
        }
        |> ignore

    let setMode (m: ThreadMode) =
        withServer (fun s ->
            if s.Service.Fsi.Mode <> m then
                runAction $"scripts run on the {ThreadMode.toString m} thread now, a new F# session was started." (fun () -> s.Service.SetMode m))

    let reset () =
        withServer (fun s -> runAction "a new F# session was started." (fun () -> s.Service.Fsi.Reset()))

    let cancel () =
        withServer (fun s ->
            match s.Service.Fsi.Cancel None with
            | Ok msg
            | Error msg -> show $"Caddis: {msg}")

    let setAutoStart (on: bool) =
        withServer (fun s -> s.Service.UpdateSettings(fun x -> { x with autoStart = on }))

    /// Puts the text on the clipboard. False if that failed.
    /// Tries a few times: another program may have the clipboard open, and while the session is locked it can't be opened.
    let trySetClipboard (text: string) : bool =
        let rec attempt (n: int) =
            try
                Windows.Clipboard.SetText text
                true
            with :? Runtime.InteropServices.COMException when n > 1 ->
                Threading.Thread.Sleep 100
                attempt (n - 1)
        try attempt 5 with _ -> false

    /// The last items, at most n.
    let private lastOf (n: int) (items: 'T list) = items |> List.rev |> List.truncate n |> List.rev

    /// The info, the latest errors and the latest messages, in a dialog. Afterwards the Info button doesn't show the errors anymore.
    let showInfo () =
        withServer (fun s ->
            let errorCount = RevitLog.errors.Count
            RevitLog.errors.MarkSeen()
            let recent =
                match RevitLog.recentLines () with
                | [] -> "none"
                | lines -> lines |> lastOf 12 |> String.concat Environment.NewLine
            use d = new TaskDialog("Info", MainInstruction = "Caddis, F# Interactive for AI agents")
            match RevitLog.errors.Lines with
            | [] -> d.MainContent <- info s
            | errors ->
                // An error may have a stack trace: its first lines only, the log file has all.
                let shortened (e: string) =
                    let lines = e.Replace("\r\n", "\n").Split '\n'
                    if lines.Length <= 4 then e
                    else String.concat Environment.NewLine lines[..3] + Environment.NewLine + "   ... (more in the log file)"
                let shown = 5
                d.MainIcon <- TaskDialogIcon.TaskDialogIconError
                d.MainContent <-
                    String.concat Environment.NewLine [
                        info s
                        ""
                        if errorCount <= shown then "Errors:" else $"{errorCount} errors, the latest {shown}:"
                        yield! errors |> lastOf shown |> List.map shortened
                    ]
            d.ExpandedContent <- $"Latest messages:{Environment.NewLine}{recent}"
            d.Show() |> ignore)

    /// Cancels a background run and stops the server, when Revit closes.
    let shutdown () =
        match server with
        | Some s ->
            s.Service.Fsi.Current |> Option.iter (s.Service.Fsi.CancelRun >> ignore)
            s.Stop()
        | None -> ()
