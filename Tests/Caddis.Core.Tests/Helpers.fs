module Caddis.Tests.Helpers

open System
open System.IO
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Caddis

// Console.Out is redirected during runs, that is process wide, so tests must not run in parallel.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

#if NETFRAMEWORK
type System.Net.Http.HttpClient with
    /// HttpClient.Send is .NET 5+
    member c.Send(request: System.Net.Http.HttpRequestMessage) = c.SendAsync(request).GetAwaiter().GetResult()
#endif

/// A new temporary folder.
let newTempDir () =
    Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CaddisTests", Guid.NewGuid().ToString "N")).FullName

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
    waitMs = None
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

/// Delays UI dispatch like a host waiting for a modal dialog, and allows finalization failures.
type QueuedHost() =
    let inner = new HeadlessHost(ignore)
    let host = inner :> ICaddisHost
    let queue = ConcurrentQueue<unit -> unit>()
    let enqueued = new ManualResetEventSlim(false)

    member val QueueUi = true with get, set
    member val Finalize: Run * RunStatus -> Result<unit, string> = fun _ -> Ok () with get, set
    member _.Enqueued = enqueued
    member _.Drain() =
        let mutable work = Unchecked.defaultof<unit -> unit>
        while queue.TryDequeue(&work) do work ()

    interface ICaddisHost with
        member _.Info = host.Info
        member _.Description = host.Description
        member _.LibFolders = host.LibFolders
        member _.References = host.References
        member this.InvokeOnUi<'T>(f: unit -> 'T) =
            if not this.QueueUi then host.InvokeOnUi f
            else
                let tcs = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
                queue.Enqueue(fun () ->
                    try tcs.TrySetResult(f ()) |> ignore
                    with e -> tcs.TrySetException e |> ignore)
                enqueued.Set()
                tcs.Task
        member _.CannotRunReason() = None
        member _.BeforeRun _ = ()
        member this.AfterRun(r, s) = this.Finalize(r, s)
        member _.Echo(_, _) = ()
        member _.Log _ = ()
        member _.Status() = []
        member _.CaptureView(v, w, h, d, p) = host.CaptureView(v, w, h, d, p)
        member _.Export(s, p) = host.Export(s, p)
        member _.Undo r = host.Undo r
        member _.Dialogs() = []
        member _.PressDialogButton(d, b) = host.PressDialogButton(d, b)
        member _.RunCommands(r, q) = host.RunCommands(r, q)

    interface IDisposable with
        member _.Dispose() =
            enqueued.Dispose()
            (inner :> IDisposable).Dispose()
