namespace Caddis.Revit

open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Autodesk.Revit.ApplicationServices
open Autodesk.Revit.DB
open Autodesk.Revit.DB.Events
open Autodesk.Revit.UI
open Caddis

/// Connects Caddis to Revit: API context, a transaction group per run, Revit's warnings and errors,
/// the elements a run changed, view capture, export and undo.
/// Create it in IExternalApplication.OnStartup, after ApiContext.init.
type RevitHost(app: ControlledApplication) =

    // read once on the main thread, Status is called from other threads
    let subVersionNumber = app.SubVersionNumber
    let versionBuild = app.VersionBuild

    let description = $"Revit {subVersionNumber} ({versionBuild}) on Windows"

    let revitApiPath = typeof<Document>.Assembly.Location

    // Caddis.Revit.Script, not Caddis.Script: 'open Caddis' shadows it here.
    // Not WPF: FCS type-checks against the SDK's reference pack of Microsoft.NETCore.App, whose WindowsBase.dll is a stub without
    // e.g. DependencyObject, and it wins over the WindowsBase of Microsoft.WindowsDesktop.App, also with -r: or --noframework.
    let references = [| revitApiPath; typeof<UIApplication>.Assembly.Location; typeof<Caddis.Revit.Script>.Assembly.Location |]

    let libFolders = [| Path.GetDirectoryName revitApiPath |]

    /// A key to tell documents apart. Revit may return a new Document object for the same document.
    let docKey (doc: Document) = doc.Title + "|" + doc.PathName

    /// The active document. Only in API context.
    let activeUiDoc () =
        match ApiContext.uiApplication () with
        | null -> failwith "Revit's UIApplication is not known yet."
        | uiApp ->
            match uiApp.ActiveUIDocument with
            | null -> failwith "No document is open in Revit."
            | d -> d

    // --- what an F# run does, collected by Revit's events. Changes count if they are made in API context by Caddis ---

    /// True while an F# run is in progress.
    let mutable runActive = false
    let mutable runId = ""

    /// Set while a capture or export makes temporary changes, they are not part of a run and are rolled back.
    let mutable internalChange = false

    /// The transaction group of a run on the 'ui' thread, with its document.
    let mutable group: (TransactionGroup * Document) option = None

    /// How many transactions the script of the current run committed.
    let mutable commits = 0

    /// The ids of the elements the run added, modified and deleted, and all of them in the order they were first seen.
    let added = HashSet<int64>()
    let modified = HashSet<int64>()
    let deleted = HashSet<int64>()
    let touched = ResizeArray<int64>()

    /// The category of each modified element, read in DocumentChanged, where the API can be used also in background mode.
    let categories = Dictionary<int64, string>()

    /// Revit's warnings and errors about the changes of the run, in order, with how often each one came.
    let failures = ResizeArray<string>()
    let failureCounts = Dictionary<string, int>()

    let addFailure (text: string) =
        match failureCounts.TryGetValue text with
        | true, n -> failureCounts[text] <- n + 1
        | _ ->
            failureCounts[text] <- 1
            failures.Add text

    let byScript () = runActive && not internalChange && ApiContext.isActive ()

    // --- undo ---

    /// The run whose changes are the latest undo step in Revit, with the key of its document.
    let mutable lastUndo: (string * string) option = None

    /// lastUndo before the current run, restored if its transaction group is rolled back.
    let mutable lastUndoBefore: (string * string) option = None

    let mutable undoCounter = 0u

    let onDocumentChanged (e: DocumentChangedEventArgs) =
        match e.Operation with
        | UndoOperation.TransactionCommitted ->
            if byScript () then
                commits <- commits + 1
                let track (set: HashSet<int64>) (ids: ICollection<ElementId>) =
                    for id in ids do
                        let v = id.Value
                        if not (added.Contains v || modified.Contains v || deleted.Contains v) then touched.Add v
                        set.Add v |> ignore
                track added (e.GetAddedElementIds())
                track modified (e.GetModifiedElementIds())
                track deleted (e.GetDeletedElementIds())
                let doc = e.GetDocument()
                for id in e.GetModifiedElementIds() do
                    if not (categories.ContainsKey id.Value) then
                        categories[id.Value] <-
                            match doc.GetElement id with
                            | null -> "unknown"
                            | el when isNull el.Category -> "without category"
                            | el -> el.Category.Name
                lastUndo <- Some(runId, docKey (e.GetDocument()))
            elif not internalChange then
                lastUndo <- None // a change in Revit, or by another add-in
        | UndoOperation.TransactionUndone
        | UndoOperation.TransactionRedone -> lastUndo <- None
        | _ -> () // rolled back transactions don't change the undo stack

    /// Removes Revit's warnings about the changes of a run, so that they don't show up in Revit, and lists them in the notes of the run.
    /// An error rolls back its transaction instead of showing Revit's dialog, which would block until someone clicks.
    let onFailuresProcessing (e: FailuresProcessingEventArgs) =
        if byScript () then
            let fa = e.GetFailuresAccessor()
            let mutable hasError = false
            for m in fa.GetFailureMessages() do
                let text = m.GetDescriptionText()
                if m.GetSeverity() = FailureSeverity.Warning then
                    addFailure $"Revit warning: {text}"
                    fa.DeleteWarning m
                else
                    hasError <- true
                    addFailure $"Revit error, the transaction '{fa.GetTransactionName()}' was rolled back: {text}"
            if hasError then
                let options = fa.GetFailureHandlingOptions()
                options.SetClearAfterRollback true |> ignore
                fa.SetFailureHandlingOptions options
                e.SetProcessingResult FailureProcessingResult.ProceedWithRollBack

    do
        app.DocumentChanged.Add onDocumentChanged
        app.FailuresProcessing.Add onFailuresProcessing

    let clearChanges () =
        added.Clear()
        modified.Clear()
        deleted.Clear()
        touched.Clear()
        categories.Clear()

    /// The elements the run created, changed and deleted, None if it changed none.
    /// Changed elements are counted per category: Revit reports many it updated itself, e.g. schedules or paths of travel.
    let changeReport () : CommandReport option =
        if touched.Count = 0 then
            None
        else
            let pick (f: int64 -> bool) = touched |> Seq.filter f |> Array.ofSeq
            let changed = pick (fun v -> modified.Contains v && not (added.Contains v) && not (deleted.Contains v))
            let categoryOf (v: int64) =
                match categories.TryGetValue v with
                | true, c -> c
                | _ -> "unknown"
            Some {
                steps = []
                notRun = []
                created = pick (fun v -> added.Contains v && not (deleted.Contains v)) |> Array.map string
                changed = Array.map string changed
                changedGroups = changed |> Array.countBy categoryOf |> Array.sortByDescending snd |> List.ofArray
                deleted = pick (fun v -> deleted.Contains v && not (added.Contains v)) |> Array.map string
                selected = None
            }

    let maxFailureNotes = 20

    let failureNotes () = [
        for text in Seq.truncate maxFailureNotes failures do
            match failureCounts[text] with
            | 1 -> text
            | n -> $"{text} ({n} times)"
        if failures.Count > maxFailureNotes then
            $"... and {failures.Count - maxFailureNotes} more different warnings or errors from Revit."
    ]

    // --- status ---

    let mutable lastStatus: (string * JsonNode) list = []

    /// Only in API context.
    let statusNow () : (string * JsonNode) list =
        let uiApp = ApiContext.uiApplication ()
        let documents = uiApp.Application.Documents |> Seq.cast<Document> |> Seq.map _.Title |> Array.ofSeq
        let document: JsonNode =
            match uiApp.ActiveUIDocument with
            | null -> null
            | uidoc ->
                let doc = uidoc.Document
                let lengthUnit =
                    try LabelUtils.GetLabelForUnit(doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId())
                    with _ -> ""
                let levels =
                    (new FilteredElementCollector(doc)).OfClass(typeof<Level>)
                    |> Seq.cast<Level>
                    |> Seq.sortBy _.Elevation
                    |> Seq.truncate 50
                    |> Seq.map (fun l ->
                        Json.obj [ "name", Json.ofString l.Name; "id", Json.ofInt64 l.Id.Value; "elevationFeet", Json.ofFloat l.Elevation ] :> JsonNode)
                    |> Array.ofSeq
                let view: JsonNode =
                    match uidoc.ActiveView with
                    | null -> null
                    | v -> Json.obj [ "name", Json.ofString v.Name; "id", Json.ofInt64 v.Id.Value; "type", Json.ofString (string v.ViewType) ]
                Json.obj [
                    "title", Json.ofString doc.Title
                    "path", Json.ofString doc.PathName
                    "isFamilyDocument", Json.ofBool doc.IsFamilyDocument
                    "isWorkshared", Json.ofBool doc.IsWorkshared
                    "isModified", Json.ofBool doc.IsModified
                    "lengthUnit", Json.ofString lengthUnit
                    "activeView", view
                    "selectedElementCount", Json.ofInt (uidoc.Selection.GetElementIds().Count)
                    "levels", Json.arr levels
                ]
        [
            "revitVersion", Json.ofString subVersionNumber
            "revitBuild", Json.ofString versionBuild
            "openDocuments", Json.ofStrings documents
            "document", document
        ]

    // --- capture ---

    let displayStyles = [
        "wireframe", DisplayStyle.Wireframe
        "hiddenline", DisplayStyle.HLR
        "hlr", DisplayStyle.HLR
        "shading", DisplayStyle.Shading
        "shaded", DisplayStyle.Shading
        "shadingwithedges", DisplayStyle.ShadingWithEdges
        "consistentcolors", DisplayStyle.FlatColors
        "flatcolors", DisplayStyle.FlatColors
        "realistic", DisplayStyle.Realistic
        "realisticwithedges", DisplayStyle.RealisticWithEdges
        "textures", DisplayStyle.Textures
    ]

    let parseDisplayStyle (s: string) =
        let key = s.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant()
        match displayStyles |> List.tryFind (fun (k, _) -> k = key) with
        | Some(_, style) -> style
        | None -> failwith $"Display style '{s}' not found. The styles are: Wireframe, HiddenLine, Shading, ShadingWithEdges, ConsistentColors, Realistic, Textures."

    /// A view that is not a template, by name or element id, or the active view.
    let findView (uidoc: UIDocument) (name: string) : View =
        if String.IsNullOrWhiteSpace name then
            match uidoc.ActiveView with
            | null -> failwith "Revit has no active view."
            | v -> v
        else
            let doc = uidoc.Document
            let views = (new FilteredElementCollector(doc)).OfClass(typeof<View>) |> Seq.cast<View> |> Seq.filter (fun v -> not v.IsTemplate) |> Array.ofSeq
            let byId =
                match Int64.TryParse name with
                | true, n -> views |> Array.tryFind (fun v -> v.Id.Value = n)
                | _ -> None
            byId
            |> Option.orElse (views |> Array.tryFind (fun v -> v.Name = name))
            |> Option.orElse (views |> Array.tryFind (fun v -> String.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            |> Option.defaultWith (fun () ->
                let names = views |> Array.filter _.CanBePrinted |> Array.map _.Name |> Array.sort |> Array.truncate 60 |> String.concat ", "
                failwith $"View '{name}' not found. Views are e.g.: {names}")

    /// Makes temporary changes in a transaction, runs f, and rolls the changes back.
    let withTemporaryChanges (doc: Document) (name: string) (change: unit -> unit) (f: unit -> 'T) : 'T =
        if doc.IsModifiable then failwith "A transaction is open in Revit. Finish or cancel what is going on in Revit first."
        internalChange <- true
        use g = new TransactionGroup(doc, name)
        g.Start() |> ignore
        try
            use t = new Transaction(doc, name)
            t.Start() |> ignore
            change ()
            match t.Commit() with
            | TransactionStatus.Committed -> f ()
            | s -> failwith $"Revit did not commit the temporary changes of the {name}: {s}"
        finally
            g.RollBack() |> ignore
            internalChange <- false

    let captureView (viewName: string, width: int, height: int, displayMode: string, path: string) =
        let uidoc = activeUiDoc ()
        let doc = uidoc.Document
        let view = findView uidoc viewName
        if not view.CanBePrinted then failwith $"Revit can't export the view '{view.Name}' as image."
        // the size of the window of the view, if it is open
        let viewSize =
            uidoc.GetOpenUIViews()
            |> Seq.tryFind (fun uv -> uv.ViewId = view.Id)
            |> Option.map (fun uv ->
                let r = uv.GetWindowRectangle()
                r.Right - r.Left, r.Bottom - r.Top)
            |> Option.defaultValue (CaptureSize.defaultMaxEdge, CaptureSize.defaultMaxEdge * 3 / 4)
        let w, h = CaptureSize.fit viewSize (width, height)
        let tempDir = Path.Combine(Path.GetTempPath(), "Caddis", "capture-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory tempDir |> ignore
        try
            let options = new ImageExportOptions()
            options.ExportRange <- ExportRange.SetOfViews
            options.SetViewsAndSheets(List<ElementId>([ view.Id ]))
            options.ZoomType <- ZoomFitType.FitToPage
            // Revit takes the other side from the extent of the view
            options.FitDirection <- if w >= h then FitDirectionType.Horizontal else FitDirectionType.Vertical
            options.PixelSize <- max w h
            options.ImageResolution <- ImageResolution.DPI_150
            options.HLRandWFViewsFileType <- ImageFileType.PNG
            options.ShadowViewsFileType <- ImageFileType.PNG
            options.FilePath <- Path.Combine(tempDir, "capture") // Revit adds the view name
            let exportImage () = doc.ExportImage options |> ignore
            if String.IsNullOrWhiteSpace displayMode then
                exportImage ()
            else
                let style = parseDisplayStyle displayMode
                let setStyle () =
                    try view.DisplayStyle <- style
                    with e -> failwith $"The display style of the view '{view.Name}' can't be changed, e.g. because its view template sets it: {e.Message}"
                withTemporaryChanges doc "capture" setStyle exportImage
            match Directory.GetFiles(tempDir, "*.png") |> Array.tryHead with
            | Some f -> File.Move(f, path, true)
            | None -> failwith "Revit did not write an image of the view."
        finally
            try Directory.Delete(tempDir, true) with _ -> ()

    // --- export ---

#if NETFRAMEWORK
    let formats = "dwg, dxf, dgn, sat, pdf, dwf, dwfx, ifc, fbx, obj, stl, nwc" // STEP needs Revit 2025
#else
    let formats = "dwg, dxf, dgn, sat, pdf, dwf, dwfx, ifc, fbx, obj, stl, step, nwc"
#endif

    /// The active view if it is a 3D view, otherwise the default 3D view '{3D}'.
    let view3D (uidoc: UIDocument) (ext: string) : View =
        match uidoc.ActiveView with
        | :? View3D as v when not v.IsTemplate -> v
        | _ ->
            (new FilteredElementCollector(uidoc.Document)).OfClass(typeof<View3D>)
            |> Seq.cast<View3D>
            |> Seq.tryFind (fun v -> not v.IsTemplate && v.Name = "{3D}")
            |> Option.map (fun v -> v :> View)
            |> Option.defaultWith (fun () ->
                failwith $"Exporting to '{ext}' needs a 3D view. Activate one in Revit, or create the default 3D view '{{3D}}'.")

    let parseElementIds (doc: Document) (ids: string[]) : List<ElementId> =
        let parsed = ids |> Array.map (fun s -> ElementId(Int64.Parse s)) // validated by RevitInfo.info.isObjectId
        let missing = parsed |> Array.filter (fun id -> isNull (doc.GetElement id))
        if missing.Length > 0 then
            failwith ("Elements not found: " + (missing |> Seq.map (fun id -> string id.Value) |> String.concat ", "))
        List<ElementId>(parsed)

    let export (selection: ExportSelection, path: string) =
        let uidoc = activeUiDoc ()
        let doc = uidoc.Document
        let ext = Path.GetExtension(path).ToLowerInvariant()
#if NETFRAMEWORK
        if ext = ".step" || ext = ".stp" then failwith "Exporting to STEP needs Revit 2025 or later."
#endif
        let folder = Path.GetDirectoryName path
        let name = Path.GetFileNameWithoutExtension path
        let elementIds =
            match selection with
            | AllObjects -> None
            | SelectedObjects ->
                let ids = uidoc.Selection.GetElementIds()
                if ids.Count = 0 then failwith "Nothing is selected in Revit."
                Some(List<ElementId>(ids))
            | ObjectIds ids -> Some(parseElementIds doc ids)
        let needs3D =
            match ext with
            | ".fbx" | ".obj" | ".stl" | ".step" | ".stp" | ".sat" -> true
            | ".ifc" | ".nwc" -> elementIds.IsSome // the elements visible in the view
            | _ -> false
        let view = if needs3D then view3D uidoc ext else uidoc.ActiveView
        if isNull view then failwith "Revit has no active view."
        if elementIds.IsSome && (view :? ViewSheet || not (view.CanUseTemporaryVisibilityModes())) then
            failwith
                $"To export only some elements to '{ext}', Caddis isolates them in the active view, which is not possible in '{view.Name}'. Activate a plan, section, elevation or 3D view, e.g. with UIDocument.ActiveView in caddis_eval."
        let viewIds () = List<ElementId>([ view.Id ])
        let viewSet () =
            let s = new ViewSet()
            s.Insert view |> ignore
            s
        // the IFC and DWF exporters need an open transaction, its changes are not kept
        let inTransaction (f: unit -> bool) =
            use t = new Transaction(doc, $"Caddis {ext} export")
            t.Start() |> ignore
            try f () finally t.RollBack() |> ignore
        /// DGN needs a seed file, Revit has them in the folder ACADInterop next to RevitAPI.dll
        let dgnOptions () =
            let units = if doc.DisplayUnitSystem = DisplayUnit.METRIC then "Metric" else "Imperial"
            let dim = if view :? View3D then "3D" else "2D"
            new DGNExportOptions(SeedName = Path.Combine(Path.GetDirectoryName revitApiPath, "ACADInterop", $"V8-{units}-Seed{dim}.dgn"))
        let write () =
            let ok =
                match ext with
                | ".dwg" -> doc.Export(folder, name, viewIds (), new DWGExportOptions())
                | ".dxf" -> doc.Export(folder, name, viewIds (), new DXFExportOptions())
                | ".dgn" -> doc.Export(folder, name, viewIds (), dgnOptions ())
                | ".sat" -> doc.Export(folder, name, viewIds (), new SATExportOptions())
                | ".pdf" -> doc.Export(folder, viewIds (), new PDFExportOptions(FileName = name, Combine = true))
                | ".dwf" -> inTransaction (fun () -> doc.Export(folder, name, viewSet (), new DWFExportOptions()))
                | ".dwfx" -> inTransaction (fun () -> doc.Export(folder, name, viewSet (), new DWFXExportOptions()))
                | ".fbx" -> doc.Export(folder, name, viewSet (), new FBXExportOptions())
                | ".obj" -> doc.Export(folder, name, new OBJExportOptions(ViewId = view.Id))
                | ".stl" -> doc.Export(folder, name, new STLExportOptions(ViewId = view.Id))
#if !NETFRAMEWORK
                | ".step"
                | ".stp" -> doc.Export(folder, name, new STEPExportOptions(ViewId = view.Id))
#endif
                | ".ifc" ->
                    let options = new IFCExportOptions()
                    if elementIds.IsSome then options.FilterViewId <- view.Id
                    inTransaction (fun () -> doc.Export(folder, name, options))
                | ".nwc" ->
                    if not (OptionalFunctionalityUtils.IsNavisworksExporterAvailable()) then
                        failwith "Exporting to nwc needs the Navisworks exporter for Revit, it is not installed."
                    let options = new NavisworksExportOptions()
                    if elementIds.IsSome then
                        options.ExportScope <- NavisworksExportScope.View
                        options.ViewId <- view.Id
                    doc.Export(folder, name, options)
                    true
                | _ -> failwith $"Caddis can't export to '{ext}' in Revit. The formats are: {formats}."
            if not ok then failwith $"Revit could not export the view '{view.Name}' to '{ext}'."
        match elementIds with
        | None ->
            internalChange <- true
            try write () finally internalChange <- false
        | Some ids ->
            let isolate () =
                view.IsolateElementsTemporary ids
                view.ConvertTemporaryHideIsolateToPermanent()
            withTemporaryChanges doc "export" isolate write

    // --- undo ---

    let undo (run: Run) : Result<string, string> =
        match run.UndoSerial with
        | None -> Error $"Run '{run.Id}' made no changes that can be undone."
        | Some _ ->
            let uiApp = ApiContext.uiApplication ()
            match lastUndo with
            | Some(id, key) when id = run.Id ->
                match uiApp.ActiveUIDocument with
                | uidoc when isNull uidoc || docKey uidoc.Document <> key -> Error "The document of this run is not the active one."
                | _ ->
                    let undoCommand = RevitCommandId.LookupPostableCommandId PostableCommand.Undo
                    if uiApp.CanPostCommand undoCommand then
                        uiApp.PostCommand undoCommand
                        run.UndoSerial <- None
                        lastUndo <- None
                        Ok $"Posted Revit's Undo command for run '{run.Id}', Revit runs it right after this call."
                    else
                        Error "Revit can't undo right now."
            | _ -> Error "Other changes were made in Revit after this run, undo them in Revit instead."

    interface ICaddisHost with

        member _.Info = RevitInfo.info

        member _.Description = description

        member _.LibFolders = libFolders

        member _.References = references

        member _.InvokeOnUi<'T>(f: unit -> 'T) : Task<'T> = ApiContext.invoke f

        member _.CannotRunReason() =
            // only checked in API context, i.e. on the 'ui' thread
            if ApiContext.isActive () then
                match ApiContext.uiApplication () with
                | null -> None
                | uiApp ->
                    match uiApp.ActiveUIDocument with
                    | uidoc when not (isNull uidoc) && uidoc.Document.IsModifiable ->
                        Some "A transaction is open in Revit. Finish or cancel what is going on in Revit first."
                    | _ -> None
            else
                None

        member _.BeforeRun(run: Run) =
            runActive <- true
            runId <- run.Id
            commits <- 0
            clearChanges ()
            failures.Clear()
            failureCounts.Clear()
            lastUndoBefore <- lastUndo
            // a run on the 'ui' thread waits in the queue of the external event until Revit is idle
            let waited = DateTime.UtcNow - run.StartedAt
            if waited > TimeSpan.FromSeconds 2.0 then
                run.AddNote
                    $"Revit started the run only after %.0f{waited.TotalSeconds} s: it runs calls into its API only while no command, edit mode or dialog is active."
            if ApiContext.isActive () && run.RecordUndo then
                match ApiContext.uiApplication () with
                | null -> ()
                | uiApp ->
                    match uiApp.ActiveUIDocument with
                    | null -> ()
                    | uidoc ->
                        let doc = uidoc.Document
                        if not doc.IsReadOnly && not doc.IsModifiable then
                            let g = new TransactionGroup(doc, $"Caddis {run.Id}")
                            if g.Start() = TransactionStatus.Started then group <- Some(g, doc) else g.Dispose()

        member _.AfterRun(run: Run, status: RunStatus) =
            let mutable finalizationError: string option = None
            match group with
            | Some(g, doc) ->
                // in API context: the run was on the 'ui' thread
                group <- None
                let rollBack (note: string) =
                    match g.RollBack() with
                    | TransactionStatus.RolledBack -> ()
                    | s -> failwith $"Revit did not roll back the transaction group: {s}."
                    lastUndo <- lastUndoBefore
                    clearChanges ()
                    if status = Succeeded then finalizationError <- Some note
                    else run.AddNote note
                try
                    try
                        if doc.IsModifiable then
                            // the group can't be assimilated while a transaction is open, and the script's Transaction object is unknown
                            rollBack
                                "The script left a transaction open, so Caddis rolled back all changes of the run, also those of committed transactions. Commit or roll back every transaction, e.g. use Caddis.Revit.Script.Transact."
                        elif status = Succeeded && commits > 0 then
                            match g.Assimilate() with
                            | TransactionStatus.Committed ->
                                undoCounter <- undoCounter + 1u
                                run.UndoSerial <- Some undoCounter
                                lastUndo <- Some(run.Id, docKey doc)
                            | s -> rollBack $"Revit did not keep the changes of the run, their transaction group ended with the status '{s}'."
                        elif commits > 0 then
                            rollBack "The run did not succeed, so Caddis rolled back all its changes to the model."
                        else
                            g.RollBack() |> ignore
                    with e ->
                        clearChanges ()
                        run.UndoSerial <- None
                        finalizationError <- Some $"Caddis could not end the transaction group of the run, Revit rolls back its changes: {e.Message}"
                finally
                    try g.Dispose()
                    with e -> finalizationError <- Some $"Caddis could not dispose the transaction group of the run: {e.Message}"
            | None ->
                // background mode, or recordUndo false, or no document: each transaction is an undo step of its own
                let leftOpen =
                    ApiContext.isActive ()
                    && (match ApiContext.uiApplication () with
                        | null -> false
                        | uiApp -> not (isNull uiApp.ActiveUIDocument) && uiApp.ActiveUIDocument.Document.IsModifiable)
                if leftOpen then
                    finalizationError <- Some "The script left a transaction open, Revit rolls it back with its changes. Commit or roll back every transaction, e.g. use Caddis.Revit.Script.Transact."
                match lastUndo with
                | Some(id, _) when id = run.Id && commits = 1 && not leftOpen ->
                    undoCounter <- undoCounter + 1u
                    run.UndoSerial <- Some undoCounter
                | _ when commits > 1 ->
                    run.AddNote $"The run committed {commits} transactions, each is an undo step in Revit. caddis_undo can't undo them, undo them in Revit."
                | _ -> ()
            for n in failureNotes () do
                run.AddNote n
            run.Commands <- changeReport ()
            runActive <- false
            match finalizationError with
            | Some message -> Error message
            | None -> Ok ()

        member _.Echo(_, _) = () // Revit has no command line

        member _.Log(msg) = RevitLog.write msg

        member _.Status() =
            // Revit may be busy, e.g. with a run on the 'ui' thread or in a dialog: then the status of the last call
            // also updates lastStatus when Revit answers too late for this call
            let fresh =
                ApiContext.invoke (fun () ->
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
                    lastStatus @ [ "revitStatusError", Json.ofString (fresh.Exception.GetBaseException().Message) ]
                else
                    lastStatus @ [ "revitBusy", Json.ofString "Revit did not answer within 1 s, the document is shown as of the previous status." ]
            // a JsonNode can only have one parent
            result |> List.map (fun (k, v) -> k, (if isNull v then null else v.DeepClone()))

        member _.CaptureView(viewName, width, height, displayMode, path) = captureView (viewName, width, height, displayMode, path)

        member _.Export(selection, path) = export (selection, path)

        member _.Undo(run) = undo run

        member _.Dialogs() = RevitDialogs.dialogs ()

        member _.PressDialogButton(dialogId, button) = RevitDialogs.press (dialogId, button)

        member _.RunCommands(_, _) = failwith "Revit has no commands to run."
