namespace Caddis

open System
open System.Threading.Tasks

/// A host without Rhino, for tests and for trying Caddis without Rhino.
/// A dedicated thread stands in for the UI thread.
type HeadlessHost(log: string -> unit) =

    let ui = new WorkerThread("Caddis headless UI")

    new() = new HeadlessHost(fun s -> eprintfn "%s" s)

    interface ICaddisHost with
        member _.Description = "Headless (no Rhino)"
        member _.LibFolders = [||]
        member _.References = [||]
        member _.InvokeOnUi<'T>(f: unit -> 'T) : Task<'T> = ui.Invoke f
        member _.CannotRunReason() = None
        member _.BeforeRun _ = ()
        member _.AfterRun _ = ()
        member _.Echo(_, _) = ()
        member _.Log msg = log msg
        member _.Status() = []
        member _.CaptureView(_, _, _, _, _) = failwith "Capturing a viewport needs Rhino."
        member _.Export(_, _) = failwith "Exporting needs Rhino."
        member _.Undo _ = Error "Undo needs Rhino."

    interface IDisposable with
        member _.Dispose() = ui.Stop()
