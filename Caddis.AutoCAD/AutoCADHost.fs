namespace Caddis.AutoCAD

open System
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

    let onObjectChanged () =
        if runActive then docChanged <- true
        else lastUndo <- None // a change made in AutoCAD, e.g. by a command or another plugin

    let objectHandler = ObjectEventHandler(fun _ _ -> onObjectChanged ())
    let erasedHandler = ObjectErasedEventHandler(fun _ _ -> onObjectChanged ())
    // every command is an undo step of its own, also those that change only the view, e.g. ZOOM
    let commandHandler = CommandEventHandler(fun _ _ -> lastUndo <- None)
    // zooming and panning with the mouse are undo steps too
    let viewHandler =
        EventHandler(fun _ _ ->
            if runActive then otherChanged <- true
            elif not redrawing then lastUndo <- None)

    let watch (doc: Document) =
        if not (isNull doc) then
            doc.CommandWillStart.AddHandler commandHandler
            doc.ViewChanged.AddHandler viewHandler
            let db = doc.Database
            db.ObjectAppended.AddHandler objectHandler
            db.ObjectModified.AddHandler objectHandler
            db.ObjectErased.AddHandler erasedHandler

    do
        let documents = CoreApp.DocumentManager
        for doc in Seq.cast<Document> documents do
            watch doc
        documents.DocumentCreated.AddHandler(DocumentCollectionEventHandler(fun _ e -> watch e.Document))
        CoreApp.SystemVariableChanged.AddHandler(
            Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventHandler(fun _ _ -> if runActive then otherChanged <- true)
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

        member _.RunCommands(_, _) = failwith "Caddis can't run AutoCAD commands yet, use caddis_eval."
