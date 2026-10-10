namespace Caddis

open System
open System.Threading.Tasks

/// A host without Rhino or Revit, for tests and for trying Caddis without a host app.
/// A dedicated thread stands in for the UI thread.
type HeadlessHost(log: string -> unit) =

    let ui = new WorkerThread("Caddis headless UI")

    static let info = {
        appName = "Headless"
        mcpServerName = "caddis"
        copyMcpCommandUi = "CaddisServer.ClaudeMcpCommand"
        objectIdKind = "an object id (a GUID)"
        isObjectId = fun s -> fst (Guid.TryParse s)
        mcp = {
            instructions =
                String.concat "\n" [
                    "Caddis runs F# code in a persistent F# Interactive session, here without a host app."
                    "Values, functions and types defined in one caddis_eval call stay available in later calls until caddis_reset."
                    "Files saved to the folder Caddis.Script.OutDir are returned with the result, images inline."
                ]
            eval = "Evaluates F# code in the persistent F# Interactive session and returns the result. Definitions stay available for later calls."
            recordUndo = "Wrap the run in an undo record of the host app. Default: true."
            command =
                Some {
                    description = "Runs commands of the host app. There is no host app here."
                    commands = "The command macros."
                    select = "Ids (GUIDs) of objects to select before the first command."
                    inputTimeoutMs = $"Cancel a command that waits this long for input. Default: {CaddisService.DefaultInputTimeoutMs}."
                }
            describe = "Looks up the types and members of the loaded assemblies and returns their signatures in F# syntax with their XML doc summaries."
            describeName = "A namespace ('System.IO'), a type ('File') or a member ('File.ReadAllText')."
            status = "Returns the state of the F# session, the thread mode, the current run and versions of F# and .NET."
            cancel = "Cancels the current run. Only runs in background mode can be cancelled."
            setMode = "Sets on which thread code runs: 'ui' or 'background'. Changing the mode resets the session."
            undo = "Undoes the changes a run made. Needs a host app."
            capture = "Returns a PNG image of a view. Needs a host app."
            captureView = "Name of the view."
            captureDisplayMode = "Name of a display mode."
            export = "Exports objects to a file. Needs a host app."
            exportFormat = "File extension of the format."
            exportObjects = "Which objects to export. Default: 'all'."
            exportObjectIds = "Ids (GUIDs) of the objects to export. Overrides 'objects'."
            dialog = None
        }
    }

    new() = new HeadlessHost(fun s -> eprintfn "%s" s)

    interface ICaddisHost with
        member _.Info = info
        member _.Description = "Headless (no host app)"
        member _.LibFolders = [||]
        member _.References = [||]
        member _.InvokeOnUi<'T>(f: unit -> 'T) : Task<'T> = ui.Invoke f
        member _.CannotRunReason() = None
        member _.BeforeRun _ = ()
        member _.AfterRun(_, _) = Ok ()
        member _.Echo(_, _) = ()
        member _.Log msg = log msg
        member _.Status() = []
        member _.CaptureView(_, _, _, _, _) = failwith "Capturing a viewport needs a host app like Rhino or Revit."
        member _.Export(_, _) = failwith "Exporting needs a host app like Rhino or Revit."
        member _.Undo _ = Error "Undo needs a host app like Rhino or Revit."
        member _.Dialogs() = []
        member _.PressDialogButton(_, _) = Error "There is no host app with dialogs."
        member _.RunCommands(_, _) = failwith "Running commands needs a host app like Rhino."

    interface IDisposable with
        member _.Dispose() = ui.Stop()
