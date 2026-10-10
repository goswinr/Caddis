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
    /// Background mode: cancel the run after this time. Ignored on the UI thread, runs there can't be cancelled.
    timeoutMs: int option
    /// Return after this time even if the run has not finished, it keeps running. None waits until it finished, 0 returns at once.
    waitMs: int option
    recordUndo: bool
    }

type CommandArgs = {
    /// Command macros, e.g. "-_Line 0,0,0 10,0,0".
    commands: string[]
    /// Objects to select before the first command, replacing the selection, in the id format of the host.
    select: string[] option
    /// Cancel a command that waits this long for input, 0 waits until someone gives it. None uses the default.
    inputTimeoutMs: int option
    /// Return after this time even if the run has not finished, it keeps running. None waits until it finished, 0 returns at once.
    waitMs: int option
    }


/// The operations behind the REST and MCP endpoints.
type CaddisService(host: ICaddisHost, store: SettingsStore, runs: RunRegistry) =

    let mutable settings = store.Load()

    let fsi = FsiHost(host, (fun () -> settings), runs)

    let api = ApiDocs(host)

    /// Raises a BusyException if a run is in progress, or only if it runs on the UI thread.
    let failIfBusy (onlyUiThread: bool) =
        match fsi.Current with
        | Some r when not onlyUiThread || r.Mode = UiThread -> raise (BusyException r.Id)
        | _ -> ()

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

    /// A command that waits this long for input that its macro does not give is cancelled, by default.
    static member DefaultInputTimeoutMs = CaddisSettings.inputTimeoutMs

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

    /// Waits for a run to finish, or at most waitMs. None waits until it finished, 0 or less returns at once.
    member _.WaitFor(run: Run, waitMs: int option) : Task =
        task {
            match waitMs with
            | None -> do! run.Completion
            | Some ms when ms > 0 && not run.IsFinished ->
                let! _ = Task.WhenAny(run.Completion, Task.Delay ms)
                ()
            | Some _ -> ()
        }
        :> Task

    /// Starts a run and waits for it, at most args.waitMs.
    member this.Eval(args: EvalArgs) : Task<Run> =
        task {
            let! run = this.StartEval args
            do! this.WaitFor(run, args.waitMs)
            return run
        }

    /// Starts a run of host commands, e.g. Rhino command macros. Raises a BusyException if a run is in progress.
    member _.StartCommands(args: CommandArgs) : Run =
        if host.Info.mcp.command.IsNone then failwith $"{host.Info.appName} has no commands to run, use /eval instead."
        // '!' cancels a running command in button macros, RunScript doesn't support it, and commands never run while another one does
        let macros =
            args.commands
            |> Array.map (fun m -> m.TrimStart().TrimStart('!').TrimStart())
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
        if macros.Length = 0 then invalidArg "commands" "Provide 'commands', e.g. [\"-_Line 0,0,0 10,0,0\"]."
        fsi.StartCommands {
            macros = macros
            select = args.select
            inputTimeout =
                match args.inputTimeoutMs |> Option.defaultValue CaddisService.DefaultInputTimeoutMs with
                | ms when ms <= 0 -> None
                | ms -> Some(TimeSpan.FromMilliseconds(float ms))
        }

    /// Switches the thread mode, saves it as default and resets the session.
    member this.SetMode(m: ThreadMode) : Task =
        task {
            do! fsi.SetMode m
            this.UpdateSettings(fun s -> { s with threadMode = m })
        }
        :> Task

    /// Type-checks code against the current session without running it. Raises a BusyException if a run is in progress.
    member _.Check(code: string option, path: string option) : Task<Diagnostic[]> =
        let code, scriptPath = resolveCode code path
        fsi.Check(code, scriptPath)

    /// Describes a namespace, type or member of the loaded assemblies, e.g. 'Brep.CreateFromBox', or searches their names if no name is given.
    /// filter: for a type, only the members whose name contains it. Uses only reflection, so it works while a run is in progress.
    member _.Describe(name: string option, search: string option, filter: string option) : string =
        let given = Option.filter (String.IsNullOrWhiteSpace >> not)
        match given name, given search with
        | Some n, _ -> api.Describe(n, given filter |> Option.toObj)
        | None, Some s -> api.Search s
        | None, None -> invalidArg "name" "Provide 'name' of a namespace, type or member, e.g. 'System.IO', 'File' or 'File.ReadAllText', or 'search', e.g. 'offset'."

    /// Saves a viewport as PNG. Returns the id of the output folder and the file.
    /// Raises a BusyException if a run is in progress on the UI thread, a background run doesn't block the capture.
    member _.CaptureViewport(view: string, width: int, height: int, displayMode: string) : Task<string * FileItem> =
        task {
            failIfBusy true
            let id = runs.NextId "v"
            let dir = runs.CreateFolder id
            let name = (if String.IsNullOrWhiteSpace view then "view" else safeFileName view) + ".png"
            let path = Path.Combine(dir, name)
            do! host.InvokeOnUi(fun () -> host.CaptureView(view, width, height, displayMode, path))
            match FileItem.listFolder dir |> Array.tryHead with
            | Some f -> return id, f
            | None -> return failwith "The viewport capture did not write a file."
        }

    /// Starts an export of objects to a file of the given format, e.g. "stl", as a run. Its files are those of the run when it finished.
    /// Some formats write more than one file, e.g. obj and mtl. Raises a BusyException if a run is in progress.
    member _.StartExport(format: string, name: string option, selection: ExportSelection) : Run =
        let ext = if isNull format then "" else format.Trim().TrimStart('.').ToLowerInvariant()
        if ext = "" || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 then
            invalidArg "format" $"Invalid file format '{format}', use a file extension like 'stl', '3dm' or 'step'."
        let baseName = name |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.map safeFileName |> Option.defaultValue "export"
        fsi.StartExport(ext, baseName, selection)

    /// The modal dialogs of the host app, which block calls into it. Empty if the host can't find them.
    member _.Dialogs() : HostDialog list =
        try host.Dialogs()
        with _ -> []

    /// Presses a button of a dialog of the host app. An empty dialog id means the only dialog.
    member _.PressDialogButton(dialogId: string option, button: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace button then invalidArg "button" "Provide the name of the 'button' to press."
        host.PressDialogButton(dialogId |> Option.defaultValue "", button.Trim())

    /// Undoes the given run, or the latest run with an undo record. Raises a BusyException if a run is in progress.
    member _.Undo(runId: string option) : Task<Result<string, string>> =
        failIfBusy false
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

    member self.Status(extra: (string * JsonNode) list) : JsonObject =
        let memoryMB =
            use p = Process.GetCurrentProcess()
            p.WorkingSet64 / 1024L / 1024L
        let hostStatus =
            try host.Status()
            with e -> [ "hostStatusError", Json.ofString e.Message ]
        let dialogs =
            match self.Dialogs() with
            | [] -> []
            | ds -> [ "dialogs", Json.arr (ds |> List.map HostDialog.toJson) :> JsonNode ]
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
            @ dialogs
            @ hostStatus
        )
