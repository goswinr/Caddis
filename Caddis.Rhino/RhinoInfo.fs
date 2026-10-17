namespace Caddis.Rhino

open System
open Caddis

/// The texts and names of Caddis in Rhino.
module RhinoInfo =

    let private mcp: McpTexts = {
        instructions =
            String.concat "\n" [
                "Caddis runs F# code inside a running Rhino in a persistent F# Interactive session. A person is usually working in the same Rhino window."
                "Values, functions and types defined in one caddis_eval call stay available in later calls until caddis_reset."
                "RhinoCommon is referenced, e.g. use Rhino.RhinoDoc.ActiveDoc for the current document. caddis_status shows its units and tolerance, check them before creating geometry."
                "The result shows F# Interactive's output, with the types of new definitions and the value of the last expression, printfn output as stdout, and what was printed to the Rhino command line, e.g. by RhinoApp.WriteLine or commands run via RhinoApp.RunScript. Long output is shortened and saved to a file."
                "Each run is one undo step in Rhino, the views are redrawn after it. Files saved to the folder Caddis.Script.OutDir are returned with the result, images inline."
                "On the 'ui' thread (the default) Rhino is blocked while code runs and a run can't be cancelled: an endless loop freezes Rhino until it is killed."
                "For long or risky code switch to 'background' mode with caddis_set_mode, there timeoutMs and caddis_cancel stop a run."
                "caddis_eval returns after waitMs (default 50 s) with status 'running' if the run has not finished. It keeps running, wait for the result with caddis_get_run."
                "Don't show modal dialogs or wait for user input in scripts."
                "Look up RhinoCommon types and members you are not sure about with caddis_describe instead of guessing their names, overloads or parameter types."
                "caddis_command runs Rhino commands like typed at the command line, for what commands do well, e.g. _Zoom, _SetDisplayMode, _Make2D, or to read what _What, _Length or _Area print. Use caddis_eval for logic, loops and geometry math. A command run in caddis_eval via RhinoApp.RunScript must get all its inputs in the macro, otherwise it waits until someone gives them in Rhino, or Caddis cancels it if the person turned that on."
                "Use caddis_check to type-check code against the session without running it, and caddis_capture_viewport to see the result."
            ]
        eval =
            String.concat " " [
                "Evaluates F# code in the persistent F# Interactive session inside Rhino and returns the result:"
                "status ('ok', 'compileError', 'runtimeError', 'cancelled', 'timeout', 'rejected' or 'running'),"
                "F# Interactive's output with the types of new definitions and the value of the last expression,"
                "stdout, what was printed to the Rhino command line, stderr, compiler errors and warnings, the exception with the script line, and files saved to Caddis.Script.OutDir, images inline."
                "Definitions stay available for later calls. RhinoCommon is referenced. NuGet packages can be used via #r \"nuget: Name\"."
                "Each run is one undo step in Rhino. In background mode code that uses the Rhino UI must be wrapped in Caddis.Script.OnUi(fun () -> ...)."
            ]
        recordUndo = "Wrap the run in a Rhino undo record so that caddis_undo can revert it. Default: true."
        command =
            Some {
                description =
                    String.concat " " [
                        "Runs Rhino commands as if typed at the Rhino command line, like a button macro."
                        "Each item of 'commands' is one command with its inputs, they run one after the other until one does not succeed."
                        "Use the scripted version of a command, with '-', where there is one: without it many commands open a dialog, which blocks Rhino until someone clicks."
                        "Prefix command and option names with '_' for their English names. Commands work on the selected objects, see 'select'."
                        "Returns the result of each command, the ids of the objects they created, changed and deleted, the selection if it changed,"
                        "and what they printed to the command line, e.g. the results of _What, _Length or _Area."
                        "A command that waits for input which the macro does not give is cancelled, the result shows the prompt with its options."
                        "Each command that changes the document is one undo step in Rhino, caddis_undo undoes all of a call. Not possible while a run is in progress."
                    ]
                commands =
                    "The command macros, e.g. [\"-_Line 0,0,0 10,0,0\", \"_Zoom _All _Extents\"]. Inputs are separated by spaces: points as x,y,z, numbers, options with their value like '_Cap=_Yes', and '_Enter' to end a prompt."
                select = "Ids (GUIDs) of objects to select before the first command, replacing the selection."
                inputTimeoutMs =
                    $"Cancel a command that waits this long for input. 0 waits until the person in Rhino gives it, e.g. picks points for '_Pause'. Default: {CaddisService.DefaultInputTimeoutMs}."
            }
        describe =
            String.concat " " [
                "Looks up the types and members of the loaded assemblies, e.g. RhinoCommon, Eto or a NuGet package loaded via #r,"
                "and returns their signatures in F# syntax with their XML doc summaries. Use it instead of guessing names, overloads or parameter types."
                "Works while a run is in progress."
            ]
        describeName =
            "A namespace ('Rhino.Geometry': its types), a type ('Brep' or 'Rhino.Geometry.Brep': its members) or a member ('Brep.CreateFromBox': all overloads with docs)."
        status =
            "Returns the state of the F# session (e.g. 'ready', 'evaluating'), the thread mode, the current run, versions of Rhino, F# and .NET, and the active document with its units, tolerance and layers."
        cancel =
            "Cancels the current run. Only runs in background mode can be cancelled, and Rhino commands while they wait for input. Queued Script.OnUi calls are skipped; an already-started call must finish first. If the remaining background code does not stop within a few seconds the session is reset."
        setMode =
            "Sets on which thread code runs. 'ui': the Rhino UI thread, safest for RhinoCommon, but Rhino is blocked while code runs and runs can't be cancelled. 'background': a separate thread, runs can be cancelled and time out, Rhino stays responsive, UI calls must be wrapped in Caddis.Script.OnUi. Changing the mode resets the session."
        undo = "Undoes the changes a run made to the Rhino document. Only works if no other changes were made in Rhino after the run."
        capture = "Returns a PNG image of a Rhino viewport, to see the result of a script. Not possible while a run on the UI thread is in progress."
        captureView = "Name of the view, e.g. 'Perspective' or 'Top'. Default: the active view."
        captureDisplayMode = "Name of a display mode, e.g. 'Shaded', 'Rendered', 'Wireframe'. Default: the mode of the view."
        export =
            "Exports objects of the Rhino document to a file in any format Rhino can write, picked by file extension, except PDF: it opens Rhino's print dialog. Returns the paths of the written files."
        exportFormat = "File extension of the format, e.g. '3dm', 'stl', 'obj', 'step', 'iges', 'dwg', 'dxf', '3mf', 'fbx', 'gltf', 'svg'."
        exportObjects = "Which objects to export. Default: 'all'."
        exportObjectIds = "A nonempty array of object ids (GUID strings) to export. Overrides 'objects'. Invalid or empty arrays are rejected."
        dialog = None
    }

    let info: HostInfo = {
        appName = "Rhino"
        mcpServerName = "caddis-rhino"
        copyMcpCommandUi = "the Rhino command 'Caddis CopyMcpCommand'"
        objectIdKind = "an object id (a GUID)"
        isObjectId = fun s -> fst (Guid.TryParse s)
        mcp = mcp
    }
