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
/// A session id is handed out on initialize, it is only used to tell apart the request ids of different clients.
/// see https://modelcontextprotocol.io/specification/2025-06-18/basic/transports#streamable-http
type McpEndpoint(svc: CaddisService) =

    /// Newest first. The client's version is used if it is in this list, otherwise the newest one.
    static let supportedVersions = [| "2025-11-25"; "2025-06-18"; "2025-03-26"; "2024-11-05" |]

    /// caddis_eval and caddis_get_run return after this time if the run has not finished,
    /// below the 60 s request timeout that MCP clients commonly use.
    static let defaultWaitMs = 50_000

    static let instructions =
        String.concat "\n" [
            "Caddis runs F# code inside a running Rhino 8 in a persistent F# Interactive session. A person is usually working in the same Rhino window."
            "Values, functions and types defined in one caddis_eval call stay available in later calls until caddis_reset."
            "RhinoCommon is referenced, e.g. use Rhino.RhinoDoc.ActiveDoc for the current document. caddis_status shows its units and tolerance, check them before creating geometry."
            "The result shows F# Interactive's output, with the types of new definitions and the value of the last expression, printfn output as stdout, and what was printed to the Rhino command line, e.g. by RhinoApp.WriteLine or commands run via RhinoApp.RunScript. Long output is shortened and saved to a file."
            "Each run is one undo step in Rhino, the views are redrawn after it. Files saved to the folder Caddis.Script.OutDir are returned with the result, images inline."
            "On the 'ui' thread (the default) Rhino is blocked while code runs and a run can't be cancelled: an endless loop freezes Rhino until it is killed."
            "For long or risky code switch to 'background' mode with caddis_set_mode, there timeoutMs and caddis_cancel stop a run."
            "caddis_eval returns after waitMs (default 50 s) with status 'running' if the run has not finished. It keeps running, wait for the result with caddis_get_run."
            "Don't show modal dialogs or wait for user input in scripts."
            "Look up RhinoCommon types and members you are not sure about with caddis_describe instead of guessing their names, overloads or parameter types."
            "caddis_command runs Rhino commands like typed at the command line, for what commands do well, e.g. _Zoom, _SetDisplayMode, _Make2D, or to read what _What, _Length or _Area print. Use caddis_eval for logic, loops and geometry math. A command run in caddis_eval via RhinoApp.RunScript must get all its inputs in the macro, otherwise it waits until someone gives them in Rhino, or Caddis cancels it if the person turned that on."
            "Use caddis_check to type-check code against the session without running it, and caddis_capture_viewport to see the result."
        ]

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

    static let tools: McpTool list = [
        {
            name = "caddis_eval"
            title = "Evaluate F#"
            description =
                String.concat " " [
                    "Evaluates F# code in the persistent F# Interactive session inside Rhino and returns the result:"
                    "status ('ok', 'compileError', 'runtimeError', 'cancelled', 'timeout', 'rejected' or 'running'),"
                    "F# Interactive's output with the types of new definitions and the value of the last expression,"
                    "stdout, what was printed to the Rhino command line, stderr, compiler errors and warnings, the exception with the script line, and files saved to Caddis.Script.OutDir, images inline."
                    "Definitions stay available for later calls. RhinoCommon is referenced. NuGet packages can be used via #r \"nuget: Name\"."
                    "Each run is one undo step in Rhino. In background mode code that uses the Rhino UI must be wrapped in Caddis.Script.OnUi(fun () -> ...)."
                ]
            properties =
                codeAndPath
                @ [
                    "waitMs",
                    intProp
                        $"Return after this time with status 'running' if the run has not finished. It keeps running, get the result with caddis_get_run. 0 returns at once. Default: {defaultWaitMs}."
                    "timeoutMs", intProp "Background mode only: cancel the run after this time. Runs on the UI thread can't be cancelled."
                    "recordUndo", boolProp "Wrap the run in a Rhino undo record so that caddis_undo can revert it. Default: true."
                ]
            required = []
            readOnly = false
            destructive = true
            idempotent = false
        }
        {
            name = "caddis_command"
            title = "Run Rhino commands"
            description =
                String.concat " " [
                    "Runs Rhino commands as if typed at the Rhino command line, like a button macro."
                    "Each item of 'commands' is one command with its inputs, they run one after the other until one does not succeed."
                    "Use the scripted version of a command, with '-', where there is one: without it many commands open a dialog, which blocks Rhino until someone clicks."
                    "Prefix command and option names with '_' for their English names. Commands work on the selected objects, see 'select'."
                    "Returns the result of each command, the ids of the objects they created, changed and deleted, the selection if it changed,"
                    "and what they printed to the command line, e.g. the results of _What, _Length or _Area."
                    "A command that waits for input which the macro does not give is cancelled, the result shows the prompt with its options."
                    "Each command that changes the document is one undo step in Rhino, caddis_undo undoes all of a call. Not possible while a run is in progress."
                ]
            properties = [
                "commands",
                Json.obj [
                    "type", Json.ofString "array"
                    "items", Json.obj [ "type", Json.ofString "string" ]
                    "description",
                    Json.ofString
                        "The command macros, e.g. [\"-_Line 0,0,0 10,0,0\", \"_Zoom _All _Extents\"]. Inputs are separated by spaces: points as x,y,z, numbers, options with their value like '_Cap=_Yes', and '_Enter' to end a prompt."
                ]
                "select",
                Json.obj [
                    "type", Json.ofString "array"
                    "items", Json.obj [ "type", Json.ofString "string" ]
                    "description", Json.ofString "Ids (GUIDs) of objects to select before the first command, replacing the selection."
                ]
                "inputTimeoutMs",
                intProp
                    $"Cancel a command that waits this long for input. 0 waits until the person in Rhino gives it, e.g. picks points for '_Pause'. Default: {CaddisService.DefaultInputTimeoutMs}."
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
            description =
                String.concat " " [
                    "Looks up the types and members of the loaded assemblies, e.g. RhinoCommon, Eto or a NuGet package loaded via #r,"
                    "and returns their signatures in F# syntax with their XML doc summaries. Use it instead of guessing names, overloads or parameter types."
                    "Works while a run is in progress."
                ]
            properties = [
                "name",
                strProp
                    "A namespace ('Rhino.Geometry': its types), a type ('Brep' or 'Rhino.Geometry.Brep': its members) or a member ('Brep.CreateFromBox': all overloads with docs)."
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
            description =
                "Returns the state of the F# session (e.g. 'ready', 'evaluating'), the thread mode, the current run, versions of Rhino, F# and .NET, and the active document with its units, tolerance and layers."
            properties = []
            required = []
            readOnly = true
            destructive = false
            idempotent = true
        }
        {
            name = "caddis_cancel"
            title = "Cancel run"
            description = "Cancels the current run. Only runs in background mode can be cancelled, and Rhino commands while they wait for input. If an F# run does not stop within a few seconds the session is reset."
            properties = [ "runId", strProp "Only cancel if this run is the current one." ]
            required = []
            readOnly = false
            destructive = true
            idempotent = true
        }
        {
            name = "caddis_reset"
            title = "Reset session"
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
            description =
                "Sets on which thread code runs. 'ui': the Rhino UI thread, safest for RhinoCommon, but Rhino is blocked while code runs and runs can't be cancelled. 'background': a separate thread, runs can be cancelled and time out, Rhino stays responsive, UI calls must be wrapped in Caddis.Script.OnUi. Changing the mode resets the session."
            properties = [ "mode", enumOf [ "ui"; "background" ] "The thread mode." ]
            required = [ "mode" ]
            readOnly = false
            destructive = true
            idempotent = true
        }
        {
            name = "caddis_undo"
            title = "Undo run"
            description = "Undoes the changes a run made to the Rhino document. Only works if no other changes were made in Rhino after the run."
            properties = [ "runId", strProp "The run to undo. Default: the latest run that made changes." ]
            required = []
            readOnly = false
            destructive = true
            idempotent = false
        }
        {
            name = "caddis_capture_viewport"
            title = "Capture viewport"
            description = "Returns a PNG image of a Rhino viewport, to see the result of a script. Not possible while a run on the UI thread is in progress."
            properties = [
                "view", strProp "Name of the view, e.g. 'Perspective' or 'Top'. Default: the active view."
                "width",
                intProp
                    $"Width in pixels. Default: follows the aspect ratio of the view if 'height' is given, otherwise the size of the view, scaled down to at most {CaptureSize.defaultMaxEdge} pixels on the long edge."
                "height", intProp "Height in pixels. Default: follows the aspect ratio of the view if 'width' is given, otherwise like 'width'."
                "displayMode", strProp "Name of a display mode, e.g. 'Shaded', 'Rendered', 'Wireframe'. Default: the mode of the view."
            ]
            required = []
            readOnly = true
            destructive = false
            idempotent = true
        }
        {
            name = "caddis_export"
            title = "Export"
            description =
                "Exports objects of the Rhino document to a file in any format Rhino can write, picked by file extension, except PDF: it opens Rhino's print dialog. Returns the paths of the written files."
            properties = [
                "format", strProp "File extension of the format, e.g. '3dm', 'stl', 'obj', 'step', 'iges', 'dwg', 'dxf', '3mf', 'fbx', 'gltf', 'svg'."
                "name", strProp "File name without extension. Default: 'export'."
                "objects", enumOf [ "all"; "selected" ] "Which objects to export. Default: 'all'."
                "objectIds",
                Json.obj [
                    "type", Json.ofString "array"
                    "items", Json.obj [ "type", Json.ofString "string" ]
                    "description", Json.ofString "Ids (GUIDs) of the objects to export. Overrides 'objects'."
                ]
            ]
            required = [ "format" ]
            readOnly = false
            destructive = false
            idempotent = true
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

    /// Runs started by a tools/call, by session id and the JSON text of the request id, so that notifications/cancelled can cancel them.
    let runsByRequest = ConcurrentDictionary<string, string>()

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
            "serverInfo", Json.obj [ "name", Json.ofString "caddis"; "title", Json.ofString "Caddis"; "version", Json.ofString svc.Version ]
            "instructions", Json.ofString instructions
        ]

    /// Results have no structuredContent, see McpView.
    static let toolResult (isError: bool) (content: JsonNode list) : JsonNode =
        Json.obj [ "content", Json.arr content; "isError", Json.ofBool isError ]

    let runResult (req: Request) (notes: string list) (stdoutFrom: int64) (run: Run) : JsonNode =
        let isError =
            match run.Status with
            | Running
            | Succeeded -> false
            | CompileError
            | RuntimeError
            | Cancelled
            | TimedOut
            | Rejected -> true
        let summary, images = McpView.run svc.Runs req.baseUrl notes stdoutFrom run
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
                    return runResult req notes 0L run

                | "caddis_command" ->
                    let a = Args.commands args
                    let run = svc.StartCommands a
                    let key = requestKey req requestId
                    runsByRequest[key] <- run.Id
                    try
                        do! svc.WaitFor(run, Some(a.waitMs |> Option.defaultValue defaultWaitMs))
                    finally
                        runsByRequest.TryRemove key |> ignore
                    return runResult req [] 0L run

                | "caddis_get_run" ->
                    let runId = Json.tryString "runId" args |> Option.defaultValue ""
                    match svc.Runs.TryGet runId with
                    | Some run ->
                        do! svc.WaitFor(run, Some(Json.tryInt "waitMs" args |> Option.defaultValue defaultWaitMs))
                        let from = Json.tryInt "stdoutOffset" args |> Option.defaultValue 0
                        return runResult req [] (int64 from) run
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
                    let! id, file =
                        svc.CaptureViewport(
                            Json.tryString "view" args |> Option.toObj,
                            Json.tryInt "width" args |> Option.defaultValue 0,
                            Json.tryInt "height" args |> Option.defaultValue 0,
                            Json.tryString "displayMode" args |> Option.toObj
                        )
                    return toolResult false [ McpView.image file; text (McpView.files req.baseUrl id "Saved to:" [ file ]) ]

                | "caddis_export" ->
                    let format = Json.tryString "format" args |> Option.toObj
                    let! id, files = svc.Export(format, Json.tryString "name" args, Args.exportSelection args)
                    return toolResult false [ text (McpView.files req.baseUrl id "Exported to:" files) ]

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
            | Some "tools/list" -> return Some(response id (Json.obj [ "tools", Json.arr (List.map toolJson tools) ]))
            | Some "tools/call" ->
                let name = Json.tryString "name" p |> Option.defaultValue ""
                if tools |> List.exists (fun t -> t.name = name) then
                    let args = Json.prop "arguments" p |> Option.defaultWith (fun () -> JsonObject())
                    let! result = callTool req id name args
                    return Some(response id result)
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
                            return WithHeaders(JsonResponse(200, r), [ "Mcp-Session-Id", newSessionId () ])
                        | Some r -> return JsonResponse(200, r)
                        | None -> return EmptyResponse 202
            | _ -> return Response.error 405 "Only POST is supported, this server offers no SSE stream."
        }
