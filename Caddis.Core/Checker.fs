namespace Caddis

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text

/// Parses and type-checks scripts without running them.
/// The FSharpChecker is only created on first use, it needs a few hundred MB of memory.
type ScriptChecker(host: ICaddisHost) =

    let checker = lazy (FSharpChecker.Create(suggestNamesForErrors = true))

    // same flags as Fesh uses when hosted on .NET Core, see https://github.com/goswinr/Fesh/blob/main/Src/Editor/Checker.fs
    let otherFlags () =
        [|
            "--targetprofile:netstandard"
            "--langversion:preview"
            "-r:" + typeof<Script>.Assembly.Location
            for r in host.References do "-r:" + r
            for dir in host.LibFolders do "--lib:" + dir
        |]

    member _.Check(code: string, scriptPath: string) : Async<Diagnostic[]> =
        async {
            let c = checker.Force()
            let source = SourceText.ofString code
            let! options, optionDiagnostics =
                c.GetProjectOptionsFromScript(
                    scriptPath,
                    source,
                    previewEnabled = true,
                    otherFlags = otherFlags (),
                    useSdkRefs = true,
                    assumeDotNetFramework = false
                )
            let! parseResults, answer = c.ParseAndCheckFileInProject(scriptPath, 0, source, options)
            let diagnostics =
                match answer with
                | FSharpCheckFileAnswer.Succeeded results -> results.Diagnostics
                | FSharpCheckFileAnswer.Aborted -> parseResults.Diagnostics
            return
                Seq.append optionDiagnostics diagnostics
                |> Seq.map Diagnostic.ofFcs
                |> Seq.distinct
                |> Array.ofSeq
        }
