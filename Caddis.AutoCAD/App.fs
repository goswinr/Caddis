namespace Caddis.AutoCAD

open System
open System.Threading.Tasks
open Caddis

/// The Caddis server of this AutoCAD process, and the actions of the CADDIS command, the ribbon and the panel.
/// Messages go to the command line and the log file, see AutoCADLog.
module App =

    let mutable server: CaddisServer option = None

    let private log (s: string) = AutoCADLog.writeLine s

    /// Called on the UI thread when the panel refreshes, so that the ribbon shows changes made in the panel. Set at startup.
    let mutable stateChanged: unit -> unit = ignore

    let private withServer (f: CaddisServer -> unit) =
        match server with
        | Some s -> f s
        | None -> log "Caddis is not loaded."

    /// A few lines about the server and session.
    let info (s: CaddisServer) =
        String.concat
            "\n"
            [
                $"Caddis {Updates.version} on {Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"
                Updates.status ()
                if s.IsRunning then $"Caddis is listening on {s.Url} , MCP endpoint: {s.McpUrl}" else "Caddis is stopped."
                $"F# session: {FsiState.toString s.Service.Fsi.State}, runs on: {ThreadMode.toString s.Service.Fsi.Mode} thread"
                $"Settings file: {s.Service.Store.SettingsPath}"
                $"Log file: {AutoCADLog.path}"
            ]

    /// Starts the server. quiet: without the commands to add Caddis to the agents, e.g. at AutoCAD's start.
    let start (quiet: bool) =
        withServer (fun s ->
            match s.Start() with
            | Ok url ->
                String.concat
                    "\n"
                    [
                        $"Caddis is listening on {url} , MCP endpoint: {url}/mcp"
                        if not quiet then
                            "To add Caddis to Claude Code or Codex, run one of these in a terminal:"
                            s.ClaudeMcpCommand
                            s.CodexMcpCommand
                    ]
                |> log
            | Error msg -> AutoCADLog.error $"Caddis could not start:\n{msg}")

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
            | e -> AutoCADLog.error $"Caddis: {e.Message}"
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
            | Error msg -> log $"Caddis: {msg}")

    let setAutoStart (on: bool) =
        withServer (fun s -> s.Service.UpdateSettings(fun x -> { x with autoStart = on }))

    let setEcho (on: bool) =
        withServer (fun s -> s.Service.UpdateSettings(fun x -> { x with echoOutput = on }))

    /// Puts the text on the clipboard. False if that failed.
    /// Tries a few times: another program may have the clipboard open.
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

    /// The info, the latest errors and the latest messages, in AutoCAD's task dialog, for the ribbon's Info button.
    /// Afterwards the Info button doesn't show the errors anymore.
    let showInfo () =
        withServer (fun s ->
            let errorCount = AutoCADLog.errors.Count
            AutoCADLog.errors.MarkSeen()
            let recent =
                match AutoCADLog.recentLines () with
                | [] -> "none"
                | lines -> lines |> lastOf 12 |> String.concat Environment.NewLine
            let d =
                Autodesk.Windows.TaskDialog(
                    WindowTitle = "Caddis",
                    MainInstruction = "Caddis, F# Interactive for AI agents",
                    CommonButtons = Autodesk.Windows.TaskDialogCommonButtons.Close,
                    MainIcon = Autodesk.Windows.TaskDialogIcon.Information,
                    ExpandedText = $"Latest messages:{Environment.NewLine}{recent}",
                    AllowDialogCancellation = true,
                    // in dialog units, wide enough for the commands to add Caddis to the agents on one line each
                    Width = 330u
                )
            let lines =
                String.concat Environment.NewLine [
                    yield! (info s).Split '\n'
                    // for when the panel can't be shown, where Set up runs them
                    if s.IsRunning then
                        ""
                        "To add Caddis to Claude Code or Codex, run one of these in a terminal. Ctrl+C copies the text of this dialog."
                        s.ClaudeMcpCommand
                        s.CodexMcpCommand
                ]
            match AutoCADLog.errors.Lines with
            | [] -> d.ContentText <- lines
            | errors ->
                // An error may have a stack trace: its first lines only, the log file has all.
                let shortened (e: string) =
                    let lines = e.Replace("\r\n", "\n").Split '\n'
                    if lines.Length <= 4 then e
                    else String.concat Environment.NewLine lines[..3] + Environment.NewLine + "   ... (more in the log file)"
                let shown = 5
                d.MainIcon <- Autodesk.Windows.TaskDialogIcon.Error
                d.ContentText <-
                    String.concat Environment.NewLine [
                        lines
                        ""
                        if errorCount <= shown then "Errors:" else $"{errorCount} errors, the latest {shown}:"
                        yield! errors |> lastOf shown |> List.map shortened
                    ]
            d.Show(Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle) |> ignore)

    /// Cancels a background run and stops the server, when AutoCAD closes.
    let shutdown () =
        match server with
        | Some s ->
            s.Service.Fsi.Current |> Option.iter (s.Service.Fsi.CancelRun >> ignore)
            s.Stop()
        | None -> ()
