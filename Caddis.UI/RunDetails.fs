namespace Caddis.UI

open System
open System.Text
open Caddis

/// Output formatting shared by both panels, including Revit category summaries and Rhino commands.
module RunDetails =

    let text (changeNoun: string) (commandLineTitle: string option) (r: Run) =
        let o = r.Outcome
        let sb = StringBuilder()
        let add (title: string) (text: string) =
            if not (String.IsNullOrWhiteSpace text) then
                sb.AppendLine($"--- {title} ---").AppendLine(text.TrimEnd()) |> ignore
        let where = if r.Kind = FSharpCode then r.ScriptPath else r.Code
        sb.AppendLine($"{r.Id}  {RunStatus.toString r.Status}  {int r.Duration.TotalMilliseconds} ms  {where}") |> ignore
        o.message |> Option.iter (add "message")
        add "notes" (r.Notes |> String.concat Environment.NewLine)
        o.value |> Option.iter (fun (t, v) -> add $"value: {t}" v)
        r.Commands
        |> Option.iter (fun c ->
            add "commands" (CommandReport.stepLines c |> String.concat Environment.NewLine)
            let ids (name: string) (a: string[]) = if a.Length = 0 then "" else $"{name} ({a.Length}): " + String.concat ", " a
            let changed =
                match c.changedGroups with
                | [] -> ids "changed" c.changed
                | groups -> $"changed ({c.changed.Length}): " + (groups |> List.map (fun (g, n) -> $"{n} {g}") |> String.concat ", ")
            [ ids "created" c.created; (if c.changed.Length = 0 then "" else changed); ids "deleted" c.deleted; c.selected |> Option.map (ids "selected") |> Option.defaultValue "" ]
            |> List.filter ((<>) "")
            |> String.concat Environment.NewLine
            |> add changeNoun)
        add "stdout" r.StdOut.Text
        commandLineTitle |> Option.iter (fun title -> add title r.CommandLine.Text)
        add "stderr" r.StdErr.Text
        add "F# Interactive" r.FsiOut.Text
        o.diagnostics
        |> Array.map (fun d -> $"{d.severity} FS{d.errorNumber} line {d.startLine}, column {d.startColumn}: {d.message}")
        |> String.concat Environment.NewLine
        |> add "diagnostics"
        o.exn |> Option.iter (fun e -> add "exception" e.text)
        r.Files |> Array.map _.path |> String.concat Environment.NewLine |> add "files"
        sb.ToString()
