module Caddis.Tests.HttpTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Caddis
open Caddis.Tests.Helpers

let server =
    lazy
        (let store = newStore ()
         store.Save { CaddisSettings.defaults with port = 27810 }
         let s = new CaddisServer(new HeadlessHost(ignore), store)
         match s.Start() with
         | Ok _ -> s
         | Error msg -> failwith msg)

let client =
    lazy
        (let c = new HttpClient(BaseAddress = Uri server.Value.Url)
         c.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", server.Value.Token)
         c)

let send (c: HttpClient) (req: HttpRequestMessage) =
    let resp = c.Send req
    let body = resp.Content.ReadAsStringAsync().Result
    resp.StatusCode, body

let post (path: string) (json: string) =
    use req = new HttpRequestMessage(HttpMethod.Post, path, Content = new StringContent(json, Encoding.UTF8, "application/json"))
    send client.Value req

/// Sends a JSON-RPC message to /mcp with the given MCP-Protocol-Version header, none if it is null.
let mcpWithVersion (version: string) (json: string) =
    use req = new HttpRequestMessage(HttpMethod.Post, "/mcp", Content = new StringContent(json, Encoding.UTF8, "application/json"))
    if not (isNull version) then req.Headers.Add("MCP-Protocol-Version", version)
    req.Headers.Accept.ParseAdd "application/json, text/event-stream"
    let resp = client.Value.Send req
    resp.StatusCode, resp.Content.ReadAsStringAsync().Result, resp.Headers

let mcp (json: string) =
    let status, body, _ = mcpWithVersion "2025-06-18" json
    status, body

/// Follows a path of property names, e.g. at ["result"; "tools"]
let at (path: string list) (n: JsonNode) = path |> List.fold (fun (n: JsonNode) (k: string) -> n[k]) n
let str path n = (at path n).GetValue<string>()
let num path n = (at path n).GetValue<int>()
let flag path n = (at path n).GetValue<bool>()

/// Calls an MCP tool and returns the result.
let callTool (name: string) (arguments: string) =
    let _, body = mcp $"""{{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{"name":"{name}","arguments":{arguments}}}}}"""
    at [ "result" ] (JsonNode.Parse body)

/// The text items of a tool result.
let resultText (result: JsonNode) =
    result["content"].AsArray() |> Seq.filter (fun c -> str [ "type" ] c = "text") |> Seq.map (str [ "text" ]) |> String.concat "\n"

[<Fact>]
let ``rejects requests without token`` () =
    use c = new HttpClient(BaseAddress = Uri server.Value.Url)
    use req = new HttpRequestMessage(HttpMethod.Get, "/status")
    let status, _ = send c req
    Assert.Equal(HttpStatusCode.Unauthorized, status)

[<Fact>]
let ``rotating the shared token immediately changes authentication in every server`` () =
    let firstStore = newStore ()
    let secondStore = SettingsStore(firstStore.Folder, "settings-revit.json", { CaddisSettings.defaults with port = 27920 })
    firstStore.Save { CaddisSettings.defaults with port = 27920 }
    use firstHost = new HeadlessHost(ignore)
    use secondHost = new HeadlessHost(ignore)
    use first = new CaddisServer(firstHost, firstStore)
    use second = new CaddisServer(secondHost, secondStore)
    for s in [ first; second ] do
        match s.Start() with Ok _ -> () | Error msg -> failwith msg
    // Finish session initialization before another test starts redirecting Console.Out.
    first.Service.Fsi.Initialize().Wait()
    second.Service.Fsi.Initialize().Wait()
    let request (s: CaddisServer) token =
        use c = new HttpClient(BaseAddress = Uri s.Url)
        use req = new HttpRequestMessage(HttpMethod.Post, "/mcp", Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""))
        req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
        use response = c.Send req
        response.StatusCode
    let old = first.Token
    Assert.Equal(old, second.Token)
    let fresh = first.NewToken()
    for s in [ first; second ] do
        Assert.Equal(HttpStatusCode.Unauthorized, request s old)
        Assert.Equal(HttpStatusCode.OK, request s fresh)
        Assert.Equal(fresh, s.Token)
        Assert.Contains(fresh, s.ClaudeMcpCommand)
    // Rotation can originate from either host.
    let newest = second.NewToken()
    Assert.Equal(HttpStatusCode.Unauthorized, request first fresh)
    Assert.Equal(HttpStatusCode.OK, request first newest)
    IO.File.WriteAllText(first.TokenPath, "invalid")
    Assert.Equal(HttpStatusCode.Unauthorized, request first newest)
    Assert.Equal(HttpStatusCode.Unauthorized, request second newest)
    IO.File.Delete first.TokenPath
    Assert.Equal(HttpStatusCode.Unauthorized, request second newest)
    Assert.False(IO.File.Exists first.TokenPath, "Authentication must not silently regenerate a missing token.")

