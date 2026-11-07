namespace Caddis.AutoCAD

open System
open System.Threading.Tasks
open System.Windows.Threading

/// AutoCAD's main thread, where its API must be used.
/// Calls from other threads go through the WPF dispatcher of that thread, AutoCAD's message loop runs it.
/// Like a modeless dialog, they run in AutoCAD's application context, not in a command, also while a command waits for input.
module internal UiThread =

    let mutable private dispatcher: Dispatcher = null

    /// Call on AutoCAD's main thread, in IExtensionApplication.Initialize.
    let init () = dispatcher <- Dispatcher.CurrentDispatcher

    /// True on AutoCAD's main thread.
    let isCurrent () = not (isNull dispatcher) && dispatcher.CheckAccess()

    /// Runs a function on the main thread, inline if already on it.
    let invoke (f: unit -> 'T) : Task<'T> =
        if isNull dispatcher then
            Task.FromException<'T>(InvalidOperationException "Caddis has not found AutoCAD's main thread yet.")
        elif dispatcher.CheckAccess() then
            try Task.FromResult(f ())
            with e -> Task.FromException<'T> e
        else
            let tcs = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
            dispatcher.BeginInvoke(
                Action(fun () ->
                    try tcs.SetResult(f ())
                    with e -> tcs.SetException e)
            )
            |> ignore
            tcs.Task

    /// Runs a function on the main thread without waiting, inline if already on it. Errors are ignored.
    let post (f: unit -> unit) =
        if isNull dispatcher then ()
        elif dispatcher.CheckAccess() then (try f () with _ -> ())
        else dispatcher.BeginInvoke(Action(fun () -> try f () with _ -> ())) |> ignore

    /// Runs a function on the main thread once AutoCAD is back in its message loop, also when called on that thread,
    /// e.g. after the command that is running now has ended. Errors are ignored.
    let postLater (f: unit -> unit) =
        if not (isNull dispatcher) then dispatcher.BeginInvoke(Action(fun () -> try f () with _ -> ())) |> ignore
