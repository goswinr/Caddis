namespace Caddis.Rhino

open System
open System.IO
open System.Reflection
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open Rhino
open Rhino.Commands
open Rhino.Display
open Rhino.FileIO
open Caddis

/// Writes to the Rhino command line from any thread.
module RhinoLog =

    let private onUi (f: unit -> unit) =
        if RhinoApp.InvokeRequired then RhinoApp.InvokeOnUiThread(Action f) else f ()

    let write (s: string) = onUi (fun () -> RhinoApp.Write s)

    let writeLine (s: string) = onUi (fun () -> RhinoApp.WriteLine s)


/// Rhino.Scripting finds the UI SynchronizationContext via reflection in the Fesh.Rhino plugin.
/// Without Fesh.Rhino set it via its public RhinoSync.SyncContext property,
/// so that Rhino.Scripting can switch to the UI thread when scripts run in background mode.
module RhinoScriptingSync =

    let private trySet (ctx: SynchronizationContext) (a: Assembly) =
        if a.GetName().Name = "Rhino.Scripting" then
            try
                // Rhino.Scripting.RhinoSync since version 0.12, Rhino.RhinoSync before
                match a.GetType "Rhino.Scripting.RhinoSync" |> Option.ofObj |> Option.orElse (a.GetType "Rhino.RhinoSync" |> Option.ofObj) with
                | None -> ()
                | Some t ->
                    match t.GetProperty("SyncContext", BindingFlags.Public ||| BindingFlags.Static) with
                    | null -> ()
                    | p -> p.SetValue(null, ctx)
            with e ->
                RhinoLog.writeLine $"Caddis: setting RhinoSync.SyncContext of Rhino.Scripting failed: {e.Message}"

    /// Call on the UI thread.
    let install () =
        let ctx = SynchronizationContext.Current
        if not (isNull ctx) then
            AppDomain.CurrentDomain.AssemblyLoad.Add(fun e -> trySet ctx e.LoadedAssembly)
            for a in AppDomain.CurrentDomain.GetAssemblies() do
                trySet ctx a


