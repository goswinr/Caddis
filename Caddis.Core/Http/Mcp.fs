namespace Caddis.Http

open System
open System.Collections.Concurrent
open System.IO
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
    }

/// The MCP endpoint: JSON-RPC over Streamable HTTP, answering every request with a single JSON response, no SSE.
/// Only tools are offered, no resources or prompts.
/// see https://modelcontextprotocol.io/specification/2025-06-18/basic/transports#streamable-http
type McpEndpoint(svc: CaddisService) =

    /// Newest first. The client's version is used if it is in this list, otherwise the newest one.
    static let supportedVersions = [| "2025-11-25"; "2025-06-18"; "2025-03-26"; "2024-11-05" |]

    /// Clients from this version on understand structuredContent and resource_link content.
    static let structuredContentSince = "2025-06-18"

    static let instructions =
        String.concat "\n" [
            "Caddis runs F# code inside a running Rhino 8 (or another host app) in a persistent F# Interactive session."
            "Values, functions and types defined in one caddis_eval call stay available in later calls until caddis_reset."
            "RhinoCommon is referenced, e.g. use Rhino.RhinoDoc.ActiveDoc for the current document."
            "printfn output is returned as stdout, the value of the last expression as value."
            "Files saved to the folder Caddis.Script.OutDir are returned with the result."
            "Use caddis_check to find compile errors without running the code, and caddis_capture_viewport to see the result."
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
                    "status ('ok', 'compileError', 'runtimeError', 'cancelled', 'timeout', 'rejected' or 'running'), the value of the last expression,"
                    "stdout, stderr, compiler diagnostics, exception details with the script line, and files saved to Caddis.Script.OutDir."
                    "Definitions stay available for later calls. RhinoCommon is referenced. NuGet packages can be used via #r \"nuget: Name\"."
                    "Each run is one undo step in Rhino. In background mode code that uses the Rhino UI must be wrapped in Caddis.Script.OnUi(fun () -> ...)."
                ]
            properties =
                codeAndPath
                @ [
                    "timeoutMs", intProp "Background mode: the run is cancelled after this time. UI-thread mode: runs can't be cancelled, the result is returned with status 'running' after this time, use caddis_get_run later."
                    "recordUndo", boolProp "Wrap the run in a Rhino undo record so that caddis_undo can revert it. Default: true."
                ]
            required = []
            readOnly = false
            destructive = true
        }
        {
            name = "caddis_get_run"
            title = "Get a run"
            description = "Returns the result of a run, e.g. of one that was still running when caddis_eval returned."
            properties = [ "runId", strProp "The id of the run, e.g. 'r12'." ]
            required = [ "runId" ]
            readOnly = true
            destructive = false
        }
        {
            name = "caddis_check"
            title = "Type-check F#"
            description = "Parses and type-checks F# code without running it. Returns the compiler errors and warnings. Lines and columns are 1-based."
            properties = codeAndPath
            required = []
            readOnly = true
            destructive = false
        }
        {
            name = "caddis_status"
            title = "Status"
            description = "Returns the state of the F# session (e.g. 'ready', 'evaluating'), the thread mode, the current run, versions of Rhino, F# and .NET, and the active document."
            properties = []
            required = []
            readOnly = true
            destructive = false
        }
        {
            name = "caddis_cancel"
            title = "Cancel run"
            description = "Cancels the current run. Only runs in background mode can be cancelled. If the run does not stop within a few seconds the session is reset."
            properties = [ "runId", strProp "Only cancel if this run is the current one." ]
            required = []
            readOnly = false
            destructive = true
        }
        {
            name = "caddis_reset"
            title = "Reset session"
            description = "Starts a new F# Interactive session. All values, functions and types defined so far are lost. Use it if the session is in a bad state."
            properties = []
            required = []
            readOnly = false
            destructive = true
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
        }
        {
            name = "caddis_undo"
            title = "Undo run"
            description = "Undoes the changes a run made to the Rhino document. Only works if no other changes were made in Rhino after the run."
            properties = [ "runId", strProp "The run to undo. Default: the latest run that made changes." ]
            required = []
            readOnly = false
            destructive = true
        }
        {
            name = "caddis_capture_viewport"
            title = "Capture viewport"
            description = "Returns a PNG image of a Rhino viewport, to see the result of a script."
            properties = [
                "view", strProp "Name of the view, e.g. 'Perspective' or 'Top'. Default: the active view."
                "width", intProp "Width in pixels. Default: the current width of the view."
                "height", intProp "Height in pixels. Default: the current height of the view."
                "displayMode", strProp "Name of a display mode, e.g. 'Shaded', 'Rendered', 'Wireframe'. Default: the mode of the view."
            ]
            required = []
            readOnly = true
            destructive = false
        }
        {
            name = "caddis_export"
            title = "Export"
            description = "Exports objects of the Rhino document to a file in any format Rhino can write, picked by file extension. Returns the local path and download URL of the written files."
            properties = [
                "format", strProp "File extension of the format, e.g. '3dm', 'stl', 'obj', 'step', 'iges', 'dwg', 'dxf', '3mf', 'fbx', 'gltf', 'pdf', 'svg'."
                "name", strProp "File name without extension. Default: 'export'."
                "objects", enumOf [ "all"; "selected" ] "Which objects to export. Default: 'all'."
                "objectIds", Json.obj [ "type", Json.ofString "array"; "items", Json.obj [ "type", Json.ofString "string" ]; "description", Json.ofString "Ids (GUIDs) of the objects to export. Overrides 'objects'." ]
            ]
            required = [ "format" ]
            readOnly = false
            destructive = false
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

    /// Runs started by a tools/call, by the JSON text of the request id, so that notifications/cancelled can cancel them.
    let runsByRequest = ConcurrentDictionary<string, string>()

    let initialize (p: JsonNode) : JsonNode =
        let requested = Json.tryString "protocolVersion" p |> Option.defaultValue supportedVersions[0]
        let version = if Array.contains requested supportedVersions then requested else supportedVersions[0]
        Json.obj [
            "protocolVersion", Json.ofString version
            "capabilities", Json.obj [ "tools", Json.obj [ "listChanged", Json.ofBool false ] ]
            "serverInfo", Json.obj [ "name", Json.ofString "caddis"; "title", Json.ofString "Caddis"; "version", Json.ofString svc.Version ]
            "instructions", Json.ofString instructions
        ]

    /// Clients send their protocol version in a header after initialization, if it is missing 2025-03-26 is assumed.
    let isModern (req: Request) =
        match req.headers["MCP-Protocol-Version"] with
        | null -> false
        | v -> String.CompareOrdinal(v, structuredContentSince) >= 0

    let toolResult (req: Request) (isError: bool) (content: JsonNode list) (structured: JsonObject option) : JsonNode =
        Json.obj [
            "content", Json.arr content
            if structured.IsSome && isModern req then "structuredContent", (structured.Value :> JsonNode)
            "isError", Json.ofBool isError
        ]

    let resourceLinks (req: Request) (id: string) (files: FileItem seq) : JsonNode list =
        if isModern req then
            [
                for f in files do
                    Json.obj [
                        "type", Json.ofString "resource_link"
                        "uri", Json.ofString (FileItem.url req.baseUrl id f)
                        "name", Json.ofString f.name
                        "mimeType", Json.ofString f.mime
                        "description", Json.ofString $"Local path: {f.path}"
                    ]
            ]
        else
            []

    let runResult (req: Request) (run: Run) : JsonNode =
        let isError =
            match run.Status with
            | Running
            | Succeeded -> false
            | CompileError
            | RuntimeError
            | Cancelled
            | TimedOut
            | Rejected -> true
        let content = text (Json.toText (run.ToJson req.baseUrl)) :: resourceLinks req run.Id run.Files
        toolResult req isError content (Some(run.ToJson req.baseUrl))

    let messageResult (req: Request) (result: Result<string, string>) : JsonNode =
        match result with
        | Ok msg -> toolResult req false [ text msg ] None
        | Error msg -> toolResult req true [ text msg ] None

    let callTool (req: Request) (requestId: JsonNode) (name: string) (args: JsonNode) : Task<JsonNode> =
        task {
            try
                match name with
                | "caddis_eval" ->
                    let a = Args.eval args
                    let! run = svc.StartEval a
                    let key = if isNull requestId then "" else requestId.ToJsonString()
                    runsByRequest[key] <- run.Id
                    try
                        do! svc.WaitFor(run, a.timeoutMs)
                    finally
                        runsByRequest.TryRemove key |> ignore
                    return runResult req run

                | "caddis_get_run" ->
                    match Json.tryString "runId" args |> Option.bind svc.Runs.TryGet with
                    | Some run -> return runResult req run
                    | None -> return toolResult req true [ text "Run not found." ] None

                | "caddis_check" ->
                    let! diagnostics = svc.Check(Json.tryString "code" args, Json.tryString "path" args)
                    let hasErrors = diagnostics |> Array.exists (fun d -> d.severity = "error")
                    let result = Json.obj [ "ok", Json.ofBool (not hasErrors); "diagnostics", Json.arr (Seq.map Diagnostic.toJson diagnostics) ]
                    let summary = if diagnostics.Length = 0 then "No errors or warnings." else Json.toText result
                    return toolResult req false [ text summary ] (Some result)

                | "caddis_status" ->
                    let status = svc.Status []
                    return toolResult req false [ text (Json.toText status) ] (Some(svc.Status []))

                | "caddis_cancel" -> return messageResult req (svc.Fsi.Cancel(Json.tryString "runId" args))

                | "caddis_reset" ->
                    do! svc.Fsi.Reset()
                    return messageResult req (Ok "A new F# session was started.")

                | "caddis_set_mode" ->
                    let m = Args.mode args
                    do! svc.SetMode m
                    return messageResult req (Ok $"Mode is '{ThreadMode.toString m}', a new F# session was started.")

                | "caddis_undo" ->
                    let! result = svc.Undo(Json.tryString "runId" args)
                    return messageResult req result

                | "caddis_capture_viewport" ->
                    let! id, file =
                        svc.CaptureViewport(
                            Json.tryString "view" args |> Option.toObj,
                            Json.tryInt "width" args |> Option.defaultValue 0,
                            Json.tryInt "height" args |> Option.defaultValue 0,
                            Json.tryString "displayMode" args |> Option.toObj
                        )
                    let image =
                        Json.obj [
                            "type", Json.ofString "image"
                            "data", Json.ofString (Convert.ToBase64String(File.ReadAllBytes file.path))
                            "mimeType", Json.ofString file.mime
                        ]
                    let info = $"Saved to {file.path}\nURL: {FileItem.url req.baseUrl id file}"
                    return toolResult req false [ image; text info ] None

                | "caddis_export" ->
                    let format = Json.tryString "format" args |> Option.toObj
                    let! id, files = svc.Export(format, Json.tryString "name" args, Args.exportSelection args)
                    let result = Json.obj [ "id", Json.ofString id; "files", Json.arr (files |> Seq.map (FileItem.toJson req.baseUrl id)) ]
                    return toolResult req false (text (Json.toText result) :: resourceLinks req id files) (Some result)

                | _ -> return toolResult req true [ text $"Unknown tool '{name}'." ] None
            with
            | BusyException runId ->
                return toolResult req true [ text $"Run '{runId}' is still in progress. Wait for it with caddis_get_run, or cancel it with caddis_cancel (background mode only)." ] None
            | e ->
                return toolResult req true [ text e.Message ] None
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
                let args = Json.prop "arguments" p |> Option.defaultWith (fun () -> JsonObject())
                let! result = callTool req id name args
                return Some(response id result)
            | Some "notifications/cancelled" ->
                match Json.prop "requestId" p with
                | Some rid ->
                    match runsByRequest.TryGetValue(rid.ToJsonString()) with
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
                    | Some r -> return JsonResponse(200, r)
                    | None -> return EmptyResponse 202
            | _ -> return Response.error 405 "Only POST is supported, this server offers no SSE stream and no sessions."
        }
