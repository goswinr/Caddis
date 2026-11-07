namespace Caddis.AutoCAD

open System
open System.Globalization
open Caddis

/// The texts and names of Caddis in AutoCAD.
module AutoCADInfo =

    /// True for a handle as AutoCAD writes it, a hexadecimal number like "2A3F".
    let isHandle (s: string) =
        not (String.IsNullOrEmpty s)
        && s.Length <= 16
        && fst (Int64.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))

    let private mcp: McpTexts = {
        instructions =
            String.concat "\n" [
                "Caddis runs F# code inside AutoCAD in a persistent F# Interactive session. A person is usually working in the same AutoCAD window."
                "Values, functions and types defined in one caddis_eval call stay available in later calls until caddis_reset."
                "The AutoCAD .NET API is referenced (AcDbMgd, AcCoreMgd, AcMgd). Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument is the active drawing, with its Database and Editor. caddis_status shows it with its units, layers, current space and extents."
                "Read and change the drawing in a transaction: 'use tr = db.TransactionManager.StartTransaction()', tr.GetObject(id, OpenMode.ForWrite), for new objects btr.AppendEntity and tr.AddNewlyCreatedDBObject, then tr.Commit(). Without Commit all changes of the transaction are rolled back."
                "Code runs like the code of a modeless window, not inside a command: Editor.Command fails with eInvalidInput, and the Editor.Get* prompts wait for the person. Caddis locks the active drawing during a run, so the run can change it."
                "Do most work with the AutoCAD .NET API in caddis_eval: reading and creating objects, logic, loops and geometry math. caddis_command runs AutoCAD commands like typed at the command line, only for what commands do better than the API, e.g. ZOOM, HATCH, OFFSET, or to read what LIST, DIST or AREA print."
                "Within caddis_eval a command can only be queued with doc.SendStringToExecute(\"_.ZOOM _E \", true, false, false): it runs after the run has ended, a space or \\n stands for Enter, and its result is not part of the run."
                "On the 'ui' thread (the default) each run that changes the drawing is one undo step in AutoCAD, named after the run, which caddis_undo can revert. The views are redrawn after a run."
                "Objects are identified by their handle as a hexadecimal string, e.g. '2A3F' from entity.Handle.ToString(). Get the ObjectId of a handle with db.GetObjectId(false, Handle(Convert.ToInt64(\"2A3F\", 16)), 0)."
                "AutoCAD rejects a run while a command is active in it. Then the person in AutoCAD has to finish or cancel the command."
                "A modal dialog, e.g. of a command run with caddis_command, blocks AutoCAD until it is closed. Results and caddis_status name an open dialog with its buttons. Close it with caddis_dialog, choosing what the person would expect, e.g. 'Cancel', or ask the person."
                "On the 'ui' thread AutoCAD is blocked while code runs and a run can't be cancelled: an endless loop freezes AutoCAD until it is killed."
                "For long or risky code switch to 'background' mode with caddis_set_mode, there timeoutMs and caddis_cancel stop a run. In background mode every call of the AutoCAD API must be inside Caddis.Script.OnUi(fun () -> ...), Caddis locks the drawing for each call, and each call that changes the drawing is its own undo step."
                "caddis_eval returns after waitMs (default 50 s) with status 'running' if the run has not finished. It keeps running, wait for the result with caddis_get_run."
                "The result shows F# Interactive's output, printfn output as stdout, and what the run wrote to the AutoCAD command line, e.g. with Editor.WriteMessage, without Caddis' own echo of the output. Use printfn for output to read."
                "Don't show modal dialogs or wait for user input in scripts."
                "Look up AutoCAD API types and members you are not sure about with caddis_describe instead of guessing their names, overloads or parameter types."
                "Use caddis_check to type-check code against the session without running it, and caddis_capture_viewport to see the active view."
            ]
        packages = None
        eval =
            String.concat " " [
                "Evaluates F# code in the persistent F# Interactive session inside AutoCAD and returns the result:"
                "status ('ok', 'compileError', 'runtimeError', 'cancelled', 'timeout', 'rejected' or 'running'),"
                "F# Interactive's output with the types of new definitions and the value of the last expression,"
                "stdout, what was written to the AutoCAD command line, stderr, compiler errors and warnings, the exception with the script line, and files saved to Caddis.Script.OutDir, images inline."
                "Definitions stay available for later calls. The AutoCAD .NET API is referenced, Caddis locks the active drawing during the run."
                "Changes need a transaction that is committed. On the 'ui' thread a run is one undo step in AutoCAD."
                "In background mode every AutoCAD API call must be wrapped in Caddis.Script.OnUi(fun () -> ...)."
            ]
        recordUndo = "'ui' thread only: make the run one undo step in AutoCAD that caddis_undo can revert. Default: true."
        command =
            Some {
                description =
                    String.concat " " [
                        "Runs AutoCAD commands in the active drawing as if typed at the command line, with Editor.Command."
                        "Each item of 'commands' is one command with its inputs, they run one after the other until one does not succeed."
                        "Inputs are separated by spaces, and unlike in scripts a space is no Enter: an empty input \"\" is Enter, e.g. to end LINE or PLINE. Quote inputs that contain spaces, e.g. \"Wall 1\"."
                        "Prefix command names with '_.' and options with '_' for their English names. Use the version of a command for the command line where there is one, e.g. _.-LAYER, _.-HATCH, _.-INSERT: a command that opens a dialog blocks AutoCAD until it is closed, in AutoCAD or with caddis_dialog."
                        "A command without inputs after its name works on the selected objects, e.g. '_.ERASE'. Other commands ask for objects at 'Select objects:', where '_P \"\"' gives them the selected objects, e.g. '_.MOVE _P \"\" 0,0 10,0'. See 'select'."
                        "Returns the result of each command, the handles of the entities they created, changed and erased, the selection if it changed,"
                        "and what they printed to the command line, e.g. the results of LIST, DIST or AREA."
                        "A command that waits for input which the macro does not give is cancelled, the result shows the prompt with its options."
                        "All commands of a call are one undo step in AutoCAD, which caddis_undo can revert. Not possible while a run is in progress."
                    ]
                commands =
                    "The command macros, e.g. [\"_.LINE 0,0 10,0 10,5 \\\"\\\"\", \"_.ZOOM _E\", \"_.-LAYER _M \\\"Wall 1\\\" \\\"\\\"\"]. Inputs: points as x,y or x,y,z, numbers, option keywords like '_D', '_P' for the selected objects, and \"\" for Enter."
                select =
                    "Handles of objects to select before the first command, replacing the selection, e.g. ['2A3F']. They are the pickfirst set and the previous selection set '_P'."
                inputTimeoutMs =
                    $"Cancel a command that waits this long at a prompt for input. 0 waits until the person in AutoCAD gives it. Default: {CaddisService.DefaultInputTimeoutMs}."
            }
        describe =
            String.concat " " [
                "Looks up the types and members of the loaded assemblies, e.g. the AutoCAD .NET API (Autodesk.AutoCAD.DatabaseServices, Autodesk.AutoCAD.Geometry, Autodesk.AutoCAD.ApplicationServices, Autodesk.AutoCAD.EditorInput) or a NuGet package loaded via #r,"
                "and returns their signatures in F# syntax with their XML doc summaries. Use it instead of guessing names, overloads or parameter types."
                "Works while a run is in progress."
            ]
        describeName =
            "A namespace ('Autodesk.AutoCAD.DatabaseServices': its types), a type ('Polyline' or 'Autodesk.AutoCAD.DatabaseServices.Polyline': its members) or a member ('Polyline.AddVertexAt': all overloads with docs)."
        status =
            "Returns the state of the F# session (e.g. 'ready', 'evaluating'), the thread mode, the current run, versions of AutoCAD, F# and .NET, the command in progress, and the active drawing with its units, layers, current space, extents and selection."
        cancel =
            "Cancels the current run. Only runs in background mode can be cancelled, and the AutoCAD commands of caddis_command while they wait for input. Queued Script.OnUi calls are skipped; an already-started call must finish first. If the remaining background code does not stop within a few seconds the session is reset."
        setMode =
            "Sets on which thread code runs. 'ui': AutoCAD's main thread, the AutoCAD API can be used directly and each run is one undo step, but AutoCAD is blocked while code runs and runs can't be cancelled. 'background': a separate thread, runs can be cancelled and time out, AutoCAD stays responsive, every AutoCAD API call must be wrapped in Caddis.Script.OnUi. Changing the mode resets the session."
        undo =
            "Undoes the changes of a run in AutoCAD by sending its UNDO command, which runs right after this call. Only works if the run is one undo step, as every run with changes on the 'ui' thread and every caddis_command call, and nothing else was done in AutoCAD after it."
        capture = "Returns a PNG image of the active view of the active drawing, to see the result of a script. Not possible while a run on the 'ui' thread is in progress."
        captureView = "Not supported in AutoCAD: leave it empty for the active view. Switch views or layouts with caddis_eval first."
        captureDisplayMode = "Not supported in AutoCAD yet: leave it empty for the current visual style."
        export =
            "Exports objects of the active drawing to a new DWG or DXF file, without changing the drawing or its file name. Returns the path of the written file."
        exportFormat = "File extension of the format: 'dwg' or 'dxf'."
        exportObjects =
            "Which objects to export: 'all' (the default) for a copy of the whole drawing, or the 'selected' ones, which must be in model space."
        exportObjectIds =
            "A nonempty array of handles of objects in model space, as hexadecimal strings, e.g. ['2A3F']. Overrides 'objects'. Invalid or empty arrays are rejected."
        dialog =
            Some(
                String.concat " " [
                    "Lists AutoCAD's open modal dialog, or presses one of its buttons. A dialog, e.g. of a command that caddis_command ran, blocks AutoCAD until it is closed."
                    "Without 'button' it returns the dialog with its text and buttons. Press what the person would expect: prefer 'Cancel' or 'Close'."
                    "Don't press buttons that save, delete or discard work, unless the person asked for it. Works while a run is in progress."
                ]
            )
    }

    let info: HostInfo = {
        appName = "AutoCAD"
        mcpServerName = "caddis-autocad"
        objectIdKind = "a handle (a hexadecimal number, e.g. '2A3F')"
        isObjectId = isHandle
        mcp = mcp
    }
