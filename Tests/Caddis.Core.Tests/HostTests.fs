/// Tests of what depends on the host app, with a host like Revit: no commands, element ids as numbers, its own MCP name.
module Caddis.Tests.HostTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open Xunit
open Caddis
open Caddis.Tests.Helpers

/// A headless host with the texts and ids of another app, that records the status of each run, the export selection and pressed dialog buttons.
/// An export with 'slow' in its file name takes 1.5 s.
type OtherAppHost() =
    let inner = new HeadlessHost(ignore) :> ICaddisHost
    let statuses = ResizeArray<string * RunStatus>()
    let mutable exported: ExportSelection option = None
    let pressed = ResizeArray<string * string>()

    let info = {
        inner.Info with
            appName = "OtherApp"
            mcpServerName = "caddis-other"
            copyMcpCommandUi = "the button 'Copy MCP'"
            objectIdKind = "an element id (a number)"
            isObjectId = fun s -> fst (Int64.TryParse s)
            mcp = {
                inner.Info.mcp with
                    instructions = "Caddis runs F# code inside OtherApp."
                    command = None
                    dialog = Some "Lists or presses buttons of dialogs of OtherApp."
            }
    }

    member _.Statuses = List.ofSeq statuses
    member _.Exported = exported
    member val ExportCount = 0 with get, set
    member _.Pressed = List.ofSeq pressed

    /// The dialogs the host shows.
    member val OpenDialogs: HostDialog list = [] with get, set

    interface ICaddisHost with
        member _.Info = info
        member _.Description = "OtherApp"
        member _.LibFolders = inner.LibFolders
        member _.References = inner.References
        member _.InvokeOnUi<'T>(f: unit -> 'T) = inner.InvokeOnUi f
        member _.CannotRunReason() = None
        member _.BeforeRun _ = ()
        member _.AfterRun(r, s) =
            statuses.Add(r.Id, s)
            Ok ()
        member _.Echo(_, _) = ()
        member _.Log _ = ()
        member _.LogError _ = ()
        member _.Status() = []
        member _.CaptureView(v, w, h, d, p) = inner.CaptureView(v, w, h, d, p)
        member this.Export(selection, path) =
            this.ExportCount <- this.ExportCount + 1
            exported <- Some selection
            if path.Contains "slow" then Threading.Thread.Sleep 1500
            File.WriteAllText(path, "exported")
        member _.Undo r = inner.Undo r
        member this.Dialogs() = this.OpenDialogs
        member _.PressDialogButton(d, b) =
            pressed.Add(d, b)
            Ok $"Pressed '{b}'."
        member _.RunCommands(_, _) = failwith "OtherApp has no commands."

let host = OtherAppHost()

let server =
    lazy
        (let store = newStore ()
         store.Save { CaddisSettings.defaults with port = 27850 }
         let s = new CaddisServer(host, store)
         match s.Start() with
         | Ok _ -> s
         | Error msg -> failwith msg)

let post (path: string) (json: string) =
    use c = new HttpClient(BaseAddress = Uri server.Value.Url)
    use req = new HttpRequestMessage(HttpMethod.Post, path, Content = new StringContent(json, Encoding.UTF8, "application/json"))
    let resp = c.Send req
    resp.StatusCode, resp.Content.ReadAsStringAsync().Result

let mcp (json: string) = post "/mcp" json |> snd |> JsonNode.Parse

/// Follows a path of property names, e.g. at ["result"; "tools"]
let at (path: string list) (n: JsonNode) = path |> List.fold (fun (n: JsonNode) (k: string) -> n[k]) n
let str path n = (at path n).GetValue<string>()

