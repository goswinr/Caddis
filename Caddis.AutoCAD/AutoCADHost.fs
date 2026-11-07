namespace Caddis.AutoCAD

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Autodesk.AutoCAD.ApplicationServices
open Autodesk.AutoCAD.DatabaseServices
open Autodesk.AutoCAD.EditorInput
open Autodesk.AutoCAD.Geometry
open Caddis

type private CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application

/// Connects Caddis to AutoCAD: main thread, document locks, undo, redraw, view capture and export.
/// Create it in IExtensionApplication.Initialize, on AutoCAD's main thread, after UiThread.init.
type AutoCADHost() =

    let acdbmgdPath = typeof<Database>.Assembly.Location

    /// The three assemblies of the AutoCAD .NET API, as every [CommandMethod] uses them.
    let references =
        [| acdbmgdPath; typeof<Document>.Assembly.Location; typeof<Autodesk.AutoCAD.ApplicationServices.Application>.Assembly.Location |]
        |> Array.filter (String.IsNullOrEmpty >> not)
        |> Array.distinct

    /// AutoCAD's folder, so that e.g. #r "AcCui.dll" resolves.
    let libFolders = [| Path.GetDirectoryName acdbmgdPath |]

    /// e.g. "AutoCAD 2025", from "AutoCAD 2025 - English" in the registry key of the product.
    let productName =
        try
            use key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey HostApplicationServices.Current.MachineRegistryProductRootKey
            match key with
            | null -> "AutoCAD"
            | k ->
                match k.GetValue "ProductName" with
                | :? string as s when not (String.IsNullOrWhiteSpace s) ->
                    match s.LastIndexOf " - " with
                    | -1 -> s
                    | i -> s.Substring(0, i)
                | _ -> "AutoCAD"
        with _ ->
            "AutoCAD"

    /// e.g. "R25.0.189.0.0", the version of acad.exe with its update.
    let build =
        try Process.GetCurrentProcess().MainModule.FileVersionInfo.ProductVersion
        with _ -> ""

    let description = $"{productName} ({build}) on Windows"

    let activeDoc () =
        match CoreApp.DocumentManager.MdiActiveDocument with
        | null -> failwith "No drawing is open in AutoCAD."
        | doc -> doc

    // --- echo of script output, written line by line ---

    let echoBuffer = StringBuilder()

    let flushEcho (all: bool) =
        let text =
            lock echoBuffer (fun () ->
                let s = echoBuffer.ToString()
                let cut = if all then s.Length else s.LastIndexOf '\n' + 1
                if cut <= 0 then
                    ""
                else
                    echoBuffer.Remove(0, cut) |> ignore
                    s.Substring(0, cut))
        // AutoCAD's messages start with a line break instead of ending with one
        if text <> "" then AutoCADLog.write ("\n" + text.TrimEnd('\r', '\n').Replace("\r\n", "\n"))

    // --- runs, document locks and undo ---

    /// True while an F# run is in progress, from BeforeRun to AfterRun.
    let mutable runActive = false
    let mutable runId = ""

    /// True if the code of the current run is evaluated in one call on the main thread, in 'ui' mode. Then BeforeRun locks the drawing for the whole run.
    let mutable runOnUi = false

    /// The lock of the drawing for the whole run in 'ui' mode, and the drawing.
    let mutable runLock: (DocumentLock * Document) option = None

    /// True while a call of Script.OnUi in background mode holds a lock of the drawing.
    let mutable callLocked = false

    /// How many calls of Script.OnUi of the current background run changed something, each is an undo step.
    let mutable changedCalls = 0

    /// Set by the events of the databases while a run is in progress.
    let mutable docChanged = false

    /// Set while a run is in progress by changes of the view or of system variables. They fire no events of the database,
    /// but AutoCAD records them in the undo step of the run, e.g. of a script that only zooms.
    let mutable otherChanged = false

    /// The run that is the latest undo step in AutoCAD, with its drawing. AutoCAD keeps no undo step for a run that changed nothing.
    let mutable lastUndo: (string * IntPtr) option = None

    let mutable undoCounter = 0u

    /// True while Caddis redraws the views after a run, which is no undo step.
    let mutable redrawing = false

    // --- state of a command run of caddis_command, on the main thread ---

    /// True while the commands of a command run run, inside AutoCAD's command EXECUTEFUNCTION.
    let mutable commandRunActive = false

    /// True while one macro of a command run is in progress, in Editor.Command.
    let mutable macroActive = false

    /// The commands that ended during the current macro, with their English name and result.
    let endedCommands = ResizeArray<string * string>()

    /// How many prompts of the current macro wait for input, and the message of the latest one, e.g. "Specify next point or [Undo]:".
    let mutable openPrompts = 0
    let mutable promptMessage = ""

    /// The objects the command run added, changed or erased, in order, and whether the first event of each was an add.
    let touchedObjects = ResizeArray<ObjectId>()
    let addedFirst = Dictionary<ObjectId, bool>()

    let touched (id: ObjectId) (added: bool) =
        if commandRunActive && not (addedFirst.ContainsKey id) then
            addedFirst[id] <- added
            touchedObjects.Add id

    let onObjectChanged (id: ObjectId) (added: bool) =
        if runActive || commandRunActive then
            docChanged <- true
            touched id added
        else
            lastUndo <- None // a change made in AutoCAD, e.g. by a command or another plugin

    let appendedHandler = ObjectEventHandler(fun _ e -> onObjectChanged e.DBObject.ObjectId true)
    let modifiedHandler = ObjectEventHandler(fun _ e -> onObjectChanged e.DBObject.ObjectId false)
    let erasedHandler = ObjectErasedEventHandler(fun _ e -> onObjectChanged e.DBObject.ObjectId false)
    // every command is an undo step of its own, also those that change only the view, e.g. ZOOM
    let commandHandler = CommandEventHandler(fun _ _ -> lastUndo <- None)
    let endedHandler (result: string) = CommandEventHandler(fun _ e -> if macroActive then endedCommands.Add(e.GlobalCommandName, result))
    let succeededHandler = endedHandler "Success"
    let cancelledHandler = endedHandler "Cancel"
    let failedHandler = endedHandler "Failure"
    let prompts =
        PromptListener(fun opened e ->
            if macroActive then
                if opened then
                    openPrompts <- openPrompts + 1
                    promptMessage <- PromptListener.Message e
                else
                    openPrompts <- max 0 (openPrompts - 1))
    // zooming and panning with the mouse are undo steps too
    let viewHandler =
        EventHandler(fun _ _ ->
            if runActive || commandRunActive then otherChanged <- true
            elif not redrawing then lastUndo <- None)

    let watch (doc: Document) =
        if not (isNull doc) then
            doc.CommandWillStart.AddHandler commandHandler
            doc.CommandEnded.AddHandler succeededHandler
            doc.CommandCancelled.AddHandler cancelledHandler
            doc.CommandFailed.AddHandler failedHandler
            doc.ViewChanged.AddHandler viewHandler
            prompts.Attach doc.Editor
            let db = doc.Database
            db.ObjectAppended.AddHandler appendedHandler
            db.ObjectModified.AddHandler modifiedHandler
            db.ObjectErased.AddHandler erasedHandler

    do
        let documents = CoreApp.DocumentManager
        for doc in Seq.cast<Document> documents do
            watch doc
        documents.DocumentCreated.AddHandler(DocumentCollectionEventHandler(fun _ e -> watch e.Document))
        CoreApp.SystemVariableChanged.AddHandler(
            Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventHandler(fun _ _ ->
                if runActive || commandRunActive then otherChanged <- true)
        )

    /// Locks the drawing for a run, like a command does. Each lock is one undo step in AutoCAD, named after the run.
    let lockFor (doc: Document) (id: string) : DocumentLock =
        try
            doc.LockDocument(DocumentLockMode.Write, "CaddisRun", $"Caddis {id}", false)
        with e ->
            let command = doc.CommandInProgress
            if command <> "" then failwith $"AutoCAD could not lock the drawing, the command {command} is running. Finish or cancel it first."
            else failwith $"AutoCAD could not lock the drawing: {e.Message}"

    /// Redraws the views after a run. Changes made outside of a command show only after the next redraw otherwise.
    let redraw () =
        match CoreApp.DocumentManager.MdiActiveDocument with
        | null -> ()
        | doc ->
            redrawing <- true
            try
                try
                    doc.TransactionManager.FlushGraphics()
                    doc.Editor.UpdateScreen()
                with _ ->
                    ()
            finally
                redrawing <- false

    /// Runs a function on the main thread. A call of Script.OnUi during a background run locks the drawing,
    /// so that the script can change it, and is its own undo step if it changed something.
    let invokeOnUi (f: unit -> 'T) : Task<'T> =
        UiThread.invoke (fun () ->
            if runActive && not runOnUi && not callLocked then
                match CoreApp.DocumentManager.MdiActiveDocument with
                | null -> f ()
                | doc ->
                    let id = runId
                    use _lock = lockFor doc id
                    callLocked <- true
                    let docBefore, otherBefore = docChanged, otherChanged
                    docChanged <- false
                    otherChanged <- false
                    try
                        f ()
                    finally
                        callLocked <- false
                        if docChanged || otherChanged then
                            changedCalls <- changedCalls + 1
                            lastUndo <- Some(id, doc.UnmanagedObject)
                        docChanged <- docBefore || docChanged
                        otherChanged <- otherBefore || otherChanged
            else
                f ())

    let undo (run: Run) : Result<string, string> =
        match run.UndoSerial, lastUndo with
        | None, _ -> Error $"Run '{run.Id}' made no changes that can be undone."
        | Some _, Some(id, docPtr) when id = run.Id ->
            match CoreApp.DocumentManager.MdiActiveDocument with
            | null -> Error "No drawing is open in AutoCAD."
            | doc when doc.UnmanagedObject <> docPtr -> Error "The drawing of this run is not the active one."
            | doc when doc.CommandInProgress <> "" -> Error $"The AutoCAD command {doc.CommandInProgress} is running. Finish or cancel it first."
            | doc ->
                // AutoCAD runs it once this call has returned: the UNDO command can't run outside of a command context
                doc.SendStringToExecute("_.UNDO 1 ", true, false, false)
                run.UndoSerial <- None
                lastUndo <- None
                Ok $"Sent AutoCAD's UNDO command for run '{run.Id}', AutoCAD runs it right after this call."
        | Some _, _ -> Error "Other changes were made in AutoCAD after this run, undo them in AutoCAD instead."

    // --- capture and export ---

    let captureView (viewName: string, width: int, height: int, displayMode: string, path: string) =
        if not (String.IsNullOrWhiteSpace viewName) then
            failwith "Caddis captures only the active view in AutoCAD: leave the view empty. Switch views or layouts with caddis_eval first."
        if not (String.IsNullOrWhiteSpace displayMode) then
            failwith "Caddis can't set a visual style for the capture in AutoCAD yet: leave the display mode empty."
        let doc = activeDoc ()
        // the size of the current viewport in pixels
        let viewSize =
            match CoreApp.GetSystemVariable "SCREENSIZE" with
            | :? Point2d as p when p.X >= 1.0 && p.Y >= 1.0 -> int p.X, int p.Y
            | _ -> CaptureSize.defaultMaxEdge, CaptureSize.defaultMaxEdge * 3 / 4
        let w, h = CaptureSize.fit viewSize (width, height)
        use bitmap = doc.CapturePreviewImage(uint32 w, uint32 h)
        if isNull bitmap then failwith "AutoCAD could not capture the view."
        bitmap.Save(path, Drawing.Imaging.ImageFormat.Png)

    let objectIdOf (db: Database) (handle: string) : ObjectId =
        let id =
            try db.GetObjectId(false, Handle(Convert.ToInt64(handle, 16)), 0) // validated by AutoCADInfo.isHandle
            with _ -> ObjectId.Null
        if id.IsNull || id.IsErased then failwith $"There is no object with the handle '{handle}' in the drawing."
        id

    /// Writes a copy of the drawing, or of some of its objects, to a new DWG or DXF file. The drawing and its file name stay as they are.
    let export (selection: ExportSelection, path: string) =
        let doc = activeDoc ()
        let db = doc.Database
        let ext = Path.GetExtension(path).ToLowerInvariant()
        if ext <> ".dwg" && ext <> ".dxf" then failwith $"Caddis can't export to '{ext}' in AutoCAD yet. The formats are: dwg, dxf."
        let ids =
            match selection with
            | AllObjects -> None
            | SelectedObjects ->
                match doc.Editor.SelectImplied() with
                | r when r.Status = PromptStatus.OK && not (isNull r.Value) && r.Value.Count > 0 -> Some(new ObjectIdCollection(r.Value.GetObjectIds()))
                | _ -> failwith "Nothing is selected in AutoCAD."
            | ObjectIds handles -> Some(new ObjectIdCollection(handles |> Array.map (objectIdOf db)))
        // Wblock of some objects needs a lock, else eLockViolation. Nothing changes, so it is no undo step.
        use _lock = doc.LockDocument()
        use copy =
            match ids with
            | None -> db.Wblock()
            | Some ids -> db.Wblock(ids, Point3d.Origin)
        if ext = ".dwg" then copy.SaveAs(path, DwgVersion.Current)
        else copy.DxfOut(path, 16, DwgVersion.Current)

    // --- command runs of caddis_command ---

    /// The objects selected in the drawing, its pickfirst set.
    let selection (ed: Editor) : ObjectId[] =
        match ed.SelectImplied() with
        | r when r.Status = PromptStatus.OK && not (isNull r.Value) -> r.Value.GetObjectIds()
        | _ -> [||]

    /// When the watchdog found the current macro waiting at a prompt, the prompt at which Caddis cancelled it,
    /// and whether it showed a dialog, which Caddis can't close.
    let mutable waitingSince: DateTime option = None
    let mutable cancelledAt: string option = None
    let mutable waitedInDialog = false

    let resetWaiting () =
        waitingSince <- None
        cancelledAt <- None
        waitedInDialog <- false
        openPrompts <- 0
        promptMessage <- ""

    /// Runs on the main thread. While a macro runs, Caddis' calls get there only when the command waits in a message loop:
    /// for input that the macro does not give, in a dialog, or e.g. while it shows its progress.
    /// A command that waits at a prompt is cancelled after the input timeout, and also when it waits otherwise for caddis_cancel, but never in a dialog.
    /// A command ignores an Esc posted to AutoCAD's window, but takes a ^C queued with SendStringToExecute as its input.
    /// A dialog doesn't take it: AutoCAD would run it later, e.g. at a prompt of the next command.
    let checkWaiting (run: Run) (inputTimeout: TimeSpan option) (doc: Document) =
        if macroActive && cancelledAt.IsNone then
            let now = DateTime.UtcNow
            if AutoCADCommandLine.dialogActive () then
                waitedInDialog <- true
                waitingSince <- None
            else
                waitingSince <- if openPrompts > 0 then Some(waitingSince |> Option.defaultValue now) else None
                let timedOut =
                    match waitingSince, inputTimeout with
                    | Some since, Some t -> now - since >= t
                    | _ -> false
                if run.CancelRequested || timedOut then
                    cancelledAt <- Some(if openPrompts > 0 && promptMessage <> "" then promptMessage else AutoCADCommandLine.prompt ())
                    doc.SendStringToExecute("\x03\x03", true, false, false)

    /// Calls a function on the main thread every 200 ms until the returned function is called.
    let startWatching (check: unit -> unit) : unit -> unit =
        let watching = ref true
        backgroundTask {
            while watching.Value do
                do! Task.Delay 200
                // awaits the main thread, so that no checks pile up while it is busy
                if watching.Value then
                    try do! UiThread.invoke (fun () -> if watching.Value then check ())
                    with _ -> ()
        }
        |> ignore
        fun () -> watching.Value <- false

    /// For a macro whose command showed a dialog.
    let dialogNote (step: CommandStep) =
        let name = step.commands |> List.tryHead |> Option.map fst |> Option.defaultValue step.macro
        $"{name} showed a dialog, which Caddis can't close, and waited until someone closed it in AutoCAD. Use the version of the command for the command line if there is one, e.g. '_.-LAYER' or '_.-HATCH'."

    /// The outcome of a macro that did not succeed, None if it did. error: the message if Editor.Command failed.
    let macroFailure (run: Run) (step: CommandStep) (inputs: string[]) (error: string option) (inDialog: bool) : RunOutcome option =
        let fail status (message: string) = Some(RunOutcome.withMessage status message)
        match step.commands |> List.tryFind (fun (_, result) -> result <> "Success"), error with
        | Some(_, "Cancel"), _ when inDialog -> fail Cancelled (dialogNote step)
        | Some(name, "Cancel"), _ ->
            match step.waitedAt with
            | Some prompt when run.CancelRequested -> fail Cancelled $"{name} was cancelled via caddis_cancel at the prompt '{prompt}'."
            | Some prompt ->
                fail
                    Cancelled
                    $"Caddis cancelled {name} because it waited for input at the prompt '{prompt}', which the macro '{step.macro}' does not give. Add the missing inputs or options to the macro, end a prompt that repeats with an empty input \"\", e.g. '_.LINE 0,0 10,0 \"\"', or pass a larger inputTimeoutMs if {name} was still busy."
            | None -> fail Cancelled $"{name} was cancelled, e.g. with Esc in AutoCAD."
        | Some(name, result), _ -> fail RuntimeError $"{name} ended with the result '{result}', see the AutoCAD command line."
        | None, Some _ when step.commands.IsEmpty ->
            let name = AutoCADMacro.commandName inputs
            let lisp =
                try Autodesk.AutoCAD.Internal.Utils.IsLispCommandDefined("C:" + name)
                with _ -> false
            if lisp then
                fail
                    RuntimeError
                    $"'{step.macro}' ran no command: {name} is defined in AutoLISP, and Editor.Command runs only the commands of AutoCAD and its plugins. Queue it in caddis_eval with doc.SendStringToExecute(\"{name} \", true, false, false), it runs after that run."
            else
                fail RuntimeError $"'{step.macro}' ran no command: AutoCAD has no command '{name}' that a macro can run."
        | None, Some message -> fail RuntimeError $"AutoCAD could not run '{step.macro}': {message}"
        | None, None -> None

    /// Commands after which caddis_undo can't tell what UNDO 1 would undo.
    let undoCommands = set [ "UNDO"; "U"; "REDO"; "MREDO"; "OOPS" ]

    /// Commands that open or close drawings. Editor.Command runs in the command context of the active drawing, where AutoCAD can't:
    /// in a test QUIT froze AutoCAD, QNEW succeeded without a new drawing, and CLOSE closed it while the commands ran.
    let documentCommands =
        set [ "QUIT"; "EXIT"; "CLOSE"; "CLOSEALL"; "NEW"; "QNEW"; "OPEN"; "RECOVER"; "RECOVERALL"; "PARTIALOPEN" ]

    /// Why caddis_command doesn't run a command that opens or closes drawings, None for other commands.
    let documentCommandMessage (name: string) =
        if not (documentCommands.Contains name) then None
        elif name = "QUIT" || name = "EXIT" then
            Some $"caddis_command doesn't run {name}: AutoCAD freezes when it quits from a command run. Leave closing AutoCAD to the person working in it."
        else
            Some(
                $"caddis_command doesn't run {name}: it runs commands in the active drawing, where AutoCAD can't open or close drawings. "
                + "Use caddis_eval with Application.DocumentManager instead, e.g. DocumentCollectionExtension.Add(Application.DocumentManager, \"acad.dwt\") "
                + "or DocumentCollectionExtension.Open(Application.DocumentManager, path, false)."
            )

    /// The handles of the entities in layouts among the touched objects, i.e. not e.g. the block table record of model space
    /// that changes when a command adds to it, or the entities of a block definition. Created, changed and erased, in order.
    let touchedEntities (db: Database) : string[] * string[] * string[] =
        use tr = db.TransactionManager.StartOpenCloseTransaction()
        let inLayout (id: ObjectId) =
            try
                id.Database = db
                && match tr.GetObject(id, OpenMode.ForRead, true) with
                   | :? Entity as e ->
                       match tr.GetObject(e.OwnerId, OpenMode.ForRead, true) with
                       | :? BlockTableRecord as btr -> btr.IsLayout
                       | _ -> false
                   | _ -> false
            with _ ->
                false
        let entities = touchedObjects |> Seq.filter inLayout |> Array.ofSeq
        tr.Commit()
        let pick (wasAdded: bool) (exists: bool) =
            entities |> Array.filter (fun id -> addedFirst[id] = wasAdded && not id.IsErased = exists) |> Array.map (fun id -> id.Handle.ToString())
        pick true true, pick false true, pick false false

    /// Runs the macros with Editor.Command, like typed at the command line, one after the other until one does not succeed.
    /// Editor.Command works only in a command context: AutoCAD runs DocumentCollection.ExecuteInCommandContextAsync, as the command
    /// EXECUTEFUNCTION, only once this call on its main thread has returned. All macros run in one such call, so they are one undo step.
    let runCommands (run: Run) (req: CommandRequest) : Task<RunOutcome> =
        let doc = activeDoc ()
        let ed = doc.Editor
        let macros =
            req.macros
            |> Array.map (fun m ->
                match AutoCADMacro.parse m with
                | Ok inputs ->
                    match documentCommandMessage (AutoCADMacro.commandName inputs) with
                    | Some message -> failwith message
                    | None -> m, inputs
                | Error message -> failwith message)
        // EXECUTEFUNCTION clears the selection when it starts, the commands get it back
        let toSelect =
            match req.select with
            | Some handles -> handles |> Array.map (objectIdOf doc.Database)
            | None -> selection ed
        let result = TaskCompletionSource<RunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)
        let started = ref false
        let stopWatching = ref ignore
        let steps = ResizeArray<CommandStep>()
        let failure: RunOutcome option ref = ref None
        let note: string option ref = ref None
        let linesBefore: string[] ref = ref [||]

        /// In the command context.
        let runMacros () =
            if CoreApp.DocumentManager.MdiActiveDocument <> doc then failwith "Another drawing became the active one before AutoCAD started the commands."
            // A command uses the pickfirst set only if no inputs follow its name, e.g. '_.ERASE'. Otherwise it asks for objects
            // at 'Select objects:', where '_P' gives the previous selection set, which SELECT sets. SELECT clears the pickfirst set.
            if toSelect.Length > 0 then
                try
                    ed.Command([| box "_.SELECT"; SelectionSet.FromObjectIds toSelect; "" |])
                    ed.SetImpliedSelection toSelect
                with e ->
                    failwith $"AutoCAD could not select the objects: {e.Message}"
                // ends the line of SELECT's last prompt, else the first command line of the macros would start with it
                ed.WriteMessage "\n"
            linesBefore.Value <- AutoCADCommandLine.history ()
            touchedObjects.Clear()
            addedFirst.Clear()
            docChanged <- false
            otherChanged <- false
            commandRunActive <- true
            for macro, inputs in macros do
                // e.g. after a command that waited in a dialog when caddis_cancel was called
                if failure.Value.IsNone && run.CancelRequested then
                    failure.Value <- Some(RunOutcome.withMessage Cancelled $"The run was cancelled via caddis_cancel before '{macro}'.")
                if failure.Value.IsNone then
                    endedCommands.Clear()
                    resetWaiting ()
                    macroActive <- true
                    let error =
                        try
                            try
                                ed.Command(Array.map box inputs)
                                None
                            with e ->
                                Some e.Message
                        finally
                            macroActive <- false
                    let step = { macro = macro; commands = List.ofSeq endedCommands; waitedAt = (if waitedInDialog then None else cancelledAt) }
                    steps.Add step
                    failure.Value <- macroFailure run step inputs error waitedInDialog
                    if waitedInDialog && failure.Value.IsNone then note.Value <- Some(dialogNote step)

        /// Back in the application context, after EXECUTEFUNCTION has ended and AutoCAD has written its last input to the command line history.
        let finish () =
            try
                stopWatching.Value ()
                let lines = AutoCADCommandLine.added linesBefore.Value (AutoCADCommandLine.history ())
                // the history ends with the empty line of the prompt after the commands
                let lines = if lines.Length > 0 && String.IsNullOrWhiteSpace lines[lines.Length - 1] then lines[.. lines.Length - 2] else lines
                for line in lines do
                    run.CommandLine.Append(line + "\n")
                // e.g. a command defined in AutoLISP may have closed the drawing
                let closed = doc.IsDisposed || isNull doc.Database
                let created, changed, deleted = if closed then [||], [||], [||] else touchedEntities doc.Database
                let selectedAfter = if closed then toSelect else selection ed
                let undoCommand = steps |> Seq.exists (fun s -> s.commands |> List.exists (fun (name, _) -> undoCommands.Contains name))
                // EXECUTEFUNCTION is an undo step of its own, also if it changed nothing
                lastUndo <- if undoCommand then None else Some(run.Id, doc.UnmanagedObject)
                if docChanged && not undoCommand then
                    undoCounter <- undoCounter + 1u
                    run.UndoSerial <- Some undoCounter
                run.Commands <-
                    Some {
                        steps = List.ofSeq steps
                        notRun = req.macros |> Array.skip steps.Count |> List.ofArray
                        created = created
                        changed = changed
                        changedGroups = []
                        deleted = deleted
                        selected = if HashSet(toSelect).SetEquals selectedAfter then None else Some(selectedAfter |> Array.map (fun id -> id.Handle.ToString()))
                    }
                result.TrySetResult(failure.Value |> Option.defaultValue { RunOutcome.ofStatus Succeeded with message = note.Value }) |> ignore
            with e ->
                result.TrySetException e |> ignore

        let callback (_: obj) : Task =
            // not if caddis_cancel cancelled the run before AutoCAD got to it
            if not result.Task.IsCompleted then
                started.Value <- true
                try
                    try
                        runMacros ()
                    finally
                        macroActive <- false
                        commandRunActive <- false
                    UiThread.postLater finish
                with e ->
                    stopWatching.Value ()
                    result.TrySetException e |> ignore
            Task.CompletedTask

        stopWatching.Value <-
            startWatching (fun () ->
                if started.Value then
                    checkWaiting run req.inputTimeout doc
                elif run.CancelRequested && result.TrySetResult(RunOutcome.withMessage Cancelled "Caddis cancelled the commands before AutoCAD started them.") then
                    stopWatching.Value ())
        try
            CoreApp.DocumentManager.ExecuteInCommandContextAsync(Func<obj, Task> callback, null) |> ignore
        with _ ->
            stopWatching.Value ()
            reraise ()
        result.Task

    // --- status ---

    let mutable lastStatus: (string * JsonNode) list = []

    let point (p: Point3d) = Json.arr [ Json.ofFloat p.X; Json.ofFloat p.Y; Json.ofFloat p.Z ]

    /// On the main thread.
    let statusNow () : (string * JsonNode) list =
        let documents = CoreApp.DocumentManager
        let maxLayers = 100
        let document: JsonNode =
            match documents.MdiActiveDocument with
            | null -> null
            | doc ->
                let db = doc.Database
                use tr = db.TransactionManager.StartOpenCloseTransaction()
                let layerName (id: ObjectId) = (tr.GetObject(id, OpenMode.ForRead) :?> LayerTableRecord).Name
                let layers = tr.GetObject(db.LayerTableId, OpenMode.ForRead) :?> LayerTable |> Seq.cast<ObjectId> |> Seq.map layerName |> Array.ofSeq
                let modelSpace = tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId db, OpenMode.ForRead) :?> BlockTableRecord
                let selected =
                    try
                        match doc.Editor.SelectImplied() with
                        | r when r.Status = PromptStatus.OK && not (isNull r.Value) -> r.Value.Count
                        | _ -> 0
                    with _ ->
                        0
                let space =
                    if db.TileMode then "model"
                    elif string (CoreApp.GetSystemVariable "CVPORT") = "1" then "paper"
                    else "model, in a viewport of a layout"
                let extents: JsonNode =
                    if db.Extmax.X >= db.Extmin.X then Json.obj [ "min", point db.Extmin; "max", point db.Extmax ] else null
                let result =
                    Json.obj [
                        "name", Json.ofString (Path.GetFileName doc.Name)
                        "path", Json.ofString (if doc.IsNamedDrawing then doc.Name else "")
                        "isModified", Json.ofBool (string (CoreApp.GetSystemVariable "DBMOD") <> "0")
                        "units", Json.ofString (string db.Insunits)
                        "measurement", Json.ofString (string db.Measurement)
                        "layout", Json.ofString (string (CoreApp.GetSystemVariable "CTAB"))
                        "space", Json.ofString space
                        "extents", extents
                        "modelSpaceObjectCount", Json.ofInt (modelSpace |> Seq.cast<ObjectId> |> Seq.length)
                        "selectedObjectCount", Json.ofInt selected
                        "currentLayer", Json.ofString (layerName db.Clayer)
                        "layerCount", Json.ofInt layers.Length
                        "layers", Json.ofStrings (Array.truncate maxLayers layers)
                    ]
                tr.Commit()
                result
        let commandInProgress =
            match documents.MdiActiveDocument with
            | null -> ""
            | doc -> doc.CommandInProgress
        [
            "autocadVersion", Json.ofString productName
            "autocadBuild", Json.ofString build
            "autocadCommandInProgress", Json.ofString commandInProgress
            "openDrawings", Json.ofStrings (Seq.cast<Document> documents |> Seq.map (fun d -> Path.GetFileName d.Name) |> Array.ofSeq)
            "document", document
        ]

    interface ICaddisHost with

        member _.Info = AutoCADInfo.info

        member _.Description = description

        member _.LibFolders = libFolders

        member _.References = references

        member _.InvokeOnUi<'T>(f: unit -> 'T) : Task<'T> = invokeOnUi f

        member _.CannotRunReason() =
            let check () =
                match CoreApp.DocumentManager.MdiActiveDocument with
                | doc when not (isNull doc) && doc.CommandInProgress <> "" ->
                    Some $"The AutoCAD command {doc.CommandInProgress} is running. Finish or cancel it first."
                | _ when not CoreApp.IsQuiescent -> Some "AutoCAD is busy, e.g. with a script or a LISP routine. Run again once it has finished."
                | _ -> None
            if UiThread.isCurrent () then
                check ()
            else
                // in background mode: AutoCAD's API must be used on its main thread
                let t = UiThread.invoke check
                if t.Wait 2000 then t.Result
                else Some "AutoCAD did not answer within 2 s, it is busy. Run again once it has finished."

        member _.BeforeRun(run: Run) =
            if UiThread.isCurrent () then
                // 'ui' mode: the whole run is one call on the main thread. Locking first: if it fails, the run fails before anything changed.
                let locked =
                    match CoreApp.DocumentManager.MdiActiveDocument with
                    | null -> None
                    | doc -> Some(lockFor doc run.Id, doc)
                runLock <- locked
                runOnUi <- true
            else
                runOnUi <- false
            runId <- run.Id
            docChanged <- false
            otherChanged <- false
            changedCalls <- 0
            runActive <- true

        member _.AfterRun(run: Run, _status: RunStatus) =
            flushEcho true
            runActive <- false
            match runLock with
            | Some(l, doc) ->
                // 'ui' mode, on the main thread
                runLock <- None
                l.Dispose()
                if docChanged || otherChanged then
                    lastUndo <- Some(run.Id, doc.UnmanagedObject)
                // caddis_undo only for changes of the drawing, not e.g. for a run that only zoomed
                if docChanged && run.RecordUndo then
                    undoCounter <- undoCounter + 1u
                    run.UndoSerial <- Some undoCounter
                redraw ()
            | None when runOnUi -> redraw () // no drawing was open
            | None ->
                // background mode: each call of Script.OnUi that changed something is an undo step
                match lastUndo with
                | Some(id, _) when id = run.Id && changedCalls = 1 && docChanged ->
                    undoCounter <- undoCounter + 1u
                    run.UndoSerial <- Some undoCounter
                | _ when changedCalls > 1 ->
                    run.AddNote
                        $"The run changed the drawing in {changedCalls} calls of Script.OnUi, each is an undo step in AutoCAD. caddis_undo can't undo them, undo them in AutoCAD."
                | _ -> ()
                UiThread.post redraw
            Ok ()

        member _.Echo(_, text) =
            lock echoBuffer (fun () -> echoBuffer.Append text |> ignore)
            flushEcho false

        member _.Log(msg) = AutoCADLog.writeLine msg

        member _.LogError(msg) = AutoCADLog.error msg

        member _.Status() =
            // AutoCAD may be busy, e.g. with a run on the 'ui' thread: then the status of the last call.
            // It also updates lastStatus when AutoCAD answers too late for this call.
            let fresh =
                UiThread.invoke (fun () ->
                    let s = statusNow ()
                    lastStatus <- s
                    s)
            let ok =
                try fresh.Wait 1000
                with _ -> false
            let result =
                if ok then
                    fresh.Result
                elif fresh.IsFaulted then
                    lastStatus @ [ "autocadStatusError", Json.ofString (fresh.Exception.GetBaseException().Message) ]
                else
                    lastStatus @ [ "autocadBusy", Json.ofString "AutoCAD did not answer within 1 s, the drawing is shown as of the previous status." ]
            // a JsonNode can only have one parent
            result |> List.map (fun (k, v) -> k, (if isNull v then null else v.DeepClone()))

        member _.CaptureView(viewName, width, height, displayMode, path) = captureView (viewName, width, height, displayMode, path)

        member _.Export(selection, path) = export (selection, path)

        member _.Undo(run) = undo run

        member _.Dialogs() = [] // not implemented for AutoCAD yet

        member _.PressDialogButton(_, _) = Error "Caddis can't press buttons of dialogs in AutoCAD yet."

        member _.RunCommands(run, request) = runCommands run request
