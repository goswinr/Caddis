namespace Caddis

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Threading.Tasks
open System.Text.Json.Nodes

type EvalArgs = {
    code: string option
    /// Full path of an .fsx file. If code is given too, the code is evaluated as if it were in that file.
    path: string option
    /// Background mode: cancel the run after this time. UI-thread mode: return the still running run after this time.
    timeoutMs: int option
    recordUndo: bool
    }


/// The operations behind the REST and MCP endpoints.
type CaddisService(host: ICaddisHost, store: SettingsStore, runs: RunRegistry) =

    let mutable settings = store.Load()

    let fsi = FsiHost(host, (fun () -> settings), runs)

    let checker = lazy ScriptChecker(host)

    let versionOf (a: Assembly) =
        match a.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> string (a.GetName().Version)
        | v -> v.InformationalVersion.Split('+')[0] // without the git commit hash

    let version = versionOf typeof<CaddisService>.Assembly

    /// The code and the script path of a request.
    let resolveCode (code: string option) (path: string option) : string * string =
        match code, path with
        | _, Some p when not (String.IsNullOrWhiteSpace p) ->
            let full = Path.GetFullPath p
            match code with
            | Some c -> c, full
            | None ->
                if not (File.Exists full) then invalidArg "path" $"File not found: {full}"
                File.ReadAllText full, full
        | Some c, _ -> c, null
        | None, _ -> invalidArg "code" "Provide 'code' or 'path'."

    let safeFileName (s: string) =
        let invalid = Path.GetInvalidFileNameChars()
        String(s.Trim().ToCharArray() |> Array.map (fun c -> if Array.contains c invalid then '_' else c))

    member _.Host = host
    member _.Fsi = fsi
    member _.Runs = runs
    member _.Store = store
    member _.Version = version
    member _.Settings = settings

    /// Changes and saves the settings.
    member _.UpdateSettings(f: CaddisSettings -> CaddisSettings) =
        settings <- f settings
        store.Save settings

    /// Starts a run and returns it once it has been handed to the evaluation thread.
    member _.StartEval(args: EvalArgs) : Task<Run> =
        let code, scriptPath = resolveCode args.code args.path
        fsi.Evaluate {
            code = code
            scriptPath = scriptPath
            recordUndo = args.recordUndo
            timeout = args.timeoutMs |> Option.filter (fun ms -> ms > 0) |> Option.map (float >> TimeSpan.FromMilliseconds)
        }

    /// Waits for a run to finish.
    /// A run on the UI thread can't be cancelled, so after the timeout it is returned while still running.
    /// In background mode the timeout cancels the run, so this always waits until it finished.
    member _.WaitFor(run: Run, timeoutMs: int option) : Task =
        task {
            match timeoutMs with
            | Some ms when ms > 0 && run.Mode = UiThread ->
                let! _ = Task.WhenAny(run.Completion, Task.Delay ms)
                ()
            | _ -> do! run.Completion
        }
        :> Task

    /// Starts a run and waits for it.
    member this.Eval(args: EvalArgs) : Task<Run> =
        task {
            let! run = this.StartEval args
            do! this.WaitFor(run, args.timeoutMs)
            return run
        }

    /// Switches the thread mode, saves it as default and resets the session.
    member this.SetMode(m: ThreadMode) : Task =
        task {
            do! fsi.SetMode m
            this.UpdateSettings(fun s -> { s with threadMode = m })
        }
        :> Task

    member _.Check(code: string option, path: string option) : Task<Diagnostic[]> =
        let code, scriptPath = resolveCode code path
        let scriptPath = if isNull scriptPath then Path.Combine(runs.RootDir, "check.fsx") else scriptPath
        checker.Value.Check(code, scriptPath) |> Async.StartAsTask

    /// Saves a viewport as PNG. Returns the id of the output folder and the file.
    member _.CaptureViewport(view: string, width: int, height: int, displayMode: string) : Task<string * FileItem> =
        task {
            let id = runs.NextId "v"
            let dir = runs.CreateFolder id
            let name = (if String.IsNullOrWhiteSpace view then "view" else safeFileName view) + ".png"
            let path = Path.Combine(dir, name)
            do! host.InvokeOnUi(fun () -> host.CaptureView(view, width, height, displayMode, path))
            match FileItem.listFolder dir |> Array.tryHead with
            | Some f -> return id, f
            | None -> return failwith "The viewport capture did not write a file."
        }

    /// Exports objects to a file of the given format, e.g. "stl". Returns the id of the output folder and the files.
    /// Some formats write more than one file, e.g. obj and mtl.
    member _.Export(format: string, name: string option, selection: ExportSelection) : Task<string * FileItem[]> =
        task {
            let ext = if isNull format then "" else format.Trim().TrimStart('.').ToLowerInvariant()
            if ext = "" || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 then
                invalidArg "format" $"Invalid file format '{format}', use a file extension like 'stl', '3dm' or 'step'."
            let id = runs.NextId "x"
            let dir = runs.CreateFolder id
            let baseName = name |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.map safeFileName |> Option.defaultValue "export"
            let path = Path.Combine(dir, baseName + "." + ext)
            do! host.InvokeOnUi(fun () -> host.Export(selection, path))
            let files = FileItem.listFolder dir
            if files.Length = 0 then failwith $"Nothing was exported to '.{ext}'."
            return id, files
        }

    /// Undoes the given run, or the latest run with an undo record.
    member _.Undo(runId: string option) : Task<Result<string, string>> =
        let run =
            match runId with
            | Some id -> runs.TryGet id
            | None -> runs.All |> Array.rev |> Array.tryFind (fun r -> r.UndoSerial.IsSome)
        match run with
        | None -> Task.FromResult(Error(match runId with Some id -> $"Run '{id}' not found." | None -> "There is no run with an undo record."))
        | Some r when not r.IsFinished -> Task.FromResult(Error $"Run '{r.Id}' is still in progress.")
        | Some r -> host.InvokeOnUi(fun () -> host.Undo r)

    /// A short summary of a run, for lists.
    member _.RunSummary(r: Run) : JsonNode =
        let firstLine =
            r.Code.Split('\n')
            |> Array.map _.Trim()
            |> Array.tryFind (fun l -> l <> "" && not (l.StartsWith "//"))
            |> Option.defaultValue ""
        Json.obj [
            "id", Json.ofString r.Id
            "status", Json.ofString (RunStatus.toString r.Status)
            "startedAt", Json.ofString (r.StartedAt.ToString "o")
            "durationMs", Json.ofInt64 (int64 r.Duration.TotalMilliseconds)
            "firstLine", Json.ofString (if firstLine.Length > 100 then firstLine.Substring(0, 100) + "..." else firstLine)
        ]

    member _.Status(extra: (string * JsonNode) list) : JsonObject =
        let memoryMB =
            use p = Process.GetCurrentProcess()
            p.WorkingSet64 / 1024L / 1024L
        let hostStatus =
            try host.Status()
            with e -> [ "hostStatusError", Json.ofString e.Message ]
        Json.obj (
            [
                "name", Json.ofString "Caddis"
                "version", Json.ofString version
                "host", Json.ofString host.Description
                "state", Json.ofString (FsiState.toString fsi.State)
                "stateMessage", (match fsi.State with Failed m -> Json.ofString m | _ -> null)
                "mode", Json.ofString (ThreadMode.toString fsi.Mode)
                "currentRun", (fsi.Current |> Json.ofOption (fun r -> Json.ofString r.Id))
                "lastRun", (runs.Last |> Json.ofOption (fun r -> Json.ofString r.Id))
                "fsharpCore", Json.ofString (versionOf typeof<list<int>>.Assembly)
                "fsharpCompilerService", Json.ofString (versionOf typeof<FSharp.Compiler.Interactive.Shell.FsiEvaluationSession>.Assembly)
                "dotnet", Json.ofString RuntimeInformation.FrameworkDescription
                "os", Json.ofString RuntimeInformation.OSDescription
                "processId", Json.ofInt Environment.ProcessId
                "memoryMB", Json.ofInt64 memoryMB
                "outputFolder", Json.ofString runs.RootDir
            ]
            @ extra
            @ hostStatus
        )
