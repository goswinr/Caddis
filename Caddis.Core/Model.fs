namespace Caddis

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open FSharp.Compiler.Diagnostics

type RunStatus =
    | Running
    | Succeeded
    | CompileError
    | RuntimeError
    | Cancelled
    | TimedOut
    /// The run could not start, e.g. because a Rhino command is active.
    | Rejected

module RunStatus =

    let toString (s: RunStatus) =
        match s with
        | Running -> "running"
        | Succeeded -> "ok"
        | CompileError -> "compileError"
        | RuntimeError -> "runtimeError"
        | Cancelled -> "cancelled"
        | TimedOut -> "timeout"
        | Rejected -> "rejected"


/// A compiler error or warning. Lines and columns are 1-based.
type Diagnostic = {
    severity: string
    message: string
    errorNumber: int
    file: string
    startLine: int
    startColumn: int
    endLine: int
    endColumn: int
    }

module Diagnostic =

    let ofFcs (d: FSharpDiagnostic) : Diagnostic = {
        severity =
            match d.Severity with
            | FSharpDiagnosticSeverity.Error -> "error"
            | FSharpDiagnosticSeverity.Warning -> "warning"
            | FSharpDiagnosticSeverity.Info -> "info"
            | FSharpDiagnosticSeverity.Hidden -> "hidden"
        message = d.Message
        errorNumber = d.ErrorNumber
        file = d.FileName
        startLine = d.StartLine
        startColumn = d.StartColumn + 1
        endLine = d.EndLine
        endColumn = d.EndColumn + 1
        }

    let toJson (d: Diagnostic) : JsonNode =
        Json.obj [
            "severity", Json.ofString d.severity
            "message", Json.ofString d.message
            "errorNumber", Json.ofInt d.errorNumber
            "file", Json.ofString d.file
            "startLine", Json.ofInt d.startLine
            "startColumn", Json.ofInt d.startColumn
            "endLine", Json.ofInt d.endLine
            "endColumn", Json.ofInt d.endColumn
        ]


type ExceptionInfo = {
    typeName: string
    message: string
    /// The full exception text including inner exceptions and stack trace.
    text: string
    /// The line in the evaluated script where the exception was raised, if found in the stack trace.
    scriptLine: int option
    }

module ExceptionInfo =

    let rec private strip (e: exn) =
        match e with
        | :? TargetInvocationException as t when not (isNull t.InnerException) -> strip t.InnerException
        | _ -> e

    /// Finds the first stack trace line pointing into the script, e.g. "at <StartupCode$FSI_0004>... in C:\x\script.fsx:line 12"
    let private findScriptLine (scriptPath: string) (text: string) =
        let fileName = Path.GetFileName scriptPath
        if String.IsNullOrEmpty fileName then
            None
        else
            let marker = fileName + ":line "
            text.Split('\n')
            |> Array.tryPick (fun ln ->
                let i = ln.IndexOf(marker, StringComparison.OrdinalIgnoreCase)
                if i < 0 then
                    None
                else
                    let digits = ln.Substring(i + marker.Length).ToCharArray() |> Array.takeWhile Char.IsDigit
                    match Int32.TryParse(String digits) with
                    | true, n -> Some n
                    | _ -> None)

    let ofExn (scriptPath: string) (e: exn) : ExceptionInfo =
        let e = strip e
        let text = e.ToString()
        {
            typeName = e.GetType().FullName
            message = e.Message
            text = text
            scriptLine = findScriptLine scriptPath text
        }

    let toJson (x: ExceptionInfo) : JsonNode =
        Json.obj [
            "type", Json.ofString x.typeName
            "message", Json.ofString x.message
            "scriptLine", Json.ofOption Json.ofInt x.scriptLine
            "text", Json.ofString x.text
        ]


/// A file produced by a run, a viewport capture or an export.
type FileItem = {
    /// Path relative to the output folder, with '/' as separator.
    name: string
    size: int64
    mime: string
    /// Full local path.
    path: string
    }

