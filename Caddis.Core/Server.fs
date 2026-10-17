namespace Caddis

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Caddis.Http

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

    let handle (ctx: HttpListenerContext) : Task =
        task {
            let r = ctx.Request
            let! resp =
                task {
                    try
                        if not (isNull r.Headers["Origin"]) then
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
                                if req.path.TrimEnd('/') = "/mcp" then return! mcp.Handle req
                                else return! Rest.handle svc serverInfo req
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
        }

    /// A backgroundTask, not a task: Start is called on the UI thread of the host,
    /// and a task would continue there after each request, so no request would be accepted while a run blocks the UI thread.
    let acceptLoop (l: HttpListener) =
        backgroundTask {
            while l.IsListening do
                try
                    let! ctx = l.GetContextAsync()
                    Task.Run(Func<Task>(fun () -> handle ctx)) |> ignore
                with _ ->
                    () // the listener was stopped
        }
        |> ignore

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

    /// Tries the port and the next ones until one is free.
    let rec tryListen (firstPort: int) (p: int) (attemptsLeft: int) : Result<HttpListener * int, string> =
        let l = new HttpListener()
        l.Prefixes.Add $"http://127.0.0.1:{p}/"
        l.Prefixes.Add $"http://localhost:{p}/"
        try
            l.Start()
            Ok(l, p)
        with e ->
            l.Close()
            if attemptsLeft > 1 then tryListen firstPort (p + 1) (attemptsLeft - 1)
            else Error $"Could not listen on any port from {firstPort} to {p}: {e.Message}"

    member _.Service = svc

    member _.IsRunning = listener.IsSome

    member _.Port = port

    /// e.g. http://127.0.0.1:7810
    member _.Url = url ()

    member _.McpUrl = url () + "/mcp"

    /// The command line to add this server to Claude Code for all projects of the current user.
    member this.ClaudeMcpCommand =
        $"claude mcp add --transport http --scope user {host.Info.mcpServerName} {this.McpUrl}"

    /// The command line to add this server to Codex, for all projects of the current user.
    member this.CodexMcpCommand =
        $"codex mcp add {host.Info.mcpServerName} --url {this.McpUrl}"

    /// Starts listening and creating the F# session. Returns the URL, or an error message.
    member this.Start() : Result<string, string> =
        lock sync (fun () ->
            match listener with
            | Some _ -> Ok(url ())
            | None ->
                let s = svc.Settings
                match tryListen s.port s.port 20 with
                | Error msg -> Error msg
                | Ok(l, p) ->
                    listener <- Some l
                    port <- p
                    acceptLoop l
                    writeDiscoveryFile ()
                    if p <> s.port then
                        // MCP clients are configured with a fixed URL, they would silently fail to connect
                        host.Log
                            $"Caddis: port {s.port} is in use, so Caddis listens on port {p}. MCP clients configured for port {s.port} won't reach this {host.Info.appName}, copy the new command via {host.Info.copyMcpCommandUi}."
                    svc.Fsi.Initialize() |> ignore
                    Ok(url ()))

    /// Stops listening. The F# session and its values are kept.
    member _.Stop() =
        lock sync (fun () ->
            match listener with
            | Some l ->
                listener <- None
                try
                    l.Stop()
                    l.Close()
                with _ ->
                    ()
                try File.Delete(discoveryPath ()) with _ -> ()
            | None -> ())

    interface IDisposable with
        member this.Dispose() = this.Stop()
