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


/// With --multiemit+ FSI loads one assembly per evaluation, all named 'FSI-ASSEMBLY' ('FSI-ASSEMBLY-MULTI' since FCS 43.12), with versions that start again in each session.
/// Later evaluations reference the earlier ones by that name. FSI resolves them in its own AssemblyResolve handler,
/// but a handler of the host app that comes first and matches only the simple name returns the wrong assembly:
/// Rhino's returns the first FSI-ASSEMBLY of the process. Then a value of an earlier run fails with a TypeLoadException,
/// or after a reset silently is the value of an old session.
/// This handler goes first and resolves by full name among the assemblies of the session that asks.
/// On .NET Framework it also resolves the dependencies of Caddis, see resolveOwn.
module FsiAssemblyResolver =

    let private sessions = ResizeArray<FsiEvaluationSession>()

#if NETFRAMEWORK
    /// The folder of Caddis.Core, with FCS and its dependencies.
    let private ownFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    let private keyToken (n: AssemblyName) = match n.GetPublicKeyToken() with null -> [||] | t -> t

    /// The folder of the host app, e.g. Rhino 8's System folder. The loader takes an assembly from there if it has exactly the requested version.
    let private hostFolder = AppDomain.CurrentDomain.BaseDirectory

    /// The name of the assembly at the path if it has the requested version or a higher one.
    /// With orLowerRevision also if only its revision is lower, e.g. System.Memory 4.0.1.1 for 4.0.1.2: a patch with the same API.
    let private satisfying (orLowerRevision: bool) (requested: AssemblyName) (path: string) =
        try
            let found = AssemblyName.GetAssemblyName path
            let f, r = found.Version, requested.Version
            let ok = isNull r || f >= r || (orLowerRevision && f.Major = r.Major && f.Minor = r.Minor && f.Build = r.Build)
            if keyToken found = keyToken requested && ok then Some found else None
        with _ ->
            None // missing, or a native library

    /// A .NET Framework plugin has no binding redirects. Then a request for e.g. System.Reflection.Metadata 10.0.0.2 fails
    /// if the host app's folder has an older version, and the host's handler returns that, e.g. Rhino 8 its 7.0.0.0.
    /// FCS then fails with a MissingMethodException when it writes the debug symbols of --multiemit+.
    /// This returns the assembly in the folder of Caddis.Core if it has the requested version or a higher one, like a binding redirect.
    /// But the host's copy first if it has the requested version or a higher one: the loader binds a request for exactly its version to it,
    /// e.g. Avalonia's for System.Memory 4.0.1.2 to Rhino 8's. A request for an older one, e.g. AvaloniaEdit's for 4.0.1.1, must get the same copy,
    /// or calls between them that pass its types, e.g. ReadOnlyMemory<char>, fail with a MissingMethodException.
    /// Also if the host's copy has a lower revision only: Revit 2024 has System.Memory 4.0.1.1, the loader binds AvaloniaEdit's request to it,
    /// so Avalonia's for 4.0.1.2 must get it too.
    let private resolveOwn (args: ResolveEventArgs) : Assembly =
        let fromOtherFolder =
            match args.RequestingAssembly with
            | null -> false
            | r -> r.IsDynamic || (r.Location <> "" && not (String.Equals(Path.GetDirectoryName r.Location, ownFolder, StringComparison.OrdinalIgnoreCase)))
        let requested = AssemblyName args.Name
        let path = Path.Combine(ownFolder, requested.Name + ".dll")
        if fromOtherFolder || not (File.Exists path) then
            null
        else
            match satisfying true requested (Path.Combine(hostFolder, requested.Name + ".dll")) with
            | Some host -> Assembly.Load host // by its exact version, the copy the loader binds to
            | None ->
                match satisfying false requested path with
                | Some found ->
                    match AppDomain.CurrentDomain.GetAssemblies() |> Array.tryFind (fun a -> a.FullName = found.FullName) with
                    | Some a -> a
                    | None -> Assembly.LoadFrom path
                | None -> null