module FileItem =

    let mimeOf (path: string) =
        match Path.GetExtension(path).ToLowerInvariant() with
        | ".png" -> "image/png"
        | ".jpg" | ".jpeg" -> "image/jpeg"
        | ".gif" -> "image/gif"
        | ".webp" -> "image/webp"
        | ".bmp" -> "image/bmp"
        | ".tif" | ".tiff" -> "image/tiff"
        | ".svg" -> "image/svg+xml"
        | ".pdf" -> "application/pdf"
        | ".txt" | ".log" | ".fsx" | ".fs" | ".py" | ".cs" -> "text/plain"
        | ".csv" -> "text/csv"
        | ".json" -> "application/json"
        | ".html" | ".htm" -> "text/html"
        | ".xml" -> "application/xml"
        | ".stl" -> "model/stl"
        | ".obj" -> "model/obj"
        | ".3mf" -> "model/3mf"
        | ".step" | ".stp" -> "model/step"
        | ".iges" | ".igs" -> "model/iges"
        | ".gltf" -> "model/gltf+json"
        | ".glb" -> "model/gltf-binary"
        | ".dwg" -> "image/vnd.dwg"
        | ".dxf" -> "image/vnd.dxf"
        | ".zip" -> "application/zip"
        | _ -> "application/octet-stream"

    let ofPath (dir: string) (path: string) : FileItem = {
        name = Path.GetRelativePath(dir, path).Replace('\\', '/')
        size = FileInfo(path).Length
        mime = mimeOf path
        path = path
        }

    /// All files in a folder and its subfolders.
    let listFolder (dir: string) : FileItem[] =
        if not (Directory.Exists dir) then
            [||]
        else
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            |> Array.sort
            |> Array.map (ofPath dir)

    /// The download URL of a file, e.g. http://127.0.0.1:7810/files/r3/plot.png
    let url (baseUrl: string) (id: string) (f: FileItem) =
        let name = f.name.Split('/') |> Array.map Uri.EscapeDataString |> String.concat "/"
        $"{baseUrl}/files/{Uri.EscapeDataString id}/{name}"

    let toJson (baseUrl: string) (id: string) (f: FileItem) : JsonNode =
        Json.obj [
            "name", Json.ofString f.name
            "size", Json.ofInt64 f.size
            "mime", Json.ofString f.mime
            "path", Json.ofString f.path
            "url", Json.ofString (url baseUrl id f)
        ]


/// What a run produced once it finished.
type RunOutcome = {
    status: RunStatus
    /// The F# type and the printed value of the last expression, if any.
    value: (string * string) option
    diagnostics: Diagnostic[]
    exn: ExceptionInfo option
    /// e.g. why a run was rejected or why the session was reset.
    message: string option
    }

module RunOutcome =

    let ofStatus (status: RunStatus) = {
        status = status
        value = None
        diagnostics = [||]
        exn = None
        message = None
        }

    let withMessage (status: RunStatus) (message: string) = { ofStatus status with message = Some message }


type RunKind =
    /// F# code evaluated in the FSI session.
    | FSharpCode
    /// Commands of the host app, e.g. Rhino command macros like "-_Line 0,0,0 10,0,0", see ICaddisHost.RunCommands.
    | HostCommands
    /// An export to a file, see ICaddisHost.Export.
    | HostExport

module RunKind =

    let toString (k: RunKind) =
        match k with
        | FSharpCode -> "fsharp"
        | HostCommands -> "commands"
        | HostExport -> "export"


/// What one macro of a command run did, e.g. "-_Line 0,0,0 10,0,0".
type CommandStep = {
    macro: string
    /// The English names and results of the commands that ran, e.g. ("Line", "Success"). Usually one, none for an unknown command name.
    commands: (string * string) list
    /// The prompt the command waited at when Caddis cancelled it, because the macro lacked an input or via caddis_cancel.
    waitedAt: string option
    }