[<Fact>]
let ``MCP uses the name and instructions of the host`` () =
    let result = at [ "result" ] (mcp """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
    Assert.Equal("caddis-other", str [ "serverInfo"; "name" ] result)
    Assert.Equal("Caddis runs F# code inside OtherApp.", str [ "instructions" ] result)
    Assert.Equal($"claude mcp add --transport http --scope user caddis-other {server.Value.McpUrl}", server.Value.ClaudeMcpCommand)
    Assert.Equal($"codex mcp add caddis-other --url {server.Value.McpUrl}", server.Value.CodexMcpCommand)

[<Fact>]
let ``MCP does not offer commands if the host has none`` () =
    let tools = (at [ "result"; "tools" ] (mcp """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")).AsArray()
    let names = tools |> Seq.map (str [ "name" ]) |> List.ofSeq
    Assert.Contains("caddis_eval", names)
    Assert.DoesNotContain("caddis_command", names)
    let call = mcp """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"caddis_command","arguments":{"commands":["x"]}}}"""
    Assert.Equal(-32602, (at [ "error"; "code" ] call).GetValue<int>())

[<Fact>]
let ``REST commands on a host without commands are a bad request`` () =
    let status, body = post "/command" """{"commands": ["x"]}"""
    Assert.Equal(HttpStatusCode.BadRequest, status)
    Assert.Contains("OtherApp has no commands to run, use /eval instead.", body)

[<Fact>]
let ``export ids are checked with the id format of the host`` () =
    let status, body = post "/export" """{"format": "dwg", "objectIds": ["12", "a-guid"]}"""
    Assert.Equal(HttpStatusCode.BadRequest, status)
    Assert.Contains("'a-guid' in 'objectIds' is not an element id (a number).", body)
    let status, _ = post "/export" """{"format": "dwg", "objectIds": [" 316577 "]}"""
    Assert.Equal(HttpStatusCode.OK, status)
    Assert.Equal(Some(ObjectIds [| "316577" |]), host.Exported)

[<Fact>]
let ``AfterRun gets the status of the run`` () =
    let runs = RunRegistry(Path.Combine(Path.GetTempPath(), "CaddisTests", "runs-" + Guid.NewGuid().ToString "N"))
    let h = OtherAppHost()
    let svc = CaddisService(h, newStore (), runs)
    let ok = eval svc "1 + 1"
    let failed = eval svc "(failwith \"boom\" : int)"
    let notCompiled = eval svc "let x: int = \"text\""
    let statusOf (r: Run) = h.Statuses |> List.find (fun (id, _) -> id = r.Id) |> snd
    Assert.Equal(Succeeded, statusOf ok)
    Assert.Equal(RuntimeError, statusOf failed)
    Assert.Equal(CompileError, statusOf notCompiled)

[<Fact>]
let ``each host has its own settings file and default port`` () =
    let folder = Path.Combine(Path.GetTempPath(), "CaddisTests", Guid.NewGuid().ToString "N")
    let rhino = SettingsStore(folder)
    let other = SettingsStore(folder, "settings-other.json", { CaddisSettings.defaults with port = 7820 })
    Assert.Equal(7810, rhino.Load().port)
    Assert.Equal(7820, other.Load().port)
    Assert.True(File.Exists(Path.Combine(folder, "settings-other.json")))
    // missing values fall back to the defaults of the host
    File.WriteAllText(other.SettingsPath, """{"autoStart": true}""")
    Assert.Equal(7820, other.Load().port)
    Assert.True(other.Load().autoStart)

/// Calls an MCP tool and returns whether the result is an error, and its text.
let callTool (name: string) (arguments: string) =
    let result = at [ "result" ] (mcp $"""{{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{{"name":"{name}","arguments":{arguments}}}}}""")
    let text = result["content"].AsArray() |> Seq.filter (fun c -> str [ "type" ] c = "text") |> Seq.map (str [ "text" ]) |> String.concat "\n"
    (at [ "isError" ] result).GetValue<bool>(), text

[<Fact>]
let ``an export is a run with its files`` () =
    let status, body = post "/export" """{"format": ".dwg", "name": "plan"}"""
    Assert.Equal(HttpStatusCode.OK, status)
    let run = JsonNode.Parse body
    Assert.Equal("export", str [ "kind" ] run)
    Assert.Equal("ok", str [ "status" ] run)
    Assert.StartsWith("x", str [ "id" ] run)
    let files = (at [ "files" ] run).AsArray()
    Assert.Equal("plan.dwg", str [ "name" ] files[0])

[<Fact>]
let ``MCP export returns a running export after waitMs, caddis_get_run gets its files`` () =
    let isError, text = callTool "caddis_export" """{"format": "obj", "name": "slow", "waitMs": 0}"""
    Assert.False isError
    Assert.Contains("still running", text)
    let runId = Text.RegularExpressions.Regex.Match(text, @"Run (x\d+)").Groups[1].Value
    // After 0 ms the UI thread may not have started the export yet, then the result says so.
    let rec whenStarted (text: string) tries =
        if text.Contains "It has not started yet" && tries > 0 then
            Threading.Thread.Sleep 10
            whenStarted (snd (callTool "caddis_get_run" $"""{{"runId": "{runId}", "waitMs": 0}}""")) (tries - 1)
        else
            text
    Assert.Contains("The export is still running in OtherApp.", whenStarted text 100)
    let status, _ = post "/export" """{"format": "obj"}"""
    Assert.Equal(HttpStatusCode.Conflict, status) // one run at a time
    let isError, text = callTool "caddis_get_run" $"""{{"runId": "{runId}", "waitMs": 10000}}"""
    Assert.False isError
    Assert.Contains($"Run {runId}: ok", text)
    Assert.Contains("slow.obj (8 B, model/obj)", text)

[<Fact>]
let ``open dialogs are in the status and in results of runs that are still running`` () =
    host.OpenDialogs <- [ { id = "0x1"; title = "Project Not Saved Recently"; text = "Save now?"; buttons = [ "Save"; "Cancel" ] } ]
    try
        use c = new HttpClient(BaseAddress = Uri server.Value.Url)
        let dialogs = (at [ "dialogs" ] (JsonNode.Parse(c.GetStringAsync("/status").Result))).AsArray()
        Assert.Equal("Project Not Saved Recently", str [ "title" ] dialogs[0])
        let _, text = callTool "caddis_export" """{"format": "obj", "name": "slow", "waitMs": 0}"""
        Assert.Contains(
            "OtherApp shows a dialog, which blocks it until it is closed: 'Project Not Saved Recently' (id 0x1): Save now? Buttons: 'Save', 'Cancel'. Press a button with caddis_dialog",
            text
        )
        let runId = Text.RegularExpressions.Regex.Match(text, @"Run (x\d+)").Groups[1].Value
        callTool "caddis_get_run" $"""{{"runId": "{runId}", "waitMs": 10000}}""" |> ignore
    finally
        host.OpenDialogs <- []

[<Fact>]
let ``caddis_dialog lists dialogs or presses a button`` () =
    let tools = (at [ "result"; "tools" ] (mcp """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")).AsArray() |> Seq.map (str [ "name" ])
    Assert.Contains("caddis_dialog", tools)
    Assert.Equal((false, "OtherApp shows no dialog."), callTool "caddis_dialog" "{}")
    host.OpenDialogs <- [ { id = "0x2"; title = "Unresolved References"; text = ""; buttons = [ "Ignore" ] } ]
    try
        let _, text = callTool "caddis_dialog" "{}"
        Assert.Equal("'Unresolved References' (id 0x2) Buttons: 'Ignore'.", text)
        Assert.Equal((false, "Pressed 'Ignore'."), callTool "caddis_dialog" """{"button": " Ignore ", "dialog": "0x2"}""")
        Assert.Contains(("0x2", "Ignore"), host.Pressed)
        let status, body = post "/dialog" """{"button": ""}"""
        Assert.Equal(HttpStatusCode.BadRequest, status)
        Assert.Contains("Provide the name of the 'button' to press.", body)
    finally
        host.OpenDialogs <- []

[<Fact>]
let ``MCP shows changed objects per group if the host groups them`` () =
    let runs = RunRegistry(Path.Combine(Path.GetTempPath(), "CaddisTests", "runs-" + Guid.NewGuid().ToString "N"))
    let run = Run("r1", FSharpCode, UiThread, "", "", runs.CreateFolder "r1", 10_000, true)
    run.Commands <-
        Some {
            steps = []
            notRun = []
            created = [| "11" |]
            changed = [| "21"; "22"; "23" |]
            changedGroups = [ "Path of Travel Lines", 2; "Schedules", 1 ]
            deleted = [||]
            selected = None
        }
    run.Complete(RunOutcome.ofStatus Succeeded) |> ignore
    let text, _ = Http.McpView.run "OtherApp" runs [] 0L run
    Assert.Contains("created (1): 11\n", text)
    Assert.Matches(@"changed \(3, all in .*r1-changed\.txt\): 2 Path of Travel Lines, 1 Schedules", text)
    Assert.DoesNotContain("21, 22", text)

[<Fact>]
let ``MCP says when a running run has not started yet`` () =
    let runs = RunRegistry(Path.Combine(Path.GetTempPath(), "CaddisTests", "runs-" + Guid.NewGuid().ToString "N"))
    let run = Run("r1", FSharpCode, UiThread, "1", "", runs.CreateFolder "r1", 10_000, true)
    let text, _ = Http.McpView.run "OtherApp" runs [] 0L run
    Assert.Contains("It has not started yet: OtherApp runs it only when it is idle", text)
    run.Started <- true
    let text, _ = Http.McpView.run "OtherApp" runs [] 0L run
    Assert.Contains("It runs on the UI thread, so it can't be cancelled and OtherApp is blocked until it finishes.", text)

[<Theory>]
[<InlineData("[]")>]
[<InlineData("[316577]")>]
[<InlineData("[\"316577\",42]")>]
[<InlineData("[\"316577\",null]")>]
[<InlineData("null")>]
[<InlineData("\"316577\"")>]
[<InlineData("{}")>]
let ``invalid export selections are rejected before calling the host`` (ids: string) =
    let before = host.ExportCount
    let args = $"""{{"format":"dwg","objects":"all","objectIds":{ids}}}"""
    let status, body = post "/export" args
    Assert.Equal(HttpStatusCode.BadRequest, status)
    Assert.Contains("objectIds", body)
    let isError, message = callTool "caddis_export" args
    Assert.True(isError, message)
    Assert.Contains("objectIds", message)
    Assert.Equal(before, host.ExportCount)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``host finalization failures become REST and MCP run errors`` (throws: bool) =
    use host = new QueuedHost(QueueUi = false)
    let failure = "The transaction group was rolled back instead of keeping the model changes."
    host.Finalize <- fun _ -> if throws then failwith failure else Error failure
    let store = newStore ()
    let service = CaddisService(host, store, RunRegistry(Path.Combine(store.Folder, "runs")))
    let request path body : Http.Request = {
        method = "POST"
        path = path
        query = Collections.Specialized.NameValueCollection()
        headers = Collections.Specialized.NameValueCollection()
        body = body
        baseUrl = "http://127.0.0.1"
    }
    let rest = Http.Rest.handle service (fun () -> []) (request "/eval" """{"code":"1 + 1"}""") |> _.Result
    match rest with
    | Http.JsonResponse(200, body) ->
        Assert.Equal("runtimeError", str [ "status" ] body)
        Assert.Null(body["value"])
        Assert.Contains(failure, str [ "message" ] body)
    | _ -> failwith $"Unexpected response: {rest}"
    let endpoint = Http.McpEndpoint(service)
    let rpc = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"caddis_eval","arguments":{"code":"1 + 1"}}}"""
    match endpoint.Handle(request "/mcp" rpc).Result with
    | Http.JsonResponse(200, body) ->
        Assert.True((at [ "result"; "isError" ] body).GetValue<bool>())
        Assert.Contains(failure, str [ "text" ] ((at [ "result"; "content" ] body).AsArray()[0]))
    | other -> failwith $"Unexpected response: {other}"
    let compileError = eval service "let x: int = \"wrong\""
    Assert.Equal(CompileError, compileError.Status)
    Assert.Contains(compileError.Notes, fun note -> note.Contains failure)
