/// Tests of setting up AI agents and of handing the port over between instances of an app.
module Caddis.Tests.AgentTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open Xunit
open Caddis
open Caddis.Tests.Helpers

/// A port of these tests. dotnet test runs the test processes of the frameworks at the same time, each gets its own ports,
/// else the server of one process could take over the port of the other's: both are 'caddis'.
let port (n: int) =
#if NETFRAMEWORK
    28100 + n
#else
    (if Environment.Version.Major >= 10 then 28200 else 28300) + n
#endif

/// A server of a headless host whose settings have the port, not started.
let newServer (port: int) =
    let store = newStore ()
    store.Save { CaddisSettings.defaults with port = port }
    new CaddisServer(new HeadlessHost(ignore), store)

/// Starts the server and waits for the F# session that it creates in the background, so that it doesn't go on into the next test.
let start (s: CaddisServer) =
    match s.Start() with
    | Ok _ -> s.Service.Fsi.Initialize().Wait()
    | Error e -> failwith e

/// Agent configs in a new temporary folder.
let tempConfigs () =
    let dir = newTempDir ()
    let path agent = Path.Combine(dir, (match agent with ClaudeCode -> ".claude.json" | Codex -> "config.toml"))
    AgentConfigs path, path

let post (baseUrl: string) (path: string) (session: string option) (json: string) =
    use c = new HttpClient(BaseAddress = Uri baseUrl)
    use req = new HttpRequestMessage(HttpMethod.Post, path, Content = new StringContent(json, Encoding.UTF8, "application/json"))
    session |> Option.iter (fun s -> req.Headers.Add("Mcp-Session-Id", s))
    let resp = c.Send req
    resp, resp.Content.ReadAsStringAsync().Result

let isOk (r: Result<string, string>) = match r with Ok _ -> true | Error _ -> false

[<Fact>]
let ``Claude Code's config has the URL of a server of user scope`` () =
    // keys that differ in case only, as Claude Code writes them for projects
    let json =
        """{ "projects": { "C:/a": { "mcpServers": { "caddis-revit": { "url": "http://127.0.0.1:1/mcp" } } }, "c:/a": {} },
             "mcpServers": { "caddis-revit": { "type": "http", "url": "http://127.0.0.1:7820/mcp" }, "local": { "command": "x" } } }"""
    Assert.Equal(Some "http://127.0.0.1:7820/mcp", McpAgent.claudeUrl json "caddis-revit")
    Assert.Equal(Some "", McpAgent.claudeUrl json "local")
    Assert.Equal(None, McpAgent.claudeUrl json "caddis-rhino")
    Assert.Equal(None, McpAgent.claudeUrl """{ "projects": {} }""" "caddis-revit")

[<Fact>]
let ``Codex's config has the URL in the table of the server`` () =
    let toml =
        String.concat "\r\n" [
            "model = \"o3\""
            "[mcp_servers.node_repl]"
            "command = 'node.exe'"
            ""
            "[mcp_servers.caddis-revit] # Caddis"
            "url = \"http://127.0.0.1:7821/mcp\""
            "[mcp_servers.caddis-revit.env]"
            "url = \"not this one\""
            "[mcp_servers.\"caddis-rhino\"]"
            "startup_timeout_sec = 20"
            "url='http://localhost:7810/mcp'"
        ]
    Assert.Equal(Some "http://127.0.0.1:7821/mcp", McpAgent.codexUrl toml "caddis-revit")
    Assert.Equal(Some "http://localhost:7810/mcp", McpAgent.codexUrl toml "caddis-rhino")
    Assert.Equal(Some "", McpAgent.codexUrl toml "node_repl")
    Assert.Equal(None, McpAgent.codexUrl toml "caddis")