/// What a command run did. Object ids are in the format of the host, e.g. GUIDs in Rhino.
type CommandReport = {
    /// The macros that ran, in order.
    steps: CommandStep list
    /// The macros after the one that failed.
    notRun: string list
    created: string[]
    changed: string[]
    /// The changed objects counted per group, e.g. per Revit category, most first. If not empty, results show it instead of the ids of changed objects:
    /// Revit reports many elements that it updated itself, e.g. schedules.
    changedGroups: (string * int) list
    deleted: string[]
    /// The selected objects after the commands, if the selection changed.
    selected: string[] option
    }

module CommandReport =

    /// e.g. "Line: Success", "no command ran"
    let stepResult (s: CommandStep) =
        match s.commands with
        | [] -> "no command ran"
        | cs -> cs |> List.map (fun (name, result) -> $"{name}: {result}") |> String.concat ", "

    /// One line per macro, e.g. "-_Line 0,0,0 10,0,0 -> Line: Success".
    let stepLines (r: CommandReport) : string list = [
        for s in r.steps do
            let waited = s.waitedAt |> Option.map (fun p -> $" (cancelled while waiting at the prompt '{p}')") |> Option.defaultValue ""
            $"{s.macro} -> {stepResult s}{waited}"
        for m in r.notRun do
            $"{m} -> not run"
    ]

    let toJson (r: CommandReport) : JsonNode =
        let ids (a: string[]) = Json.ofStrings a :> JsonNode
        Json.obj [
            "steps",
            Json.arr [
                for s in r.steps ->
                    Json.obj [
                        "macro", Json.ofString s.macro
                        "commands", Json.arr [ for name, result in s.commands -> Json.obj [ "name", Json.ofString name; "result", Json.ofString result ] ]
                        "waitedAt", Json.ofOption Json.ofString s.waitedAt
                    ]
            ]
            "notRun", Json.ofStrings r.notRun
            "created", ids r.created
            "changed", ids r.changed
            "changedByGroup", (if r.changedGroups.IsEmpty then null else Json.obj [ for g, n in r.changedGroups -> g, Json.ofInt n ] :> JsonNode)
            "deleted", ids r.deleted
            "selected", Json.ofOption ids r.selected
        ]


