namespace Caddis.AutoCAD

open System
open System.Threading.Tasks
open Caddis

/// The Caddis server of this AutoCAD process, and the actions of the CADDIS command.
module App =

    let mutable server: CaddisServer option = None

    let private log (s: string) = AutoCADLog.writeLine s

    let private withServer (f: CaddisServer -> unit) =
        match server with
        | Some s -> f s
        | None -> log "Caddis is not loaded."

    /// A few lines about the server and session.
    let info (s: CaddisServer) =
        String.concat
            "\n"
            [
                $"Caddis {s.Service.Version} on {Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"
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

    /// Cancels a background run and stops the server, when AutoCAD closes.
    let shutdown () =
        match server with
        | Some s ->
            s.Service.Fsi.Current |> Option.iter (s.Service.Fsi.CancelRun >> ignore)
            s.Stop()
        | None -> ()
