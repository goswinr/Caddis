namespace Caddis.UI

open System
open System.Text
open Caddis

/// The code of runs as F# scripts, for the Export buttons of the code view.
module ScriptExport =

    /// The runs that 'Export all' saves: the successful F# runs, oldest first.
    let exportable (runs: Run[]) = runs |> Array.filter (fun r -> r.Kind = FSharpCode && r.Status = Succeeded)

    /// The comment before the code of a run, e.g. "// ---- run r3, started 2026-10-11 13:58:12, took 120 ms ----"
    let separator (r: Run) =
        let started = r.StartedAt.ToLocalTime().ToString "yyyy-MM-dd HH:mm:ss"
        $"// ---- run {r.Id}, started {started}, took {int r.Duration.TotalMilliseconds} ms ----"

    /// e.g. "1 successful F# run", "3 successful F# runs"
    let count (n: int) = if n = 1 then "1 successful F# run" else $"{n} successful F# runs"

    /// One script of the given runs and their code, in the given order, each after its separator.
    /// The code of runs sent by agents usually has '\n' line endings, Fantomas' output those of the OS, so all get the ones of the OS.
    let combine (appName: string) (runs: (Run * string) list) =
        let nl = Environment.NewLine
        let sb = StringBuilder()
        let line (text: string) = sb.Append(text).Append(nl) |> ignore
        let now = DateTime.Now.ToString "yyyy-MM-dd HH:mm"
        line $"// {count (List.length runs)} in {appName}, exported from the Caddis panel on {now}."
        line "// Each run was evaluated on its own in F# Interactive, oldest first. A run sees the values of earlier runs in the same F# session."
        for r, code in runs do
            line ""
            line (separator r)
            line (code.Replace("\r\n", "\n").TrimEnd().Replace("\n", nl))
        sb.ToString()
