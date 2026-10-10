namespace Caddis

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// A dedicated thread that runs work items one after the other.
/// Used to evaluate scripts in background mode, and as a stand-in UI thread in the headless host.
type WorkerThread(name: string) =
    let queue = new BlockingCollection<unit -> unit>()

    let thread =
        Thread(
            ThreadStart(fun () ->
                for work in queue.GetConsumingEnumerable() do
                    try work () with _ -> () // work items handle their own exceptions, this only keeps the thread alive
            ),
            IsBackground = true,
            Name = name
        )

    do
        if OperatingSystem.IsWindows() then thread.SetApartmentState ApartmentState.STA // so that scripts can create windows
        thread.Start()

    /// True if called from this worker's thread.
    member _.IsCurrent = Thread.CurrentThread.ManagedThreadId = thread.ManagedThreadId

    /// Runs the function on this thread, inline if already on it.
    member this.Invoke(f: unit -> 'T) : Task<'T> =
        let tcs = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
        let work () =
            try tcs.SetResult(f ())
            with e -> tcs.SetException e
        if this.IsCurrent then
            work ()
        else
            try queue.Add work
            with :? InvalidOperationException -> tcs.SetException(ObjectDisposedException name)
        tcs.Task

    /// Lets the thread end after the current work item. It does not abort it.
    member _.Stop() =
        try queue.CompleteAdding() with _ -> ()

    interface IDisposable with
        member this.Dispose() = this.Stop()
