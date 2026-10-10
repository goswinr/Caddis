module Caddis.Tests.HttpTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
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

let mcp (json: string) =
    use req = new HttpRequestMessage(HttpMethod.Post, "/mcp", Content = new StringContent(json, Encoding.UTF8, "application/json"))
    req.Headers.Add("MCP-Protocol-Version", "2025-06-18")
    req.Headers.Accept.ParseAdd "application/json, text/event-stream"
    send client.Value req

/// Follows a path of property names, e.g. at ["result"; "tools"]
let at (path: string list) (n: JsonNode) = path |> List.fold (fun (n: JsonNode) (k: string) -> n[k]) n
let str path n = (at path n).GetValue<string>()
let num path n = (at path n).GetValue<int>()
let flag path n = (at path n).GetValue<bool>()

[<Fact>]
let ``rejects requests without token`` () =
    use c = new HttpClient(BaseAddress = Uri server.Value.Url)
    use req = new HttpRequestMessage(HttpMethod.Get, "/status")
    let status, _ = send c req
    Assert.Equal(HttpStatusCode.Unauthorized, status)

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
    Assert.Equal("Capturing a viewport needs Rhino.", str [ "error" ] (JsonNode.Parse body))

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
    Assert.Contains("caddis_capture_viewport", names)

[<Fact>]
let ``MCP evaluates code`` () =
    let _, body = mcp """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"caddis_eval","arguments":{"code":"printfn \"from mcp\"\n40 + 2"}}}"""
    let msg = JsonNode.Parse body
    Assert.Equal(7, num [ "id" ] msg)
    let result = at [ "result" ] msg
    Assert.False(flag [ "isError" ] result)
    Assert.Equal("42", str [ "structuredContent"; "value"; "text" ] result)
    Assert.Contains("from mcp", str [ "structuredContent"; "stdout" ] result)

[<Fact>]
let ``MCP reports failed runs as tool errors`` () =
    let _, body = mcp """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"caddis_eval","arguments":{"code":"let x: int = \"no\""}}}"""
    let result = at [ "result" ] (JsonNode.Parse body)
    Assert.True(flag [ "isError" ] result)
    Assert.Equal("compileError", str [ "structuredContent"; "status" ] result)

[<Fact>]
let ``MCP unknown method is an error`` () =
    let _, body = mcp """{"jsonrpc":"2.0","id":9,"method":"resources/list"}"""
    Assert.Equal(-32601, num [ "error"; "code" ] (JsonNode.Parse body))

[<Fact>]
let ``writes a discovery file`` () =
    let path = IO.Path.Combine(server.Value.Service.Store.InstancesFolder, $"{Environment.ProcessId}.json")
    let info = JsonNode.Parse(IO.File.ReadAllText path)
    Assert.Equal(server.Value.Port, num [ "port" ] info)
