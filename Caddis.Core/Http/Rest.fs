/// The plain REST endpoints, e.g. for curl and scripts.
module Caddis.Http.Rest

open System
open System.Threading.Tasks
open System.Text.Json.Nodes
open Caddis

let private segments (path: string) =
    path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray |> List.map Uri.UnescapeDataString

let private queryInt (req: Request) (name: string) =
    match Int32.TryParse req.query[name] with
    | true, i -> i
    | _ -> 0

/// A short description of all endpoints, returned for GET /help
let private help () : JsonNode =
    Json.obj [
        "GET /status", Json.ofString "State of the server and the F# session."
        "POST /eval",
        Json.ofString
            "Evaluate F#. Body: {code?, path?, waitMs?, timeoutMs?, recordUndo?}. Returns the run, with status 202 if it is still running after waitMs (default: wait until it finished). timeoutMs cancels background runs."
        "POST /command",
        Json.ofString
            "Run Rhino commands like typed at the command line. Body: {commands: ['-_Line 0,0,0 10,0,0'], select?: [guid], inputTimeoutMs?, waitMs?}. Returns the run like /eval."
        "GET /runs", Json.ofString "The recent runs."
        "GET /runs/{id}", Json.ofString "One run. Query: waitMs, to wait for a run that is still running (default 0)."
        "POST /cancel", Json.ofString "Cancel the current run (background mode only). Body: {runId?}"
        "POST /reset", Json.ofString "Start a new F# session, all values are lost."
        "POST /mode", Json.ofString "Body: {mode: 'ui'|'background'}. Resets the session."
        "POST /check", Json.ofString "Type-check against the current session without running. Body: {code?, path?}"
        "GET /describe",
        Json.ofString "Signatures and docs of a namespace, type or member, e.g. 'Brep.CreateFromBox'. Query: name, filter, or search to find names."
        "POST /undo", Json.ofString "Undo a run in Rhino. Body: {runId?}, default is the latest run with changes."
        "GET /viewport", Json.ofString "PNG of a viewport. Query: view, width, height, displayMode."
        "POST /export", Json.ofString "Export to a file. Body: {format, name?, objects?: 'all'|'selected', objectIds?: [guid]}"
        "GET /files/{id}/{name}", Json.ofString "Download a file of a run, capture or export."
        "POST /mcp", Json.ofString "MCP endpoint (Streamable HTTP, JSON-RPC)."
    ]

/// Handles all requests except /mcp. Exceptions are turned into responses by the server.
let handle (svc: CaddisService) (serverInfo: unit -> (string * JsonNode) list) (req: Request) : Task<Response> =
    task {
        let body () = Json.parse req.body
        match req.method, segments req.path with
        | "GET", []
        | "GET", [ "status" ] -> return JsonResponse(200, svc.Status(serverInfo ()))

        | "GET", [ "help" ] -> return JsonResponse(200, help ())

        | "POST", [ "eval" ] ->
            let! run = svc.Eval(Args.eval (body ()))
            return JsonResponse((if run.IsFinished then 200 else 202), run.ToJson req.baseUrl)

        | "POST", [ "command" ] ->
            let a = Args.commands (body ())
            let run = svc.StartCommands a
            do! svc.WaitFor(run, a.waitMs)
            return JsonResponse((if run.IsFinished then 200 else 202), run.ToJson req.baseUrl)

        | "GET", [ "runs" ] ->
            let items = svc.Runs.All |> Array.rev |> Seq.map svc.RunSummary
            return JsonResponse(200, Json.obj [ "runs", Json.arr items ])

        | "GET", [ "runs"; id ] ->
            match svc.Runs.TryGet id with
            | Some r ->
                do! svc.WaitFor(r, Some(queryInt req "waitMs"))
                return JsonResponse((if r.IsFinished then 200 else 202), r.ToJson req.baseUrl)
            | None -> return Response.error 404 $"Run '{id}' not found."

        | "POST", [ "cancel" ] ->
            match svc.Fsi.Cancel(Json.tryString "runId" (body ())) with
            | Ok msg -> return Response.message msg
            | Error msg -> return Response.error 409 msg

        | "POST", [ "reset" ] ->
            do! svc.Fsi.Reset()
            return Response.message "A new F# session was started."

        | "POST", [ "mode" ] ->
            let m = Args.mode (body ())
            do! svc.SetMode m
            return Response.message $"Mode is '{ThreadMode.toString m}', a new F# session was started."

        | "POST", [ "check" ] ->
            let b = body ()
            let! diagnostics = svc.Check(Json.tryString "code" b, Json.tryString "path" b)
            let hasErrors = diagnostics |> Array.exists (fun d -> d.severity = "error")
            return JsonResponse(200, Json.obj [ "ok", Json.ofBool (not hasErrors); "diagnostics", Json.arr (Seq.map Diagnostic.toJson diagnostics) ])

        | "GET", [ "describe" ] ->
            let q (name: string) = Option.ofObj req.query[name]
            return JsonResponse(200, Json.obj [ "text", Json.ofString (svc.Describe(q "name", q "search", q "filter")) ])

        | "POST", [ "undo" ] ->
            match! svc.Undo(Json.tryString "runId" (body ())) with
            | Ok msg -> return Response.message msg
            | Error msg -> return Response.error 409 msg

        | "GET", [ "viewport" ] ->
            let! _, file = svc.CaptureViewport(req.query["view"], queryInt req "width", queryInt req "height", req.query["displayMode"])
            return FileResponse(file.path, file.mime)

        | "POST", [ "export" ] ->
            let b = body ()
            let! id, files = svc.Export(Json.tryString "format" b |> Option.toObj, Json.tryString "name" b, Args.exportSelection b)
            return JsonResponse(200, Json.obj [ "id", Json.ofString id; "files", Json.arr (files |> Seq.map (FileItem.toJson req.baseUrl id)) ])

        | "GET", "files" :: id :: name when not name.IsEmpty ->
            match svc.Runs.TryGetFile(id, String.concat "/" name) with
            | Some path -> return FileResponse(path, FileItem.mimeOf path)
            | None -> return Response.error 404 "File not found."

        | _ -> return Response.error 404 $"Not found: {req.method} {req.path}. See GET /help"
    }
