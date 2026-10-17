namespace Caddis.Revit

open System
open Caddis

/// The texts and names of Caddis in Revit.
module RevitInfo =

    let private mcp: McpTexts = {
        instructions =
            String.concat "\n" [
                "Caddis runs F# code inside a running Revit in a persistent F# Interactive session. A person is usually working in the same Revit window."
                "Values, functions and types defined in one caddis_eval call stay available in later calls until caddis_reset."
                "RevitAPI and RevitAPIUI are referenced. Caddis.Revit.Script.Doc is the active Document, Script.UiDoc the active UIDocument, e.g. for the selection, and Script.UiApp the UIApplication. caddis_status shows the active document with its length unit, active view and levels."
                "The Revit API uses feet for lengths and radians for angles, whatever the units of the document. Convert with e.g. UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters) and UnitUtils.ConvertFromInternalUnits."
                "Changes to the model need a transaction: Caddis.Revit.Script.Transact(\"name\", fun doc -> ...), or 'use t = new Transaction(doc, \"name\")' with t.Start() and t.Commit()."
                "On the 'ui' thread (the default) code runs in Revit's API context and each run is wrapped in a transaction group: if the run succeeds its transactions are one undo step in Revit that caddis_undo can revert, if it fails Caddis rolls back all its changes."
                "Revit warnings about changes, e.g. overlapping walls, are removed and listed in the notes of the result. An error rolls back its transaction, Commit returns RolledBack, and is listed too. The result lists the ids of the elements a run created, changed and deleted."
                "Revit runs a call only while no command, edit mode or modal dialog is active in it. If a run stays 'running' without starting, the person in Revit has to finish what they are doing."
                "A Revit dialog, e.g. 'Project Not Saved Recently', blocks all calls until it is closed. Results and caddis_status name an open dialog with its buttons. Close it with caddis_dialog, choosing what the person would expect, e.g. 'Cancel' or 'Ignore', or ask the person."
                "On the 'ui' thread Revit is blocked while code runs and a run can't be cancelled: an endless loop freezes Revit until it is killed."
                "For long or risky code switch to 'background' mode with caddis_set_mode, there timeoutMs and caddis_cancel stop a run. In background mode every Revit API call must be inside Caddis.Revit.Script.OnUi(fun () -> ...) or Script.Transact, each transaction is its own undo step and a failed run is not rolled back."
                "caddis_eval returns after waitMs (default 50 s) with status 'running' if the run has not finished. It keeps running, wait for the result with caddis_get_run."
                "Don't show modal dialogs, e.g. TaskDialog, or wait for user input, e.g. Selection.PickObject, in scripts."
                "Look up Revit API types and members you are not sure about with caddis_describe instead of guessing their names, overloads or parameter types. The API changes between Revit versions, e.g. element ids are Int64: ElementId(316577L) and id.Value."
                "Use caddis_check to type-check code against the session without running it, and caddis_capture_viewport to see a view."
            ]
        eval =
            String.concat " " [
                "Evaluates F# code in the persistent F# Interactive session inside Revit and returns the result:"
                "status ('ok', 'compileError', 'runtimeError', 'cancelled', 'timeout', 'rejected' or 'running'),"
                "F# Interactive's output with the types of new definitions and the value of the last expression,"
                "stdout, stderr, compiler errors and warnings, the exception with the script line, the ids of the elements the run created, changed and deleted,"
                "Revit's warnings and errors, and files saved to Caddis.Script.OutDir, images inline."
                "Definitions stay available for later calls. RevitAPI and RevitAPIUI are referenced, Caddis.Revit.Script.Doc is the active document. NuGet packages can be used via #r \"nuget: Name\"."
                "Changes need a transaction, e.g. Caddis.Revit.Script.Transact(\"name\", fun doc -> ...). On the 'ui' thread a run is one undo step in Revit and is rolled back if it fails."
                "In background mode every Revit API call must be wrapped in Caddis.Revit.Script.OnUi(fun () -> ...)."
            ]
        recordUndo =
            "'ui' thread only: wrap the run in a Revit transaction group, so that it is one undo step that caddis_undo can revert, and is rolled back if it fails. Default: true."
        command = None
        describe =
            String.concat " " [
                "Looks up the types and members of the loaded assemblies, e.g. the Revit API (Autodesk.Revit.DB, Autodesk.Revit.UI), Caddis.Revit.Script or a NuGet package loaded via #r,"
                "and returns their signatures in F# syntax with their XML doc summaries. Use it instead of guessing names, overloads or parameter types."
                "Works while a run is in progress."
            ]
        describeName =
            "A namespace ('Autodesk.Revit.DB': its types), a type ('Wall' or 'Autodesk.Revit.DB.Wall': its members) or a member ('Wall.Create': all overloads with docs)."
        status =
            "Returns the state of the F# session (e.g. 'ready', 'evaluating'), the thread mode, the current run, versions of Revit, F# and .NET, and the active document with its length unit, active view, levels and selection."
        cancel = "Cancels the current run. Only runs in background mode can be cancelled. Queued Script.OnUi calls are skipped; an already-started call must finish first. If the remaining background code does not stop within a few seconds the session is reset."
        setMode =
            "Sets on which thread code runs. 'ui': in Revit's API context on its UI thread, the Revit API can be used directly, each run is one undo step and is rolled back if it fails, but Revit is blocked while code runs and runs can't be cancelled. 'background': a separate thread, runs can be cancelled and time out, Revit stays responsive, every Revit API call must be wrapped in Caddis.Revit.Script.OnUi or Script.Transact. Changing the mode resets the session."
        undo =
            "Undoes the changes of a run in Revit by posting Revit's Undo command, which runs right after this call. Only works if the run is one undo step, as every run with changes on the 'ui' thread, and no other changes were made in Revit after it."
        capture =
            "Returns a PNG image of a Revit view, to see the result of a script. The image shows the whole view, or its crop region, not the zoom of the window. Not possible while a run on the 'ui' thread is in progress."
        captureView = "Name of the view, e.g. '{3D}' or 'Level 1', or its element id. Default: the active view."
        captureDisplayMode =
            "A display style for the capture: 'Wireframe', 'HiddenLine', 'Shading', 'ShadingWithEdges', 'ConsistentColors', 'Realistic' or 'Textures'. Default: the style of the view."
        export =
            String.concat " " [
                "Exports from the active Revit document to a file, picked by file extension, without dialogs, as a run. Returns the paths of the written files."
                "dwg, dxf, dgn, pdf, dwf and dwfx export the active view, of a sheet also its views and images. fbx, obj, stl, step and sat export a 3D view: the active one, or '{3D}'."
                "ifc and nwc export the whole model, or with 'selected' or 'objectIds' only those elements."
                "With 'selected' or 'objectIds' Caddis isolates the elements in the view during the export, and restores the view after it; for the 2D formats activate a plan, section or 3D view first."
                "Exports of a large model can take minutes and hundreds of MB, e.g. obj or ifc: export only the elements needed. A run still going after waitMs returns 'running', get its files with caddis_get_run."
            ]
        exportFormat =
            "File extension of the format: 'dwg', 'dxf', 'dgn', 'sat', 'pdf', 'dwf', 'dwfx', 'ifc', 'fbx', 'obj', 'stl', 'step', or 'nwc' if the Navisworks exporter is installed."
        exportObjects = "Which elements to export: 'all' (the default) or the 'selected' ones in Revit."
        exportObjectIds = "A nonempty array of element ids as strings, e.g. ['316577']. Overrides 'objects'. Invalid or empty arrays are rejected."
        dialog =
            Some(
                String.concat " " [
                    "Lists Revit's open modal dialog, or presses one of its buttons. A dialog, e.g. 'Project Not Saved Recently' or 'Unresolved References', blocks all calls into Revit until it is closed."
                    "Without 'button' it returns the dialog with its text and buttons. Press what the person would expect: prefer 'Cancel', 'Close' or 'Ignore and continue'."
                    "Don't press buttons that save, delete or discard work, unless the person asked for it. Works while a run is in progress."
                ]
            )
    }

    let info: HostInfo = {
        appName = "Revit"
        mcpServerName = "caddis-revit"
        objectIdKind = "an element id (a number)"
        isObjectId = fun s -> fst (Int64.TryParse s)
        mcp = mcp
    }
