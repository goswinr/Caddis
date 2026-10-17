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

    /// A few lines about the server and session. Without the token.
    let info (s: CaddisServer) =
        let autoStart = if s.Service.Settings.autoStart then "on" else "off"
        String.concat
            Environment.NewLine
            [
                $"Caddis {Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString 3} on {Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"
                if s.IsRunning then $"Caddis is listening on {s.Url} , MCP endpoint: {s.McpUrl}" else "Caddis is stopped."
                $"F# session: {FsiState.toString s.Service.Fsi.State}, runs on: {ThreadMode.toString s.Service.Fsi.Mode} thread"
                $"Auto start: {autoStart}"
                $"Token file: {s.TokenPath}"
                $"Settings file: {s.Service.Store.SettingsPath}"
                $"Log file: {RevitLog.path}"
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

    /// Tries a few times: another program may have the clipboard open, and while the session is locked it can't be opened.
    let private trySetClipboard (text: string) : bool =
        let rec attempt (n: int) =
            try
                Windows.Clipboard.SetText text
                true
            with :? Runtime.InteropServices.COMException when n > 1 ->
                Threading.Thread.Sleep 100
                attempt (n - 1)
        try attempt 5 with _ -> false

    let copyMcpCommand () =
        withServer (fun s ->
            if not s.IsRunning then
                show "Start the server first."
            elif trySetClipboard s.ClaudeMcpCommand then
                show
                    $"Copied the command to add Caddis to Claude Code, with the token, to the clipboard:{Environment.NewLine}{Environment.NewLine}claude mcp add --transport http --scope user {RevitInfo.info.mcpServerName} {s.McpUrl} ..."
            elif ApiContext.isActive () then
                // not with show: the token stays out of the log
                log "Caddis: the clipboard could not be opened, the MCP command is shown in a dialog."
                TaskDialog.Show(
                    "Copy MCP Command",
                    $"The clipboard is in use by another program. Press Ctrl+C in this dialog to copy its text, the command is the last line:{Environment.NewLine}{Environment.NewLine}{s.ClaudeMcpCommand}"
                )
                |> ignore
            else
                log "Caddis: the clipboard is in use by another program. Try again, or use the ribbon button 'Copy MCP', which shows the command if the clipboard stays busy.")

    let copyToken () =
        withServer (fun s ->
            if trySetClipboard s.Token then log "Caddis: copied the token to the clipboard."
            else log "Caddis: the clipboard is in use by another program, try again.")

    let setListenOnLan (on: bool) =
        withServer (fun s ->
            if s.Service.Settings.listenOnLan <> on then
                s.Service.UpdateSettings(fun x -> { x with listenOnLan = on })
                if s.IsRunning then log "Caddis: stop and start the server to apply the network setting.")

    let newToken () =
        withServer (fun s ->
            s.NewToken() |> ignore
            show "Created a new token, the old one does not work anymore. Copy the MCP command again and update your MCP client.")

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
