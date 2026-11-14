namespace Caddis.UI

open System
open System.Globalization
open System.Text
open Caddis

/// The texts of the panel about a run: its status and duration in words, the line of the run header, and the Output tab,
/// including Revit category summaries and Rhino and AutoCAD commands.
module RunDetails =

    /// The status in words, e.g. "runtime error", as the run header and the tooltips of the list show it.
    let statusWord (s: RunStatus) =
        match s with
        | Running -> "running"
        | Succeeded -> "ok"
        | CompileError -> "compile error"
        | RuntimeError -> "runtime error"
        | Cancelled -> "cancelled"
        | TimedOut -> "timed out"
        | Rejected -> "rejected"

    /// A duration that reads at a glance: "140 ms", "12.2 s" or "2 min 5 s".
    let duration (t: TimeSpan) =
        if t.TotalMilliseconds < 1000.0 then $"{int t.TotalMilliseconds} ms"
        elif t.TotalSeconds < 60.0 then t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        else $"{int t.TotalMinutes} min {t.Seconds} s"

    /// When the run started, in local time, e.g. "14:52:07".
    let startTime (r: Run) = r.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)

    /// The line of the run header after the id, e.g. "ok · 140 ms · 14:52:07 · background thread".
    let summary (appName: string) (r: Run) =
        let what =
            match r.Kind, r.Mode with
            | FSharpCode, UiThread -> "UI thread"
            | FSharpCode, Background -> "background thread"
            | HostCommands, _ -> $"{appName} commands"
            | HostExport, _ -> "export"
        $"{statusWord r.Status} · {duration r.Duration} · {startTime r} · {what}"

    /// The text of the Output tab: a section per part of the result, each under a line like "--- stdout ---".
    /// For a failed run the message, the diagnostics and the exception come first, since they say what went wrong.
    let text (changeNoun: string) (commandLineTitle: string option) (r: Run) =
        let o = r.Outcome
        let failed = r.Status <> Succeeded && r.Status <> Running
        let sb = StringBuilder()
        let add (title: string) (text: string) =
            if not (String.IsNullOrWhiteSpace text) then
                sb.AppendLine($"--- {title} ---").AppendLine(text.TrimEnd()) |> ignore
        let errors () =
            o.diagnostics
            |> Array.map (fun d -> $"{d.severity} FS{d.errorNumber} line {d.startLine}, column {d.startColumn}: {d.message}")
            |> String.concat Environment.NewLine
            |> add "diagnostics"
            o.exn |> Option.iter (fun e -> add "exception" e.text)
        o.message |> Option.iter (add "message")
        if failed then errors ()
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
        if not failed then errors ()
        r.Files |> Array.map _.path |> String.concat Environment.NewLine |> add "files"
        sb.ToString()
