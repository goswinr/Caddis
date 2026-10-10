module Caddis.Tests.ApiDocsTests

open Xunit
open Caddis

/// Describes the assemblies loaded in the test process, e.g. Caddis.Core, FSharp.Core and System.
let api = lazy ApiDocs(new HeadlessHost(ignore))

let describe name = api.Value.Describe(name, null)

[<Fact>]
let ``describes a member in F# syntax with its XML docs`` () =
    let text = describe "Caddis.Script.OnUi"
    Assert.Contains("static member OnUi<'T>: f: (unit -> 'T) -> 'T", text)
    Assert.Contains("Runs a function on the UI thread of the host app", text) // from Caddis.Core.xml

[<Fact>]
let ``finds types by their short name`` () =
    let text = describe "Script"
    Assert.StartsWith("static class Caddis.Script in Caddis.Core", text)
    Assert.Contains("static member RunId: string with get", text)
    Assert.Contains("The id of the current run, e.g. \"r12\".", text) // not cut at 'e.g.'

[<Fact>]
let ``shows curried F# functions`` () =
    let text = describe "Microsoft.FSharp.Collections.ListModule.Map"
    Assert.Contains("static member Map<'T, 'TResult>: mapping: ('T -> 'TResult) -> list: 'T list -> 'TResult list", text)

[<Fact>]
let ``filters the members of a type`` () =
    let text = api.Value.Describe("System.Collections.Generic.List<int>", "add")
    Assert.Contains("member Add: item: 'T -> unit", text)
    Assert.Contains("member AddRange: collection: IEnumerable<'T> -> unit", text)
    Assert.DoesNotContain("Remove", text)

[<Fact>]
let ``explains out parameters`` () =
    let text = describe "System.Int32.TryParse"
    Assert.Contains("static member TryParse: s: string * [<Out>] result: byref<int> -> bool", text)
    Assert.Contains("In F# [<Out>] arguments can be left out", text)

[<Fact>]
let ``lists the values of enums`` () =
    Assert.Contains("| Monday = 1", describe "System.DayOfWeek")

[<Fact>]
let ``shows large types in less detail to fit`` () =
    let text = describe "System.Linq.Enumerable" // hundreds of overloads
    Assert.True(text.Length < 17_000, $"The text has {text.Length} characters.")
    Assert.Contains("SelectMany", text)
    Assert.Contains("Zip", text) // the last member is not cut off
    Assert.Contains("Only", text) // one overload per member, or only the names

[<Fact>]
let ``ignores the case of member names`` () =
    Assert.Contains("static member OnUi<'T>", describe "Caddis.Script.onui")

[<Fact>]
let ``suggests similar members and types`` () =
    Assert.Contains("Similar: Script.OnUi", describe "Caddis.Script.RunOnUi")
    Assert.Contains("Similar: Script.OutDir", describe "Script.OutputDir") // all words of the name
    Assert.Contains("Similar types: Script", describe "Scripts")

[<Fact>]
let ``lists the types of a namespace`` () =
    let text = describe "Caddis.Http"
    Assert.StartsWith("namespace Caddis.Http:", text)
    Assert.Contains("McpEndpoint", text)

[<Fact>]
let ``searches type and member names`` () =
    let text = api.Value.Search "onui"
    Assert.Contains("Caddis.Script.OnUi  (method)", text)
