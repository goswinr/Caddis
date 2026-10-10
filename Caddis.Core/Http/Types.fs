namespace Caddis.Http

open System
open System.Collections.Specialized
open System.Text.Json.Nodes
open Caddis

/// An HTTP request, independent of HttpListener so that the handlers can be tested without a server.
type Request = {
    /// Upper case, e.g. "POST"
    method: string
    /// e.g. "/eval"
    path: string
    query: NameValueCollection
    headers: NameValueCollection
    body: string
    /// e.g. "http://127.0.0.1:7810", used to build the download URLs of files.
    baseUrl: string
    }

type Response =
    | JsonResponse of status: int * body: JsonNode
    | FileResponse of path: string * mime: string
    | EmptyResponse of status: int
    /// A response with additional headers, e.g. Mcp-Session-Id.
    | WithHeaders of Response * headers: (string * string) list

module Response =

    let error (status: int) (message: string) = JsonResponse(status, Json.obj [ "error", Json.ofString message ])

    let message (text: string) = JsonResponse(200, Json.obj [ "message", Json.ofString text ])


/// Reads the arguments of the operations, the same for REST bodies and MCP tool arguments.
module Args =

    let eval (n: JsonNode) : EvalArgs = {
        code = Json.tryString "code" n
        path = Json.tryString "path" n
        timeoutMs = Json.tryInt "timeoutMs" n
        waitMs = Json.tryInt "waitMs" n
        recordUndo = Json.tryBool "recordUndo" n |> Option.defaultValue true
        }

    let mode (n: JsonNode) : ThreadMode =
        match Json.tryString "mode" n with
        | None -> invalidArg "mode" "Provide 'mode': 'ui' or 'background'."
        | Some s ->
            match ThreadMode.tryParse s with
            | Some m -> m
            | None -> invalidArg "mode" $"Unknown mode '{s}', use 'ui' or 'background'."

    /// 'objectIds' (array of GUIDs) wins over 'objects' ("all" or "selected", the default is "all").
    let exportSelection (n: JsonNode) : ExportSelection =
        match Json.tryStringArray "objectIds" n with
        | Some ids when ids.Length > 0 ->
            ids
            |> Array.map (fun s ->
                match Guid.TryParse s with
                | true, g -> g
                | _ -> invalidArg "objectIds" $"'{s}' in 'objectIds' is not an object id (a GUID).")
            |> ObjectIds
        | _ ->
            match Json.tryString "objects" n |> Option.map _.Trim().ToLowerInvariant() with
            | None
            | Some ""
            | Some "all" -> AllObjects
            | Some "selected" -> SelectedObjects
            | Some s -> invalidArg "objects" $"Unknown value '{s}' for 'objects', use 'all' or 'selected', or give 'objectIds'."
