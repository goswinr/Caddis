namespace Caddis

open System
open System.IO
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Caddis.Http

/// The HTTP server: owns the CaddisService and serves the REST and MCP endpoints.
/// Every request needs the header 'Authorization: Bearer <token>'. Requests from web pages, with an Origin header, are rejected.
type CaddisServer(host: ICaddisHost, store: SettingsStore) =

    do RunRegistry.DeleteStaleFolders()

    let runs = RunRegistry(RunRegistry.DefaultRootDir)
    let svc = CaddisService(host, store, runs)
    let mcp = McpEndpoint(svc)

    let sync = obj ()
    let mutable listener: HttpListener option = None
    let mutable port = 0
    let mutable listenOnLan = false
    let mutable token = store.GetOrCreateToken()

    let maxBodyBytes = 20L * 1024L * 1024L

    let discoveryPath () =
        Path.Combine(store.InstancesFolder, $"{Environment.ProcessId}.json")

    let url () =
        if listenOnLan then $"http://{Dns.GetHostName()}:{port}" else $"http://127.0.0.1:{port}"

    let serverInfo () : (string * JsonNode) list = [
        "server",
        Json.obj [
            "running", Json.ofBool listener.IsSome
            "url", Json.ofString (url ())
            "mcpUrl", Json.ofString (url () + "/mcp")
            "listenOnLan", Json.ofBool listenOnLan
            "tokenFile", Json.ofString store.TokenPath
        ]
    ]

    let isAuthorized (r: HttpListenerRequest) =
        match r.Headers["Authorization"] with
        | null -> false
        | h when h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
            let given = Encoding.UTF8.GetBytes(h.Substring(7).Trim())
            let expected = Encoding.UTF8.GetBytes token
            CryptographicOperations.FixedTimeEquals(ReadOnlySpan<byte>(given), ReadOnlySpan<byte>(expected))
        | _ -> false

    let respond (ctx: HttpListenerContext) (resp: Response) : Task =
        task {
            let r = ctx.Response
            try
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
            finally
                r.Close()
        }

    let handle (ctx: HttpListenerContext) : Task =
        task {
            let r = ctx.Request
            let! resp =
                task {
                    try
                        if not (isNull r.Headers["Origin"]) then
                            return Response.error 403 "Requests from web pages (with an Origin header) are not allowed."
                        elif not (isAuthorized r) then
                            return Response.error 401 $"Missing or wrong token. Send the header 'Authorization: Bearer <token>'. The token is in {store.TokenPath}, the Rhino command 'Caddis' shows it too."
                        elif r.ContentLength64 > maxBodyBytes then
                            return Response.error 413 "The request body is too large."
                        else
                            use reader = new StreamReader(r.InputStream, Encoding.UTF8)
                            let! body = reader.ReadToEndAsync()
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
            with _ ->
                () // the client disconnected
        }

    let acceptLoop (l: HttpListener) =
        task {
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
                    "listenOnLan", Json.ofBool listenOnLan
                    "host", Json.ofString host.Description
                    "startedAt", Json.ofString (DateTime.UtcNow.ToString "o")
                    "tokenFile", Json.ofString store.TokenPath
                ]
            File.WriteAllText(discoveryPath (), Json.toText info)
        with e ->
            host.Log $"Caddis: writing the discovery file failed: {e.Message}"

    /// Tries the port and the next ones until one is free.
    let rec tryListen (lan: bool) (firstPort: int) (p: int) (attemptsLeft: int) : Result<HttpListener * int, string> =
        let l = new HttpListener()
        if lan then
            l.Prefixes.Add $"http://+:{p}/"
        else
            l.Prefixes.Add $"http://127.0.0.1:{p}/"
            l.Prefixes.Add $"http://localhost:{p}/"
        try
            l.Start()
            Ok(l, p)
        with
        | :? HttpListenerException as e when lan && e.ErrorCode = 5 -> // access denied
            l.Close()
            Error(
                String.concat "\n" [
                    "Listening on the network needs a one-time permission from an administrator."
                    "Run this in a terminal as administrator, then start Caddis again:"
                    $"netsh http add urlacl url=http://+:{p}/ user=\"{Environment.UserDomainName}\\{Environment.UserName}\""
                ]
            )
        | e ->
            l.Close()
            if attemptsLeft > 1 then tryListen lan firstPort (p + 1) (attemptsLeft - 1)
            else Error $"Could not listen on any port from {firstPort} to {p}: {e.Message}"

    member _.Service = svc

    member _.IsRunning = listener.IsSome

    member _.Port = port

    /// e.g. http://127.0.0.1:7810
    member _.Url = url ()

    member _.McpUrl = url () + "/mcp"

    member _.Token = token

    member _.TokenPath = store.TokenPath

    /// The command line to add this server to Claude Code for all projects of the current user.
    member this.ClaudeMcpCommand =
        $"claude mcp add --transport http --scope user caddis {this.McpUrl} --header \"Authorization: Bearer {token}\""

    /// Starts listening and creating the F# session. Returns the URL, or an error message.
    member this.Start() : Result<string, string> =
        lock sync (fun () ->
            match listener with
            | Some _ -> Ok(url ())
            | None ->
                let s = svc.Settings
                match tryListen s.listenOnLan s.port s.port 20 with
                | Error msg -> Error msg
                | Ok(l, p) ->
                    listener <- Some l
                    port <- p
                    listenOnLan <- s.listenOnLan
                    acceptLoop l
                    writeDiscoveryFile ()
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

    /// Creates and saves a new token, the old one stops working.
    member _.NewToken() =
        token <- store.NewToken()
        token

    interface IDisposable with
        member this.Dispose() = this.Stop()
