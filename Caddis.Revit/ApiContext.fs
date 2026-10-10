namespace Caddis.Revit

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Autodesk.Revit.UI

/// Runs functions in Revit's API context. Unlike in Rhino, being on the UI thread is not enough to call the Revit API:
/// only an external command, an event handler or an external event may. Caddis uses one external event with a queue.
/// Revit runs it only when no command, edit mode or modal dialog is active.
/// see https://help.autodesk.com/view/RVT/2026/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Advanced_Topics_External_Events_html
module internal ApiContext =

    let mutable private mainThreadId = -1

    /// How many calls in API context are in progress, they can be nested. Only changed on the main thread.
    let mutable private depth = 0

    let mutable private uiApp: UIApplication = null

    let private queue = ConcurrentQueue<UIApplication -> unit>()

    let mutable private externalEvent: ExternalEvent option = None

    /// True if called on Revit's main thread inside an external event or command of Caddis, where the Revit API can be used.
    let isActive () =
        Thread.CurrentThread.ManagedThreadId = mainThreadId && depth > 0

    /// The UIApplication of the latest call in API context, null before the first one.
    let uiApplication () = uiApp

    /// Runs f as a call in API context. For the external event and the commands of Caddis, which Revit runs in API context.
    let enter (app: UIApplication) (f: unit -> 'T) : 'T =
        if not (isNull app) then uiApp <- app
        depth <- depth + 1
        try f () finally depth <- depth - 1

    type private Handler() =
        interface IExternalEventHandler with
            member _.GetName() = "Caddis"

            member _.Execute(app: UIApplication) =
                enter app (fun () ->
                    let mutable work = Unchecked.defaultof<UIApplication -> unit>
                    while queue.TryDequeue(&work) do
                        work app) // each work item catches its own exceptions

    /// Creates the external event. Call in IExternalApplication.OnStartup, that is in API context on the main thread.
    let init () =
        mainThreadId <- Thread.CurrentThread.ManagedThreadId
        externalEvent <- Some(ExternalEvent.Create(Handler()))

    /// Disposes the external event. Calls that are still queued never run.
    let shutdown () =
        externalEvent |> Option.iter (fun e -> e.Dispose())
        externalEvent <- None

    /// Runs f in API context: inline if already in it, otherwise queued for the external event, when Revit is idle.
    let invoke (f: unit -> 'T) : Task<'T> =
        if isActive () then
            try Task.FromResult(f ())
            with e -> Task.FromException<'T> e
        else
            let tcs = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
            // set if Revit did not accept the event, then the queued work must not run anymore
            let dropped = ref false
            queue.Enqueue(fun _ ->
                if not dropped.Value then
                    try tcs.TrySetResult(f ()) |> ignore
                    with e -> tcs.TrySetException e |> ignore)
            match externalEvent with
            | None -> tcs.TrySetException(InvalidOperationException "Caddis is not loaded in Revit.") |> ignore
            | Some ev ->
                match ev.Raise() with
                | ExternalEventRequest.Accepted
                | ExternalEventRequest.Pending -> () // pending: raised before and not run yet, it runs this work too
                | r ->
                    dropped.Value <- true
                    tcs.TrySetException(InvalidOperationException $"Revit did not accept the call into its API: {r}.") |> ignore
            tcs.Task