[<Fact>]
let ``an agent is active if its URL has the port of the server`` () =
    Assert.Equal(AgentState.NotSetUp, McpAgent.state None 7820)
    Assert.Equal(AgentState.Active, McpAgent.state (Some "http://127.0.0.1:7820/mcp") 7820)
    Assert.Equal(AgentState.Active, McpAgent.state (Some "http://localhost:7820/mcp/") 7820)
    Assert.Equal(AgentState.Inactive "http://127.0.0.1:7821/mcp", McpAgent.state (Some "http://127.0.0.1:7821/mcp") 7820)
    Assert.Equal(AgentState.Inactive "http://example.com:7820/mcp", McpAgent.state (Some "http://example.com:7820/mcp") 7820)
    Assert.Equal(AgentState.Inactive "", McpAgent.state (Some "") 7820)
    Assert.Equal(None, McpAgent.localPort "http://127.0.0.1:7820/status")

[<Fact>]
let ``the agents' commands add the server for all projects of the user`` () =
    let url = "http://127.0.0.1:7820/mcp"
    let command agent = McpAgent.commandLine agent (McpAgent.addArgs agent "caddis-revit" url)
    Assert.Equal($"claude mcp add --transport http --scope user caddis-revit {url}", command ClaudeCode)
    Assert.Equal($"codex mcp add caddis-revit --url {url}", command Codex)
    Assert.Equal("claude mcp remove --scope user caddis-revit", McpAgent.commandLine ClaudeCode (McpAgent.removeArgs ClaudeCode "caddis-revit"))

[<Fact>]
let ``a config is read again when it changed, and a half written one keeps the URL found before`` () =
    let configs, path = tempConfigs ()
    Assert.Equal(None, configs.RegisteredUrl(ClaudeCode, "caddis"))
    File.WriteAllText(path ClaudeCode, """{"mcpServers":{"caddis":{"url":"http://127.0.0.1:1/mcp"}}}""")
    Assert.Equal(Some "http://127.0.0.1:1/mcp", configs.RegisteredUrl(ClaudeCode, "caddis"))
    File.WriteAllText(path ClaudeCode, """{"mcpServers":{"caddis":{"url":"http://127.0.0.1:22/mcp"}}}""")
    Assert.Equal(Some "http://127.0.0.1:22/mcp", configs.RegisteredUrl(ClaudeCode, "caddis"))
    File.WriteAllText(path ClaudeCode, """{"mcpServers":{"cad""")
    Assert.Equal(Some "http://127.0.0.1:22/mcp", configs.RegisteredUrl(ClaudeCode, "caddis"))
    File.WriteAllText(path ClaudeCode, "{}")
    Assert.Equal(None, configs.RegisteredUrl(ClaudeCode, "caddis"))
    Assert.Equal(None, configs.RegisteredUrl(Codex, "caddis"))

