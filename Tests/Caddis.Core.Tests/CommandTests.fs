module Caddis.Tests.CommandTests

open System
open System.IO
open System.Threading
open Xunit
open Caddis
open Caddis.Http
open Caddis.Tests.Helpers

/// A headless host whose commands run the given function.
type CommandHost(runCommands: Run * CommandRequest -> RunOutcome) =
    let inner = new HeadlessHost(ignore) :> ICaddisHost

    interface ICaddisHost with
        member _.Info = inner.Info
        member _.Description = inner.Description
        member _.LibFolders = inner.LibFolders
        member _.References = inner.References
        member _.InvokeOnUi<'T>(f: unit -> 'T) = inner.InvokeOnUi f
        member _.CannotRunReason() = inner.CannotRunReason()
        member _.BeforeRun r = inner.BeforeRun r
        member _.AfterRun(r, s) = inner.AfterRun(r, s)
        member _.Echo(k, t) = inner.Echo(k, t)
        member _.Log m = inner.Log m
        member _.Status() = inner.Status()
        member _.CaptureView(v, w, h, d, p) = inner.CaptureView(v, w, h, d, p)
        member _.Export(s, p) = inner.Export(s, p)
        member _.Undo r = inner.Undo r
        member _.Dialogs() = inner.Dialogs()
        member _.PressDialogButton(d, b) = inner.PressDialogButton(d, b)
        member _.RunCommands(run, req) = runCommands (run, req)

let newServiceWith (host: ICaddisHost) =
    let runs = RunRegistry(Path.Combine(Path.GetTempPath(), "CaddisTests", "runs-" + Guid.NewGuid().ToString "N"))
    CaddisService(host, newStore (), runs)

let commandArgs (commands: string list) = {
    commands = Array.ofList commands
    select = None
    inputTimeoutMs = None
    waitMs = None
    }

[<Fact>]
let ``a command run reports its steps and leaves out a leading exclamation mark`` () =
    let mutable received = None
    let host =
        CommandHost(fun (run, req) ->
            received <- Some req
            run.CommandLine.Append "Command: -_Line\n"
            run.Commands <-
                Some {
                    steps = [ for m in req.macros -> { macro = m; commands = [ "Line", "Success" ]; waitedAt = None } ]
                    notRun = []
                    created = [| string (Guid.NewGuid()) |]
                    changed = [||]
                    changedGroups = []
                    deleted = [||]
                    selected = None
                }
            RunOutcome.ofStatus Succeeded)
    let svc = newServiceWith host
    let run = svc.StartCommands(commandArgs [ "! -_Line 0,0,0 10,0,0"; "  " ])
    run.Completion.Wait(5000) |> ignore
    assertOk run
    Assert.Equal(HostCommands, run.Kind)
    Assert.StartsWith("c", run.Id)
    let req = received.Value
    Assert.Equal<string[]>([| "-_Line 0,0,0 10,0,0" |], req.macros)
    Assert.Equal(Some(TimeSpan.FromMilliseconds(float CaddisService.DefaultInputTimeoutMs)), req.inputTimeout)
    let text, _ = McpView.run "Headless" svc.Runs "http://127.0.0.1:7810" [] 0L run
    Assert.Contains("--- commands ---\n-_Line 0,0,0 10,0,0 -> Line: Success", text)
    Assert.Contains("--- Headless command line ---\nCommand: -_Line", text)

[<Fact>]
let ``inputTimeoutMs 0 waits for the user`` () =
    let mutable received = None
    let svc =
        newServiceWith (
            CommandHost(fun (_, req) ->
                received <- Some req
                RunOutcome.ofStatus Succeeded)
        )
    let run = svc.StartCommands { commandArgs [ "_Line _Pause _Pause" ] with inputTimeoutMs = Some 0 }
    run.Completion.Wait(5000) |> ignore
    Assert.Equal(None, received.Value.inputTimeout)

[<Fact>]
let ``no commands is an argument error`` () =
    let svc = newServiceWith (CommandHost(fun _ -> RunOutcome.ofStatus Succeeded))
    Assert.Throws<ArgumentException>(fun () -> svc.StartCommands(commandArgs [ " "; "!" ]) |> ignore) |> ignore

[<Theory>]
[<InlineData("{\"commands\":[\"_Delete\",42]}")>]
[<InlineData("{\"commands\":[\"_Delete\"],\"select\":[42]}")>]
let ``command inputs do not silently discard invalid array entries`` (json: string) =
    use host = new HeadlessHost(ignore)
    Assert.Throws<ArgumentException>(fun () -> Args.commands (host :> ICaddisHost).Info (Json.parse json) |> ignore) |> ignore

