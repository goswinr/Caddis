/// Tests of the extension of Claude Desktop: its files, what Claude Desktop installed, and its relay, run with Node.
module Caddis.Tests.DesktopTests

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Caddis
open Caddis.Tests.Helpers
open Caddis.Tests.AgentTests

let bundle: DesktopExtension.Bundle = {
    serverName = "caddis-rhino"
    appName = "Rhino"
    port = 7811
    version = "1.2.3"
    platforms = [ "win32"; "darwin" ]
    instructions = "Caddis runs F# code inside Rhino."
    tools = [ Json.obj [ "name", Json.ofString "caddis_eval"; "description", Json.ofString "Run F# code. Long details follow." ] ]
}

[<Fact>]
let ``the manifest runs the relay with the port of the extension's setting`` () =
    let m = DesktopExtension.manifest bundle
    let at (path: string list) = path |> List.fold (fun (n: JsonNode) (k: string) -> n[k]) (m :> JsonNode)
    Assert.Equal("caddis-rhino", (at [ "name" ]).GetValue<string>())
    Assert.Equal("Caddis for Rhino", (at [ "display_name" ]).GetValue<string>())
    Assert.Equal("1.2.3", (at [ "version" ]).GetValue<string>())
    Assert.Equal("node", (at [ "server"; "type" ]).GetValue<string>())
    let args = (at [ "server"; "mcp_config"; "args" ]).AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
    Assert.Equal<string list>([ "${__dirname}/server/relay.js"; "${user_config.port}"; "Rhino" ], args)
    Assert.Equal(7811, (at [ "user_config"; "port"; "default" ]).GetValue<int>())
    Assert.Equal("Run F# code.", (at [ "tools" ]).[0].["description"].GetValue<string>())
    Assert.True((at [ "tools_generated" ]).GetValue<bool>())
    Assert.Null((DesktopExtension.manifest { bundle with tools = [] })["tools"])

[<Fact>]
let ``the bundle is a zip of the manifest, the relay with what Caddis offers, and the icon`` () =
    let path = Path.Combine(newTempDir (), "caddis-rhino.mcpb")
    DesktopExtension.writeBundle bundle path
    use zip = ZipFile.OpenRead path
    let names = zip.Entries |> Seq.map _.FullName |> Set.ofSeq
    Assert.Equal<Set<string>>(set [ "manifest.json"; "server/relay.js"; "server/caddis.json"; "icon.png" ], names)
    let read (name: string) =
        use reader = new StreamReader(zip.GetEntry(name).Open())
        reader.ReadToEnd()
    Assert.StartsWith("#!/usr/bin/env node", read "server/relay.js")
    let offer = JsonNode.Parse(read "server/caddis.json")
    Assert.Equal("Caddis runs F# code inside Rhino.", offer["instructions"].GetValue<string>())
    Assert.Equal("caddis_eval", offer["tools"].[0].["name"].GetValue<string>())
    Assert.Equal("caddis-rhino", JsonNode.Parse(read "manifest.json").["name"].GetValue<string>())

/// Claude Desktop's folder with the extension installed as it records it, with these settings, None for no settings file.
let installed (settings: string option) =
    let dir = newTempDir ()
    let id = "local.mcpb.goswin-rothenthal.caddis-rhino"
    let manifest = DesktopExtension.manifest bundle
    let installations = Json.obj [ "extensions", Json.obj [ id, Json.obj [ "id", Json.ofString id; "manifest", manifest ] ] ]
    let path = Path.Combine(dir, "extensions-installations.json")
    File.WriteAllText(path, Json.toText installations)
    settings
    |> Option.iter (fun s ->
        Directory.CreateDirectory(Path.Combine(dir, "Claude Extensions Settings")) |> ignore
        File.WriteAllText(Path.Combine(dir, "Claude Extensions Settings", id + ".json"), s))
    path

[<Fact>]
let ``the installed extension relays to the port of its setting, or to the default`` () =
    Assert.Equal(Some "http://127.0.0.1:7811/mcp", DesktopExtension.installedUrl (installed None) "caddis-rhino")
    Assert.Equal(Some "http://127.0.0.1:7811/mcp", DesktopExtension.installedUrl (installed (Some """{"isEnabled": true}""")) "caddis-rhino")
    let changed = installed (Some """{"isEnabled": true, "userConfig": {"port": 7815}}""")
    Assert.Equal(Some "http://127.0.0.1:7815/mcp", DesktopExtension.installedUrl changed "caddis-rhino")
    Assert.Equal(None, DesktopExtension.installedUrl (installed (Some """{"isEnabled": false}""")) "caddis-rhino")
    Assert.Equal(None, DesktopExtension.installedUrl (installed None) "caddis-revit")
    let configs = AgentConfigs(fun _ -> changed)
    Assert.True(configs.IsInstalled ClaudeDesktop)
    Assert.Equal(AgentState.Active, configs.State(ClaudeDesktop, "caddis-rhino", 7815))
    Assert.Equal(AgentState.Inactive "http://127.0.0.1:7815/mcp", configs.State(ClaudeDesktop, "caddis-rhino", 7816))
    Assert.False(AgentConfigs(fun _ -> Path.Combine(newTempDir (), "Claude", "extensions-installations.json")).IsInstalled ClaudeDesktop)