/// One evaluation of F# code in the FSI session, or one run of host commands.
type Run(id: string, kind: RunKind, mode: ThreadMode, code: string, scriptPath: string, outDir: string, maxChars: int, recordUndo: bool) =
    let startedAt = DateTime.UtcNow
    let watch = Stopwatch.StartNew()
    let stdOut = CaptureBuffer maxChars
    let stdErr = CaptureBuffer maxChars
    let fsiOut = CaptureBuffer maxChars
    let commandLine = CaptureBuffer maxChars
    let completion = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable outcome = RunOutcome.ofStatus Running
    let mutable duration = TimeSpan.Zero
    let mutable files: FileItem[] = [||]
    let cancellation = new CancellationTokenSource()
    let mutable cancellationStarted = 0
    let uiCallbackLock = obj ()
    let mutable activeUiCallbacks = 0
    let mutable timeoutRequested = false
    let notes = ResizeArray<string>()

    member _.Id = id
    member _.Kind = kind
    member _.Mode = mode
    /// The F# code, or the command macros one per line.
    member _.Code = code
    /// Used as file name for FSI, so __SOURCE_DIRECTORY__, #load and line numbers in stack traces work.
    member _.ScriptPath = scriptPath
    /// The folder for files of this run, see Caddis.Script.OutDir.
    member _.OutDir = outDir
    member _.RecordUndo = recordUndo
    member _.StartedAt = startedAt
    member _.StdOut = stdOut
    member _.StdErr = stdErr
    member _.FsiOut = fsiOut
    /// What was printed to the command line of the host app during the run, e.g. by Rhino commands, set by the host.
    member _.CommandLine = commandLine
    member _.Outcome = outcome
    member _.Status = outcome.status
    member _.IsFinished = outcome.status <> Running
    member _.Duration = if outcome.status = Running then watch.Elapsed else duration
    /// Completes when the outcome is set.
    member _.Completion: Task = completion.Task
    /// The files in the output folder, set when the run finishes.
    member _.Files = files

    /// Set when the evaluation thread picks up the run. A run on the UI thread waits until the host app runs it,
    /// e.g. Revit only when no dialog or command is active.
    member val Started = false with get, set

    member _.CancelRequested = cancellation.IsCancellationRequested
    member _.TimeoutRequested = timeoutRequested
    member _.MarkCancelRequested() = lock uiCallbackLock (fun () -> cancellation.Cancel())
    /// Cancels waits for queued script callbacks, without aborting work already running in the host.
    member internal _.CancellationToken = cancellation.Token
    member internal _.TryStartCancellation() = Interlocked.CompareExchange(&cancellationStarted, 1, 0) = 0
    member internal _.TryBeginUiCallback() =
        lock uiCallbackLock (fun () ->
            if cancellation.IsCancellationRequested || outcome.status <> Running then false
            else
                activeUiCallbacks <- activeUiCallbacks + 1
                true)
    member internal _.EndUiCallback() = lock uiCallbackLock (fun () -> activeUiCallbacks <- activeUiCallbacks - 1)
    member internal _.HasActiveUiCallback = lock uiCallbackLock (fun () -> activeUiCallbacks > 0)
    member _.MarkTimeoutRequested() = timeoutRequested <- true

    /// Set by the host when the run is wrapped in an undo record. The latest one if the run made several.
    member val UndoSerial: uint32 option = None with get, set

    /// How many undo records the run made, e.g. one per Rhino command. Only used if UndoSerial is set.
    member val UndoSteps = 1 with get, set

    /// The runtime serial number of the document of the undo record.
    member val DocSerial = 0u with get, set

    /// Set by the host after a command run. Also after an F# run by a host that tracks the objects it changed, e.g. Revit, then without steps.
    member val Commands: CommandReport option = None with get, set

    /// F# runs: cancel host commands that the script starts, e.g. via RhinoApp.RunScript, when they wait this long for input.
    member val InputTimeout: TimeSpan option = None with get, set

    /// Remarks of the host about the run, e.g. that it cancelled a command that waited for input.
    member _.Notes: string list = lock notes (fun () -> List.ofSeq notes)

    member _.AddNote(note: string) = lock notes (fun () -> notes.Add note)

    /// Sets the final outcome. Only the first call has an effect, later ones return false.
    /// e.g. a thread that was abandoned after it ignored a cancellation might finish much later.
    member this.Complete(o: RunOutcome) : bool =
        let isFirst =
            lock this (fun () ->
                if outcome.status = Running && o.status <> Running then
                    outcome <- o
                    duration <- watch.Elapsed
                    files <- (try FileItem.listFolder outDir with _ -> [||])
                    true
                else
                    false)
        if isFirst then completion.TrySetResult() |> ignore
        isFirst

    member this.ToJson(baseUrl: string) : JsonObject =
        let o = outcome
        let value =
            match o.value with
            | Some(t, v) -> Json.obj [ "type", Json.ofString t; "text", Json.ofString v ] :> JsonNode
            | None -> null
        Json.obj [
            "id", Json.ofString id
            "kind", Json.ofString (RunKind.toString kind)
            "status", Json.ofString (RunStatus.toString o.status)
            "started", Json.ofBool this.Started
            "mode", Json.ofString (ThreadMode.toString mode)
            "scriptPath", Json.ofString scriptPath
            "startedAt", Json.ofString (startedAt.ToString "o")
            "durationMs", Json.ofInt64 (int64 this.Duration.TotalMilliseconds)
            "message", Json.ofOption Json.ofString o.message
            "notes", Json.ofStrings this.Notes
            "value", value
            "stdout", Json.ofString stdOut.Text
            "stderr", Json.ofString stdErr.Text
            "fsiOutput", Json.ofString fsiOut.Text
            "commandLine", Json.ofString commandLine.Text
            "outputTruncated", Json.ofBool (stdOut.Truncated || stdErr.Truncated || fsiOut.Truncated || commandLine.Truncated)
            "diagnostics", Json.arr (o.diagnostics |> Seq.map Diagnostic.toJson)
            "exception", Json.ofOption ExceptionInfo.toJson o.exn
            "commands", Json.ofOption CommandReport.toJson this.Commands
            "undoable", Json.ofBool this.UndoSerial.IsSome
            "outDir", Json.ofString outDir
            "files", Json.arr (files |> Seq.map (FileItem.toJson baseUrl id))
        ]


