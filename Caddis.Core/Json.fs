namespace Caddis

open System
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Small helpers to build and read System.Text.Json nodes, so that no extra JSON library is needed.
module Json =

    let ofString (s: string) : JsonNode = if isNull s then null else JsonValue.Create s :> JsonNode
    let ofInt (i: int) : JsonNode = JsonValue.Create i :> JsonNode
    let ofInt64 (i: int64) : JsonNode = JsonValue.Create i :> JsonNode
    let ofBool (b: bool) : JsonNode = JsonValue.Create b :> JsonNode
    let ofFloat (f: float) : JsonNode = JsonValue.Create f :> JsonNode
    let ofOption (f: 'T -> JsonNode) (o: 'T option) : JsonNode = match o with Some v -> f v | None -> null

    let obj (props: (string * JsonNode) list) : JsonObject =
        let o = JsonObject()
        for k, v in props do o[k] <- v
        o

    let arr (items: JsonNode seq) : JsonArray =
        let a = JsonArray()
        for i in items do a.Add i
        a

    let ofStrings (items: string seq) : JsonArray = items |> Seq.map ofString |> arr

    /// Returns null for empty or white space text. Raises a JsonException on invalid JSON.
    let parse (text: string) : JsonNode =
        if String.IsNullOrWhiteSpace text then null else JsonNode.Parse text

    /// The value of a property if the node is an object and the property is not null.
    let prop (name: string) (node: JsonNode) : JsonNode option =
        match node with
        | :? JsonObject as o ->
            // .NET 10 has an overload with the index too
            let mutable v: JsonNode = null
            if o.TryGetPropertyValue(name, &v) && not (isNull v) then Some v else None
        | _ -> None

    let tryString (name: string) (node: JsonNode) : string option =
        prop name node
        |> Option.bind (fun v ->
            match v.GetValueKind() with
            | JsonValueKind.String -> Some(v.GetValue<string>())
            | JsonValueKind.Null -> None
            | _ -> Some(v.ToJsonString()))

    let tryInt (name: string) (node: JsonNode) : int option =
        prop name node
        |> Option.bind (fun v ->
            match v.GetValueKind() with
            | JsonValueKind.Number ->
                try Some(v.GetValue<int>())
                with _ -> try Some(int (v.GetValue<float>())) with _ -> None
            | JsonValueKind.String ->
                match Int32.TryParse(v.GetValue<string>()) with
                | true, i -> Some i
                | _ -> None
            | _ -> None)

    let tryBool (name: string) (node: JsonNode) : bool option =
        prop name node
        |> Option.bind (fun v ->
            match v.GetValueKind() with
            | JsonValueKind.True -> Some true
            | JsonValueKind.False -> Some false
            | JsonValueKind.String ->
                match Boolean.TryParse(v.GetValue<string>()) with
                | true, b -> Some b
                | _ -> None
            | _ -> None)

    let tryStringArray (name: string) (node: JsonNode) : string[] option =
        match prop name node with
        | Some(:? JsonArray as a) ->
            a
            |> Seq.choose (fun v ->
                if isNull v || v.GetValueKind() <> JsonValueKind.String then None
                else Some(v.GetValue<string>()))
            |> Array.ofSeq
            |> Some
        | _ -> None

    // Relaxed escaping so that code and output in responses stay readable, e.g. '<' is not written as <.
    let private indented =
        JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private compact =
        JsonSerializerOptions(WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let toText (node: JsonNode) : string = if isNull node then "null" else node.ToJsonString indented

    let toCompactText (node: JsonNode) : string = if isNull node then "null" else node.ToJsonString compact
