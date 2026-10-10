namespace Caddis.Rhino

open System
open System.Collections.Generic
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
/// Not captured into the command line output of a run: switching the capture off keeps what was captured so far.
module RhinoLog =

    let private onUi (f: unit -> unit) =
        if RhinoApp.InvokeRequired then RhinoApp.InvokeOnUiThread(Action f) else f ()

    let private uncaptured (f: unit -> unit) =
        if RhinoApp.CommandWindowCaptureEnabled then
            // A write on the UI thread first prints what other threads wrote, e.g. a script in background mode,
            // which must be captured: let that happen before the capture is off.
            RhinoApp.Write ""
            RhinoApp.CommandWindowCaptureEnabled <- false
            try f () finally RhinoApp.CommandWindowCaptureEnabled <- true
        else
            f ()

    let write (s: string) = onUi (fun () -> uncaptured (fun () -> RhinoApp.Write s))

    let writeLine (s: string) = onUi (fun () -> uncaptured (fun () -> RhinoApp.WriteLine s))


/// Rhino.Scripting finds the UI SynchronizationContext via reflection in the Fesh.Rhino plugin.
/// Without Fesh.Rhino set it via its public RhinoSync.SyncContext property,
/// so that Rhino.Scripting can switch to the UI thread when scripts run in background mode.
module RhinoScriptingSync =

    let private trySet (ctx: SynchronizationContext) (a: Assembly) =
        if a.GetName().Name = "Rhino.Scripting" then
            try
                // Rhino.Scripting.RhinoSync since version 0.7, Rhino.RhinoSync before
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

    /// RhinoCommon uses System.Drawing.Bitmap, e.g. in RhinoView.CaptureToBitmap.
    /// On Windows it is in the WindowsDesktop framework folder, which FSI does not search.
    let references =
        let drawing = try typeof<Drawing.Bitmap>.Assembly.Location with _ -> null
        [| rhinoCommonPath; drawing |] |> Array.filter (String.IsNullOrEmpty >> not)

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

    /// The undo record on top of Rhino's undo stack, 0 after an undo or redo.
    let mutable lastUndoRecord = 0u

    /// The undo record below lastUndoRecord, 0 if unknown.
    let mutable previousUndoRecord = 0u

    /// Set by document events while a run is in progress. Rhino keeps no undo record for runs without changes.
    let mutable runActive = false
    let mutable docChanged = false
    let changed () = if runActive then docChanged <- true

    // --- what the commands of a command run do, collected by the events below ---

    /// True while a command run is in progress.
    let mutable commandRunActive = false

    /// True while one macro of a command run is in progress.
    let mutable macroActive = false

    /// The commands that ended during the current macro, with their English name and result.
    let endedCommands = ResizeArray<string * string>()

    /// The undo records of the command run that were not purged.
    let commandUndoRecords = ResizeArray<uint32>()

    /// The objects the command run added, changed or deleted, in order, and whether the first event of each was an add.
    let touchedObjects = ResizeArray<Guid>()
    let addedFirst = Dictionary<Guid, bool>()

    let touched (id: Guid) (added: bool) =
        if commandRunActive && addedFirst.TryAdd(id, added) then touchedObjects.Add id

    do
        Command.UndoRedo.Add(fun e ->
            if e.IsBeginRecording then
                previousUndoRecord <- lastUndoRecord
                lastUndoRecord <- e.UndoSerialNumber
                if commandRunActive then commandUndoRecords.Add e.UndoSerialNumber
            elif e.IsPurgeRecord then
                commandUndoRecords.Remove e.UndoSerialNumber |> ignore
                // Rhino purges a record without changes right after it ended, e.g. of a run that only printed,
                // then the record below it is on top again.
                if e.UndoSerialNumber = lastUndoRecord then
                    lastUndoRecord <- previousUndoRecord
                    previousUndoRecord <- 0u
                elif e.UndoSerialNumber = previousUndoRecord then
                    previousUndoRecord <- 0u
            elif e.IsBeginUndo || e.IsBeginRedo then
                lastUndoRecord <- 0u
                previousUndoRecord <- 0u)
        Command.EndCommand.Add(fun e -> if macroActive then endedCommands.Add(e.CommandEnglishName, string e.CommandResult))
        RhinoDoc.AddRhinoObject.Add(fun e ->
            changed ()
            touched e.ObjectId true)
        RhinoDoc.DeleteRhinoObject.Add(fun e ->
            changed ()
            touched e.ObjectId false)
        // followed by a delete and an add event with the same id, except during undo and redo
        RhinoDoc.ReplaceRhinoObject.Add(fun _ -> changed ())
        RhinoDoc.UndeleteRhinoObject.Add(fun _ -> changed ())
        RhinoDoc.ModifyObjectAttributes.Add(fun e ->
            changed ()
            touched e.RhinoObject.Id false)
        RhinoDoc.LayerTableEvent.Add(fun _ -> changed ())

    // --- what is printed to the command line during a run, on the UI thread ---

    let mutable wasCapturing = false
    let mutable capturedCount = 0

    let beginCapture () =
        wasCapturing <- RhinoApp.CommandWindowCaptureEnabled
        RhinoApp.CommandWindowCaptureEnabled <- true
        capturedCount <- RhinoApp.CapturedCommandWindowStrings(false).Length

    /// The text printed since beginCapture or the previous call.
    let takeCaptured () =
        let captured = RhinoApp.CapturedCommandWindowStrings(false)
        // another plugin might have cleared the captured strings
        let text = String.concat "" (if captured.Length >= capturedCount then captured[capturedCount..] else captured)
        capturedCount <- captured.Length
        text

    let endCapture () =
        if not wasCapturing then
            RhinoApp.CapturedCommandWindowStrings(true) |> ignore
            RhinoApp.CommandWindowCaptureEnabled <- false

    // --- UI thread ---

    /// How many calls of invokeOnUi are in progress on the UI thread, they can be nested. A command that begins in one
    /// during an F# run was started by the script: in UI-thread mode the whole run is one, in background mode Caddis.Script.OnUi.
    /// Commands that someone types in Rhino begin outside of them.
    let mutable uiCallDepth = 0

    let invokeOnUi (f: unit -> 'T) : Task<'T> =
        let counted () =
            uiCallDepth <- uiCallDepth + 1
            try f () finally uiCallDepth <- uiCallDepth - 1
        if not RhinoApp.InvokeRequired then
            try Task.FromResult(counted ())
            with e -> Task.FromException<'T> e
        else
            let tcs = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
            RhinoApp.InvokeOnUiThread(
                Action(fun () ->
                    try tcs.SetResult(counted ())
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

    /// True if the file starts like a 3dm file.
    let isRhinoFile (path: string) =
        let magic = "3D Geometry File Format"
        use f = File.OpenRead path
        let bytes = Array.zeroCreate<byte> magic.Length
        f.ReadAtLeast(bytes, bytes.Length, false) = bytes.Length && Encoding.ASCII.GetString bytes = magic

    /// Writes any format Rhino can export, picked by the file extension, without showing dialogs.
    /// Other formats than 3dm are written with the scripted '-_Export' command, '_EnterEnd' accepts the default options.
    /// Not with RhinoDoc.Export or RhinoDoc.WriteFile: for formats like STL they show the options dialog,
    /// even with SuppressDialogBoxes, and block until someone clicks OK.
    let export (selection: ExportSelection, path: string) =
        let doc = activeDoc ()
        if Command.InCommand() then failwith "A Rhino command is running. Finish or cancel it first."
        let ext = Path.GetExtension(path).ToLowerInvariant()
        if ext = ".pdf" then
            failwith "Exporting to PDF opens Rhino's print dialog, which waits for a click. Capture a viewport instead, or write a PDF in a script with Rhino.FileIO.FilePdf."
        let is3dm = ext = ".3dm"
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
                    | ObjectIds ids -> selectOnly (ids |> Array.map (fun (s: string) -> Guid.Parse s)) // validated by RhinoInfo.info.isObjectId
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
        if not ok || not (File.Exists path) then failwith $"Rhino could not export to '{ext}'."
        // for an extension without exporter Rhino writes a 3dm file
        if not is3dm && isRhinoFile path then
            File.Delete path
            failwith $"Rhino has no exporter for '{ext}'."

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
                // A command run has one undo record per command that changed something.
                let mutable undone = 0
                while undone < run.UndoSteps && RhinoApp.RunScript("_Undo", false) do
                    undone <- undone + 1
                if undone > 0 then run.UndoSerial <- None
                if undone = run.UndoSteps then Ok $"Undid run '{run.Id}'."
                elif undone = 0 then Error "Rhino could not undo the run."
                else Error $"Rhino undid only {undone} of the {run.UndoSteps} undo steps of run '{run.Id}'."

    // --- cancelling commands that wait for input: of command runs, and of F# runs with the setting cancelWaitingCommands ---

    /// During an F# run, one entry per running command: true if the script started it, see uiCallDepth.
    let scriptCommands = Stack<bool>()

    /// Notes for the F# run about the commands of its script that Caddis cancelled.
    let scriptNotes = ResizeArray<string>()

    /// When checkWaiting first found the watched command waiting.
    let mutable waitingSince: DateTime option = None

    /// When and at which prompt Caddis cancelled the watched command.
    let mutable cancelPosted: (DateTime * string) option = None

    /// True if the command still ran a while after the cancel: a dialog or another window holds it until someone closes it.
    let mutable cancelIgnored = false

    let resetWaiting () =
        waitingSince <- None
        cancelPosted <- None
        cancelIgnored <- false

    /// A command of the current macro of a command run runs, or one that the script of an F# run started.
    let watchedCommandRuns () =
        (commandRunActive && macroActive && Command.InCommand())
        || (runActive && scriptCommands.Count > 0 && scriptCommands.Peek())

    /// Runs on the UI thread. While a command runs the UI thread only gets to it when the command waits in a message loop:
    /// for input that its macro does not give, or in a dialog. Then the command is cancelled after the input timeout, or at once for caddis_cancel.
    let checkWaiting (run: Run) (inputTimeout: TimeSpan option) (docSerial: uint32) =
        if watchedCommandRuns () then
            let now = DateTime.UtcNow
            match cancelPosted with
            | Some(posted, _) -> if now - posted > TimeSpan.FromSeconds 2.0 then cancelIgnored <- true
            | None ->
                let since = waitingSince |> Option.defaultValue now
                waitingSince <- Some since
                if run.CancelRequested || inputTimeout |> Option.exists (fun t -> now - since >= t) then
                    cancelPosted <- Some(now, RhinoApp.CommandPrompt)
                    RhinoApp.PostCancelEvent docSerial

    /// Calls checkWaiting on the UI thread every 200 ms until the returned function is called.
    let startWatching (run: Run) (inputTimeout: TimeSpan option) (docSerial: uint32) : unit -> unit =
        let watching = ref true
        backgroundTask {
            while watching.Value do
                do! Task.Delay 200
                // awaits the UI thread, so that no checks pile up while it is busy
                if watching.Value then
                    do! invokeOnUi (fun () -> try checkWaiting run inputTimeout docSerial with _ -> ())
        }
        |> ignore
        fun () -> watching.Value <- false

    /// Stops watching the commands of the current F# run.
    let mutable stopWatchingScript: unit -> unit = ignore

    do
        Command.BeginCommand.Add(fun _ ->
            if runActive then
                let byScript = uiCallDepth > 0
                scriptCommands.Push byScript
                if byScript then resetWaiting ())
        Command.EndCommand.Add(fun e ->
            if runActive && scriptCommands.Count > 0 && scriptCommands.Pop() then
                match cancelPosted with
                | Some(_, prompt) ->
                    let name = e.CommandEnglishName
                    if cancelIgnored then
                        scriptNotes.Add
                            $"The command {name} that the script started showed a dialog or another window, which Caddis can't close, and waited until someone closed it in Rhino."
                    else
                        scriptNotes.Add
                            $"Caddis cancelled the command {name} that the script started, because it waited for input at the prompt '{prompt}'. The script went on after it. Give all inputs in the macro, or use caddis_command."
                | None -> ()
                resetWaiting ())

    /// For a macro whose command waited in a dialog or another window.
    let dialogNote (step: CommandStep) =
        let name = step.commands |> List.tryHead |> Option.map fst |> Option.defaultValue step.macro
        $"{name} showed a dialog or another window, which Caddis can't close, and waited until someone closed it in Rhino. Use the scripted version of the command with '-' if there is one, e.g. '-_{name}'."

    /// The outcome of a macro that did not succeed, None if it did.
    let macroFailure (run: Run) (ran: bool) (inDialog: bool) (step: CommandStep) : RunOutcome option =
        let fail status (message: string) = Some(RunOutcome.withMessage status message)
        match step.commands |> List.tryFind (fun (_, result) -> result <> "Success" && result <> "Nothing") with
        | Some(_, "Cancel") when inDialog -> fail Cancelled (dialogNote step)
        | Some(name, "Cancel") ->
            match step.waitedAt with
            | Some prompt when run.CancelRequested -> fail Cancelled $"{name} was cancelled via caddis_cancel at the prompt '{prompt}'."
            | Some prompt ->
                fail
                    Cancelled
                    $"Caddis cancelled {name} because it waited for input at the prompt '{prompt}', which the macro '{step.macro}' does not give. Add the missing inputs or options to the macro, end option prompts with _Enter, or pass a larger inputTimeoutMs if {name} was still busy."
            | None -> fail Cancelled $"{name} was cancelled, e.g. with Esc in Rhino."
        | Some(name, result) -> fail RuntimeError $"{name} ended with the result '{result}', see the Rhino command line."
        | None when step.commands.IsEmpty ->
            fail RuntimeError $"'{step.macro}' ran no command, e.g. because the command name is unknown, see the Rhino command line."
        | None when not ran -> fail RuntimeError $"Rhino could not run '{step.macro}'."
        | None -> None

    /// Runs the macros with RhinoApp.RunScript like typed at the command line, one after the other until one does not succeed.
    let runCommands (run: Run) (req: CommandRequest) : RunOutcome =
        let doc = activeDoc ()
        let docSerial = doc.RuntimeSerialNumber
        run.DocSerial <- docSerial
        let selection () = doc.Objects.GetSelectedObjects(false, false) |> Seq.map _.Id |> Array.ofSeq
        let idList (ids: Guid seq) = ids |> Seq.map string |> String.concat ", "
        match req.select |> Option.map (Array.map (fun (s: string) -> Guid.Parse s)) with // validated by RhinoInfo.info.isObjectId
        | None -> ()
        | Some ids ->
            let missing = ids |> Array.filter (fun id -> isNull (doc.Objects.FindId id))
            if missing.Length > 0 then failwith $"Objects not found: {idList missing}"
            doc.Objects.UnselectAll() |> ignore
            let notSelected = ids |> Array.filter (fun id -> not (doc.Objects.Select(id, true, true)))
            if notSelected.Length > 0 then failwith $"Objects that can't be selected, e.g. because they are hidden or locked: {idList notSelected}"
        let selectedBefore = selection ()

        beginCapture ()
        commandUndoRecords.Clear()
        touchedObjects.Clear()
        addedFirst.Clear()
        commandRunActive <- true
        let stopWatching = startWatching run req.inputTimeout docSerial

        let steps = ResizeArray<CommandStep>()
        let mutable failure: RunOutcome option = None
        let mutable note: string option = None
        try
            for macro in req.macros do
                if failure.IsNone then
                    endedCommands.Clear()
                    resetWaiting ()
                    macroActive <- true
                    let ran =
                        try RhinoApp.RunScript(docSerial, macro, true)
                        finally macroActive <- false
                    run.CommandLine.Append(takeCaptured ())
                    let inDialog = cancelIgnored
                    let waitedAt = if inDialog then None else cancelPosted |> Option.map snd
                    let step = { macro = macro; commands = List.ofSeq endedCommands; waitedAt = waitedAt }
                    steps.Add step
                    failure <- macroFailure run ran inDialog step
                    if inDialog && failure.IsNone then note <- Some(dialogNote step)
        finally
            stopWatching ()
            commandRunActive <- false
            endCapture ()

        let exists (id: Guid) = not (isNull (doc.Objects.FindId id))
        let pick (wasAdded: bool) (stillExists: bool) =
            touchedObjects |> Seq.filter (fun id -> addedFirst[id] = wasAdded && exists id = stillExists) |> Seq.map string |> Array.ofSeq
        let selectedAfter = selection ()
        run.Commands <-
            Some {
                steps = List.ofSeq steps
                notRun = req.macros |> Array.skip steps.Count |> List.ofArray
                created = pick true true
                changed = pick false true
                changedGroups = []
                deleted = pick false false
                selected = if set selectedBefore = set selectedAfter then None else Some(Array.map string selectedAfter)
            }
        if commandUndoRecords.Count > 0 then
            run.UndoSerial <- Some(Seq.last commandUndoRecords)
            run.UndoSteps <- commandUndoRecords.Count
        doc.Views.Redraw()
        failure |> Option.defaultValue { RunOutcome.ofStatus Succeeded with message = note }

    interface ICaddisHost with

        member _.Info = RhinoInfo.info

        member _.Description = description

        member _.LibFolders = libFolders

        member _.References = references

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
                | _ -> ()
                // e.g. RhinoApp.WriteLine or commands run via RhinoApp.RunScript
                beginCapture ()
                scriptCommands.Clear()
                scriptNotes.Clear()
                resetWaiting ()
                match run.InputTimeout, RhinoDoc.ActiveDoc with
                | Some _, doc when not (isNull doc) -> stopWatchingScript <- startWatching run run.InputTimeout doc.RuntimeSerialNumber
                | _ -> ())

        member _.AfterRun(run: Run, _status: RunStatus) =
            flushEcho true
            onUi (fun () ->
                stopWatchingScript ()
                stopWatchingScript <- ignore
                for n in scriptNotes do
                    run.AddNote n
                runActive <- false
                run.CommandLine.Append(takeCaptured ())
                endCapture ()
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
            Ok ()

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

        member _.Dialogs() = [] // not implemented for Rhino yet

        member _.PressDialogButton(_, _) = Error "Caddis can't press buttons of dialogs in Rhino."

        member _.RunCommands(run, request) = runCommands run request
