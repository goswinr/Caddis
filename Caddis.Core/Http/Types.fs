namespace Caddis.Http

open System.Collections.Specialized
open System.Text.Json
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

    /// Unlike settings parsing, request arrays must not silently discard invalid entries.
    let private stringArray (name: string) (n: JsonNode) : string[] option =
        match n with
        | :? JsonObject as o when o.ContainsKey name ->
            match o[name] with
            | :? JsonArray as a ->
                a
                |> Seq.map (fun v ->
                    if isNull v || v.GetValueKind() <> JsonValueKind.String then
                        invalidArg name $"Every entry in '{name}' must be a string."
                    v.GetValue<string>())
                |> Array.ofSeq
                |> Some
            | _ -> invalidArg name $"'{name}' must be an array of strings."
        | _ -> None

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
            match Json.prop "commands" n with
            | Some v when v.GetValueKind() = JsonValueKind.String -> [| v.GetValue<string>() |]
            | _ -> stringArray "commands" n |> Option.defaultValue [||]
        select = stringArray "select" n |> Option.map (objectIds info "select")
        inputTimeoutMs = Json.tryInt "inputTimeoutMs" n
        waitMs = Json.tryInt "waitMs" n
        }

    /// 'objectIds' (array of ids of the host) wins over 'objects' ("all" or "selected", the default is "all").
    let exportSelection (info: HostInfo) (n: JsonNode) : ExportSelection =
        match stringArray "objectIds" n with
        | Some ids when ids.Length = 0 -> invalidArg "objectIds" "'objectIds' must contain at least one object id."
        | Some ids -> ObjectIds(objectIds info "objectIds" ids)
        | None ->
            match Json.tryString "objects" n |> Option.map _.Trim().ToLowerInvariant() with
            | None
            | Some ""
            | Some "all" -> AllObjects
            | Some "selected" -> SelectedObjects
            | Some s -> invalidArg "objects" $"Unknown value '{s}' for 'objects', use 'all' or 'selected', or give 'objectIds'."
