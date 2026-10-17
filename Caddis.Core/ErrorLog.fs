namespace Caddis

open System
open System.Collections.Generic

/// The latest internal errors of Caddis, kept apart from its other messages, so that many messages don't push them out.
/// Each host keeps one for its panel, and Revit for its Info button. Called from any thread.
type ErrorLog(maxKept: int) =

    let kept = Queue<string>()

    /// Since the start or Clear, also errors no longer kept.
    let mutable count = 0

    /// count when the errors were last shown.
    let mutable seenCount = 0

    member _.Add(msg: string) =
        lock kept (fun () ->
            kept.Enqueue(DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss" + " " + msg)
            while kept.Count > maxKept do
                kept.Dequeue() |> ignore
            count <- count + 1)

    /// The kept errors with their time, oldest first.
    member _.Lines: string list = lock kept (fun () -> List.ofSeq kept)

    /// The number of errors since the start or Clear, also those no longer kept.
    member _.Count: int = lock kept (fun () -> count)

    /// The number of errors since they were last shown.
    member _.Unseen: int = lock kept (fun () -> count - seenCount)

    /// Called when the errors are shown to the user.
    member _.MarkSeen() = lock kept (fun () -> seenCount <- count)

    /// Forgets the errors, e.g. after the user has read them.
    member _.Clear() =
        lock kept (fun () ->
            kept.Clear()
            count <- 0
            seenCount <- 0)
