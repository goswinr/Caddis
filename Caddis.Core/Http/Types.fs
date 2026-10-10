namespace Caddis.Http

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

    /// The ids, trimmed, if all are object ids of the host.
    let private objectIds (info: HostInfo) (name: string) (ids: string[]) : string[] =
        ids
        |> Array.map (fun s ->
            let id = if isNull s then "" else s.Trim()
            if info.isObjectId id then id else invalidArg name $"'{s}' in '{name}' is not {info.objectIdKind}.")

    /// 'commands' is an array of macros, or a single macro as string.
    let commands (info: HostInfo) (n: JsonNode) : CommandArgs = {
        commands =
            match Json.tryStringArray "commands" n with
            | Some a -> a
            | None -> Json.tryString "commands" n |> Option.toArray
        select = Json.tryStringArray "select" n |> Option.map (objectIds info "select")
        inputTimeoutMs = Json.tryInt "inputTimeoutMs" n
        waitMs = Json.tryInt "waitMs" n
        }

    /// 'objectIds' (array of ids of the host) wins over 'objects' ("all" or "selected", the default is "all").
    let exportSelection (info: HostInfo) (n: JsonNode) : ExportSelection =
        match Json.tryStringArray "objectIds" n with
        | Some ids when ids.Length > 0 -> ObjectIds(objectIds info "objectIds" ids)
        | _ ->
            match Json.tryString "objects" n |> Option.map _.Trim().ToLowerInvariant() with
            | None
            | Some ""
            | Some "all" -> AllObjects
            | Some "selected" -> SelectedObjects
            | Some s -> invalidArg "objects" $"Unknown value '{s}' for 'objects', use 'all' or 'selected', or give 'objectIds'."