#endif

    let private resolve (args: ResolveEventArgs) : Assembly =
        // 'FSI-ASSEMBLY,' or 'FSI-ASSEMBLY-MULTI,', the full name is compared below
        if not (args.Name.StartsWith("FSI-ASSEMBLY", StringComparison.Ordinal)) then
#if NETFRAMEWORK
            try resolveOwn args with _ -> null
#else
            null
#endif
        else
            let live = lock sessions (fun () -> sessions.ToArray())
            let find (s: FsiEvaluationSession) = s.DynamicAssemblies |> Array.tryFind (fun a -> a.FullName = args.Name)
            match args.RequestingAssembly with
            | null -> live |> Array.rev |> Array.tryPick find |> Option.toObj
            | requester ->
                // not for FSI sessions of other plugins, e.g. Fesh
                live
                |> Array.tryFind (fun s -> s.DynamicAssemblies |> Array.exists (fun a -> obj.ReferenceEquals(a, requester)))
                |> Option.bind find
                |> Option.toObj

    let private handler = ResolveEventHandler(fun _ args -> resolve args)

    /// Puts the handler first in AppDomain.AssemblyResolve, before the handlers of the host app.
    /// The event has no public way to insert at the front, so this sets its backing field:
    /// in AssemblyLoadContext on .NET Core, in the AppDomain on .NET Framework.
    let private ensureFirst () =
#if NETFRAMEWORK
        let field = typeof<AppDomain>.GetField("_AssemblyResolve", BindingFlags.NonPublic ||| BindingFlags.Instance)
        let owner = box AppDomain.CurrentDomain
#else
        let field = typeof<Runtime.Loader.AssemblyLoadContext>.GetField("AssemblyResolve", BindingFlags.NonPublic ||| BindingFlags.Static)
        let owner = null
#endif
        match field with
        | null ->
            AppDomain.CurrentDomain.remove_AssemblyResolve handler
            AppDomain.CurrentDomain.add_AssemblyResolve handler
            failwith "the backing field of the AssemblyResolve event was not found"
        | field ->
            lock sessions (fun () ->
                let others = Delegate.Remove(field.GetValue owner :?> Delegate, handler)
                field.SetValue(owner, Delegate.Combine(handler, others)))

    /// Puts the handler first. On .NET Framework call it before Caddis.Core uses System.Text.Json or FCS,
    /// so that their dependencies are resolved in the folder of Caddis.Core. Raises an exception if the handler could not be put first.
    let install () = ensureFirst ()

    /// Call when a session was created. Raises an exception if the handler could not be put first.
    let register (s: FsiEvaluationSession) =
        lock sessions (fun () -> sessions.Add s)
        ensureFirst ()

    /// Call before a session is disposed.
    let unregister (s: FsiEvaluationSession) = lock sessions (fun () -> sessions.Remove s |> ignore)


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
            | _ -> ())

    // --- the session ---

    /// FCS calls exit for these options, that would close the host app.
    let endsProcess (arg: string) =
        match arg.Trim().TrimStart('-', '/').ToLowerInvariant() with
        | "help" | "?" | "full-help" | "version" | "langversion:?" | "abortonerror" -> true
        | a -> a.StartsWith("fsi-server-report-references", StringComparison.Ordinal)

    /// A conditional compilation symbol for the host app, e.g. RHINO.
    let hostSymbol = host.Info.appName.ToUpperInvariant() |> String.filter Char.IsLetterOrDigit

    let fsiArgs (mode: ThreadMode) =
        let s = getSettings ()
        [|
            "fsi.exe" // the first argument is ignored, like a program name
            "--langversion:preview"
            // A hosted session never exits, --exec only makes it non-interactive. Then FCS doesn't evaluate inside
            // ControlledExecution, so runs can't be cancelled. Runs on the UI thread are never cancelled.
            if mode = UiThread then "--exec"
            "--nologo" // the banner would be echoed to the host
            // One assembly per evaluation, needed for line numbers in stack traces. The default on .NET Core, not on .NET Framework.
            "--multiemit+"
            // FSI only defines INTERACTIVE
#if NETFRAMEWORK
            "--define:NETFRAMEWORK"
#else
            "--define:NET"
            "--define:NETCOREAPP"
#endif
            "--define:CADDIS"
            if hostSymbol <> "" then "--define:" + hostSymbol
            "-r:" + typeof<Script>.Assembly.Location // so that scripts can use Caddis.Script
            for r in host.References do "-r:" + r
            for dir in host.LibFolders do "--lib:" + dir
            for a in s.extraFsiArgs do
                if endsProcess a then host.Log $"Caddis: the F# Interactive argument '{a}' is ignored, it would close {host.Info.appName}."
                else a
        |]

    let createSession (mode: ThreadMode) : FsiEvaluationSession =
        // Don't change the global fsi object, e.g. its PrintWidth: it is shared with other FSI sessions in this process, e.g. from Fesh.
        let config = FsiEvaluationSession.GetDefaultConfiguration(FSharp.Compiler.Interactive.Shell.Settings.fsi, false)
        // FCS resolves FSharp.Core relative to the current directory, which is usually the folder of the host app.
        // see https://github.com/dotnet/fsharp/issues/9064
        let prevDir = Environment.CurrentDirectory
        try
            Directory.SetCurrentDirectory(Path.GetDirectoryName typeof<list<int>>.Assembly.Location)
