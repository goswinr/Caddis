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

    /// All files in a folder and its subfolders.
    let listFolder (dir: string) : FileItem[] =
        if not (Directory.Exists dir) then
            [||]
        else
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            |> Array.sort
            |> Array.map (fun p ->
                {
                    name = Path.GetRelativePath(dir, p).Replace('\\', '/')
                    size = FileInfo(p).Length
                    mime = mimeOf p
                    path = p
                })

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


/// One evaluation of F# code in the FSI session.
type Run(id: string, mode: ThreadMode, code: string, scriptPath: string, outDir: string, maxChars: int, recordUndo: bool) =
    let startedAt = DateTime.UtcNow
    let watch = Stopwatch.StartNew()
    let stdOut = CaptureBuffer maxChars
    let stdErr = CaptureBuffer maxChars
    let fsiOut = CaptureBuffer maxChars
    let completion = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable outcome = RunOutcome.ofStatus Running
    let mutable duration = TimeSpan.Zero
    let mutable files: FileItem[] = [||]
    let mutable cancelRequested = false
    let mutable timeoutRequested = false

    member _.Id = id
    member _.Mode = mode
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
    member _.Outcome = outcome
    member _.Status = outcome.status
    member _.IsFinished = outcome.status <> Running
    member _.Duration = if outcome.status = Running then watch.Elapsed else duration
    /// Completes when the outcome is set.
    member _.Completion: Task = completion.Task
    /// The files in the output folder, set when the run finishes.
    member _.Files = files

    member _.CancelRequested = cancelRequested
    member _.TimeoutRequested = timeoutRequested
    member _.MarkCancelRequested() = cancelRequested <- true
    member _.MarkTimeoutRequested() = timeoutRequested <- true

    /// Set by the host when the run is wrapped in an undo record.
    member val UndoSerial: uint32 option = None with get, set

    /// The runtime serial number of the document of the undo record.
    member val DocSerial = 0u with get, set

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
            "status", Json.ofString (RunStatus.toString o.status)
            "mode", Json.ofString (ThreadMode.toString mode)
            "scriptPath", Json.ofString scriptPath
            "startedAt", Json.ofString (startedAt.ToString "o")
            "durationMs", Json.ofInt64 (int64 this.Duration.TotalMilliseconds)
            "message", Json.ofOption Json.ofString o.message
            "value", value
            "stdout", Json.ofString stdOut.Text
            "stderr", Json.ofString stdErr.Text
            "fsiOutput", Json.ofString fsiOut.Text
            "outputTruncated", Json.ofBool (stdOut.Truncated || stdErr.Truncated || fsiOut.Truncated)
            "diagnostics", Json.arr (o.diagnostics |> Seq.map Diagnostic.toJson)
            "exception", Json.ofOption ExceptionInfo.toJson o.exn
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


/// Which objects to export.
type ExportSelection =
    | AllObjects
    | SelectedObjects
    | ObjectIds of Guid[]


/// The services an app that hosts Caddis provides, e.g. Rhino.
type ICaddisHost =
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

    /// Called on the evaluation thread after a run, also after errors and cancellation.
    abstract AfterRun: Run -> unit

    /// Called for all script output, from any thread.
    abstract Echo: kind: OutputKind * text: string -> unit

    /// Shows a message from Caddis itself to the user, e.g. on the Rhino command line. Called from any thread.
    abstract Log: string -> unit

    /// Host specific entries for the status endpoint.
    abstract Status: unit -> (string * JsonNode) list

    /// Saves a viewport as PNG. Called on the UI thread.
    /// An empty view name means the active view. Width or height of 0 means the current size of the view.
    abstract CaptureView: viewName: string * width: int * height: int * displayMode: string * path: string -> unit

    /// Exports objects to a file, the format is picked from the extension of the path. Called on the UI thread.
    abstract Export: selection: ExportSelection * path: string -> unit

    /// Undoes the changes of the given run, if they are the last ones. Called on the UI thread.
    abstract Undo: Run -> Result<string, string>
