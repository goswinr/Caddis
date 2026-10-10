module Caddis.Tests.EvalTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open Caddis
open Caddis.Http
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
let ``type-checks against the definitions of earlier runs`` () =
    assertOk (eval svc.Value "let definedBeforeCheck = 1")
    let diagnostics = svc.Value.Check(Some "let e = definedBeforeCheck + 1\nlet f: int = \"x\"", None).Result
    let errors = diagnostics |> Array.filter (fun d -> d.severity = "error")
    let err = Assert.Single errors // only the type error, definedBeforeCheck is known
    Assert.Equal(2, err.startLine)
    Assert.Equal("", err.file)

/// The text an MCP client gets for a run.
let mcpText (run: Run) =
    let text, _ = McpView.run svc.Value.Runs "http://127.0.0.1:7810" [] 0L run
    text

[<Fact>]
let ``shows a run to MCP clients without empty fields`` () =
    let text = mcpText (eval svc.Value "1 + 1")
    Assert.Matches(@"^Run r\d+: ok after \d+ ms\.\n--- F# Interactive ---\nval it: int = 2$", text)

[<Fact>]
let ``shows compile errors to MCP clients in one line each`` () =
    let text = mcpText (eval svc.Value "let a = 1\nlet b: int = \"text\"")
    Assert.Contains("compileError", text)
    Assert.Contains("error FS0001 at 2:14-2:20: This expression was expected to have type 'int' but here has type 'string'", text)
    Assert.DoesNotContain("Stopped due to error", text)

[<Fact>]
let ``shows MCP clients only the stack frames in the script`` () =
    let run = eval svc.Value "let thrower (a: int) = if a > 1 then failwith \"boom\" else a\nthrower 2"
    let text = mcpText run
    Assert.Contains("System.Exception: boom", text)
    Assert.Contains("in script.fsx:line 1", text)
    Assert.DoesNotContain("System.Reflection", text)
    Assert.DoesNotContain(run.OutDir, text) // the full path is shortened
    Assert.Equal(Some 1, run.Outcome.exn.Value.scriptLine)

[<Fact>]
let ``shortens long output for MCP clients and saves it to a file`` () =
    let run = eval svc.Value "for i in 1 .. 5000 do printfn \"line %d\" i" // more than shown, less than captured
    let text = mcpText run
    Assert.True(text.Length < McpView.maxStdOut + 2000, $"The text has {text.Length} characters.")
    Assert.Contains("line 1\n", text)
    Assert.Contains("line 5000", text)
    Assert.DoesNotContain("line 2500\n", text)
    let path = Regex.Match(text, @"all captured stdout output is in (.+)$", RegexOptions.Multiline).Groups[1].Value.Trim()
    Assert.Contains("line 2500", File.ReadAllText path)

[<Fact>]
let ``returns images saved by a run inline`` () =
    let run = eval svc.Value "System.IO.File.WriteAllBytes(System.IO.Path.Combine(Caddis.Script.OutDir, \"plot.png\"), [| 1uy; 2uy; 3uy |])"
    let text, images = McpView.run svc.Value.Runs "http://127.0.0.1:7810" [] 0L run
    let image = Assert.Single images
    Assert.Equal("image/png", image["mimeType"].GetValue<string>())
    Assert.Equal("AQID", image["data"].GetValue<string>())
    Assert.Contains("plot.png (3 B, image/png, shown below)", text)

[<Fact>]
let ``reset loses definitions`` () =
    let s = newService ()
    assertOk (eval s "let beforeReset = 1")
    s.Fsi.Reset().Wait()
    let run = eval s "beforeReset"
    Assert.Equal(CompileError, run.Status)
