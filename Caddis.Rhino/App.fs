namespace Caddis.Rhino

open System
open System.Threading.Tasks
open Caddis

/// The Caddis server of this Rhino process, and the actions used by the Caddis command and panel.
module App =

    let mutable server: CaddisServer option = None

    let private log (s: string) = RhinoLog.writeLine s

    let private withServer (f: CaddisServer -> unit) =
        match server with
        | Some s -> f s
        | None -> log "Caddis is not loaded."

    /// A few lines about the server and session. Without the token, the command history might be shared.
    let info (s: CaddisServer) =
        String.concat
            Environment.NewLine
            [
                if s.IsRunning then $"Caddis is listening on {s.Url} , MCP endpoint: {s.McpUrl}" else "Caddis is stopped."
                $"F# session: {FsiState.toString s.Service.Fsi.State}, runs on: {ThreadMode.toString s.Service.Fsi.Mode} thread"
                $"Token file: {s.TokenPath}"
            ]

    let start () =
        withServer (fun s ->
            match s.Start() with
            | Ok url -> log $"Caddis is listening on {url} , MCP endpoint: {url}/mcp"
            | Error msg -> log $"Caddis could not start:{Environment.NewLine}{msg}")

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
            | e -> log $"Caddis: {e.Message}"
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

    let setListenOnLan (on: bool) =
        withServer (fun s ->
            if s.Service.Settings.listenOnLan <> on then
                s.Service.UpdateSettings(fun x -> { x with listenOnLan = on })
                if s.IsRunning then log "Caddis: stop and start the server to apply the network setting.")

    let private copyToClipboard (text: string) =
        use clipboard = new Eto.Forms.Clipboard()
        clipboard.Text <- text

    let copyMcpCommand () =
        withServer (fun s ->
            if s.IsRunning then
                copyToClipboard s.ClaudeMcpCommand
                log "Caddis: copied the 'claude mcp add ...' command with the token to the clipboard."
            else
                log "Caddis: start the server first.")

    let copyToken () =
        withServer (fun s ->
            copyToClipboard s.Token
            log "Caddis: copied the token to the clipboard.")

    let newToken () =
        withServer (fun s ->
            s.NewToken() |> ignore
            log "Caddis: created a new token, the old one does not work anymore. Update your MCP client config.")

    /// Cancels a background run and stops the server, when Rhino closes.
    let shutdown () =
        match server with
        | Some s ->
            s.Service.Fsi.Current |> Option.iter (s.Service.Fsi.CancelRun >> ignore)
            s.Stop()
        | None -> ()