[<Fact>]
let ``another instance takes over the port once the first is idle, and the agents on it go on with it`` () =
    let home = port 0
    use a = newServer home
    use b = newServer home
    let configs, path = tempConfigs ()
    a.AgentConfigs <- configs
    b.AgentConfigs <- configs
    start a
    start b
    Assert.Equal(home, a.Port)
    Assert.Equal(home + 1, b.Port)
    let homeUrl = a.Url
    File.WriteAllText(path ClaudeCode, """{"mcpServers":{"caddis":{"type":"http","url":""" + "\"" + a.McpUrl + "\"}}}")
    Assert.Equal(AgentState.Active, a.AgentState ClaudeCode)
    Assert.Equal(AgentState.Inactive a.McpUrl, b.AgentState ClaudeCode)
    Assert.Equal(AgentState.NotSetUp, b.AgentState Codex)
    // an agent connected to the port
    let resp, _ = post homeUrl "/mcp" None """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""
    let session = resp.Headers.GetValues "Mcp-Session-Id" |> Seq.head
    assertOk (eval a.Service "1") // the F# session is ready
    let run = a.Service.StartEval(evalArgs "System.Threading.Thread.Sleep 4000").Result

    // cancelled while A runs, A keeps the port
    use cts = new CancellationTokenSource(1500)
    let cancelled = b.ActivateAgent(ClaudeCode, ignore, cts.Token)
    Assert.ThrowsAny<OperationCanceledException>(fun () -> cancelled.GetAwaiter().GetResult() |> ignore) |> ignore
    Assert.Equal(home, a.Port)
    Assert.False run.IsFinished

    // waits until the run is done
    let waits = Collections.Concurrent.ConcurrentQueue<string>()
    let activated = b.ActivateAgent(ClaudeCode, waits.Enqueue, CancellationToken.None).Result
    Assert.True(isOk activated, $"%A{activated}")
    Assert.True run.IsFinished
    Assert.Contains($"run '{run.Id}' is in progress", Seq.head waits)
    Assert.Equal(home, b.Port)
    Assert.True a.IsRunning
    Assert.NotEqual(home, a.Port)
    Assert.Equal(AgentState.Active, b.AgentState ClaudeCode)
    Assert.Equal(AgentState.Inactive b.McpUrl, a.AgentState ClaudeCode)
    let status, _ = post a.Url "/cancel" None "{}"
    Assert.Equal(HttpStatusCode.Conflict, status.StatusCode) // no run to cancel, but A answers on its new port

    // the agent's session goes on with B, which tells it once that it is another Caddis
    let call () =
        post homeUrl "/mcp" (Some session) """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"caddis_status","arguments":{}}}"""
        |> snd
    Assert.Contains("Note: this MCP session was started with another Caddis server", call ())
    Assert.DoesNotContain("Note: this MCP session", call ())
    Assert.Empty(b.Service.Runs.All)

[<Fact>]
let ``the port of another app's Caddis is not taken, a free one is`` () =
    let store = newStore ()
    store.Save { CaddisSettings.defaults with port = port 10 }
    use other = new CaddisServer(HostTests.OtherAppHost(), store)
    use s = newServer (port 15)
    start other
    start s
    match s.TakeOverPort(port 10, ignore, CancellationToken.None).Result with
    | Ok msg -> failwith $"took the port of another app: {msg}"
    | Error e -> Assert.Contains("This is the MCP server 'caddis-other' of OtherApp, not 'caddis'.", e)
    Assert.Equal(port 15, s.Port)
    Assert.Equal(port 10, other.Port)
    Assert.True(isOk (s.TakeOverPort(port 18, ignore, CancellationToken.None).Result))
    Assert.Equal(port 18, s.Port)
    Assert.Equal($"http://127.0.0.1:{port 18}", s.Url)

/// Runs the programs of the agents that are installed, with their config in a temporary folder.
/// Passes without checking anything where none is installed, e.g. on CI.
[<Fact>]
let ``Set up adds the server with the agent's program, also again`` () =
    let dir = newTempDir ()
    let vars = [ "CLAUDE_CONFIG_DIR", Path.Combine(dir, "claude"); "CODEX_HOME", Path.Combine(dir, "codex") ]
    let before = vars |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable name)
    use s = newServer (port 20)
    try
        for name, folder in vars do
            Directory.CreateDirectory folder |> ignore
            Environment.SetEnvironmentVariable(name, folder)
        start s
        for agent in McpAgent.all do
            if (AgentCli.find (McpAgent.program agent)).IsSome then
                Assert.Equal(AgentState.NotSetUp, s.AgentState agent)
                let first = s.SetUpAgent(agent).Result
                Assert.True(isOk first, $"%A{first}")
                Assert.Equal(AgentState.Active, s.AgentState agent)
                // e.g. after the port changed: Claude Code's 'mcp add' fails if the name exists
                let again = s.SetUpAgent(agent).Result
                Assert.True(isOk again, $"%A{again}")
                Assert.Equal(AgentState.Active, s.AgentState agent)
    finally
        for name, value in before do
            Environment.SetEnvironmentVariable(name, value)
