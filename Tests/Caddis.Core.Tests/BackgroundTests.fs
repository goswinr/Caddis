module Caddis.Tests.BackgroundTests

open System
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
