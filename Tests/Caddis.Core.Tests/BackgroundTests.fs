module Caddis.Tests.BackgroundTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Caddis
open Caddis.Tests.Helpers

let svc =
    lazy
        (let s = newService ()
         s.SetMode(Background).Wait()
         s)

let start code = svc.Value.StartEval(evalArgs code).Result

let waitFinished (run: Run) =
    Assert.True(run.Completion.Wait(TimeSpan.FromSeconds 20.0), $"Run {run.Id} did not finish.")

[<Fact>]
let ``evaluates on the background thread`` () =
    let run = eval svc.Value "System.Threading.Thread.CurrentThread.Name"
    Assert.Equal("\"Caddis FSI\"", valueText run)

[<Fact>]
let ``Script.OnUi runs on the UI thread`` () =
    let run = eval svc.Value "Caddis.Script.OnUi(fun () -> System.Threading.Thread.CurrentThread.Name)"
    Assert.Equal("\"Caddis headless UI\"", valueText run)

[<Fact>]
let ``rejects a second run while one is in progress`` () =
    let first = start "System.Threading.Thread.Sleep 1500"
    let ex = Assert.ThrowsAny<exn>(fun () -> start "1" |> ignore)
    match ex with
    | BusyException id -> Assert.Equal(first.Id, id)
    | :? AggregateException as a ->
        match a.InnerException with
        | BusyException id -> Assert.Equal(first.Id, id)
        | e -> failwith $"unexpected {e}"
    | e -> failwith $"unexpected {e}"
    waitFinished first
    assertOk first

[<Fact>]
let ``cancels a sleeping run and stays usable`` () =
    assertOk (eval svc.Value "let keptOverCancel = 7")
    let run = start "while true do System.Threading.Thread.Sleep 10"
    Task.Delay(500).Wait()
    match svc.Value.Fsi.Cancel None with
    | Ok _ -> ()
    | Error msg -> failwith msg
    waitFinished run
    Assert.Equal(Cancelled, run.Status)
    Assert.Equal("7", valueText (eval svc.Value "keptOverCancel"))

[<Fact>]
let ``cancels a busy loop`` () =
    assertOk (eval svc.Value "let keptOverBusyCancel = 8")
    let run = start "let mutable i = 0L\nwhile true do i <- i + 1L"
    Task.Delay(500).Wait()
    svc.Value.Fsi.Cancel None |> ignore
    waitFinished run
    Assert.Equal(Cancelled, run.Status)
    Assert.Equal(Some "The run was cancelled.", run.Outcome.message) // not abandoned and reset
    Assert.Equal("8", valueText (eval svc.Value "keptOverBusyCancel"))

[<Fact>]
let ``times out`` () =
    let run = svc.Value.Eval({ evalArgs "while true do System.Threading.Thread.Sleep 10" with timeoutMs = Some 500 }).Result
    Assert.Equal(TimedOut, run.Status)
    assertOk (eval svc.Value "1 + 1")

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``cancelling a queued OnUi call returns promptly and never executes its callback`` (timeout: bool) =
    use host = new QueuedHost()
    let store = newStore ()
    store.Save { CaddisSettings.defaults with threadMode = Background; echoOutput = false }
    let runs = RunRegistry(Path.Combine(store.Folder, "runs"))
    let service = CaddisService(host, store, runs)
    assertOk (eval service "let keptOverQueuedCancel = 37")
    let marker = Path.Combine(store.Folder, "unexpected-edit.txt")
    let code = $"""Caddis.Script.OnUi(fun () -> System.IO.File.WriteAllText(@"{marker}", "changed"))"""
    let run = service.StartEval { evalArgs code with timeoutMs = if timeout then Some 1500 else None } |> _.Result
    try
        Assert.True(host.Enqueued.Wait 10000, "The UI call was not queued.")
        if not timeout then
            let cancellation = Task.Run(fun () -> service.Fsi.Cancel(Some run.Id))
            Assert.True(cancellation.Wait 2000, "The cancellation request blocked on the UI queue.")
            match cancellation.Result with Ok _ -> () | Error msg -> failwith msg
        Assert.True(run.Completion.Wait 10000, "The cancelled run did not finish while the UI was blocked.")
        Assert.Equal((if timeout then TimedOut else Cancelled), run.Status)
        Assert.False(File.Exists marker)
        Assert.Equal("37", valueText (eval service "keptOverQueuedCancel"))

        // Drain the cancelled callback while a new run owns the session and has its own queued callback.
        host.Enqueued.Reset()
        let next = service.StartEval(evalArgs "Caddis.Script.OnUi(fun () -> Caddis.Script.RunId)") |> _.Result
        Assert.True(host.Enqueued.Wait 10000)
        host.Drain()
        waitFinished next
        assertOk next
        Assert.Equal($"\"{next.Id}\"", valueText next)
        Assert.False(File.Exists marker, "The cancelled callback modified the document after the queue resumed.")
    finally
        host.Drain()
        run.Completion.Wait 10000 |> ignore

[<Fact>]
let ``cancellation waits for a UI callback that has already started`` () =
    use host = new QueuedHost()
    let store = newStore ()
    store.Save { CaddisSettings.defaults with threadMode = Background; echoOutput = false }
    let service = CaddisService(host, store, RunRegistry(Path.Combine(store.Folder, "runs")))
    let entered = Path.Combine(store.Folder, "entered.txt")
    let release = Path.Combine(store.Folder, "release.txt")
    let after = Path.Combine(store.Folder, "after.txt")
    let code = $"""
Caddis.Script.OnUi(fun () ->
    System.IO.File.WriteAllText(@"{entered}", "started")
    while not (System.IO.File.Exists @"{release}") do System.Threading.Thread.Sleep 20)
System.IO.File.WriteAllText(@"{after}", "should not run")
"""
    let run = service.StartEval(evalArgs code).Result
    let mutable drain = Task.CompletedTask
    try
        Assert.True(host.Enqueued.Wait 10000)
        drain <- Task.Run(Action host.Drain)
        Assert.True(SpinWait.SpinUntil((fun () -> File.Exists entered), 10000))
        let cancellation = Task.Run(fun () -> service.Fsi.Cancel(Some run.Id))
        Assert.True(cancellation.Wait 2000)
        Assert.False(run.Completion.Wait 5500, "The run finished before its active UI callback, including past the abandonment deadline.")
        File.WriteAllText(release, "resume")
        Assert.True(drain.Wait 10000)
        waitFinished run
        Assert.Equal(Cancelled, run.Status)
        Assert.Equal(Some "The run was cancelled.", run.Outcome.message) // cleanup after the callback must not abandon the session
        Assert.False(File.Exists after)
    finally
        File.WriteAllText(release, "resume")
        host.Drain()
        drain.Wait 10000 |> ignore
        run.Completion.Wait 10000 |> ignore