[<Fact>]
let ``Claude Desktop from its MSIX package has its settings in the package's folder, unless it is installed by the installer too`` () =
    let local, roaming = newTempDir (), newTempDir ()
    let settingsFolder () = DesktopExtension.windowsSettingsFolder local roaming
    Assert.Equal(Path.Combine(roaming, "Claude"), settingsFolder ())
    let packaged = Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude")
    Directory.CreateDirectory packaged |> ignore
    Assert.Equal(packaged, settingsFolder ())
    let program = DesktopExtension.installerProgram local
    Directory.CreateDirectory(Path.GetDirectoryName program) |> ignore
    File.WriteAllText(program, "")
    Assert.Equal(Path.Combine(roaming, "Claude"), settingsFolder ())

[<Fact>]
let ``Set up opens the extension made for the running server`` () =
    use s = newServer (port 30)
    let opened = ResizeArray<string>()
    s.OpenFile <- opened.Add
    start s
    let result = s.SetUpAgent(ClaudeDesktop).Result
    Assert.True(isOk result, $"%A{result}")
    let path = Seq.exactlyOne opened
    Assert.EndsWith("caddis.mcpb", path)
    use zip = ZipFile.OpenRead path
    use reader = new StreamReader(zip.GetEntry("manifest.json").Open())
    let manifest = JsonNode.Parse(reader.ReadToEnd())
    Assert.Equal(s.Port, manifest["user_config"].["port"].["default"].GetValue<int>())
    Assert.Contains("caddis_eval", manifest["tools"].AsArray() |> Seq.map (fun t -> t["name"].GetValue<string>()))

/// The relay of the extension in a temporary folder, run with Node, for Caddis on the port. None if Node isn't installed, e.g. on CI without it.
type Relay(b: DesktopExtension.Bundle, caddisPort: int) =
    let dir = newTempDir ()
    do DesktopExtension.writeFolder b dir
    let node = AgentCli.find "node"
    let proc =
        node
        |> Option.map (fun exe ->
            let script = Path.Combine(dir, "server", "relay.js")
            let psi = ProcessStartInfo(exe, $"\"{script}\" {caddisPort} Headless")
            psi.UseShellExecute <- false
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.StandardOutputEncoding <- Encoding.UTF8
            Process.Start psi)

    member _.Started = proc.IsSome

    member _.Send(json: string) =
        proc.Value.StandardInput.WriteLine json
        proc.Value.StandardInput.Flush()

    /// The next message the relay writes, within the time.
    member _.Next(seconds: float) : JsonNode =
        let line = proc.Value.StandardOutput.ReadLineAsync()
        if not (line.Wait(TimeSpan.FromSeconds seconds)) then failwith $"The relay wrote nothing within {seconds} s."
        JsonNode.Parse line.Result

    /// The result of a request.
    member this.Result(json: string) : JsonNode = (this.Request json).["result"]

    member this.Request(json: string) : JsonNode =
        this.Send json
        this.Next 20.0

    interface IDisposable with
        member _.Dispose() =
            proc
            |> Option.iter (fun p ->
                try p.Kill() with _ -> ()
                p.Dispose())

let initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""
let toolsList = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""
let eval = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"caddis_eval","arguments":{"code":"6 * 7"}}}"""
let texts (result: JsonNode) = result["content"].AsArray() |> Seq.map (fun c -> c["text"].GetValue<string>()) |> String.concat "\n"
let toolNames (result: JsonNode) = result["tools"].AsArray() |> Seq.map (fun t -> t["name"].GetValue<string>()) |> List.ofSeq

[<Fact>]
let ``the relay passes the messages of Claude Desktop to Caddis and back`` () =
    use s = newServer (port 31)
    start s
    use relay = new Relay(s.DesktopBundle(), s.Port)
    if relay.Started then
        let init = relay.Request initialize
        Assert.Equal("caddis", init["result"].["serverInfo"].["name"].GetValue<string>())
        Assert.True(init["result"].["capabilities"].["tools"].["listChanged"].GetValue<bool>())
        relay.Send """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
        Assert.Contains("caddis_eval", toolNames (relay.Result toolsList))
        let result = relay.Result eval
        Assert.False(result["isError"].GetValue<bool>(), texts result)
        Assert.Contains("42", texts result)

[<Fact>]
let ``without Caddis the relay lists the tools of the bundle, and tells Claude Desktop once Caddis answers`` () =
    use s = newServer (port 32)
    start s
    let b = s.DesktopBundle()
    let offPort = port 33
    use relay = new Relay(b, offPort)
    if relay.Started then
        let init = relay.Result initialize
        Assert.Contains("doesn't answer at http://127.0.0.1:", init["instructions"].GetValue<string>())
        Assert.StartsWith(s.Service.Host.Info.mcp.instructions, init["instructions"].GetValue<string>())
        Assert.Contains("caddis_eval", toolNames (relay.Result toolsList))
        let result = relay.Result eval
        Assert.True(result["isError"].GetValue<bool>())
        Assert.Contains("Ask the person to start Headless", texts result)
        // Caddis starts on the port
        use later = newServer offPort
        start later
        let changed = relay.Next 15.0
        Assert.Equal("notifications/tools/list_changed", changed["method"].GetValue<string>())
        Assert.Contains("42", texts (relay.Result eval))