module Proc =

    let isRunning (pid: int) =
        try
            use p = Process.GetProcessById pid
            not p.HasExited
        with _ ->
            false


/// Keeps the recent runs and the output folders of runs, captures and exports.
type RunRegistry(rootDir: string) =
    let runs = ConcurrentDictionary<string, Run>()
    let order = ConcurrentQueue<Run>()
    let maxKept = 200
    let mutable counter = 0

    /// One folder per process, e.g. %TEMP%/Caddis/1234
    static member DefaultRootDir =
        Path.Combine(Path.GetTempPath(), "Caddis", string Environment.ProcessId)

    /// Deletes the output folders of Caddis processes that are not running anymore.
    static member DeleteStaleFolders() =
        let parent = Path.Combine(Path.GetTempPath(), "Caddis")
        if Directory.Exists parent then
            for dir in Directory.GetDirectories parent do
                match Int32.TryParse(Path.GetFileName dir) with
                | true, pid when pid <> Environment.ProcessId && not (Proc.isRunning pid) ->
                    try Directory.Delete(dir, true) with _ -> ()
                | _ -> ()

    member _.RootDir = rootDir

    /// The id of the folder with saved output, see SaveLog.
    static member LogsId = "logs"

    /// Saves the output of a run to a text file, e.g. 'logs/r12-stdout.txt', and returns it.
    /// Its download URL uses the id RunRegistry.LogsId.
    member _.SaveLog(runId: string, stream: string, text: string) : FileItem =
        let dir = Path.Combine(rootDir, RunRegistry.LogsId)
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, $"{runId}-{stream}.txt")
        File.WriteAllText(path, text)
        FileItem.ofPath dir path

    /// A new unique id with the given prefix, e.g. "r12" for runs, "v13" for viewport captures.
    member _.NextId(prefix: string) = prefix + string (Interlocked.Increment(&counter))

    /// Creates the output folder for the given id.
    member _.CreateFolder(id: string) =
        let dir = Path.Combine(rootDir, id)
        Directory.CreateDirectory dir |> ignore
        dir

    member _.Add(run: Run) =
        runs[run.Id] <- run
        order.Enqueue run
        while order.Count > maxKept do
            match order.TryDequeue() with
            | true, old -> runs.TryRemove old.Id |> ignore // its files stay on disk until the process exits
            | _ -> ()

    member _.TryGet(id: string) : Run option =
        match runs.TryGetValue id with
        | true, r -> Some r
        | _ -> None

    /// The kept runs, oldest first.
    member _.All: Run[] = order.ToArray()

    member _.Last: Run option = order.ToArray() |> Array.tryLast

    /// The full path of a file in an output folder, or None if it does not exist or is outside of that folder.
    member _.TryGetFile(id: string, name: string) : string option =
        if String.IsNullOrWhiteSpace id || String.IsNullOrWhiteSpace name || id.IndexOfAny [| '/'; '\\'; '.' |] >= 0 then
            None
        else
            let dir = Path.GetFullPath(Path.Combine(rootDir, id)) + string Path.DirectorySeparatorChar
            let full = Path.GetFullPath(Path.Combine(dir, name))
            if full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists full then Some full else None


