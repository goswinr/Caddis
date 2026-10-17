namespace Caddis

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Caddis.Http

/// What the Caddis on a port answered when asked to hand it over.
type internal ReleaseAnswer =
    /// It moved to another port, or is about to.
    | Released of holder: string
    /// It has a run or another request in progress: ask again later.
    | Busy of reason: string
    /// Nothing answered: no program listens there, or not one that speaks HTTP.
    | Free
    | Refused of reason: string

/// The HTTP server: owns the CaddisService and serves the REST and MCP endpoints.
/// It listens on localhost only. Requests from web pages, with an Origin header, are rejected.
type CaddisServer(host: ICaddisHost, store: SettingsStore) =

    do RunRegistry.DeleteStaleFolders()

    let runs = RunRegistry(RunRegistry.DefaultRootDir)
    let svc = CaddisService(host, store, runs)
    let mcp = McpEndpoint(svc)

    let sync = obj ()
    let mutable listener: HttpListener option = None
    let mutable port = 0

    /// The requests being handled. The port is handed over to another instance only while the request that asks for it is the only one.
    let mutable inFlight = 0

    /// While the port is handed over, see release: 0 not, 1 checking if idle, 2 handing over, then requests are answered with 503.
    let mutable releasing = 0

    /// e.g. "Revit (process 12345)", to tell instances of the app apart.
    let label = $"{host.Info.appName} (process {Environment.ProcessId})"

    let maxBodyBytes = 20L * 1024L * 1024L

    let tooLarge = Response.error 413 $"The request body is larger than {maxBodyBytes / 1024L / 1024L} MB."

    let discoveryPath () =
        Path.Combine(store.InstancesFolder, $"{Environment.ProcessId}.json")

    let url () = $"http://127.0.0.1:{port}"

    let serverInfo () : (string * JsonNode) list = [
        "server",
        Json.obj [
            "running", Json.ofBool listener.IsSome
            "url", Json.ofString (url ())
            "mcpUrl", Json.ofString (url () + "/mcp")
        ]
    ]

    let rec write (r: HttpListenerResponse) (resp: Response) : Task =
        task {
            match resp with
            | JsonResponse(status, body) ->
                let bytes = Encoding.UTF8.GetBytes(Json.toText body)
                r.StatusCode <- status
                r.ContentType <- "application/json; charset=utf-8"
                r.ContentLength64 <- int64 bytes.Length
                do! r.OutputStream.WriteAsync(bytes, 0, bytes.Length)
            | FileResponse(path, mime) ->
                use fs = File.OpenRead path
                r.StatusCode <- 200
                r.ContentType <- mime
                r.ContentLength64 <- fs.Length
                r.AddHeader("Content-Disposition", $"inline; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName path)}")
                do! fs.CopyToAsync r.OutputStream
            | EmptyResponse status ->
                r.StatusCode <- status
                r.ContentLength64 <- 0L
            | WithHeaders(inner, headers) ->
                for name, value in headers do
                    r.AddHeader(name, value)
                do! write r inner
        }

    let respond (ctx: HttpListenerContext) (resp: Response) : Task =
        task {
            try
                do! write ctx.Response resp
            finally
                ctx.Response.Close()
        }

    /// The body as UTF-8 text, or None if it is larger than maxBodyBytes.
    /// Reads at most that much, also for chunked requests that don't say their length up front.
    let readBody (r: HttpListenerRequest) : Task<string option> =
        task {
            use ms = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 81920
            let mutable reading = true
            let mutable tooLarge = false
            while reading do
                let! n = r.InputStream.ReadAsync(buffer, 0, buffer.Length)
                if n = 0 then
                    reading <- false
                elif ms.Length + int64 n > maxBodyBytes then
                    tooLarge <- true
                    reading <- false
                else
                    ms.Write(buffer, 0, n)
            if tooLarge then
                return None
            else
                let bytes = ms.GetBuffer()
                let bom = if ms.Length >= 3L && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then 3 else 0
                return Some(Encoding.UTF8.GetString(bytes, bom, int ms.Length - bom))
        }

    let enter () = Interlocked.Increment(&inFlight) |> ignore
    let leave () = Interlocked.Decrement(&inFlight) |> ignore
    let releaseState () = Volatile.Read(&releasing)

    /// A reason why the port can't be handed over now: a run, or a request other than the one that asks for it.
    let busyReason () =
        match svc.Fsi.Current with
        | Some r -> Some $"run '{r.Id}' is in progress"
        | None when Volatile.Read(&inFlight) > 1 -> Some "it is answering another request"
        | None -> None

    /// POST /release: another instance of this app takes over the port, once no run and no other request is in progress.
    /// Returns the response, and who takes the port if it is handed over: then this server moves to another port after the response.
    let release (req: Request) : Response * string option =
        let body = Json.parse req.body
        let by = Json.tryString "by" body |> Option.defaultValue "another app"
        let answer status (key: string) (text: string) =
            JsonResponse(status, Json.obj [ key, Json.ofString text; "host", Json.ofString label ])
        match Json.tryString "mcpServerName" body with
        | Some name when name = host.Info.mcpServerName ->
            let busy =
                match busyReason () with
                | Some reason -> Some reason
                | None ->
                    // Check again after holding back new requests: one that came in before is counted in inFlight.
                    Interlocked.Exchange(&releasing, 1) |> ignore
                    let reason = busyReason ()
                    Volatile.Write(&releasing, (if reason.IsSome then 0 else 2))
                    reason
            match busy with
            | Some reason -> answer 409 "error" reason, None
            | None -> answer 200 "message" $"Caddis in {label} hands port {port} over to {by}.", Some by
        | name ->
            let theirs = name |> Option.map (fun n -> $"'{n}'") |> Option.defaultValue "none"
            answer 403 "error" $"This is the MCP server '{host.Info.mcpServerName}' of {host.Info.appName}, not {theirs}.", None

    let writeDiscoveryFile () =
        try
            Directory.CreateDirectory store.InstancesFolder |> ignore
            for f in Directory.GetFiles(store.InstancesFolder, "*.json") do
                match Int32.TryParse(Path.GetFileNameWithoutExtension f) with
                | true, pid when pid <> Environment.ProcessId && not (Proc.isRunning pid) -> File.Delete f
                | _ -> ()
            let info =
                Json.obj [
                    "pid", Json.ofInt Environment.ProcessId
                    "port", Json.ofInt port
                    "url", Json.ofString (url ())
                    "mcpUrl", Json.ofString (url () + "/mcp")
                    "app", Json.ofString host.Info.appName
                    "mcpServerName", Json.ofString host.Info.mcpServerName
                    "host", Json.ofString host.Description
                    "startedAt", Json.ofString (DateTime.UtcNow.ToString "o")
                ]
            File.WriteAllText(discoveryPath (), Json.toText info)
        with e ->
            host.LogError $"Caddis: writing the discovery file failed: {e.Message}"

    let deleteDiscoveryFile () =
        try File.Delete(discoveryPath ()) with _ -> ()

    let listenOn (p: int) : Result<HttpListener, string> =
        let l = new HttpListener()
        l.Prefixes.Add $"http://127.0.0.1:{p}/"
        l.Prefixes.Add $"http://localhost:{p}/"
        try
            l.Start()
            Ok l
        with e ->
            l.Close()
            Error e.Message

    let close (l: HttpListener) =
        try
            l.Stop()
            l.Close()
        with _ ->
            ()

    /// Listens on the first free one of 20 ports from firstPort, except the one to skip.
    let tryListen (firstPort: int) (skip: int) : Result<HttpListener * int, string> =
        let rec attempt (p: int) (attemptsLeft: int) (lastError: string) =
            if attemptsLeft = 0 then Error $"Could not listen on any port from {firstPort} to {p - 1}: {lastError}"
            elif p = skip then attempt (p + 1) attemptsLeft lastError
            else
                match listenOn p with
                | Ok l -> Ok(l, p)
                | Error e -> attempt (p + 1) (attemptsLeft - 1) e
        attempt firstPort 20 ""

    /// A backgroundTask, not a task: Start is called on the UI thread of the host,
    /// and a task would continue there after each request, so no request would be accepted while a run blocks the UI thread.
    let rec acceptLoop (l: HttpListener) =
        backgroundTask {
            while l.IsListening do
                try
                    let! ctx = l.GetContextAsync()
                    Task.Run(Func<Task>(fun () -> handle ctx)) |> ignore
                with _ ->
                    () // the listener was stopped
        }
        |> ignore

    /// After handing the port over: listens on the next free port from the one of the settings, but not the one handed over.
    and moveAway (by: string) =
        lock sync (fun () ->
            match listener with
            | Some old ->
                let handedOver = port
                match tryListen svc.Settings.port handedOver with
                | Ok(l, p) ->
                    close old
                    listener <- Some l
                    port <- p
                    acceptLoop l
                    writeDiscoveryFile ()
                    host.Log
                        $"Caddis: {by} took over port {handedOver}, where AI agents connect. This {host.Info.appName} listens on port {p} now, click 'Activate' in the Caddis panel to connect the agents to it again."
                | Error msg ->
                    close old
                    listener <- None
                    deleteDiscoveryFile ()
                    host.Log $"Caddis: {by} took over port {handedOver}. This {host.Info.appName} stopped listening: {msg}"
            | None -> ())
        Volatile.Write(&releasing, 0)

    and handle (ctx: HttpListenerContext) : Task =
        task {
            enter ()
            try
                let r = ctx.Request
                let handOverTo = ref None
                // while release checks if it is idle, for a moment
                let waitUntil = DateTime.UtcNow.AddMilliseconds 100.0
                while releaseState () = 1 && DateTime.UtcNow < waitUntil do
                    do! Task.Delay 1
                let! resp =
                    task {
                        try
                            if releaseState () <> 0 then
                                return
                                    Response.error 503 $"Caddis is handing this port over to another {host.Info.appName}, try again in a moment."
                            elif not (isNull r.Headers["Origin"]) then
                                return Response.error 403 "Requests from web pages (with an Origin header) are not allowed."
                            elif r.ContentLength64 > maxBodyBytes then
                                return tooLarge
                            else
                                match! readBody r with
                                | None -> return tooLarge
                                | Some body ->
                                    let req = {
                                        method = r.HttpMethod.ToUpperInvariant()
                                        path = r.Url.AbsolutePath
                                        query = r.QueryString
                                        headers = r.Headers
                                        body = body
                                        baseUrl = r.Url.GetLeftPart(UriPartial.Authority)
                                    }
                                    let path = req.path.TrimEnd('/')
                                    if path = "/mcp" then
                                        return! mcp.Handle req
                                    elif path = "/release" && req.method = "POST" then
                                        let resp, by = release req
                                        handOverTo.Value <- by
                                        return resp
                                    else
                                        return! Rest.handle svc serverInfo req
                        with
                        | BusyException runId ->
                            return
                                JsonResponse(
                                    409,
                                    Json.obj [
                                        "error", Json.ofString $"Run '{runId}' is still in progress."
                                        "runId", Json.ofString runId
                                    ]
                                )
                        | :? ArgumentException as e -> return Response.error 400 e.Message
                        | :? FormatException as e -> return Response.error 400 e.Message
                        | :? JsonException as e -> return Response.error 400 $"Invalid JSON: {e.Message}"
                        | Failure msg -> return Response.error 400 msg // from failwith, e.g. 'View not found'
                        | e -> return Response.error 500 $"{e.GetType().Name}: {e.Message}"
                    }
                try
                    do! respond ctx resp
                with
                | :? HttpListenerException
                | :? IOException
                | :? ObjectDisposedException -> () // the client disconnected
                | e -> host.LogError $"Caddis: writing the response to {r.HttpMethod} {r.Url.AbsolutePath} failed: {e}"
                // after the response: closing the listener would abort it
                handOverTo.Value |> Option.iter moveAway
            finally
                leave ()
        }

    /// Asks the Caddis that listens on the port to hand it over to this one.
    let askToRelease (target: int) (ct: CancellationToken) : Task<ReleaseAnswer> =
        task {
            use client = new HttpClient(Timeout = TimeSpan.FromSeconds 10.0)
            let body = Json.obj [ "mcpServerName", Json.ofString host.Info.mcpServerName; "by", Json.ofString label ]
            use content = new StringContent(Json.toText body, Encoding.UTF8, "application/json")
            try
                use! resp = client.PostAsync($"http://127.0.0.1:{target}/release", content, ct)
                let! text = resp.Content.ReadAsStringAsync()
                let json =
                    try Json.parse text
                    with _ -> null
                let holder = Json.tryString "host" json |> Option.defaultValue $"the program on port {target}"
                let error = Json.tryString "error" json |> Option.defaultValue $"HTTP status {int resp.StatusCode}"
                match int resp.StatusCode with
                | 200 -> return Released holder
                | 409 -> return Busy $"Caddis in {holder} is not idle: {error}."
                | 404 -> return Refused $"Port {target} is used by another program, or by a Caddis that can't hand its port over."
                | _ -> return Refused $"Caddis in {holder} can't hand over port {target}: {error}"
            with
            | :? HttpRequestException -> return Free // nothing listens there, or no HTTP server
            | :? TaskCanceledException when not ct.IsCancellationRequested -> return Refused $"The program on port {target} doesn't answer."
        }

    /// Listens on this port instead of the current one. Tries a few times, 100 ms apart, while another instance closes it.
    let switchTo (target: int) (tries: int) : Task<Result<unit, string>> =
        let attempt () =
            lock sync (fun () ->
                match listener with
                | None -> Some(Error "The server was stopped.")
                | Some _ when port = target -> Some(Ok())
                | Some old ->
                    match listenOn target with
                    | Ok l ->
                        close old
                        listener <- Some l
                        port <- target
                        acceptLoop l
                        writeDiscoveryFile ()
                        Some(Ok())
                    | Error _ -> None)
        task {
            let mutable result = attempt ()
            let mutable left = tries - 1
            while result.IsNone && left > 0 do
                do! Task.Delay 100
                result <- attempt ()
                left <- left - 1
            return result |> Option.defaultValue (Error $"Port {target} is used by another program.")
        }

    member _.Service = svc

    member _.IsRunning = listener.IsSome

    member _.Port = port

    /// e.g. http://127.0.0.1:7810
    member _.Url = url ()

    member _.McpUrl = url () + "/mcp"

    /// e.g. "Revit (process 12345)", to tell instances of the app apart.
    member _.Label = label

    /// Reads the agents' config files. Tests set one that reads other files.
    member val AgentConfigs = AgentConfigs() with get, set

    /// The command line that adds this server to the agent, for all projects of the current user.
    member this.AgentCommand(agent: McpAgent) =
        McpAgent.commandLine agent (McpAgent.addArgs agent host.Info.mcpServerName this.McpUrl)

    /// The command line to add this server to Claude Code for all projects of the current user.
    member this.ClaudeMcpCommand = this.AgentCommand ClaudeCode

    /// The command line to add this server to Codex, for all projects of the current user.
    member this.CodexMcpCommand = this.AgentCommand Codex

    /// Whether the agent connects to this server. Never active while it is stopped.
    member this.AgentState(agent: McpAgent) : AgentState =
        this.AgentConfigs.State(agent, host.Info.mcpServerName, (if this.IsRunning then port else 0))

    /// The other running Caddis that listens on the port, from the discovery files: e.g. "Revit (process 12345)",
    /// and the title of its main window, which names the open document in Rhino and Revit on Windows. None if there is none.
    member _.InstanceOnPort(p: int) : (string * string) option =
        try
            Directory.GetFiles(store.InstancesFolder, "*.json")
            |> Array.tryPick (fun f ->
                try
                    let n = Json.parse (File.ReadAllText f)
                    match Json.tryInt "pid" n, Json.tryInt "port" n with
                    | Some pid, Some filePort when filePort = p && pid <> Environment.ProcessId && Proc.isRunning pid ->
                        let app = Json.tryString "app" n |> Option.defaultValue "Caddis"
                        let title =
                            try
                                use proc = Process.GetProcessById pid
                                proc.MainWindowTitle
                            with _ ->
                                ""
                        Some($"{app} (process {pid})", title)
                    | _ -> None
                with _ ->
                    None)
        with _ ->
            None

    /// Opens a file with the app that the system has for it, e.g. an .mcpb file with Claude Desktop. Tests set one that doesn't.
    member val OpenFile: string -> unit = (fun path -> Process.Start(ProcessStartInfo(path, UseShellExecute = true)) |> ignore) with get, set

    /// The extension of Claude Desktop for this server, with its port, and its instructions and tools for while it isn't running.
    member this.DesktopBundle() : DesktopExtension.Bundle = {
        serverName = host.Info.mcpServerName
        appName = host.Info.appName
        port = port
        version = svc.Version
        platforms = [ (if OperatingSystem.IsMacOS() then "darwin" else "win32") ]
        instructions = host.Info.mcp.instructions
        tools = mcp.Tools
    }

    /// Adds this server to the agent for all projects of the user with the agent's program, e.g. 'claude mcp add ...'.
    /// Replaces the agent's server of the same name.
    /// For Claude Desktop it writes the extension and opens it, Claude Desktop asks the person to install it.
    member this.SetUpAgent(agent: McpAgent) : Task<Result<string, string>> =
        task {
            let name = McpAgent.name agent
            let serverName = host.Info.mcpServerName
            if not this.IsRunning then
                return Error "Start the server first."
            else
                match McpAgent.program agent with
                | None ->
                    try
                        let path = Path.Combine(runs.RootDir, $"{serverName}.mcpb")
                        DesktopExtension.writeBundle (this.DesktopBundle()) path
                        this.OpenFile path
                        return
                            Ok
                                $"Opened the extension {serverName}.mcpb, Claude Desktop asks to install it. It relays to this {host.Info.appName} at {this.McpUrl}."
                    with e ->
                        return Error $"Could not open the extension of {name}: {e.Message}"
                | Some program ->
                    match AgentCli.find program with
                    | None ->
                        return
                            Error
                                $"{name} was not found, there is no program '{program}' on PATH. Install {name}, or copy the command with a right-click on the button and run it in a terminal."
                    | Some exe ->
                        let url = this.McpUrl
                        let args = McpAgent.addArgs agent serverName url
                        try
                            if agent = ClaudeCode && this.AgentConfigs.RegisteredUrl(agent, serverName).IsSome then
                                let! _ = AgentCli.run exe (McpAgent.removeArgs agent serverName)
                                ()
                            let! code, output = AgentCli.run exe args
                            if code = 0 then
                                return
                                    Ok
                                        $"{name} connects to this {host.Info.appName} now, at {url}. Sessions of {name} that were open before see it after a restart."
                            else
                                return Error $"'{McpAgent.commandLine agent args}' failed: {output}"
                        with e ->
                            return Error $"Could not run '{exe}': {e.Message}"
        }

    /// Listens on the port instead of the current one. If another Caddis of this app listens there, it hands the port over once
    /// no run and no other request is in progress; until then onWaiting is called with the reason, about every second.
    /// Error if the port can't be taken, e.g. another program listens there. Cancelling raises an OperationCanceledException.
    member this.TakeOverPort(target: int, onWaiting: string -> unit, ct: CancellationToken) : Task<Result<string, string>> =
        task {
            let mutable answer = None
            while answer.IsNone do
                ct.ThrowIfCancellationRequested()
                if not this.IsRunning then
                    answer <- Some(Refused "Start the server first.")
                elif port = target then
                    answer <- Some Free
                else
                    match! askToRelease target ct with
                    | Busy reason ->
                        onWaiting reason
                        do! Task.Delay(1000, ct)
                    | a -> answer <- Some a
            match answer with
            | Some(Refused reason) -> return Error reason
            | a ->
                let from =
                    match a with
                    | Some(Released holder) -> Some holder
                    | _ -> None
                match! switchTo target (if from.IsSome then 30 else 1) with
                | Error e -> return Error e
                | Ok() ->
                    let handedOver = from |> Option.map (fun h -> $", Caddis in {h} handed it over") |> Option.defaultValue ""
                    return Ok $"Caddis listens on port {target} now{handedOver}. AI agents set up for this port reach this {host.Info.appName}."
        }

    /// Makes the agent connect to this server. If the agent has the URL of a Caddis on this computer, this server takes over its port,
    /// from the other instance of the app once it is idle, see TakeOverPort, and the agent keeps its URL.
    /// Otherwise, or if the port can't be taken, the agent is set up with the URL of this server.
    member this.ActivateAgent(agent: McpAgent, onWaiting: string -> unit, ct: CancellationToken) : Task<Result<string, string>> =
        task {
            match this.AgentState agent with
            | AgentState.Active -> return Ok $"{McpAgent.name agent} connects to this {host.Info.appName} already."
            | AgentState.NotSetUp -> return! this.SetUpAgent agent
            | AgentState.Inactive registered ->
                match McpAgent.localPort registered with
                | None -> return! this.SetUpAgent agent
                | Some p ->
                    match! this.TakeOverPort(p, onWaiting, ct) with
                    | Ok msg -> return Ok msg
                    | Error why when not this.IsRunning -> return Error why
                    | Error why ->
                        match! this.SetUpAgent agent with
                        | Ok msg -> return Ok $"{why} {msg}"
                        | Error e -> return Error $"{why} {e}"
        }

    /// Starts listening and creating the F# session. Returns the URL, or an error message.
    member this.Start() : Result<string, string> =
        lock sync (fun () ->
            match listener with
            | Some _ -> Ok(url ())
            | None ->
                let s = svc.Settings
                match tryListen s.port -1 with
                | Error msg -> Error msg
                | Ok(l, p) ->
                    listener <- Some l
                    port <- p
                    acceptLoop l
                    writeDiscoveryFile ()
                    if p <> s.port then
                        // MCP clients are configured with a fixed URL, they would silently fail to connect
                        host.Log
                            $"Caddis: port {s.port} is in use, so Caddis listens on port {p}. AI agents set up for port {s.port} don't reach this {host.Info.appName}, click 'Activate' in the Caddis panel to connect them to it."
                    svc.Fsi.Initialize() |> ignore
                    Ok(url ()))

    /// Stops listening. The F# session and its values are kept.
    member _.Stop() =
        lock sync (fun () ->
            match listener with
            | Some l ->
                listener <- None
                close l
                deleteDiscoveryFile ()
            | None -> ())

    interface IDisposable with
        member this.Dispose() = this.Stop()
