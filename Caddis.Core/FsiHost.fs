namespace Caddis

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.Interactive.Shell
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols

type FsiState =
    | NotLoaded
    | Initializing
    | Ready
    | Evaluating
    | Failed of string

module FsiState =

    let toString (s: FsiState) =
        match s with
        | NotLoaded -> "notLoaded"
        | Initializing -> "initializing"
        | Ready -> "ready"
        | Evaluating -> "evaluating"
        | Failed _ -> "failed"


/// Raised when a run is requested while another one is still running.
exception BusyException of runId: string


type EvalRequest = {
    code: string
    /// Full path of the script file, or null to use 'script.fsx' in the output folder of the run.
    scriptPath: string
    /// Wrap the run in an undo record of the host.
    recordUndo: bool
    /// Background mode only: cancel the run after this time.
    timeout: TimeSpan option
    }


/// Hosts one F# Interactive session and evaluates code in it, one run at a time,
/// either on the host's UI thread or on a dedicated background thread.
/// Adapted from Fesh, see https://github.com/goswinr/Fesh/blob/main/Src/Fsi.fs
type FsiHost(host: ICaddisHost, getSettings: unit -> CaddisSettings, runs: RunRegistry) =

    /// How long a cancelled run may take to stop before its thread is abandoned and the session is reset.
    let abandonAfter = TimeSpan.FromSeconds 5.0

    let sync = obj ()
    let mutable state = NotLoaded
    let mutable mode = (getSettings ()).threadMode
    let mutable session: FsiEvaluationSession option = None
    let mutable worker: WorkerThread option = None // only in background mode
    let mutable initTask: Task = Task.CompletedTask
    let mutable current: Run option = None

    /// True if a run's thread was abandoned. It might still use the session, so the session is not disposed on reset.
    let mutable abandoned = false

    /// The folders of script files already added via #I, reset with the session.
    let includedFolders = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let echo kind text =
        if (getSettings ()).echoOutput then host.Echo(kind, text)

    // The same writers are used for all runs, because a session keeps the writers it was created with.
    let stdOut = new RoutingWriter(StdOut, echo)
    let stdErr = new RoutingWriter(StdErr, echo)
    let fsiOut = new RoutingWriter(FsiOut, echo)
    let fsiErr = new RoutingWriter(FsiOut, echo)

    // --- capturing Console.Out and Console.Error during a run ---

    let mutable captureOwner: Run option = None
    let mutable savedOut: TextWriter = null
    let mutable savedErr: TextWriter = null

    let beginCapture (run: Run) =
        lock sync (fun () ->
            savedOut <- Console.Out
            savedErr <- Console.Error
            captureOwner <- Some run
            stdOut.Target <- Some run.StdOut
            stdErr.Target <- Some run.StdErr
            fsiOut.Target <- Some run.FsiOut
            fsiErr.Target <- Some run.FsiOut
            ScriptState.runId <- run.Id
            ScriptState.outDir <- run.OutDir
            Console.SetOut stdOut
            Console.SetError stdErr)

    /// Does nothing if the capture was already ended for this run, e.g. by abandoning it.
    let endCapture (run: Run) =
        lock sync (fun () ->
            match captureOwner with
            | Some r when obj.ReferenceEquals(r, run) ->
                captureOwner <- None
                Console.SetOut savedOut
                Console.SetError savedErr
                stdOut.Target <- None
                stdErr.Target <- None
                fsiOut.Target <- None
                fsiErr.Target <- None
                ScriptState.runId <- ""
                ScriptState.outDir <- ""
            | _ -> ())

    // --- the session ---

    let fsiArgs () =
        let s = getSettings ()
        [|
            "fsi.exe" // the first argument is ignored, like a program name
            "--langversion:preview"
            "--exec" // don't read further input from stdin
            "--debug+"
            "--debug:full"
            "--optimize+"
            "--nologo"
            "--multiemit+" // one assembly per evaluation, needed for line numbers in stack traces
            if OperatingSystem.IsWindows() then "--gui+"
            "-r:" + typeof<Script>.Assembly.Location // so that scripts can use Caddis.Script
            for r in host.References do "-r:" + r
            for dir in host.LibFolders do "--lib:" + dir
            for a in s.extraFsiArgs do a
        |]

    let createSession () : FsiEvaluationSession =
        // Don't change the global fsi object, e.g. its PrintWidth: it is shared with other FSI sessions in this process, e.g. from Fesh.
        let config = FsiEvaluationSession.GetDefaultConfiguration(FSharp.Compiler.Interactive.Shell.Settings.fsi, false)
        // FCS resolves FSharp.Core relative to the current directory, which is usually the folder of the host app.
        // see https://github.com/dotnet/fsharp/issues/9064
        let prevDir = Environment.CurrentDirectory
        try
            Directory.SetCurrentDirectory(Path.GetDirectoryName typeof<list<int>>.Assembly.Location)
            FsiEvaluationSession.Create(config, fsiArgs (), new StringReader(""), fsiOut, fsiErr)
        finally
            try Directory.SetCurrentDirectory prevDir with _ -> ()

    let flags = BindingFlags.NonPublic ||| BindingFlags.Instance

    /// The internal ControlledExecution of a session.
    /// see https://github.com/dotnet/fsharp/blob/main/src/Compiler/Interactive/ControlledExecution.fs
    let controlledExecution (s: FsiEvaluationSession) : obj =
        let controller = s.GetType().GetField("fsiInterruptController", flags).GetValue s
        controller.GetType().GetField("controlledExecution", flags).GetValue controller

    /// FCS evaluates inside System.Runtime.ControlledExecution.Run only in interactive sessions,
    /// that creates a new CancellationTokenSource for each evaluation.
    /// A hosted session is not interactive, so set the flag via reflection to make runs cancellable.
    let enableCancellation (s: FsiEvaluationSession) =
        try
            let ce = controlledExecution s
            ce.GetType().GetField("isInteractive", flags).SetValue(ce, true)
        with e ->
            host.Log $"Caddis: background runs can't be cancelled, setting up cancellation via reflection failed: {e.Message}"

    /// Cancels the CancellationTokenSource of the latest evaluation of the session.
    let tryCancelEvaluation (s: FsiEvaluationSession) : bool =
        try
            let ce = controlledExecution s
            match ce.GetType().GetField("cts", flags).GetValue ce with
            | :? ValueOption<CancellationTokenSource> as cts ->
                match cts with
                | ValueSome c ->
                    c.Cancel()
                    true
                | ValueNone -> false
            | _ -> false
        with _ ->
            false

    /// Creates a new session in the background, after stopping and disposing the old one.
    let startSession () : Task =
        lock sync (fun () ->
            match state with
            | Initializing -> initTask
            | _ ->
                state <- Initializing
                let oldSession = session
                let oldWorker = worker
                let canDispose = not abandoned
                abandoned <- false
                session <- None
                worker <- None
                lock includedFolders (fun () -> includedFolders.Clear())
                let m = mode
                initTask <-
                    Task.Run(
                        Action(fun () ->
                            oldWorker |> Option.iter (fun w -> w.Stop())
                            if canDispose then
                                oldSession |> Option.iter (fun s -> try (s :> IDisposable).Dispose() with _ -> ())
                            try
                                let s = createSession ()
                                if m = Background then enableCancellation s
                                lock sync (fun () ->
                                    session <- Some s
                                    worker <- if m = Background then Some(new WorkerThread("Caddis FSI")) else None
                                    state <- Ready)
                            with e ->
                                lock sync (fun () -> state <- Failed e.Message)
                                host.Log $"Caddis: creating the F# Interactive session failed:\n{e}")
                    )
                initTask)

    // --- evaluation ---

    /// Adds the folder of a script file via #I, so that files next to it can be used in #r and #load.
    let includeScriptFolder (s: FsiEvaluationSession) (scriptPath: string) =
        let dir = Path.GetDirectoryName scriptPath
        let isNew = lock includedFolders (fun () -> not (String.IsNullOrEmpty dir) && includedFolders.Add dir)
        if isNew && Directory.Exists dir then
            let result, _ =
                try s.EvalInteractionNonThrowing("#I @\"" + dir + "\"", scriptPath)
                with e -> Choice2Of2 e, [||]
            match result with
            | Choice1Of2 _ -> ()
            | Choice2Of2 _ -> lock includedFolders (fun () -> includedFolders.Remove dir |> ignore)

    let maxValueChars = 20_000

    let formatValue (v: FsiValue) : string * string =
        let typeName =
            try v.FSharpType.Format(FSharpDisplayContext.Empty.WithShortTypeNames true)
            with _ -> v.ReflectionType.Name
        let text =
            try
                let s = sprintf "%A" v.ReflectionValue
                if s.Length > maxValueChars then s.Substring(0, maxValueChars) + " ..." else s
            with e ->
                $"<printing the value failed: {e.Message}>"
        typeName, text

    let rec stripInvocation (e: exn) =
        match e with
        | :? TargetInvocationException as t when not (isNull t.InnerException) -> stripInvocation t.InnerException
        | :? AggregateException as a when a.InnerExceptions.Count = 1 -> stripInvocation a.InnerException
        | _ -> e

    let cancelledOutcome (run: Run) =
        if run.TimeoutRequested then RunOutcome.withMessage TimedOut "The run was cancelled because it reached its timeout."
        else RunOutcome.withMessage Cancelled "The run was cancelled."

    let outcomeOf (run: Run) (result: Choice<FsiValue option, exn>) (diagnostics: FSharpDiagnostic[]) : RunOutcome =
        let diags = diagnostics |> Array.map Diagnostic.ofFcs |> Array.distinct
        let hasErrors = diagnostics |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
        let withDiags (o: RunOutcome) = { o with diagnostics = diags }
        match result with
        | Choice1Of2 _ when hasErrors -> RunOutcome.ofStatus CompileError |> withDiags
        | Choice1Of2 value -> { RunOutcome.ofStatus Succeeded with value = value |> Option.map formatValue } |> withDiags
        | Choice2Of2 e ->
            match stripInvocation e with
            | :? OperationCanceledException -> cancelledOutcome run |> withDiags
            | _ when run.CancelRequested || run.TimeoutRequested -> cancelledOutcome run |> withDiags
            | :? FsiCompilationException -> RunOutcome.ofStatus CompileError |> withDiags
            | e -> { RunOutcome.ofStatus RuntimeError with exn = Some(ExceptionInfo.ofExn run.ScriptPath e) } |> withDiags

    /// Ends a run: frees the session for the next run and sets the outcome.
    let finishRun (run: Run) (outcome: RunOutcome) =
        lock sync (fun () ->
            match current with
            | Some r when obj.ReferenceEquals(r, run) ->
                current <- None
                if state = Evaluating then state <- Ready
            | _ -> ())
        run.Complete outcome |> ignore

    /// Called on the evaluation thread: the UI thread or the background worker.
    let evaluateOnThisThread (s: FsiEvaluationSession) (run: Run) : unit =
        let outcome =
            match (try host.CannotRunReason() with e -> Some e.Message) with
            | Some reason -> RunOutcome.withMessage Rejected reason
            | None ->
                let mutable beforeRunDone = false
                try
                    try
                        if File.Exists run.ScriptPath then includeScriptFolder s run.ScriptPath
                        beginCapture run
                        host.BeforeRun run
                        beforeRunDone <- true
                        let result, diagnostics = s.EvalInteractionNonThrowing(run.Code, run.ScriptPath)
                        outcomeOf run result diagnostics
                    with e ->
                        { RunOutcome.ofStatus RuntimeError with exn = Some(ExceptionInfo.ofExn run.ScriptPath e) }
                finally
                    endCapture run
                    if beforeRunDone then
                        try host.AfterRun run
                        with e -> host.Log $"Caddis: cleaning up after run {run.Id} failed: {e.Message}"
        finishRun run outcome

    do
        // so that Caddis.Script.OnUi works
        ScriptState.invokeOnUi <- fun f -> host.InvokeOnUi(fun () -> f.Invoke()).GetAwaiter().GetResult()

    // --- public members ---

    member _.State = state

    member _.Mode = mode

    /// The run in progress, if any.
    member _.Current = current

    member _.Runs = runs

    /// Starts creating the session in the background, if there is none yet.
    member _.Initialize() : Task =
        lock sync (fun () ->
            match state with
            | NotLoaded
            | Failed _ -> startSession ()
            | Initializing
            | Ready
            | Evaluating -> initTask)

    /// Starts a run and returns it once it has been handed to the evaluation thread.
    /// Await run.Completion for the result. Raises a BusyException if another run is in progress.
    member this.Evaluate(req: EvalRequest) : Task<Run> =
        task {
            do! this.Initialize()

            let s, w, m, run =
                lock sync (fun () ->
                    match current with
                    | Some r -> raise (BusyException r.Id)
                    | None -> ()
                    match state, session with
                    | Ready, Some s ->
                        let id = runs.NextId "r"
                        let outDir = runs.CreateFolder id
                        let scriptPath =
                            if String.IsNullOrWhiteSpace req.scriptPath then Path.Combine(outDir, "script.fsx")
                            else req.scriptPath
                        let run = Run(id, mode, req.code, scriptPath, outDir, (getSettings ()).maxOutputChars, req.recordUndo)
                        runs.Add run
                        current <- Some run
                        state <- Evaluating
                        s, worker, mode, run
                    | Failed msg, _ -> failwith $"F# Interactive could not be started: {msg}"
                    | st, _ -> failwith $"F# Interactive is not ready, its state is '{FsiState.toString st}'.")

            let work () = evaluateOnThisThread s run
            let dispatched =
                match m, w with
                | Background, Some w -> w.Invoke work
                | _ -> host.InvokeOnUi work

            // if handing the work to the thread failed, the run would never finish otherwise
            dispatched.ContinueWith(
                Action<Task<unit>>(fun t ->
                    if t.IsFaulted then
                        let e = stripInvocation t.Exception
                        finishRun run { RunOutcome.ofStatus RuntimeError with exn = Some(ExceptionInfo.ofExn run.ScriptPath e) })
            )
            |> ignore

            match req.timeout, m with
            | Some t, Background ->
                Task.Delay(t).ContinueWith(
                    Action<Task>(fun _ ->
                        if not run.IsFinished then
                            run.MarkTimeoutRequested()
                            this.CancelRun run |> ignore)
                )
                |> ignore
            | _ -> ()

            return run
        }

    /// Requests cancellation of a background run.
    /// If it does not stop within a few seconds its thread is abandoned and the session is reset.
    /// Returns false for runs on the UI thread, they can't be cancelled.
    member this.CancelRun(run: Run) : bool =
        if run.IsFinished || run.Mode <> Background then
            false
        else
            run.MarkCancelRequested()
            // Try repeatedly: the CancellationTokenSource only exists once FCS has started to evaluate.
            // The check under the lock ensures that only this run's token is cancelled.
            let tryCancel () =
                lock sync (fun () ->
                    match current, session with
                    | Some r, Some s when obj.ReferenceEquals(r, run) -> tryCancelEvaluation s |> ignore
                    | _ -> ())
            task {
                let deadline = DateTime.UtcNow + abandonAfter
                while not run.IsFinished && DateTime.UtcNow < deadline do
                    tryCancel ()
                    let! _ = Task.WhenAny(run.Completion, Task.Delay 250)
                    ()
                if not run.IsFinished then this.Abandon run
            }
            |> ignore
            true

    /// Gives up on a run that ignored cancellation and starts a new session.
    member private _.Abandon(run: Run) =
        let wasCurrent =
            lock sync (fun () ->
                match current with
                | Some r when obj.ReferenceEquals(r, run) ->
                    current <- None
                    abandoned <- true
                    state <- Ready // so that startSession below creates a new session
                    true
                | _ -> false)
        if wasCurrent then
            endCapture run
            let msg =
                "The run did not stop after it was cancelled. Its thread was abandoned and the F# session was reset, values defined in earlier runs are lost."
            run.Complete { cancelledOutcome run with message = Some msg } |> ignore
            host.Log("Caddis: " + msg)
            startSession () |> ignore

    /// Cancels the current run. If a run id is given, only if it is the current one.
    member this.Cancel(runId: string option) : Result<string, string> =
        match lock sync (fun () -> current) with
        | None -> Error "No run is in progress."
        | Some r when runId.IsSome && runId.Value <> r.Id -> Error $"Run '{runId.Value}' is not in progress, the current run is '{r.Id}'."
        | Some r when r.Mode = UiThread -> Error "Runs on the UI thread can't be cancelled, only runs in background mode."
        | Some r ->
            this.CancelRun r |> ignore
            Ok $"Cancellation of run '{r.Id}' was requested."

    /// Starts a new session. All values defined in the old one are lost.
    /// A run in background mode is cancelled first, a run on the UI thread raises a BusyException.
    member this.Reset() : Task =
        task {
            match lock sync (fun () -> current) with
            | Some r when r.Mode = Background ->
                this.CancelRun r |> ignore
                let! _ = Task.WhenAny(r.Completion, Task.Delay(abandonAfter + TimeSpan.FromSeconds 2.0))
                ()
            | Some r -> raise (BusyException r.Id)
            | None -> ()
            do! lock sync (fun () -> initTask) // let a running initialization finish first
            do! startSession ()
        }
        :> Task

    /// Switches between evaluating on the UI thread and on a background thread. This resets the session.
    /// Raises a BusyException if a run is in progress.
    member _.SetMode(m: ThreadMode) : Task =
        task {
            match lock sync (fun () -> current) with
            | Some r -> raise (BusyException r.Id)
            | None -> ()
            do! lock sync (fun () -> initTask)
            let needsRestart =
                lock sync (fun () ->
                    let changed = m <> mode
                    mode <- m
                    changed || (match state with NotLoaded | Failed _ -> true | _ -> false))
            if needsRestart then do! startSession ()
        }
        :> Task