/// The size of a viewport capture.
module CaptureSize =

    /// Without a requested size, captures are scaled down to at most this many pixels on the long edge,
    /// models scale larger images down anyway.
    let defaultMaxEdge = 1280

    /// The capture size for a view of the given size. A requested width or height of 0 follows the aspect ratio of the view.
    /// If neither is given the size of the view is used, scaled down to at most defaultMaxEdge.
    let fit (viewWidth: int, viewHeight: int) (width: int, height: int) : int * int =
        let vw = max 1 viewWidth
        let vh = max 1 viewHeight
        let scaled (a: int) (num: int) (den: int) = max 1 (int (Math.Round(float a * float num / float den)))
        match width > 0, height > 0 with
        | true, true -> width, height
        | true, false -> width, scaled width vh vw
        | false, true -> scaled height vw vh, height
        | false, false ->
            let longEdge = max vw vh
            if longEdge <= defaultMaxEdge then vw, vh
            else scaled vw defaultMaxEdge longEdge, scaled vh defaultMaxEdge longEdge


/// Which objects to export.
type ExportSelection =
    | AllObjects
    | SelectedObjects
    /// Ids in the format of the host, see HostInfo.isObjectId.
    | ObjectIds of string[]


/// Commands to run in the host app, see ICaddisHost.RunCommands.
type CommandRequest = {
    /// Run one after the other until one doesn't succeed.
    macros: string[]
    /// Objects to select before the first command, replacing the selection.
    select: string[] option
    /// Cancel a command that waits this long for input. None waits until someone gives the input in the host app.
    inputTimeout: TimeSpan option
    }


/// A modal dialog of the host app, which blocks calls into it until it is closed.
type HostDialog = {
    /// e.g. the window handle "0x5A0C2E"
    id: string
    title: string
    /// The text of the dialog, e.g. its question.
    text: string
    /// The names of its buttons, e.g. ["Save the project"; "Cancel"].
    buttons: string list
    }

module HostDialog =

    let toJson (d: HostDialog) : JsonNode =
        Json.obj [
            "id", Json.ofString d.id
            "title", Json.ofString d.title
            "text", Json.ofString d.text
            "buttons", Json.ofStrings d.buttons
        ]

    /// e.g. "'Project Not Saved Recently' (id 0x5A0C2E): You have not saved ... Buttons: 'Save the project', 'Cancel'."
    let describe (d: HostDialog) =
        let text = if String.IsNullOrWhiteSpace d.text then "" else ": " + d.text.Trim()
        let buttons = d.buttons |> List.map (fun b -> $"'{b}'") |> String.concat ", "
        $"'{d.title}' (id {d.id}){text} Buttons: {buttons}."


/// The texts of the caddis_command tool, for hosts that have commands.
type CommandToolTexts = {
    description: string
    /// The 'commands' argument.
    commands: string
    /// The 'select' argument.
    select: string
    /// The 'inputTimeoutMs' argument.
    inputTimeoutMs: string
    }

/// The texts of the MCP endpoint that depend on the host app, written for the model.
type McpTexts = {
    /// Sent on initialize: what Caddis does in this app and how to use the tools.
    instructions: string
    /// Added to the instructions if NuGet packages can be used: packages that suit this app and how to use them. None if there are none.
    packages: string option
    eval: string
    /// The 'recordUndo' argument of caddis_eval.
    recordUndo: string
    /// None if the host has no commands, then caddis_command is not offered.
    command: CommandToolTexts option
    describe: string
    /// The 'name' argument of caddis_describe.
    describeName: string
    status: string
    cancel: string
    setMode: string
    undo: string
    capture: string
    /// The 'view' argument of caddis_capture_viewport.
    captureView: string
    /// The 'displayMode' argument of caddis_capture_viewport.
    captureDisplayMode: string
    export: string
    /// The 'format' argument of caddis_export.
    exportFormat: string
    /// The 'objects' argument of caddis_export.
    exportObjects: string
    /// The 'objectIds' argument of caddis_export.
    exportObjectIds: string
    /// The description of caddis_dialog. None if the host can't find its dialogs, then the tool is not offered.
    dialog: string option
    }