[<Fact>]
let ``rejects requests from web pages`` () =
    use req = new HttpRequestMessage(HttpMethod.Get, "/status")
    req.Headers.Add("Origin", "http://evil.example")
    let status, _ = send client.Value req
    Assert.Equal(HttpStatusCode.Forbidden, status)

[<Fact>]
let ``serves status on localhost too`` () =
    use c = new HttpClient(BaseAddress = Uri $"http://localhost:{server.Value.Port}")
    c.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", server.Value.Token)
    use req = new HttpRequestMessage(HttpMethod.Get, "/status")
    let status, body = send c req
    Assert.Equal(HttpStatusCode.OK, status)
    Assert.Equal("Caddis", str [ "name" ] (JsonNode.Parse body))

[<Fact>]
let ``evaluates via REST and serves its files`` () =
    let status, body = post "/eval" """{"code": "System.IO.File.WriteAllText(System.IO.Path.Combine(Caddis.Script.OutDir, \"b.txt\"), \"content\")\n3 * 3"}"""
    Assert.Equal(HttpStatusCode.OK, status)
    let run = JsonNode.Parse body
    Assert.Equal("ok", str [ "status" ] run)
    Assert.Equal("9", str [ "value"; "text" ] run)
    let url = str [ "url" ] (run["files"].AsArray()[0])
    use req = new HttpRequestMessage(HttpMethod.Get, url)
    let fileStatus, content = send client.Value req
    Assert.Equal(HttpStatusCode.OK, fileStatus)
    Assert.Equal("content", content)

[<Fact>]
let ``returns 400 for missing code`` () =
    let status, _ = post "/eval" "{}"
    Assert.Equal(HttpStatusCode.BadRequest, status)

[<Fact>]
let ``returns 400 with a plain message when the host fails`` () =
    use req = new HttpRequestMessage(HttpMethod.Get, "/viewport")
    let status, body = send client.Value req
    Assert.Equal(HttpStatusCode.BadRequest, status)
    Assert.Equal("Capturing a viewport needs a host app like Rhino or Revit.", str [ "error" ] (JsonNode.Parse body))

[<Fact>]
let ``MCP initialize negotiates the protocol version`` () =
    let status, body = mcp """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""
    Assert.Equal(HttpStatusCode.OK, status)
    let result = at [ "result" ] (JsonNode.Parse body)
    Assert.Equal("2025-06-18", str [ "protocolVersion" ] result)
    Assert.Equal("caddis", str [ "serverInfo"; "name" ] result)

[<Fact>]
let ``MCP notifications get 202`` () =
    let status, body = mcp """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
    Assert.Equal(HttpStatusCode.Accepted, status)
    Assert.Equal("", body)

[<Fact>]
let ``MCP lists the tools`` () =
    mcp """{"jsonrpc":"2.0","id":"a","method":"tools/list"}""" |> ignore // the second call must work too
    let _, body = mcp """{"jsonrpc":"2.0","id":"b","method":"tools/list"}"""
    let names = (at [ "result"; "tools" ] (JsonNode.Parse body)).AsArray() |> Seq.map (str [ "name" ])
    Assert.Contains("caddis_eval", names)
    Assert.Contains("caddis_command", names)
    Assert.Contains("caddis_capture_viewport", names)
    Assert.DoesNotContain("caddis_dialog", names) // the headless host has no dialogs

[<Fact>]
let ``MCP commands without Rhino are a tool error`` () =
    let result = callTool "caddis_command" """{"commands":["-_Line 0,0,0 10,0,0"]}"""
    Assert.True(flag [ "isError" ] result)
    let text = resultText result
    Assert.Matches(@"^Run c\d+: runtimeError", text)
    Assert.Contains("Running commands needs a host app like Rhino.", text)

[<Fact>]
let ``REST runs a single command given as string`` () =
    let status, body = post "/command" """{"commands": "_Zoom _All _Extents"}"""
    Assert.Equal(HttpStatusCode.OK, status)
    let run = JsonNode.Parse body
    Assert.Equal("commands", str [ "kind" ] run)
    Assert.Equal("Running commands needs a host app like Rhino.", str [ "message" ] run)
    Assert.Equal("_Zoom _All _Extents", server.Value.Service.Runs.TryGet(str [ "id" ] run).Value.Code)

