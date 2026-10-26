namespace Caddis.Http

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Threading.Tasks
open System.Text.Json.Nodes
open Caddis

/// A tool offered via MCP.
type private McpTool = {
    name: string
    title: string
    description: string
    properties: (string * JsonNode) list
    required: string list
    readOnly: bool
    destructive: bool
    idempotent: bool
    }

/// The MCP endpoint: JSON-RPC over Streamable HTTP, answering every request with a single JSON response, no SSE.
/// Only tools are offered, no resources or prompts.
/// A session id is handed out on initialize, it is only used to tell apart the request ids of different clients,
/// and to tell agents whose session another Caddis started that this is another one.
/// see https://modelcontextprotocol.io/specification/2025-06-18/basic/transports#streamable-http
type McpEndpoint(svc: CaddisService) =

    /// Newest first. The client's version is used if it is in this list, otherwise the newest one.
    static let supportedVersions = [| "2025-11-25"; "2025-06-18"; "2025-03-26"; "2024-11-05" |]

    /// caddis_eval and caddis_get_run return after this time if the run has not finished,
    /// below the 60 s request timeout that MCP clients commonly use.
    static let defaultWaitMs = 50_000

    static let strProp (description: string) : JsonNode =
        Json.obj [ "type", Json.ofString "string"; "description", Json.ofString description ]

    static let intProp (description: string) : JsonNode =
        Json.obj [ "type", Json.ofString "integer"; "description", Json.ofString description ]

    static let boolProp (description: string) : JsonNode =
        Json.obj [ "type", Json.ofString "boolean"; "description", Json.ofString description ]

    static let enumOf (values: string list) (description: string) : JsonNode =
        Json.obj [ "type", Json.ofString "string"; "enum", Json.ofStrings values; "description", Json.ofString description ]

    static let codeAndPath = [
        "code", strProp "F# script code, like in an .fsx file."
        "path", strProp "Full path of an .fsx file. If 'code' is given too, the code is evaluated as if it were in that file, so __SOURCE_DIRECTORY__ and relative #load and #r paths work."
    ]

    static let idArray (description: string) : JsonNode =
        Json.obj [ "type", Json.ofString "array"; "items", Json.obj [ "type", Json.ofString "string" ]; "description", Json.ofString description ]

    /// The tools, with the texts that depend on the host app from HostInfo.mcp, and on whether NuGet packages can be used.
    static let toolsOf (t: McpTexts) (nuget: NuGetSupport) : McpTool list = [
        {
            name = "caddis_eval"
            title = "Evaluate F#"
            description = t.eval + " " + NuGetSupport.evalText nuget
            properties =
                codeAndPath
                @ [
                    "waitMs",
                    intProp
                        $"Return after this time with status 'running' if the run has not finished. It keeps running, get the result with caddis_get_run. 0 returns at once. Default: {defaultWaitMs}."
                    "timeoutMs", intProp "Background mode only: cancel the run after this time. Runs on the UI thread can't be cancelled."
                    "recordUndo", boolProp t.recordUndo
                ]
            required = []
            readOnly = false
            destructive = true
            idempotent = false
        }
        match t.command with
        | None -> ()
        | Some c ->
            {
                name = "caddis_command"
                title = "Run commands"
                description = c.description
                properties = [
                    "commands", idArray c.commands
                    "select", idArray c.select
                    "inputTimeoutMs", intProp c.inputTimeoutMs
                    "waitMs",
                    intProp
                        $"Return after this time with status 'running' if the commands have not finished. They keep running, get the result with caddis_get_run. Default: {defaultWaitMs}."
                ]
                required = [ "commands" ]
                readOnly = false
                destructive = true
                idempotent = false
            }
        {
            name = "caddis_get_run"
            title = "Get a run"
            description = "Returns a run, waiting up to waitMs for it to finish. Use it for a run that was still running when caddis_eval or caddis_command returned."
            properties = [
                "runId", strProp "The id of the run, e.g. 'r12'."
                "waitMs", intProp $"Wait at most this long for the run to finish. 0 returns at once. Default: {defaultWaitMs}."
                "stdoutOffset", intProp "Only return stdout from this character offset on, e.g. the stdoutLength of the previous call, to get only new output of a running run."
            ]
            required = [ "runId" ]
            readOnly = true
            destructive = false
            idempotent = true
        }
        {
            name = "caddis_check"
            title = "Type-check F#"
            description =
                "Parses and type-checks F# code against the current session without running it, so definitions from earlier caddis_eval calls are known. Returns the compiler errors and warnings. Lines and columns are 1-based. Not possible while a run is in progress."
            properties = codeAndPath
            required = []
            readOnly = true
            destructive = false
            idempotent = true
        }
        {
            name = "caddis_describe"
            title = "Describe API"
            description = t.describe
            properties = [
                "name", strProp t.describeName
                "filter", strProp "For a type: only the members whose name contains this text, e.g. 'Offset'."
                "search", strProp "If no name is given: find types and members whose name contains this text, e.g. 'fillet'."
            ]
            required = []
            readOnly = true
            destructive = false
            idempotent = true
        }
        {
            name = "caddis_status"
            title = "Status"
            description = t.status
            properties = []
            required = []
            readOnly = true
            destructive = false
            idempotent = true
        }
        {
            name = "caddis_cancel"
            title = "Cancel run"
            description = t.cancel
            properties = [ "runId", strProp "Only cancel if this run is the current one." ]
            required = []
            readOnly = false
            destructive = true
            idempotent = true
        }
        {
            name = "caddis_reset"
            title = "Reset FSI session"
            description = "Starts a new F# Interactive session. All values, functions and types defined so far are lost. Use it if the session is in a bad state."
            properties = []
            required = []
            readOnly = false
            destructive = true
            idempotent = false
        }
        {
            name = "caddis_set_mode"
            title = "Set thread mode"
            description = t.setMode
            properties = [ "mode", enumOf [ "ui"; "background" ] "The thread mode." ]
            required = [ "mode" ]
            readOnly = false
            destructive = true
            idempotent = true
        }
        {
            name = "caddis_undo"
            title = "Undo run"
            description = t.undo
            properties = [ "runId", strProp "The run to undo. Default: the latest run that made changes." ]
            required = []
            readOnly = false
            destructive = true
            idempotent = false
        }
        {
            name = "caddis_capture_viewport"
            title = "Capture viewport"
            description = t.capture
            properties = [
                "view", strProp t.captureView
                "width",
                intProp
                    $"Width in pixels. Default: follows the aspect ratio of the view if 'height' is given, otherwise the size of the view, scaled down to at most {CaptureSize.defaultMaxEdge} pixels on the long edge."
                "height", intProp "Height in pixels. Default: follows the aspect ratio of the view if 'width' is given, otherwise like 'width'."
                "displayMode", strProp t.captureDisplayMode
            ]
            required = []
            readOnly = true
            destructive = false
            idempotent = true
        }
        {
            name = "caddis_export"
            title = "Export"
            description = t.export
            properties = [
                "format", strProp t.exportFormat
                "name", strProp "File name without extension. Default: 'export'."
                "objects", enumOf [ "all"; "selected" ] t.exportObjects
                "objectIds", idArray t.exportObjectIds
                "waitMs",
                intProp
                    $"Return after this time with status 'running' if the export has not finished. It keeps running, get the files with caddis_get_run. Default: {defaultWaitMs}."
            ]
            required = [ "format" ]
            readOnly = false
            destructive = false
            idempotent = true
        }
        match t.dialog with
        | None -> ()
        | Some description ->
            {
                name = "caddis_dialog"
                title = "Dialogs"
                description = description
                properties = [
                    "button", strProp "The name of the button to press, as listed. Without it the open dialogs are returned."
                    "dialog", strProp "The id of the dialog, as listed. Default: the only open dialog."
                ]
                required = []
                readOnly = false
                destructive = true
                idempotent = false
            }
    ]

    static let toolJson (t: McpTool) : JsonNode =
        // a JsonNode can only have one parent, so clone the property schemas that are shared between tools
        let properties = t.properties |> List.map (fun (k, v) -> k, v.DeepClone())
        Json.obj [
            "name", Json.ofString t.name
            "title", Json.ofString t.title
            "description", Json.ofString t.description
            "inputSchema", Json.obj [ "type", Json.ofString "object"; "properties", Json.obj properties; "required", Json.ofStrings t.required ]
            "annotations",
            Json.obj [
                "title", Json.ofString t.title
                "readOnlyHint", Json.ofBool t.readOnly
                "destructiveHint", Json.ofBool t.destructive
                "idempotentHint", Json.ofBool t.idempotent
                "openWorldHint", Json.ofBool false
            ]
        ]

    static let response (id: JsonNode) (result: JsonNode) : JsonNode =
        Json.obj [ "jsonrpc", Json.ofString "2.0"; "id", (if isNull id then null else id.DeepClone()); "result", result ]

    static let errorResponse (id: JsonNode) (code: int) (message: string) : JsonNode =
        Json.obj [
            "jsonrpc", Json.ofString "2.0"
            "id", (if isNull id then null else id.DeepClone())
            "error", Json.obj [ "code", Json.ofInt code; "message", Json.ofString message ]
        ]

    static let text (s: string) : JsonNode = Json.obj [ "type", Json.ofString "text"; "text", Json.ofString s ]

    static let newSessionId () =
        Convert.ToHexString(RandomNumberGenerator.GetBytes 16).ToLowerInvariant()

    /// The instructions of the host app, with its NuGet packages if they can be used, or else the advice to install the .NET SDK.
    static let instructionsOf (info: HostInfo) (nuget: NuGetSupport) =
        match NuGetSupport.installAdvice info.appName nuget, info.mcp.packages with
        | Some advice, _ -> info.mcp.instructions + "\n" + $"If a task needs a NuGet package, tell the person in {info.appName}: {advice}"
        | None, Some packages -> info.mcp.instructions + "\n" + packages
        | None, None -> info.mcp.instructions

    let info = svc.Host.Info

    /// Not kept: an SDK installed while the host app runs makes NuGet packages available.
    let tools () = toolsOf info.mcp (DotnetSdk.current ())

    /// Runs started by a tools/call, by session id and the JSON text of the request id, so that notifications/cancelled can cancel them.
    let runsByRequest = ConcurrentDictionary<string, string>()

    /// The session ids handed out by this server.
    let issuedSessions = ConcurrentDictionary<string, bool>()

    /// The first tool result in a session that another Caddis started tells the agent: another instance of the app took over the port,
    /// see CaddisServer.TakeOverPort, or the app was restarted. Caddis doesn't reject such sessions, so that agents just go on.
    let withSessionNote (req: Request) (result: JsonNode) : JsonNode =
        match req.headers["Mcp-Session-Id"], result with
        | null, _ -> result
        | session, (:? JsonObject as o) when issuedSessions.TryAdd(session, true) ->
            match o["content"] with
            | :? JsonArray as content ->
                content.Insert(
                    0,
                    text
                        $"Note: this MCP session was started with another Caddis server, now Caddis in {svc.Host.Description} answers it: another {info.appName} handed its port over to this one, or {info.appName} was restarted. Values defined by earlier runs and the ids of runs from before may not exist here, and the open document may be another one."
                )
            | _ -> ()
            result
        | _ -> result

    let requestKey (req: Request) (requestId: JsonNode) =
        let session = req.headers["Mcp-Session-Id"] |> Option.ofObj |> Option.defaultValue ""
        let id = if isNull requestId then "" else requestId.ToJsonString()
        session + " " + id

    let initialize (p: JsonNode) : JsonNode =
        let requested = Json.tryString "protocolVersion" p |> Option.defaultValue supportedVersions[0]
        let version = if Array.contains requested supportedVersions then requested else supportedVersions[0]
        Json.obj [
            "protocolVersion", Json.ofString version
            "capabilities", Json.obj [ "tools", Json.obj [ "listChanged", Json.ofBool false ] ]
            "serverInfo", Json.obj [ "name", Json.ofString info.mcpServerName; "title", Json.ofString "Caddis"; "version", Json.ofString svc.Version ]
            "instructions", Json.ofString(instructionsOf info (DotnetSdk.current ()))
        ]

    /// Results have no structuredContent, see McpView.
    static let toolResult (isError: bool) (content: JsonNode list) : JsonNode =
        Json.obj [ "content", Json.arr content; "isError", Json.ofBool isError ]

    let runResult (notes: string list) (stdoutFrom: int64) (run: Run) : JsonNode =
        let isError =
            match run.Status with
            | Running
            | Succeeded -> false
            | CompileError
            | RuntimeError
            | Cancelled
            | TimedOut
            | Rejected -> true
        // a dialog blocks calls into the host app, so a run waiting for one would never start or finish
        let dialogNotes =
            if run.IsFinished then
                []
            else
                svc.Dialogs()
                |> List.map (fun d ->
                    let how = if info.mcp.dialog.IsSome then "Press a button with caddis_dialog, or ask the person" else "Ask the person"
                    $"{info.appName} shows a dialog, which blocks it until it is closed: {HostDialog.describe d} {how} to close it in {info.appName}.")
        let summary, images = McpView.run info.appName svc.Runs (notes @ dialogNotes) stdoutFrom run
        toolResult isError (text summary :: images)

    static let messageResult (result: Result<string, string>) : JsonNode =
        match result with
        | Ok msg -> toolResult false [ text msg ]
        | Error msg -> toolResult true [ text msg ]

    let callTool (req: Request) (requestId: JsonNode) (name: string) (args: JsonNode) : Task<JsonNode> =
        task {
            try
                match name with
                | "caddis_eval" ->
                    let a = Args.eval args
                    let a = { a with waitMs = a.waitMs |> Option.orElse (Some defaultWaitMs) }
                    let! run = svc.StartEval a
                    let key = requestKey req requestId
                    runsByRequest[key] <- run.Id
                    try
                        do! svc.WaitFor(run, a.waitMs)
                    finally
                        runsByRequest.TryRemove key |> ignore
                    let notes = [
                        if a.timeoutMs.IsSome && run.Mode = UiThread then
                            "timeoutMs was ignored: runs on the UI thread can't be cancelled. Switch to background mode with caddis_set_mode for runs that can time out."
                    ]
                    return runResult notes 0L run

                | "caddis_command" ->
                    let a = Args.commands info args
                    let run = svc.StartCommands a
                    let key = requestKey req requestId
                    runsByRequest[key] <- run.Id
                    try
                        do! svc.WaitFor(run, Some(a.waitMs |> Option.defaultValue defaultWaitMs))
                    finally
                        runsByRequest.TryRemove key |> ignore
                    return runResult [] 0L run

                | "caddis_get_run" ->
                    let runId = Json.tryString "runId" args |> Option.defaultValue ""
                    match svc.Runs.TryGet runId with
                    | Some run ->
                        do! svc.WaitFor(run, Some(Json.tryInt "waitMs" args |> Option.defaultValue defaultWaitMs))
                        let from = Json.tryInt "stdoutOffset" args |> Option.defaultValue 0
                        return runResult [] (int64 from) run
                    | None -> return toolResult true [ text $"Run '{runId}' not found. Only the latest runs are kept." ]

                | "caddis_check" ->
                    let! diagnostics = svc.Check(Json.tryString "code" args, Json.tryString "path" args)
                    return toolResult false [ text (McpView.check diagnostics) ]

                | "caddis_describe" ->
                    let description = svc.Describe(Json.tryString "name" args, Json.tryString "search" args, Json.tryString "filter" args)
                    return toolResult false [ text description ]

                | "caddis_status" ->
                    return toolResult false [ text (Json.toText (svc.Status [])) ]

                | "caddis_cancel" -> return messageResult (svc.Fsi.Cancel(Json.tryString "runId" args))

                | "caddis_reset" ->
                    do! svc.Fsi.Reset()
                    return messageResult (Ok "A new F# session was started.")

                | "caddis_set_mode" ->
                    let m = Args.mode args
                    do! svc.SetMode m
                    return messageResult (Ok $"Mode is '{ThreadMode.toString m}', a new F# session was started.")

                | "caddis_undo" ->
                    let! result = svc.Undo(Json.tryString "runId" args)
                    return messageResult result

                | "caddis_capture_viewport" ->
                    let! _, file =
                        svc.CaptureViewport(
                            Json.tryString "view" args |> Option.toObj,
                            Json.tryInt "width" args |> Option.defaultValue 0,
                            Json.tryInt "height" args |> Option.defaultValue 0,
                            Json.tryString "displayMode" args |> Option.toObj
                        )
                    return toolResult false [ McpView.image file; text (McpView.files "Saved to:" [ file ]) ]

                | "caddis_export" ->
                    let format = Json.tryString "format" args |> Option.toObj
                    let run = svc.StartExport(format, Json.tryString "name" args, Args.exportSelection info args)
                    do! svc.WaitFor(run, Some(Json.tryInt "waitMs" args |> Option.defaultValue defaultWaitMs))
                    return runResult [] 0L run

                | "caddis_dialog" ->
                    match Json.tryString "button" args with
                    | None ->
                        match svc.Dialogs() with
                        | [] -> return toolResult false [ text $"{info.appName} shows no dialog." ]
                        | ds -> return toolResult false [ text (ds |> List.map HostDialog.describe |> String.concat "\n") ]
                    | Some button -> return messageResult (svc.PressDialogButton(Json.tryString "dialog" args, button))

                | _ -> return toolResult true [ text $"Unknown tool '{name}'." ]
            with
            | BusyException runId ->
                return toolResult true [ text $"Run '{runId}' is still in progress. Wait for it with caddis_get_run, or cancel it with caddis_cancel (background mode only)." ]
            | e ->
                return toolResult true [ text e.Message ]
        }

    let handleMessage (req: Request) (msg: JsonNode) : Task<JsonNode option> =
        task {
            let id = Json.prop "id" msg |> Option.toObj
            let p = Json.prop "params" msg |> Option.toObj
            match Json.tryString "method" msg with
            | None ->
                // a response to a server request (there are none), or an invalid message
                return if isNull id then None else Some(errorResponse id -32600 "Invalid request: 'method' is missing.")
            | Some "initialize" -> return Some(response id (initialize p))
            | Some "ping" -> return Some(response id (JsonObject()))
            | Some "tools/list" -> return Some(response id (Json.obj [ "tools", Json.arr (List.map toolJson (tools ())) ]))
            | Some "tools/call" ->
                let name = Json.tryString "name" p |> Option.defaultValue ""
                if tools () |> List.exists (fun t -> t.name = name) then
                    let args = Json.prop "arguments" p |> Option.defaultWith (fun () -> JsonObject())
                    let! result = callTool req id name args
                    return Some(response id (withSessionNote req result))
                else
                    return Some(errorResponse id -32602 $"Unknown tool: '{name}'.")
            | Some "notifications/cancelled" ->
                match Json.prop "requestId" p with
                | Some rid ->
                    match runsByRequest.TryGetValue(requestKey req rid) with
                    | true, runId -> svc.Fsi.Cancel(Some runId) |> ignore
                    | _ -> ()
                | None -> ()
                return None
            | Some _ when isNull id -> return None // other notifications, e.g. notifications/initialized
            | Some m -> return Some(errorResponse id -32601 $"Method not found: {m}")
        }

    /// The tools as tools/list lists them, new nodes on each call.
    member _.Tools: JsonNode list = List.map toolJson (tools ())

    /// The instructions as initialize sends them.
    member _.Instructions: string = instructionsOf info (DotnetSdk.current ())

    member _.Handle(req: Request) : Task<Response> =
        task {
            match req.method with
            | "POST" ->
                let version = req.headers["MCP-Protocol-Version"]
                if not (isNull version) && not (Array.contains version supportedVersions) then
                    let supported = String.concat ", " supportedVersions
                    return JsonResponse(400, errorResponse null -32600 $"Unsupported MCP-Protocol-Version '{version}', supported are {supported}.")
                else
                    let parsed =
                        try Ok(Json.parse req.body)
                        with e -> Error e.Message
                    match parsed with
                    | Error msg -> return JsonResponse(400, errorResponse null -32700 $"Parse error: {msg}")
                    | Ok null -> return JsonResponse(400, errorResponse null -32600 "Invalid request: empty body.")
                    | Ok(:? JsonArray as batch) -> // batches were removed in protocol version 2025-06-18, but are cheap to support
                        let responses = ResizeArray<JsonNode>()
                        for m in batch do
                            let! r = handleMessage req m
                            r |> Option.iter responses.Add
                        if responses.Count = 0 then return EmptyResponse 202
                        else return JsonResponse(200, Json.arr responses)
                    | Ok msg ->
                        match! handleMessage req msg with
                        | Some r when Json.tryString "method" msg = Some "initialize" ->
                            let session = newSessionId ()
                            issuedSessions[session] <- true
                            return WithHeaders(JsonResponse(200, r), [ "Mcp-Session-Id", session ])
                        | Some r -> return JsonResponse(200, r)
                        | None -> return EmptyResponse 202
            | _ -> return Response.error 405 "Only POST is supported, this server offers no SSE stream."
        }