/// What differs between the apps that host Caddis, e.g. Rhino and Revit.
type HostInfo = {
    /// e.g. "Rhino" or "Revit", used in messages.
    appName: string
    /// The name of the MCP server, in its serverInfo and in the 'claude mcp add' command, e.g. "caddis".
    mcpServerName: string
    /// What object ids look like, for error messages, e.g. "an object id (a GUID)".
    objectIdKind: string
    /// True if the text is an object id of this host.
    isObjectId: string -> bool
    mcp: McpTexts
    }


/// The services an app that hosts Caddis provides, e.g. Rhino.
type ICaddisHost =
    /// The texts and names that depend on the host app.
    abstract Info: HostInfo

    /// e.g. "Rhino 8.19 on Windows"
    abstract Description: string

    /// Folders passed to FSI and the type checker as --lib, so that e.g. #r "RhinoCommon.dll" resolves.
    abstract LibFolders: string[]

    /// Assemblies referenced by default in FSI and the type checker, e.g. RhinoCommon.dll
    abstract References: string[]

    /// Runs a function on the host's UI thread, inline if already on it.
    abstract InvokeOnUi<'T> : (unit -> 'T) -> Task<'T>

    /// A reason if scripts can't run right now, e.g. while a Rhino command is active.
    abstract CannotRunReason: unit -> string option

    /// Called on the evaluation thread right before the code of a run is evaluated.
    abstract BeforeRun: Run -> unit

    /// Called on the evaluation thread after a run, also after errors and cancellation, with the status the run will end with.
    /// Writes what was printed to the host's command line during the run to run.CommandLine.
    /// Return an error if finalization did not keep the intended result, e.g. a transaction was rolled back.
    abstract AfterRun: run: Run * status: RunStatus -> Result<unit, string>

    /// Called for all script output, from any thread.
    abstract Echo: kind: OutputKind * text: string -> unit

    /// Shows a message from Caddis itself to the user, e.g. on the Rhino command line. Called from any thread.
    abstract Log: string -> unit

    /// Shows an internal error of Caddis to the user, e.g. that the F# session could not be created. Called from any thread.
    /// The host keeps errors apart from the messages of Log, so that many messages don't push them out.
    abstract LogError: string -> unit

    /// Host specific entries for the status endpoint.
    abstract Status: unit -> (string * JsonNode) list

    /// Saves a viewport as PNG. Called on the UI thread.
    /// An empty view name means the active view. Width and height of 0 are resolved with CaptureSize.fit.
    abstract CaptureView: viewName: string * width: int * height: int * displayMode: string * path: string -> unit

    /// Exports objects to a file, the format is picked from the extension of the path. Called on the UI thread.
    abstract Export: selection: ExportSelection * path: string -> unit

    /// Undoes the changes of the given run, if they are the last ones. Called on the UI thread.
    abstract Undo: Run -> Result<string, string>

    /// The modal dialogs the host app shows. Called from any thread, also while the UI thread is busy, so it must not need it.
    /// Empty if the host can't find them.
    abstract Dialogs: unit -> HostDialog list

    /// Presses a button of a dialog, found by its name. dialogId is from Dialogs, or empty for the only dialog. Called from any thread.
    abstract PressDialogButton: dialogId: string * button: string -> Result<string, string>

    /// Runs commands of the host app, e.g. Rhino command macros, writes what they print to run.CommandLine,
    /// sets run.Commands and the undo records, and returns the outcome. Called on the UI thread.
    /// A command that waits for input is cancelled after request.inputTimeout, or when run.CancelRequested is set.
    abstract RunCommands: run: Run * request: CommandRequest -> RunOutcome