/// Connects Caddis to Rhino: UI thread, undo records, redraw, viewport capture and export.
type RhinoHost() =

    let rhinoCommonPath = typeof<RhinoApp>.Assembly.Location

    /// The folder of the loaded RhinoCommon.dll comes first: on Windows it is 'System/netcore' with the .NET Core build.
    /// The folder of Rhino.exe has the .NET Framework build of RhinoCommon, but also Eto.dll and other shared dlls.
    let libFolders =
        let exeFolder = try RhinoApp.GetExecutableDirectory().FullName with _ -> null
        [| Path.GetDirectoryName rhinoCommonPath; exeFolder |]
        |> Array.filter (String.IsNullOrEmpty >> not)
        |> Array.distinct

    let description =
        let os = if OperatingSystem.IsMacOS() then "Mac" else "Windows"
        $"Rhino {RhinoApp.Version} on {os}"

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
        if text <> "" then RhinoLog.write text

    // --- undo ---

    /// The undo record Rhino began last, 0 after an undo or redo.
    let mutable lastUndoRecord = 0u

    /// Set by document events while a run is in progress. Rhino keeps no undo record for runs without changes.
    let mutable runActive = false
    let mutable docChanged = false
    let changed () = if runActive then docChanged <- true

    do
        Command.UndoRedo.Add(fun e ->
            if e.IsBeginRecording then lastUndoRecord <- e.UndoSerialNumber
            elif e.IsBeginUndo || e.IsBeginRedo || e.IsPurgeRecord then lastUndoRecord <- 0u)
        RhinoDoc.AddRhinoObject.Add(fun _ -> changed ())
        RhinoDoc.DeleteRhinoObject.Add(fun _ -> changed ())
        RhinoDoc.ReplaceRhinoObject.Add(fun _ -> changed ())
        RhinoDoc.UndeleteRhinoObject.Add(fun _ -> changed ())
        RhinoDoc.ModifyObjectAttributes.Add(fun _ -> changed ())
        RhinoDoc.LayerTableEvent.Add(fun _ -> changed ())

    // --- UI thread ---

    let invokeOnUi (f: unit -> 'T) : Task<'T> =
        if not RhinoApp.InvokeRequired then
            try Task.FromResult(f ())
            with e -> Task.FromException<'T> e
        else
            let tcs = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
            RhinoApp.InvokeOnUiThread(
                Action(fun () ->
                    try tcs.SetResult(f ())
                    with e -> tcs.SetException e)
            )
            tcs.Task

    /// Runs on the UI thread and waits, for calls from the evaluation thread.
    let onUi (f: unit -> unit) = (invokeOnUi f).GetAwaiter().GetResult()

    let activeDoc () =
        match RhinoDoc.ActiveDoc with
        | null -> failwith "There is no active Rhino document."
        | doc -> doc

    let captureView (viewName: string, width: int, height: int, displayMode: string, path: string) =
        let doc = activeDoc ()
        let view =
            if String.IsNullOrWhiteSpace viewName then doc.Views.ActiveView
            else doc.Views.Find(viewName, false)
        if isNull view then
            let names = doc.Views.GetViewList ViewTypeFilter.Model |> Array.map _.MainViewport.Name |> String.concat ", "
            failwith $"View '{viewName}' not found. The views are: {names}"
        let viewSize = view.ActiveViewport.Size
        let w, h = CaptureSize.fit (viewSize.Width, viewSize.Height) (width, height)
        let size = Drawing.Size(w, h)
        use bitmap =
            if String.IsNullOrWhiteSpace displayMode then
                view.CaptureToBitmap size
            else
                match DisplayModeDescription.FindByName displayMode with
                | null ->
                    let modes = DisplayModeDescription.GetDisplayModes() |> Array.map _.EnglishName |> String.concat ", "
                    failwith $"Display mode '{displayMode}' not found. The display modes are: {modes}"
                | mode -> view.CaptureToBitmap(size, mode)
        if isNull bitmap then failwith "Rhino could not capture the view."
        bitmap.Save(path, Drawing.Imaging.ImageFormat.Png)

    /// Writes any format Rhino can export, picked by the file extension, without showing dialogs.
    /// Other formats than 3dm are written with the scripted '-_Export' command, '_EnterEnd' accepts the default options.
    /// Not with RhinoDoc.Export or RhinoDoc.WriteFile: for formats like STL they show the options dialog,
    /// even with SuppressDialogBoxes, and block until someone clicks OK.
    let export (selection: ExportSelection, path: string) =
        let doc = activeDoc ()
        if Command.InCommand() then failwith "A Rhino command is running. Finish or cancel it first."
        let is3dm = Path.GetExtension(path).Equals(".3dm", StringComparison.OrdinalIgnoreCase)
        let previous = doc.Objects.GetSelectedObjects(false, false) |> Seq.map _.Id |> Array.ofSeq
        // selects only these objects, returns how many could be selected
        let selectOnly (ids: Guid seq) =
            doc.Objects.UnselectAll() |> ignore
            ids |> Seq.filter (fun id -> doc.Objects.Select(id, true, true)) |> Seq.length
        let ok =
            try
                let count =
                    match selection with
                    | AllObjects -> selectOnly (doc.Objects.GetObjectList(DocObjects.ObjectEnumeratorSettings()) |> Seq.map _.Id)
                    | SelectedObjects -> previous.Length
                    | ObjectIds ids -> selectOnly ids
                if count = 0 then failwith "There are no objects to export."
                if is3dm then
                    use options =
                        new FileWriteOptions(
                            SuppressAllInput = true,
                            WriteSelectedObjectsOnly = true,
                            UpdateDocumentPath = false // keep the name and path of the document
                        )
                    doc.WriteFile(path, options)
                else
                    RhinoApp.RunScript($"-_Export \"{path}\" _EnterEnd", false)
            finally
                // restore the selection of the user
                doc.Objects.UnselectAll() |> ignore
                for id in previous do
                    doc.Objects.Select(id, true, true) |> ignore
        if not ok || not (File.Exists path) then failwith $"Rhino could not export to '{Path.GetExtension path}'."

    let undo (run: Run) : Result<string, string> =
        match run.UndoSerial with
        | None -> Error $"Run '{run.Id}' made no changes that can be undone."
        | Some serial when serial <> lastUndoRecord -> Error "Other changes were made in Rhino after this run, undo them in Rhino instead."
        | Some _ ->
            match RhinoDoc.FromRuntimeSerialNumber run.DocSerial with
            | null -> Error "The document of this run is closed."
            | doc when not (obj.ReferenceEquals(doc, RhinoDoc.ActiveDoc)) -> Error "The document of this run is not the active one."
            | _ when Command.InCommand() -> Error "A Rhino command is running. Finish or cancel it first."
            | _ ->
                // The Undo command, not RhinoDoc.Undo(): called outside of a command, that leaves an undo record open,
                // and then later runs get no undo record anymore.
                if RhinoApp.RunScript("_Undo", false) then
                    run.UndoSerial <- None
                    Ok $"Undid run '{run.Id}'."
                else
                    Error "Rhino could not undo the run."

    interface ICaddisHost with

        member _.Description = description

        member _.LibFolders = libFolders

        member _.References = [| rhinoCommonPath |]

        member _.InvokeOnUi<'T>(f: unit -> 'T) : Task<'T> = invokeOnUi f

        member _.CannotRunReason() =
            if Command.InCommand() then Some "A Rhino command is running. Finish or cancel it first."
            else None

        member _.BeforeRun(run: Run) =
            onUi (fun () ->
                runActive <- true
                docChanged <- false
                match RhinoDoc.ActiveDoc with
                | doc when run.RecordUndo && not (isNull doc) ->
                    let serial = doc.BeginUndoRecord $"Caddis {run.Id}"
                    if serial <> 0u then
                        run.UndoSerial <- Some serial
                        run.DocSerial <- doc.RuntimeSerialNumber
                | _ -> ())

        member _.AfterRun(run: Run) =
            flushEcho true
            onUi (fun () ->
                runActive <- false
                match run.UndoSerial with
                | Some serial ->
                    match RhinoDoc.FromRuntimeSerialNumber run.DocSerial with
                    | null -> ()
                    | doc -> doc.EndUndoRecord serial |> ignore
                    if not docChanged then run.UndoSerial <- None
                | None -> ()
                match RhinoDoc.ActiveDoc with
                | null -> ()
                | doc ->
                    // a script might have disabled redraw, e.g. for speed
                    doc.Views.RedrawEnabled <- true
                    doc.Views.Redraw())

        member _.Echo(_, text) =
            lock echoBuffer (fun () -> echoBuffer.Append text |> ignore)
            flushEcho false

        member _.Log(msg) = RhinoLog.writeLine msg

        member _.Status() =
            // don't use the UI thread here: it might be blocked by a run
            let doc = RhinoDoc.ActiveDoc
            let maxLayers = 100
            let document () : JsonNode =
                let layers = doc.Layers |> Seq.filter (fun l -> not l.IsDeleted) |> Seq.map _.FullPath |> Array.ofSeq
                Json.obj [
                    "name", Json.ofString doc.Name
                    "path", Json.ofString doc.Path
                    "units", Json.ofString (string doc.ModelUnitSystem)
                    "absoluteTolerance", Json.ofFloat doc.ModelAbsoluteTolerance
                    "angleToleranceDegrees", Json.ofFloat doc.ModelAngleToleranceDegrees
                    // not Objects.Count, that includes deleted objects kept for undo
                    "objectCount", Json.ofInt (doc.Objects.ObjectCount(DocObjects.ObjectEnumeratorSettings()))
                    "selectedObjectCount", Json.ofInt (doc.Objects.GetSelectedObjects(false, false) |> Seq.length)
                    "currentLayer", Json.ofString doc.Layers.CurrentLayer.FullPath
                    "layerCount", Json.ofInt layers.Length
                    "layers", Json.ofStrings (Array.truncate maxLayers layers)
                ]
            [
                "rhinoVersion", Json.ofString (string RhinoApp.Version)
                "rhinoCommandRunning", Json.ofBool (Command.InCommand())
                "document",
                (if isNull doc then null
                 else
                     // the document might be changed by a run at the same time
                     try document ()
                     with e -> Json.obj [ "name", Json.ofString doc.Name; "error", Json.ofString e.Message ] :> JsonNode)
            ]

        member _.CaptureView(viewName, width, height, displayMode, path) = captureView (viewName, width, height, displayMode, path)

        member _.Export(selection, path) = export (selection, path)

        member _.Undo(run) = undo run