[<Fact>]
let ``REST commands with an invalid id to select is a bad request`` () =
    let status, body = post "/command" """{"commands": ["-_Loft"], "select": ["nope"]}"""
    Assert.Equal(HttpStatusCode.BadRequest, status)
    Assert.Contains("'nope' in 'select'", body)

[<Fact>]
let ``MCP evaluates code`` () =
    let _, body = mcp """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"caddis_eval","arguments":{"code":"printfn \"from mcp\"\n40 + 2"}}}"""
    let msg = JsonNode.Parse body
    Assert.Equal(7, num [ "id" ] msg)
    let result = at [ "result" ] msg
    Assert.False(flag [ "isError" ] result)
    Assert.Null(result["structuredContent"]) // Claude Code would show the model only that, see McpView
    let text = resultText result
    Assert.Contains("val it: int = 42", text)
    Assert.Contains("--- stdout ---\nfrom mcp", text)

[<Fact>]
let ``MCP reports failed runs as tool errors`` () =
    let result = callTool "caddis_eval" """{"code":"let x: int = \"no\""}"""
    Assert.True(flag [ "isError" ] result)
    Assert.Contains("compileError", resultText result)

[<Fact>]
let ``MCP unknown method is an error`` () =
    let _, body = mcp """{"jsonrpc":"2.0","id":9,"method":"resources/list"}"""
    Assert.Equal(-32601, num [ "error"; "code" ] (JsonNode.Parse body))

[<Fact>]
let ``MCP unknown tool is an invalid params error`` () =
    let _, body = mcp """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"caddis_nope","arguments":{}}}"""
    Assert.Equal(-32602, num [ "error"; "code" ] (JsonNode.Parse body))

[<Fact>]
let ``MCP initialize falls back to the newest version and hands out a session id`` () =
    let status, body, headers =
        mcpWithVersion null """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""
    Assert.Equal(HttpStatusCode.OK, status)
    Assert.Equal("2025-11-25", str [ "result"; "protocolVersion" ] (JsonNode.Parse body))
    Assert.Matches("^[0-9a-f]{32}$", Assert.Single(headers.GetValues "Mcp-Session-Id"))

[<Fact>]
let ``MCP rejects an unsupported protocol version header`` () =
    let status, body, _ = mcpWithVersion "1999-01-01" """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""
    Assert.Equal(HttpStatusCode.BadRequest, status)
    Assert.Contains("Unsupported MCP-Protocol-Version", body)

[<Fact>]
let ``MCP eval returns a running run and get_run waits for it`` () =
    let running = callTool "caddis_eval" """{"code":"System.Threading.Thread.Sleep 1500\n5","waitMs":100}"""
    Assert.False(flag [ "isError" ] running)
    let runId = Regex.Match(resultText running, @"^Run (r\d+): still running").Groups[1].Value
    Assert.False(String.IsNullOrEmpty runId, resultText running)
    // the run blocks the UI thread, so a capture has to wait
    let capture = callTool "caddis_capture_viewport" "{}"
    Assert.True(flag [ "isError" ] capture)
    Assert.Contains($"Run '{runId}' is still in progress", resultText capture)
    let finished = resultText (callTool "caddis_get_run" $"""{{"runId":"{runId}","waitMs":20000}}""")
    Assert.Contains($"Run {runId}: ok", finished)
    Assert.Contains("val it: int = 5", finished)

[<Fact>]
let ``MCP returns images saved by a run inline`` () =
    let result = callTool "caddis_eval" """{"code":"System.IO.File.WriteAllBytes(System.IO.Path.Combine(Caddis.Script.OutDir, \"a.png\"), [| 1uy |])"}"""
    let image = result["content"].AsArray() |> Seq.find (fun c -> str [ "type" ] c = "image")
    Assert.Equal("image/png", str [ "mimeType" ] image)

[<Fact>]
let ``MCP describes APIs`` () =
    let result = callTool "caddis_describe" """{"name":"Caddis.Script.RunId"}"""
    Assert.False(flag [ "isError" ] result)
    Assert.Contains("static member RunId: string with get", resultText result)
    let missing = callTool "caddis_describe" "{}"
    Assert.True(flag [ "isError" ] missing)

[<Fact>]
let ``REST searches APIs`` () =
    use req = new HttpRequestMessage(HttpMethod.Get, "/describe?search=OnUi")
    let status, body = send client.Value req
    Assert.Equal(HttpStatusCode.OK, status)
    Assert.Contains("Caddis.Script.OnUi", str [ "text" ] (JsonNode.Parse body))

/// A second server, in background mode.
let backgroundServer =
    lazy
        (let store = newStore ()
         store.Save { CaddisSettings.defaults with port = 27830; threadMode = Background }
         let s = new CaddisServer(new HeadlessHost(ignore), store)
         match s.Start() with
         | Ok _ -> s
         | Error msg -> failwith msg)

