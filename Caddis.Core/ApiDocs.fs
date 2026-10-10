namespace Caddis

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.ComponentModel
open System.IO
open System.Reflection
open System.Text
open System.Text.RegularExpressions
open System.Xml
open System.Xml.Linq

/// Looks up the types and members of the loaded assemblies, e.g. RhinoCommon, and describes them in F# syntax with their XML docs,
/// so that AI agents can check an API instead of guessing it. Uses only reflection, so it works while a run is in progress.
type ApiDocs(host: ICaddisHost) =

    /// The longest description returned. Longer ones are cut at a line end.
    static let maxChars = 16_000
    static let maxSearchResults = 60
    static let maxSuggestions = 8

    static let anyMember = BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static
    static let declaredOnly = anyMember ||| BindingFlags.DeclaredOnly

    static let collapse (s: string) = Regex.Replace(s.Trim(), @"\s+", " ")

    // --- names in F# syntax ---

    static let abbreviations =
        dict [
            "System.Void", "unit"
            "Microsoft.FSharp.Core.Unit", "unit"
            "System.Boolean", "bool"
            "System.Byte", "byte"
            "System.SByte", "sbyte"
            "System.Int16", "int16"
            "System.UInt16", "uint16"
            "System.Int32", "int"
            "System.UInt32", "uint32"
            "System.Int64", "int64"
            "System.UInt64", "uint64"
            "System.Single", "float32"
            "System.Double", "float"
            "System.Decimal", "decimal"
            "System.Char", "char"
            "System.String", "string"
            "System.Object", "obj"
            "System.IntPtr", "nativeint"
            "System.UIntPtr", "unativeint"
        ]

    /// The name without generic arity and with the declaring types of nested types, e.g. 'List' or 'Brep.ExtrudeFlags'.
    static let rec plainName (t: Type) =
        let n =
            match t.Name.IndexOf '`' with
            | -1 -> t.Name
            | i -> t.Name.Substring(0, i)
        if t.IsNested && not t.IsGenericParameter then plainName t.DeclaringType + "." + n else n

    /// A type in F# syntax, e.g. 'float', 'Point3d[]', 'List<Curve>', '(unit -> int)' or 'int option'.
    static let rec fsName (t: Type) : string =
        if t.IsByRef then
            $"byref<{fsName (t.GetElementType())}>"
        elif t.IsArray then
            fsName (t.GetElementType()) + "[" + String(',', t.GetArrayRank() - 1) + "]"
        elif t.IsPointer then
            $"nativeptr<{fsName (t.GetElementType())}>"
        elif t.IsGenericParameter then
            "'" + t.Name
        else
            match abbreviations.TryGetValue(if isNull t.FullName then "" else t.FullName) with
            | true, a -> a
            | _ when t.IsGenericType ->
                let args = t.GetGenericArguments() |> Array.map fsName
                match t.GetGenericTypeDefinition().FullName, args with
                | "Microsoft.FSharp.Core.FSharpFunc`2", [| a; b |] -> $"({a} -> {b})"
                | "Microsoft.FSharp.Core.FSharpOption`1", [| a |] -> $"{a} option"
                | "Microsoft.FSharp.Collections.FSharpList`1", [| a |] -> $"{a} list"
                | n, _ when not (isNull n) && n.StartsWith "System.Tuple`" -> "(" + String.concat " * " args + ")"
                | _ -> plainName t + "<" + String.concat ", " args + ">"
            | _ -> plainName t

    static let namespacePrefix (t: Type) =
        if String.IsNullOrEmpty t.Namespace then "" else t.Namespace + "."

    /// e.g. 'Rhino.Geometry.Brep' or "System.Collections.Generic.List<'T>"
    static let displayName (t: Type) =
        let typeParams =
            if t.IsGenericTypeDefinition then "<" + (t.GetGenericArguments() |> Array.map fsName |> String.concat ", ") + ">" else ""
        namespacePrefix t + plainName t + typeParams

    /// The name a query is matched against, e.g. 'System.Collections.Generic.List'.
    static let matchName (t: Type) = namespacePrefix t + plainName t

    static let kindOf (t: Type) =
        if t.IsEnum then "enum"
        elif t.IsValueType then "struct"
        elif t.IsInterface then "interface"
        elif typeof<Delegate>.IsAssignableFrom t then "delegate"
        elif t.IsAbstract && t.IsSealed then "static class"
        elif t.IsAbstract then "abstract class"
        else "class"

    // --- signatures in F# syntax ---

    static let parameter (p: ParameterInfo) =
        let name = if String.IsNullOrEmpty p.Name then "arg" else p.Name
        let t = fsName p.ParameterType
        if p.IsOut && p.ParameterType.IsByRef then $"[<Out>] {name}: {t}"
        elif p.IsDefined(typeof<ParamArrayAttribute>, false) then $"[<ParamArray>] {name}: {t}"
        elif p.IsOptional then $"?{name}: {t}"
        else $"{name}: {t}"

    /// How F# functions group their arguments, e.g. [|1; 2|] for 'f a (b, c)'.
    static let argumentCounts (m: MethodBase) : int[] =
        try
            m.CustomAttributes
            |> Seq.tryFind (fun a -> a.AttributeType.FullName = "Microsoft.FSharp.Core.CompilationArgumentCountsAttribute")
            |> Option.map (fun a ->
                match a.ConstructorArguments[0].Value with
                | :? IReadOnlyCollection<CustomAttributeTypedArgument> as xs -> xs |> Seq.map (fun x -> x.Value :?> int) |> Array.ofSeq
                | _ -> [||])
            |> Option.defaultValue [||]
        with _ ->
            [||]

    /// The parameters tupled, or curried for F# functions with several argument groups.
    static let parameters (m: MethodBase) =
        let ps = m.GetParameters()
        let tupled (ps: ParameterInfo[]) =
            if ps.Length = 0 then "unit" else ps |> Array.map parameter |> String.concat " * "
        let counts = argumentCounts m
        if counts.Length > 1 && Array.sum counts = ps.Length then
            let groups = ResizeArray<string>()
            let mutable i = 0
            for c in counts do
                groups.Add(tupled ps[i .. i + c - 1])
                i <- i + c
            String.concat " -> " groups
        else
            tupled ps

    static let operatorNames =
        dict [
            "op_Addition", "(+)"
            "op_Subtraction", "(-)"
            "op_Multiply", "(*)"
            "op_Division", "(/)"
            "op_Modulus", "(%)"
            "op_UnaryNegation", "(~-)"
            "op_UnaryPlus", "(~+)"
        ]

    static let isStatic (m: MemberInfo) =
        match m with
        | :? MethodBase as mb -> mb.IsStatic
        | :? PropertyInfo as p -> (p.GetMethod |> Option.ofObj |> Option.defaultValue p.SetMethod).IsStatic
        | :? FieldInfo as f -> f.IsStatic
        | :? EventInfo as e -> e.AddMethod |> Option.ofObj |> Option.exists _.IsStatic
        | _ -> false

    static let signature (m: MemberInfo) : string =
        let prefix = if isStatic m then "static " else ""
        match m with
        | :? ConstructorInfo as c -> $"new: {parameters c} -> {fsName c.DeclaringType}"
        | :? MethodInfo as mi ->
            let name =
                match operatorNames.TryGetValue mi.Name with
                | true, op -> op
                | _ -> mi.Name
            let generics =
                if mi.IsGenericMethodDefinition then "<" + (mi.GetGenericArguments() |> Array.map fsName |> String.concat ", ") + ">" else ""
            let isAbstract = if mi.IsAbstract && not mi.DeclaringType.IsInterface then "abstract " else ""
            $"{prefix}{isAbstract}member {name}{generics}: {parameters mi} -> {fsName mi.ReturnType}"
        | :? PropertyInfo as p ->
            let accessors =
                match p.GetGetMethod(), p.GetSetMethod() with
                | null, _ -> "with set"
                | _, null -> "with get"
                | _ -> "with get, set"
            let index =
                match p.GetIndexParameters() with
                | [||] -> ""
                | ps -> (ps |> Array.map parameter |> String.concat " * ") + " -> "
            $"{prefix}member {p.Name}: {index}{fsName p.PropertyType} {accessors}"
        | :? FieldInfo as f ->
            let value =
                if f.IsLiteral then
                    match (try f.GetRawConstantValue() with _ -> null) with
                    | null -> ""
                    | :? string as s -> $" = \"{s}\""
                    | v -> $" = {v}"
                else
                    ""
            $"{prefix}val {f.Name}: {fsName f.FieldType}{value}"
        | :? EventInfo as e -> $"{prefix}event {e.Name}: {fsName e.EventHandlerType}"
        | :? Type as t -> $"type {plainName t} ({kindOf t})"
        | _ -> m.Name

    static let hasAttribute (attributeType: Type) (m: MemberInfo) =
        try m.CustomAttributes |> Seq.exists (fun a -> a.AttributeType = attributeType) with _ -> false

    static let isObsolete (m: MemberInfo) = hasAttribute typeof<ObsoleteAttribute> m

    /// Not obsolete and not hidden from IntelliSense.
    static let isVisible (m: MemberInfo) =
        not (isObsolete m)
        && (try
                match m.GetCustomAttribute<EditorBrowsableAttribute>(false) with
                | null -> true
                | a -> a.State <> EditorBrowsableState.Never
            with _ ->
                true)

    static let objectMethods = set [ "Equals"; "GetHashCode"; "ToString"; "GetType"; "Finalize"; "MemberwiseClone" ]

    /// Methods without property and event accessors (but with operators), and without the methods every object has.
    static let isListed (m: MemberInfo) =
        match m with
        | :? MethodInfo as mi -> (not mi.IsSpecialName || mi.Name.StartsWith "op_") && not (objectMethods.Contains mi.Name)
        | :? ConstructorInfo as c -> not c.IsStatic
        | :? Type -> false // nested types are listed separately
        | :? FieldInfo as f -> not f.IsSpecialName // e.g. 'value__' of enums
        | _ -> true

    static let paramCount (m: MemberInfo) =
        match m with
        | :? MethodBase as mb -> mb.GetParameters().Length
        | :? PropertyInfo as p -> p.GetIndexParameters().Length
        | _ -> 0

    // --- XML docs ---

    /// The id of a member in XML doc files, e.g. 'M:Rhino.Geometry.Brep.CreateFromBox(Rhino.Geometry.BoundingBox)'.
    static let rec docTypeName (t: Type) : string =
        if t.IsByRef then
            docTypeName (t.GetElementType()) + "@"
        elif t.IsArray then
            let rank = t.GetArrayRank()
            docTypeName (t.GetElementType()) + (if rank = 1 then "[]" else "[" + String.concat "," (Seq.replicate rank "0:") + "]")
        elif t.IsPointer then
            docTypeName (t.GetElementType()) + "*"
        elif t.IsGenericParameter then
            (if isNull t.DeclaringMethod then "`" else "``") + string t.GenericParameterPosition
        elif t.IsGenericType && not t.IsGenericTypeDefinition then
            let def = t.GetGenericTypeDefinition()
            let name = Regex.Replace(def.FullName.Replace('+', '.'), @"`\d+", "")
            name + "{" + (t.GetGenericArguments() |> Array.map docTypeName |> String.concat ",") + "}"
        else
            (if isNull t.FullName then t.Name else t.FullName).Replace('+', '.')

    static let docId (m: MemberInfo) : string =
        let typeId (t: Type) = (if isNull t.FullName then t.Name else t.FullName).Replace('+', '.')
        let paramList (ps: ParameterInfo[]) =
            if ps.Length = 0 then "" else "(" + (ps |> Array.map (fun p -> docTypeName p.ParameterType) |> String.concat ",") + ")"
        match m with
        | :? Type as t -> "T:" + typeId t
        | :? ConstructorInfo as c -> $"M:{typeId c.DeclaringType}.#ctor{paramList (c.GetParameters())}"
        | :? MethodInfo as mi ->
            let arity = if mi.IsGenericMethodDefinition then "``" + string (mi.GetGenericArguments().Length) else ""
            let conversion = if mi.Name = "op_Implicit" || mi.Name = "op_Explicit" then "~" + docTypeName mi.ReturnType else ""
            $"M:{typeId mi.DeclaringType}.{mi.Name}{arity}{paramList (mi.GetParameters())}{conversion}"
        | :? PropertyInfo as p -> $"P:{typeId p.DeclaringType}.{p.Name}{paramList (p.GetIndexParameters())}"
        | :? FieldInfo as f -> $"F:{typeId f.DeclaringType}.{f.Name}"
        | :? EventInfo as e -> $"E:{typeId e.DeclaringType}.{e.Name}"
        | _ -> ""

    /// e.g. 'T:Rhino.Geometry.Brep' -> 'Brep', 'M:Rhino.Geometry.Brep.Join(...)' -> 'Brep.Join'
    static let shortCref (cref: string) =
        let s = if cref.Length > 2 && cref[1] = ':' then cref.Substring 2 else cref
        let s = match s.IndexOf '(' with -1 -> s | i -> s.Substring(0, i)
        let parts = s.Split('.')
        if cref.StartsWith "T:" || parts.Length < 2 then parts[parts.Length - 1]
        else parts[parts.Length - 2] + "." + parts[parts.Length - 1]

    /// The text of a doc element, with references like <see cref="T:Rhino.Geometry.Brep"/> as 'Brep'.
    static let docText (e: XElement) : string =
        let sb = StringBuilder()
        let attr (el: XElement) (name: string) =
            match el.Attribute(XName.Get name) with
            | null -> null
            | a -> a.Value
        let rec walk (n: XNode) =
            match n with
            | :? XText as t -> sb.Append(t.Value) |> ignore
            | :? XElement as el ->
                match el.Name.LocalName with
                | "see"
                | "seealso" when el.IsEmpty ->
                    match attr el "cref", attr el "langword" with
                    | null, null -> ()
                    | null, word -> sb.Append(word) |> ignore
                    | cref, _ -> sb.Append(shortCref cref) |> ignore
                | "paramref"
                | "typeparamref" -> sb.Append(attr el "name") |> ignore
                | "para"
                | "br" ->
                    sb.Append(' ') |> ignore
                    for c in el.Nodes() do walk c
                    sb.Append(' ') |> ignore
                | _ ->
                    for c in el.Nodes() do walk c
            | _ -> ()
        for c in e.Nodes() do walk c
        Regex.Replace(collapse (sb.ToString()), @"^(///\s*)+", "") // some summaries in RhinoCommon.xml start with '///'

    /// The end of a sentence, but not of an abbreviation like 'e.g.'
    static let sentenceEnd = Regex(@"(?<!\b(e\.g|i\.e|etc|vs))\.\s", RegexOptions.IgnoreCase)

    static let firstSentence (s: string) =
        let s =
            match sentenceEnd.Match s with
            | m when m.Success -> s.Substring(0, m.Index + 1)
            | _ -> s
        if s.Length > 160 then s.Substring(0, 157) + "..." else s

    /// Cuts lines after maxChars characters, with a hint what to do then.
    static let fit (hint: string) (lines: string seq) =
        let sb = StringBuilder()
        let mutable cut = 0
        for l in lines do
            if cut > 0 || sb.Length + l.Length + 1 > maxChars then cut <- cut + 1
            else sb.Append(l).Append('\n') |> ignore
        if cut > 0 then sb.Append($"... {cut} more lines. {hint}\n") |> ignore
        let text = sb.ToString().TrimEnd()
        if text.Contains "[<Out>]" then
            text
            + "\nIn F# [<Out>] arguments can be left out, the method then returns a tuple of its result and the out values, e.g. 'let ok, value = ...'."
        else
            text

    /// The Levenshtein distance, ignoring case.
    static let distance (a: string) (b: string) =
        let a = a.ToLowerInvariant()
        let b = b.ToLowerInvariant()
        let mutable prev = Array.init (b.Length + 1) id
        for i in 1 .. a.Length do
            let cur = Array.zeroCreate (b.Length + 1)
            cur[0] <- i
            for j in 1 .. b.Length do
                let cost = if a[i - 1] = b[j - 1] then 0 else 1
                cur[j] <- min (min (prev[j] + 1) (cur[j - 1] + 1)) (prev[j - 1] + cost)
            prev <- cur
        prev[b.Length]

    /// The words of a PascalCase or camelCase name, in lower case, e.g. 'CreateFromBox' -> create, from, box.
    static let words (name: string) =
        Regex.Matches(name, "[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])") |> Seq.map _.Value.ToLowerInvariant() |> Array.ofSeq

    /// The candidates most similar to a name: containing it, contained in it, containing all its words, or a few typos away.
    /// e.g. 'CreateFromBox' for 'CreateBox'.
    static let similar (name: string) (candidates: string seq) : string list =
        let maxDistance = max 2 (name.Length / 3)
        let nameWords = words name
        candidates
        |> Seq.distinct
        |> Seq.choose (fun c ->
            if c.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(c, StringComparison.OrdinalIgnoreCase) then
                Some(0, c)
            elif nameWords.Length > 1 && nameWords |> Array.forall (fun w -> c.Contains(w, StringComparison.OrdinalIgnoreCase)) then
                Some(1, c)
            elif abs (c.Length - name.Length) <= maxDistance then
                let d = distance name c
                if d <= maxDistance then Some(1 + d, c) else None
            else
                None)
        |> Seq.sortBy (fun (d, c) -> d, c.Length, c)
        |> Seq.truncate maxSuggestions
        |> Seq.map snd
        |> List.ofSeq

    // --- assemblies and types ---

    let typesCache = ConcurrentDictionary<Assembly, Type[]>()
    let docsCache = ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, string>>()

    /// The host's references, e.g. RhinoCommon, and Caddis.Core for Caddis.Script.
    let priorityPaths =
        HashSet<string>(Array.append host.References [| typeof<Script>.Assembly.Location |], StringComparer.OrdinalIgnoreCase)

    let location (a: Assembly) =
        try if a.IsDynamic then "" else a.Location with _ -> ""

    /// .NET, F# and compiler assemblies: left out of searches, found by short type names only if nothing else matches.
    let isFramework (a: Assembly) =
        let n = a.GetName().Name
        n = "mscorlib" || n = "netstandard" || n = "WindowsBase" || n = "Accessibility"
        || n.StartsWith "System" || n.StartsWith "Microsoft." || n.StartsWith "FSharp." || n.StartsWith "Presentation"

    let rank (a: Assembly) =
        if priorityPaths.Contains(location a) then 0
        elif isFramework a then 2
        else 1

    /// The loaded assemblies, the host's references first and framework assemblies last. Without FSI's dynamic ones.
    let assemblies () =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Array.filter (fun a -> not a.IsDynamic && not (a.GetName().Name.StartsWith "FSI-ASSEMBLY"))
        |> Array.distinctBy _.FullName
        |> Array.sortBy rank

    /// The public types. If some can't be loaded, e.g. because they need an assembly that is missing, the others.
    let typesOf (a: Assembly) : Type[] =
        typesCache.GetOrAdd(
            a,
            fun a ->
                let visible (ts: Type[]) = ts |> Array.filter (fun t -> not (isNull t) && t.IsVisible)
                try
                    a.GetExportedTypes()
                with _ ->
                    try visible (a.GetTypes())
                    with
                    | :? ReflectionTypeLoadException as e -> visible e.Types
                    | _ -> [||]
        )

    let allTypes () = [| for a in assemblies () do yield! typesOf a |]

    /// The XML doc file of an assembly, next to it or in one of the host's lib folders, by member id.
    let docsOf (a: Assembly) : IReadOnlyDictionary<string, string> =
        docsCache.GetOrAdd(
            a,
            fun a ->
                let docs = Dictionary<string, string>()
                try
                    let fileName = a.GetName().Name + ".xml"
                    let folders = [ Path.GetDirectoryName(location a); yield! host.LibFolders ]
                    let file =
                        folders
                        |> List.filter (String.IsNullOrEmpty >> not)
                        |> List.map (fun d -> Path.Combine(d, fileName))
                        |> List.tryFind File.Exists
                    match file with
                    | None -> ()
                    | Some path ->
                        // keep the XML of each member as a string, parsing all of RhinoCommon.xml into an XDocument needs much more memory
                        use reader = XmlReader.Create path
                        let mutable more = reader.ReadToFollowing "member"
                        while more do
                            let name = reader.GetAttribute "name"
                            let xml = reader.ReadOuterXml()
                            if not (isNull name) then docs[name] <- xml
                            more <- (reader.NodeType = XmlNodeType.Element && reader.Name = "member") || reader.ReadToFollowing "member"
                with _ ->
                    ()
                docs :> IReadOnlyDictionary<_, _>
        )

    /// The parsed doc element of a member, if its assembly has an XML doc file.
    let docOf (m: MemberInfo) : XElement option =
        let docs = docsOf (match m with :? Type as t -> t.Assembly | _ -> m.DeclaringType.Assembly)
        match docs.TryGetValue(docId m) with
        | true, xml -> (try Some(XElement.Parse xml) with _ -> None)
        | _ -> None

    let summaryOf (m: MemberInfo) : string =
        match docOf m |> Option.bind (fun d -> d.Element(XName.Get "summary") |> Option.ofObj) with
        | Some s -> docText s
        | None -> ""

    /// The summary, parameters, return value and remarks of a member, as indented lines.
    let docLines (m: MemberInfo) : string list =
        match docOf m with
        | None -> []
        | Some d -> [
            let text (name: string) = d.Elements(XName.Get name) |> Seq.map docText |> Seq.filter ((<>) "")
            for s in text "summary" do "    " + s
            for p in d.Elements(XName.Get "param") do
                match p.Attribute(XName.Get "name"), docText p with
                | null, _
                | _, "" -> ()
                | n, t -> $"    {n.Value}: {t}"
            for r in text "returns" do "    returns: " + r
            for v in text "value" do "    value: " + v
            for r in text "remarks" do "    remarks: " + (if r.Length > 400 then r.Substring(0, 397) + "..." else r)
          ]

    /// Types with this name: the full name first, then the short name in the host's and other non-framework assemblies,
    /// then framework types, e.g. 'File'.
    let findTypes (name: string) : Type[] =
        let types = allTypes ()
        let matching (f: Type -> bool) = types |> Array.filter f
        let tries = [
            fun () -> matching (fun t -> matchName t = name)
            fun () -> matching (fun t -> String.Equals(matchName t, name, StringComparison.OrdinalIgnoreCase))
            fun () -> matching (fun t -> plainName t = name && not (isFramework t.Assembly))
            fun () -> matching (fun t -> String.Equals(plainName t, name, StringComparison.OrdinalIgnoreCase) && not (isFramework t.Assembly))
            fun () -> matching (fun t -> plainName t = name)
        ]
        tries |> List.tryPick (fun f -> match f () with [||] -> None | ts -> Some ts) |> Option.defaultValue [||]

    let namespaceOf (name: string) : string option =
        allTypes ()
        |> Seq.choose (fun t -> Option.ofObj t.Namespace)
        |> Seq.tryFind (fun ns -> String.Equals(ns, name, StringComparison.OrdinalIgnoreCase))

    // --- descriptions ---

    let describeNamespace (ns: string) : string =
        let types = allTypes () |> Array.filter (fun t -> t.Namespace = ns && not t.IsNested) |> Array.distinctBy displayName
        let children =
            allTypes ()
            |> Seq.choose (fun t -> Option.ofObj t.Namespace)
            |> Seq.filter (fun n -> n.StartsWith(ns + ".", StringComparison.Ordinal))
            |> Seq.map (fun n -> ns + "." + n.Substring(ns.Length + 1).Split('.')[0])
            |> Seq.distinct
            |> Seq.sort
            |> List.ofSeq
        fit "Look up a type by its name for its members." [
            $"namespace {ns}: {types.Length} types"
            if not children.IsEmpty then "namespaces: " + String.concat ", " children
            for kind, ts in types |> Array.groupBy kindOf |> Array.sortBy fst do
                $"{kind}: " + (ts |> Array.map plainName |> Array.sort |> String.concat ", ")
        ]

    let describeType (t: Type) (filter: string) : string =
        let matchesFilter (name: string) =
            String.IsNullOrWhiteSpace filter || name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
        let bases =
            Seq.unfold (fun (b: Type) -> if isNull b || b = typeof<obj> || b = typeof<ValueType> || b = typeof<Enum> then None else Some(b, b.BaseType)) t.BaseType
            |> List.ofSeq
        let header = [
            $"{kindOf t} {displayName t} in {t.Assembly.GetName().Name} {t.Assembly.GetName().Version}"
            if not bases.IsEmpty then "inherits: " + (bases |> List.map fsName |> String.concat ", ")
            match (try t.GetInterfaces() with _ -> [||]) with
            | [||] -> ()
            | _ when t.IsEnum -> ()
            | xs ->
                let names = xs |> Array.map fsName |> Array.sort
                "implements: " + (names |> Array.truncate 8 |> String.concat ", ") + (if names.Length > 8 then ", ..." else "")
            match summaryOf t with
            | "" -> ()
            | s -> s
        ]
        /// The members in less detail the larger the type is:
        /// 0: all overloads with the first sentence of their docs, 1: all overloads, 2: one overload per name, 3: only the names.
        let body (detail: int) : string list = [
            if t.IsEnum then
                for f in t.GetFields(BindingFlags.Public ||| BindingFlags.Static) do
                    if matchesFilter f.Name then
                        let value = try string (f.GetRawConstantValue()) with _ -> "?"
                        let doc = if detail = 0 then (match summaryOf f with "" -> "" | s -> "  // " + firstSentence s) else ""
                        $"| {f.Name} = {value}{doc}"
            elif typeof<Delegate>.IsAssignableFrom t && not t.IsAbstract then
                match t.GetMethod "Invoke" with
                | null -> ()
                | invoke -> $"delegate of {parameters invoke} -> {fsName invoke.ReturnType}"
            else
                if detail = 2 then $"Only one overload per member is shown, look up '{plainName t}.Name' for all overloads with docs."
                if detail = 3 then $"Only the names are shown, use 'filter' or look up '{plainName t}.Name' for signatures and docs."
                let members =
                    (try t.GetMembers declaredOnly with _ -> [||])
                    |> Array.filter (fun m -> isListed m && matchesFilter m.Name && isVisible m)
                let section (title: string) (ms: MemberInfo seq) = [
                    let ms = ms |> Seq.sortBy (fun m -> m.Name, paramCount m) |> List.ofSeq
                    let detail = if title = "constructors" then min detail 2 else detail // constructors have no names
                    if not ms.IsEmpty then
                        $"--- {title} ---"
                        if detail = 3 then
                            ms |> List.map _.Name |> List.distinct |> String.concat ", "
                        else
                            for _, overloads in ms |> List.groupBy _.Name do
                                for m in (if detail = 2 then [ overloads.Head ] else overloads) do
                                    let more =
                                        match overloads.Length - 1 with
                                        | n when detail <> 2 || n = 0 -> ""
                                        | 1 -> "  (+1 overload)"
                                        | n -> $"  (+{n} overloads)"
                                    signature m + more
                                    if detail = 0 then
                                        match summaryOf m with
                                        | "" -> ()
                                        | s -> "    " + firstSentence s
                ]
                yield! section "constructors" (members |> Seq.filter (fun m -> m :? ConstructorInfo))
                yield! section "static members" (members |> Seq.filter (fun m -> isStatic m && not (m :? ConstructorInfo)))
                yield! section "properties" (members |> Seq.filter (fun m -> not (isStatic m) && (m :? PropertyInfo || m :? FieldInfo)))
                yield! section "methods" (members |> Seq.filter (fun m -> not (isStatic m) && m :? MethodInfo))
                yield! section "events" (members |> Seq.filter (fun m -> not (isStatic m) && m :? EventInfo))
                let nested = (try t.GetNestedTypes() with _ -> [||]) |> Array.filter (fun n -> matchesFilter n.Name && isVisible n)
                if nested.Length > 0 then
                    "--- nested types ---"
                    for n in nested |> Array.sortBy _.Name do
                        signature n
                // members of base types and interfaces, by name only
                let inheritedFrom = if t.IsInterface then List.ofArray (t.GetInterfaces()) else bases
                let inherited = [
                    for b in inheritedFrom do
                        let names =
                            (try b.GetMembers declaredOnly with _ -> [||])
                            |> Array.filter (fun m -> isListed m && not (m :? ConstructorInfo) && matchesFilter m.Name && isVisible m)
                            |> Array.map _.Name
                            |> Array.distinct
                            |> Array.sort
                        if names.Length > 0 then $"from {fsName b}: " + String.concat ", " names
                ]
                if not inherited.IsEmpty then
                    $"--- inherited, look up '{plainName t}.Name' for details ---"
                    yield! inherited
        ]
        let hint = "Use 'filter' to show only members whose name contains a text, or look up 'Type.Member' for its overloads and docs."
        let lines =
            seq { 0..3 }
            |> Seq.map (fun detail -> header @ body detail)
            |> Seq.tryFind (fun lines -> List.sumBy (fun (l: string) -> l.Length + 1) lines <= maxChars)
            |> Option.defaultWith (fun () -> header @ body 3)
        let lines = if String.IsNullOrWhiteSpace filter || lines.Length > header.Length then lines else lines @ [ $"No member name contains '{filter}'." ]
        fit hint lines

    let describeMember (t: Type) (name: string) : string =
        let allMembers =
            (try t.GetMembers(anyMember ||| BindingFlags.FlattenHierarchy) with _ -> [||])
            |> Array.append (if t.IsInterface then t.GetInterfaces() |> Array.collect (fun i -> try i.GetMembers anyMember with _ -> [||]) else [||])
            |> Array.filter (fun m -> not (m :? MethodInfo) || isListed m || objectMethods.Contains m.Name)
        let found =
            match allMembers |> Array.filter (fun m -> m.Name = name) with
            | [||] -> allMembers |> Array.filter (fun m -> String.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            | ms -> ms
        match found with
        | [||] ->
            let candidates = allMembers |> Seq.filter (fun m -> isListed m && isVisible m) |> Seq.map _.Name
            let similarNames =
                match similar name candidates with
                | [] -> ""
                | names -> " Similar: " + (names |> List.map (fun n -> $"{plainName t}.{n}") |> String.concat ", ") + "."
            $"{displayName t} has no public member '{name}'.{similarNames} Look up '{matchName t}' for all its members, or use 'search' to find '{name}' in other types."
        | [| :? Type as nested |] -> $"'{name}' is a nested type, look up '{plainName nested}'."
        | ms ->
            let ms = ms |> Array.sortBy (fun m -> isObsolete m, paramCount m) |> Array.distinctBy docId
            fit "" [
                let overloads = if ms.Length > 1 then $" ({ms.Length} overloads)" else ""
                $"{displayName t}.{ms[0].Name}{overloads}"
                for m in ms do
                    let from = if m.DeclaringType <> t then $"  // from {fsName m.DeclaringType}" else ""
                    signature m + from
                    if isObsolete m then
                        let msg = try m.GetCustomAttribute<ObsoleteAttribute>().Message with _ -> null
                        "    OBSOLETE" + (if String.IsNullOrWhiteSpace msg then "" else ": " + collapse msg)
                    yield! docLines m
            ]

    /// Describes a namespace, a type or a member, e.g. 'Rhino.Geometry', 'Brep' or 'Brep.CreateFromBox'.
    /// For a type, filter limits the members to those whose name contains it.
    member _.Describe(name: string, filter: string) : string =
        // e.g. 'List<T>', 'List`1' or 'Brep.Join(...)'
        let name = Regex.Replace(name.Trim().Replace('+', '.'), @"<.*>|`\d+|\(.*$", "").Trim('.', ' ')
        if name = "" then
            invalidArg "name" "Provide the name of a namespace, type or member, e.g. 'System.IO', 'File' or 'File.ReadAllText'."
        let others (ts: Type[]) =
            if ts.Length > 1 then
                "\nOther types with this name: " + (ts |> Seq.skip 1 |> Seq.map displayName |> Seq.distinct |> String.concat ", ")
            else
                ""
        match findTypes name with
        | [||] ->
            match namespaceOf name with
            | Some ns -> describeNamespace ns
            | None ->
                let i = name.LastIndexOf '.'
                match (if i > 0 then findTypes (name.Substring(0, i)) else [||]) with
                | [||] ->
                    let lastPart = name.Substring(i + 1)
                    let candidates = allTypes () |> Seq.filter (fun t -> not (isFramework t.Assembly)) |> Seq.map plainName
                    let hint = "Types of assemblies that a script loads with #r are found after the run that loads them."
                    match similar lastPart candidates with
                    | [] -> $"Nothing named '{name}' was found in the loaded assemblies. {hint}"
                    | names -> $"Nothing named '{name}' was found in the loaded assemblies. Similar types: " + String.concat ", " names + $"\n{hint}"
                | ts -> describeMember ts[0] (name.Substring(i + 1)) + others ts
        | ts -> describeType ts[0] filter + others ts

    /// Finds types and members whose name contains the query, in the host's and other non-framework assemblies.
    member _.Search(query: string) : string =
        let q = query.Trim()
        if q = "" then invalidArg "search" "Provide a text to search for in type and member names."
        let quality (name: string) =
            if String.Equals(name, q, StringComparison.OrdinalIgnoreCase) then Some 0
            elif name.StartsWith(q, StringComparison.OrdinalIgnoreCase) then Some 1
            elif name.Contains(q, StringComparison.OrdinalIgnoreCase) then Some 2
            else None
        let results = ResizeArray<(int * int * string) * string>()
        for a in assemblies () |> Array.filter (isFramework >> not) do
            let r = rank a
            for t in typesOf a do
                match quality t.Name with
                | Some q when isVisible t -> results.Add((r, q, displayName t), $"{displayName t}  ({kindOf t})")
                | _ -> ()
                let members = try t.GetMembers declaredOnly with _ -> [||]
                for name, ms in members |> Array.groupBy _.Name do
                    match quality name with
                    | Some q ->
                        match ms |> Array.filter (fun m -> isListed m && isVisible m) with
                        | [||] -> ()
                        | ms ->
                            let kinds = ms |> Array.map (fun m -> m.MemberType.ToString().ToLowerInvariant()) |> Array.distinct |> String.concat ", "
                            let overloads = if ms.Length > 1 then $", {ms.Length} overloads" else ""
                            results.Add((r, q, matchName t + "." + name), $"{matchName t}.{name}  ({kinds}{overloads})")
                    | None -> ()
        if results.Count = 0 then
            $"No type or member name contains '{q}'."
        else
            let shown = results |> Seq.sortBy fst |> Seq.truncate maxSearchResults |> Seq.map snd |> List.ofSeq
            let more = if results.Count > shown.Length then $"\n... {results.Count - shown.Length} more, search for a longer text." else ""
            String.concat "\n" shown + more + "\nLook up one with 'name' for its signatures and docs."
