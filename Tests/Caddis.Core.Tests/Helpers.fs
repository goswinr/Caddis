module Caddis.Tests.Helpers

open System
open System.IO
open Xunit
open Caddis

// Console.Out is redirected during runs, that is process wide, so tests must not run in parallel.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

/// A settings store in a new temporary folder.
let newStore () =
    SettingsStore(Path.Combine(Path.GetTempPath(), "CaddisTests", Guid.NewGuid().ToString "N"))

/// A service with a headless host, in UI-thread mode.
let newService () =
    let host = new HeadlessHost(ignore)
    let runs = RunRegistry(Path.Combine(Path.GetTempPath(), "CaddisTests", "runs-" + Guid.NewGuid().ToString "N"))
    CaddisService(host, newStore (), runs)

let evalArgs code = {
    code = Some code
    path = None
    timeoutMs = None
    recordUndo = false
    }

let eval (svc: CaddisService) code =
    svc.Eval(evalArgs code).GetAwaiter().GetResult()

let valueText (run: Run) =
    match run.Outcome.value with
    | Some(_, text) -> text
    | None -> failwith $"Run {run.Id} has no value, status {RunStatus.toString run.Status}, exception: {run.Outcome.exn}, diagnostics: {run.Outcome.diagnostics}"

let assertOk (run: Run) =
    if run.Status <> Succeeded then
        failwith $"Expected status ok but got {RunStatus.toString run.Status}.\nexception: {run.Outcome.exn}\ndiagnostics: {run.Outcome.diagnostics}\nmessage: {run.Outcome.message}"