#if NETFRAMEWORK
            NetFramework.showPortablePdbLineNumbers ()
            FsiEvaluationSession.Create(config, fsiArgs mode, new StringReader(""), fsiOut, fsiErr, legacyReferenceResolver = NetFramework.referenceResolver)
#else
            FsiEvaluationSession.Create(config, fsiArgs mode, new StringReader(""), fsiOut, fsiErr)
#endif
        finally
            try Directory.SetCurrentDirectory prevDir with _ -> ()

    let flags = BindingFlags.NonPublic ||| BindingFlags.Instance

#if NETFRAMEWORK
    /// The run whose code FSI is evaluating, and its thread. Only in background mode.
    let mutable evaluating: (Run * Thread) option = None

    /// Called under the state lock. Takes the thread of the run, so that it is aborted only once.
    /// On .NET Framework FCS has no CancellationTokenSource, an interactive session catches a ThreadAbortException,
    /// resets it and returns an OperationCanceledException.
    /// see https://github.com/dotnet/fsharp/blob/main/src/Compiler/Interactive/ControlledExecution.fs
    let tryTakeStop (_: FsiEvaluationSession) (run: Run) : (unit -> unit) option =
        match evaluating with
        | Some(r, thread) when obj.ReferenceEquals(r, run) ->
            evaluating <- None
            // Abort blocks while the thread is in a finally block, so never under a lock.
            Some(fun () -> Task.Run(fun () -> try thread.Abort() with _ -> ()) |> ignore)
        | _ -> None
#else
    /// The internal ControlledExecution of a session.
    /// see https://github.com/dotnet/fsharp/blob/main/src/Compiler/Interactive/ControlledExecution.fs
    let controlledExecution (s: FsiEvaluationSession) : obj =
        let controller = s.GetType().GetField("fsiInterruptController", flags).GetValue s
        controller.GetType().GetField("controlledExecution", flags).GetValue controller

    /// Called under the state lock. An interactive session evaluates inside System.Runtime.ControlledExecution.Run,
    /// with a new CancellationTokenSource for each evaluation. Snapshot that exact source, so a later run's source is never cancelled.
    let tryTakeStop (s: FsiEvaluationSession) (_: Run) : (unit -> unit) option =
        try
            let ce = controlledExecution s
            match ce.GetType().GetField("cts", flags).GetValue ce with
            | :? ValueOption<CancellationTokenSource> as cts ->
                match cts with
                | ValueSome c ->
                    Some(fun () ->
                        try
                            // ControlledExecution's abort callback may wait for the evaluation thread.
                            // It must not block the caller, the state lock, or the abandonment deadline.
                            c.CancelAsync().ContinueWith(Action<Task>(fun t -> if t.IsFaulted then t.Exception |> ignore)) |> ignore
                        with :? ObjectDisposedException -> ())
                | ValueNone -> None
            | _ -> None
        with _ ->
            None