[<Fact>]
let ``a command run blocks other runs and can be cancelled`` () =
    use started = new ManualResetEventSlim()
    let host =
        CommandHost(fun (run, _) ->
            started.Set()
            // like RhinoHost, which cancels a waiting command when CancelRequested is set
            while not run.CancelRequested do
                Thread.Sleep 20
            RunOutcome.withMessage Cancelled "cancelled")
    let svc = newServiceWith host
    let run = svc.StartCommands(commandArgs [ "-_Loft" ])
    Assert.True(started.Wait 5000)
    Assert.Throws<BusyException>(fun () -> svc.Eval(evalArgs "1 + 1").GetAwaiter().GetResult() |> ignore) |> ignore
    Assert.Throws<BusyException>(fun () -> svc.StartCommands(commandArgs [ "_Zoom" ]) |> ignore) |> ignore
    match svc.Fsi.Cancel(Some run.Id) with
    | Ok _ -> ()
    | Error msg -> failwith msg
    Assert.True(run.Completion.Wait 5000)
    Assert.Equal(Cancelled, run.Status)
    assertOk (eval svc "1 + 1")

[<Fact>]
let ``an exception of the host is the message of the run`` () =
    let svc = newServiceWith (CommandHost(fun _ -> failwith "Objects not found: x"))
    let run = svc.StartCommands(commandArgs [ "-_Loft" ])
    run.Completion.Wait(5000) |> ignore
    Assert.Equal(RuntimeError, run.Status)
    Assert.Equal(Some "Objects not found: x", run.Outcome.message)
    Assert.Equal(None, run.Outcome.exn)

[<Fact>]
let ``cancelWaitingCommands is saved and off by default`` () =
    let store = newStore ()
    Assert.False(store.Load().cancelWaitingCommands)
    store.Save { CaddisSettings.defaults with cancelWaitingCommands = true }
    Assert.True(store.Load().cancelWaitingCommands)

[<Fact>]
let ``F# runs get the input timeout only with cancelWaitingCommands`` () =
    let svc = newService ()
    let off = eval svc "1"
    Assert.Equal(None, off.InputTimeout)
    svc.UpdateSettings(fun s -> { s with cancelWaitingCommands = true })
    let on = eval svc "1"
    Assert.Equal(Some(TimeSpan.FromMilliseconds(float CaddisSettings.inputTimeoutMs)), on.InputTimeout)

[<Fact>]
let ``MCP shows the notes of a run`` () =
    let runs = RunRegistry(Path.Combine(Path.GetTempPath(), "CaddisTests", "runs-" + Guid.NewGuid().ToString "N"))
    let run = Run("r1", FSharpCode, UiThread, "1", "", runs.CreateFolder "r1", 10_000, true)
    run.AddNote "Caddis cancelled the command Length that the script started."
    run.Complete(RunOutcome.ofStatus Succeeded) |> ignore
    let text, _ = McpView.run "Headless" runs "http://127.0.0.1:7810" [] 0L run
    Assert.Contains("ok after", text)
    Assert.Contains("\nCaddis cancelled the command Length that the script started.", text)

[<Fact>]
let ``MCP shows the objects of a command run and saves long id lists`` () =
    let runs = RunRegistry(Path.Combine(Path.GetTempPath(), "CaddisTests", "runs-" + Guid.NewGuid().ToString "N"))
    let run = Run("c1", HostCommands, UiThread, "-_Line 0,0,0 10,0,0\n-_Loft\n_Zoom", "", runs.CreateFolder "c1", 10_000, true)
    let created = string (Guid.NewGuid())
    let deleted = Array.init (McpView.maxIds + 10) (fun _ -> string (Guid.NewGuid()))
    run.Commands <-
        Some {
            steps = [
                { macro = "-_Line 0,0,0 10,0,0"; commands = [ "Line", "Success" ]; waitedAt = None }
                { macro = "-_Loft"; commands = [ "Loft", "Cancel" ]; waitedAt = Some "Select curves to loft ( Point )" }
            ]
            notRun = [ "_Zoom" ]
            created = [| created |]
            changed = [||]
            changedGroups = []
            deleted = deleted
            selected = Some [||]
        }
    run.UndoSerial <- Some 7u
    run.UndoSteps <- 2
    run.Complete(RunOutcome.withMessage Cancelled "Caddis cancelled Loft.") |> ignore
    let text, _ = McpView.run "Headless" runs "http://127.0.0.1:7810" [] 0L run
    Assert.Contains("its changes can be undone with caddis_undo (2 undo steps)", text)
    Assert.Contains("-_Loft -> Loft: Cancel (cancelled while waiting at the prompt 'Select curves to loft ( Point )')\n_Zoom -> not run", text)
    Assert.Contains($"created (1): {created}", text)
    Assert.Contains($"deleted ({deleted.Length}, the first {McpView.maxIds}, all in ", text)
    Assert.Contains("selected: nothing", text)
    let file = Path.Combine(runs.RootDir, RunRegistry.LogsId, "c1-deleted.txt")
    Assert.Equal(deleted.Length, File.ReadAllLines(file).Length)