[<Fact>]
let ``MCP cancels a background run on notifications/cancelled`` () =
    use c = new HttpClient(BaseAddress = Uri backgroundServer.Value.Url)
    c.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", backgroundServer.Value.Token)
    let postAsync (json: string) =
        let req = new HttpRequestMessage(HttpMethod.Post, "/mcp", Content = new StringContent(json, Encoding.UTF8, "application/json"))
        req.Headers.Add("MCP-Protocol-Version", "2025-06-18")
        c.SendAsync req
    let evalCall =
        postAsync """{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"caddis_eval","arguments":{"code":"while true do System.Threading.Thread.Sleep 10"}}}"""
    // repeat the notification, the run only starts once the F# session is ready
    let deadline = DateTime.UtcNow.AddSeconds 30.0
    while not evalCall.IsCompleted && DateTime.UtcNow < deadline do
        postAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":42}}""").Result |> ignore
        evalCall.Wait 500 |> ignore
    Assert.True(evalCall.IsCompleted, "The run was not cancelled.")
    let result = at [ "result" ] (JsonNode.Parse(evalCall.Result.Content.ReadAsStringAsync().Result))
    Assert.True(flag [ "isError" ] result)
    Assert.Matches(@"^Run r\d+: cancelled after", resultText result)

[<Fact>]
let ``REST eval returns 202 for a running run that can be awaited`` () =
    let status, body = post "/eval" """{"code": "System.Threading.Thread.Sleep 500\n7", "waitMs": 0}"""
    Assert.Equal(HttpStatusCode.Accepted, status)
    use req = new HttpRequestMessage(HttpMethod.Get, $"""/runs/{str [ "id" ] (JsonNode.Parse body)}?waitMs=20000""")
    let status, body = send client.Value req
    Assert.Equal(HttpStatusCode.OK, status)
    Assert.Equal("7", str [ "value"; "text" ] (JsonNode.Parse body))

[<Fact>]
let ``rejects bodies over the limit also without a length`` () =
    use content = new StreamContent(new IO.MemoryStream(Array.create (21 * 1024 * 1024) (byte 'a')))
    use req = new HttpRequestMessage(HttpMethod.Post, "/eval", Content = content)
    req.Headers.TransferEncodingChunked <- true
    let status, _ = send client.Value req
    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status)

[<Fact>]
let ``writes a discovery file`` () =
    let path = IO.Path.Combine(server.Value.Service.Store.InstancesFolder, $"{Environment.ProcessId}.json")
    let info = JsonNode.Parse(IO.File.ReadAllText path)
    Assert.Equal(server.Value.Port, num [ "port" ] info)

/// Posts to the UI thread of the host, like the SynchronizationContext of Rhino's UI thread.
type UiContext(host: ICaddisHost) =
    inherit Threading.SynchronizationContext()
    override _.Post(d, state) = host.InvokeOnUi(fun () -> d.Invoke state) |> ignore
    override this.CreateCopy() = this

[<Fact>]
let ``answers requests while a run blocks the UI thread the server was started on`` () =
    let store = newStore ()
    store.Save { CaddisSettings.defaults with port = 27830 }
    let host = new HeadlessHost(ignore) :> ICaddisHost
    use s = new CaddisServer(host, store)
    // like Rhino, which starts the server from a command on the UI thread
    let started =
        host.InvokeOnUi(fun () ->
            Threading.SynchronizationContext.SetSynchronizationContext(UiContext host)
            s.Start())
    match started.Result with
    | Ok _ -> ()
    | Error msg -> failwith msg
    use c = new HttpClient(BaseAddress = Uri s.Url)
    c.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", s.Token)
    use evalReq =
        new HttpRequestMessage(HttpMethod.Post, "/eval", Content = new StringContent("""{"code": "System.Threading.Thread.Sleep 3000", "waitMs": 0}"""))
    let status, _ = send c evalReq
    Assert.Equal(HttpStatusCode.Accepted, status)
    let watch = Diagnostics.Stopwatch.StartNew()
    use statusReq = new HttpRequestMessage(HttpMethod.Get, "/status")
    let status, _ = send c statusReq
    Assert.Equal(HttpStatusCode.OK, status)
    use captureReq = new HttpRequestMessage(HttpMethod.Get, "/viewport")
    let status, _ = send c captureReq
    Assert.Equal(HttpStatusCode.Conflict, status) // the UI thread is busy
    Assert.True(watch.ElapsedMilliseconds < 1500, $"The requests took {watch.ElapsedMilliseconds} ms.")