#endif

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
                                oldSession
                                |> Option.iter (fun s ->
                                    FsiAssemblyResolver.unregister s
                                    try (s :> IDisposable).Dispose() with _ -> ())
                            try
                                let s = createSession m
                                try FsiAssemblyResolver.register s
                                with e -> host.LogError $"Caddis: values of earlier runs might not be found, setting up the resolver for FSI assemblies failed: {e.Message}"
                                lock sync (fun () ->
                                    session <- Some s
                                    worker <- if m = Background then Some(new WorkerThread("Caddis FSI")) else None
                                    state <- Ready)
                            with e ->
                                lock sync (fun () -> state <- Failed e.Message)
                                host.LogError $"Caddis: creating the F# Interactive session failed:\n{e}")
                    )
                initTask)

    /// FCS type-checks code as the file 'stdin.fsx' in the folder the session was created in,
    /// so relative #load paths of a script would not be found. This sets that folder via reflection during f.
    let withCheckFolder (s: FsiEvaluationSession) (scriptPath: string) (f: unit -> 'T) : 'T =
        let folder =
            if String.IsNullOrEmpty scriptPath then None
            else
                try
                    let builder = s.GetType().GetField("tcConfigB", flags).GetValue s
                    Some(builder, builder.GetType().GetProperty("implicitIncludeDir", flags ||| BindingFlags.Public))
                with _ ->
                    None
        match folder with
        | Some(builder, prop) when not (isNull prop) ->
            let prev = prop.GetValue builder
            prop.SetValue(builder, Path.GetDirectoryName scriptPath)
            try f () finally prop.SetValue(builder, prev)
        | _ -> f ()

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

    /// Cancelling a pending OnUi call wakes the script and leaves a no-op in the host's queue.
    /// Once the callback has started, its completion still belongs to the original run.
    let rec invokeForRun (run: Run) (f: Func<obj>) : obj =
        let token = run.CancellationToken
        token.ThrowIfCancellationRequested()
        if run.IsFinished then invalidOp $"Run '{run.Id}' has already finished."
        let completion = TaskCompletionSource<obj>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable phase = 0 // 0 queued, 1 started, 2 cancelled before starting
        use registration =
            token.Register(fun () ->
                if Interlocked.CompareExchange(&phase, 2, 0) = 0 then completion.TrySetCanceled token |> ignore)
        let work () =
            if Interlocked.CompareExchange(&phase, 1, 0) = 0 then
                try
                    if not (run.TryBeginUiCallback()) then
                        token.ThrowIfCancellationRequested()
                        invalidOp $"Run '{run.Id}' has already finished."
                    let result =
                        try
                            let context: ScriptState.Context = { run = run; invokeOnUi = invokeForRun run }
                            ScriptState.withContext context f.Invoke
                        finally run.EndUiCallback()
                    completion.TrySetResult result |> ignore
                with e -> completion.TrySetException e |> ignore
        (host.InvokeOnUi work).ContinueWith(
            Action<Task<unit>>(fun t ->
                if t.IsFaulted then completion.TrySetException(t.Exception.InnerExceptions) |> ignore
                elif t.IsCanceled then completion.TrySetCanceled() |> ignore)
        ) |> ignore
        let result = completion.Task.GetAwaiter().GetResult()
        token.ThrowIfCancellationRequested()
        result

    /// Called on the evaluation thread: the UI thread or the background worker.
    let evaluateOnThisThread (s: FsiEvaluationSession) (run: Run) : unit =
        run.Started <- true
        let evaluate () =
            match (try host.CannotRunReason() with e -> Some e.Message) with
            | Some reason -> RunOutcome.withMessage Rejected reason
            | None ->
                let mutable beforeRunDone = false
                // a RuntimeError until the evaluation returns, e.g. if a ThreadInterruptedException ends it
                let mutable outcome = RunOutcome.ofStatus RuntimeError
                try
                    try
                        if File.Exists run.ScriptPath then includeScriptFolder s run.ScriptPath
                        beginCapture run
                        host.BeforeRun run
                        beforeRunDone <- true
#if NETFRAMEWORK
                        let result, diagnostics =
                            if run.Mode = Background then lock sync (fun () -> evaluating <- Some(run, Thread.CurrentThread))
                            try s.EvalInteractionNonThrowing(run.Code, run.ScriptPath)
                            finally lock sync (fun () -> evaluating <- None)
#else
                        let result, diagnostics = s.EvalInteractionNonThrowing(run.Code, run.ScriptPath)
#endif
                        outcome <- outcomeOf run result diagnostics
                    with e ->
#if NETFRAMEWORK
                        // FSI resets an abort only while it runs the code, not e.g. while it compiles it
                        if (e :? ThreadAbortException) then Thread.ResetAbort()
#endif
                        outcome <-
                            if run.CancelRequested then cancelledOutcome run
                            else { RunOutcome.ofStatus RuntimeError with exn = Some(ExceptionInfo.ofExn run.ScriptPath e) }
                finally
                    endCapture run
                    if beforeRunDone then
                        let finalized =
                            try host.AfterRun(run, outcome.status)
                            with e -> Error $"Cleaning up after run '{run.Id}' failed: {e.Message}"
                        match finalized with
                        | Ok () -> ()
                        | Error message ->
                            run.AddNote message
                            if outcome.status = Succeeded then
                                outcome <- { outcome with status = RuntimeError; value = None; message = Some message }
                outcome
        let context: ScriptState.Context = { run = run; invokeOnUi = invokeForRun run }
        let outcome =
#if NETFRAMEWORK
            try
                ScriptState.withContext context evaluate
            with :? ThreadAbortException ->
                Thread.ResetAbort() // aborted after FSI returned, during cleanup the abort waits until the finally block ends
                cancelledOutcome run
#else
            ScriptState.withContext context evaluate
#endif
        finishRun run outcome

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
                        let settings = getSettings ()
                        let run = Run(id, FSharpCode, mode, req.code, scriptPath, outDir, settings.maxOutputChars, req.recordUndo)
                        if settings.cancelWaitingCommands then
                            run.InputTimeout <- Some(TimeSpan.FromMilliseconds(float CaddisSettings.inputTimeoutMs))
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

    /// Starts a run of the host app on the UI thread, in any mode and also while the session is not ready, e.g. of commands or an export.
    /// It is the current run like an F# run, so the two never overlap. Raises a BusyException if another run is in progress.
    /// work: called on the UI thread with the run and its output folder, returns the outcome.
    member private _.StartHostRun(kind: RunKind, idPrefix: string, code: string, work: Run -> RunOutcome) : Run =
        let run =
            lock sync (fun () ->
                match current with
                | Some r -> raise (BusyException r.Id)
                | None -> ()
                let id = runs.NextId idPrefix
                let run = Run(id, kind, UiThread, code, "", runs.CreateFolder id, (getSettings ()).maxOutputChars, true)
                runs.Add run
                current <- Some run
                run)
        let onUi () =
            run.Started <- true
            let outcome =
                match (try host.CannotRunReason() with e -> Some e.Message) with
                | Some reason -> RunOutcome.withMessage Rejected reason
                | None ->
                    // e.g. objects to select not found, the stack trace would only show Caddis code
                    try work run
                    with e -> RunOutcome.withMessage RuntimeError e.Message
            finishRun run outcome
        (host.InvokeOnUi onUi)
            .ContinueWith(
                Action<Task<unit>>(fun t ->
                    if t.IsFaulted then
                        finishRun run { RunOutcome.ofStatus RuntimeError with exn = Some(ExceptionInfo.ofExn "" (stripInvocation t.Exception)) })
            )
        |> ignore
        run

    /// Starts a run of host commands, e.g. Rhino command macros. Raises a BusyException if another run is in progress.
    member this.StartCommands(req: CommandRequest) : Run =
        this.StartHostRun(HostCommands, "c", String.concat "\n" req.macros, fun run -> host.RunCommands(run, req))

    /// Starts an export to a file with the given extension, e.g. "stl", in the output folder of the run.
    /// Raises a BusyException if another run is in progress.
    member this.StartExport(ext: string, baseName: string, selection: ExportSelection) : Run =
        let what =
            match selection with
            | AllObjects -> "all"
            | SelectedObjects -> "selected"
            | ObjectIds ids -> $"{ids.Length} objects"
        this.StartHostRun(
            HostExport,
            "x",
            $"export {what} to {baseName}.{ext}",
            fun run ->
                host.Export(selection, Path.Combine(run.OutDir, baseName + "." + ext))
                if FileItem.listFolder run.OutDir |> Array.isEmpty then RunOutcome.withMessage RuntimeError $"Nothing was exported to '.{ext}'."
                else RunOutcome.ofStatus Succeeded
        )

    /// Parses and type-checks code against the current session, so definitions of earlier runs are known, without running it.
    /// Runs on the evaluation thread, so it never overlaps with a run. Raises a BusyException if a run is in progress.
    member this.Check(code: string, scriptPath: string) : Task<Diagnostic[]> =
        task {
            do! this.Initialize()
            let s, w, m =
                lock sync (fun () ->
                    match current with
                    | Some r -> raise (BusyException r.Id)
                    | None -> ()
                    match state, session with
                    | Ready, Some s -> s, worker, mode
                    | Failed msg, _ -> failwith $"F# Interactive could not be started: {msg}"
                    | st, _ -> failwith $"F# Interactive is not ready, its state is '{FsiState.toString st}'.")
            let work () =
                if not (String.IsNullOrEmpty scriptPath) && File.Exists scriptPath then includeScriptFolder s scriptPath
                let parseResults, checkResults, _ = withCheckFolder s scriptPath (fun () -> s.ParseAndCheckInteraction code)
                Array.append parseResults.Diagnostics checkResults.Diagnostics
                |> Array.map (fun d ->
                    let diag = Diagnostic.ofFcs d
                    // FCS checks the code as a file 'stdin.fsx' in the current directory
                    if Path.GetFileName diag.file = "stdin.fsx" then { diag with file = "" } else diag)
                |> Array.distinct
            match m, w with
            | Background, Some w -> return! w.Invoke work
            | _ -> return! host.InvokeOnUi work
        }

    /// Requests cancellation of a background run.
    /// If it does not stop within a few seconds its thread is abandoned and the session is reset.
    /// Returns false for runs on the UI thread, they can't be cancelled.
    member this.CancelRun(run: Run) : bool =
        if run.IsFinished || run.Mode <> Background then
            false
        else
            run.MarkCancelRequested()
            if run.TryStartCancellation() then
                backgroundTask {
                    let mutable deadline = DateTime.UtcNow + abandonAfter
                    while not run.IsFinished && (DateTime.UtcNow < deadline || run.HasActiveUiCallback) do
                        // Give cleanup its full grace period once an already-started host call returns.
                        if run.HasActiveUiCallback then deadline <- DateTime.UtcNow + abandonAfter
                        // FCS can be stopped only after evaluation starts. Never stop a later run.
                        let stop =
                            lock sync (fun () ->
                                match current, session with
                                | Some r, Some s when obj.ReferenceEquals(r, run) && not run.HasActiveUiCallback -> tryTakeStop s run
                                | _ -> None)
                        stop |> Option.iter (fun f -> f ())
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
        | Some r when r.Kind = HostExport -> Error $"Run '{r.Id}' is an export, it can't be cancelled."
        | Some r when r.Kind = HostCommands ->
            // the host cancels the command when it waits for input
            r.MarkCancelRequested()
            Ok $"Cancellation of run '{r.Id}' was requested. A command that waits in a dialog can only be stopped in {host.Info.appName}."
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
