module Caddis.Tests.EvalTests

open System.IO
open Xunit
open Caddis
open Caddis.Tests.Helpers

/// One session for all tests in this module, creating it takes a few seconds.
let svc = lazy (newService ())

[<Fact>]
let ``returns the value of the last expression`` () =
    let run = eval svc.Value "1 + 1"
    assertOk run
    Assert.Equal(Some("int", "2"), run.Outcome.value)

[<Fact>]
let ``captures printfn and eprintfn`` () =
    let run = eval svc.Value "printfn \"hello out\"\neprintfn \"hello err\""
    assertOk run
    Assert.Contains("hello out", run.StdOut.Text)
    Assert.Contains("hello err", run.StdErr.Text)

[<Fact>]
let ``keeps definitions between runs`` () =
    assertOk (eval svc.Value "let twentyOne = 21")
    let run = eval svc.Value "twentyOne * 2"
    Assert.Equal("42", valueText run)

[<Fact>]
let ``reports compile errors with 1-based positions`` () =
    let run = eval svc.Value "let a = 1\nlet b: int = \"text\""
    Assert.Equal(CompileError, run.Status)
    let err = run.Outcome.diagnostics |> Array.find (fun d -> d.severity = "error")
    Assert.Equal(2, err.startLine)
    Assert.Equal(14, err.startColumn)

[<Fact>]
let ``reports runtime errors with the script line`` () =
    // with --optimize+ small functions are inlined, so the line would be the call site, not the line in the function.
    let run = eval svc.Value "let a = 1\nlet b = 2\nif a < b then failwith \"boom\""
    Assert.Equal(RuntimeError, run.Status)
    let ex = run.Outcome.exn.Value
    Assert.Equal("boom", ex.message)
    Assert.Equal(Some 3, ex.scriptLine)

[<Fact>]
let ``returns files saved to Script.OutDir`` () =
    let run = eval svc.Value "System.IO.File.WriteAllText(System.IO.Path.Combine(Caddis.Script.OutDir, \"a.txt\"), \"hi\")"
    assertOk run
    let file = Assert.Single run.Files
    Assert.Equal("a.txt", file.name)
    Assert.Equal("text/plain", file.mime)
    Assert.Equal(run.OutDir, Path.GetDirectoryName file.path)

[<Fact>]
let ``evaluates a file with its source directory`` () =
    let dir = Directory.CreateTempSubdirectory("CaddisTests").FullName
    let path = Path.Combine(dir, "test.fsx")
    File.WriteAllText(path, "__SOURCE_DIRECTORY__")
    let run = svc.Value.Eval({ evalArgs "" with code = None; path = Some path }).Result
    assertOk run
    Assert.Equal($"\"{dir}\"", valueText run)

[<Fact>]
let ``type-checks without running`` () =
    let diagnostics = svc.Value.Check(Some "let c: int = \"x\"", None).Result
    Assert.Contains(diagnostics, fun d -> d.severity = "error" && d.startLine = 1)
    let ok = svc.Value.Check(Some "let d = 1 + 1", None).Result
    Assert.DoesNotContain(ok, fun d -> d.severity = "error")

[<Fact>]
let ``reset loses definitions`` () =
    let s = newService ()
    assertOk (eval s "let beforeReset = 1")
    s.Fsi.Reset().Wait()
    let run = eval s "beforeReset"
    Assert.Equal(CompileError, run.Status)
