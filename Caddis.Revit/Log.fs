namespace Caddis.Revit

open System
open System.Collections.Generic
open System.IO
open Caddis

/// Messages of Caddis itself. Revit has no command line, so they go to a log file
/// and the latest ones are kept for the Info button and the panel.
/// Errors are kept apart from the other messages, so that many messages don't push them out.
module RevitLog =

    let private maxKept = 30

    let private recent = Queue<string>()

    let mutable private latest = ""

    /// The latest errors, for the Info button and the panel.
    let errors = ErrorLog 20

    /// %AppData%/Caddis/revit.log, started anew when it is larger than 1 MB.
    let path = Path.Combine(SettingsStore.DefaultFolder, "revit.log")

    do
        try
            if File.Exists path && FileInfo(path).Length > 1_000_000L then File.Delete path
        with _ ->
            ()

    /// Called under the lock.
    let private append (line: string) =
        latest <- line
        try
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.AppendAllText(path, line + Environment.NewLine)
        with _ ->
            ()

    let private stamp (msg: string) = DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss" + " " + msg

    /// Called from any thread.
    let write (msg: string) =
        let line = stamp msg
        lock recent (fun () ->
            recent.Enqueue line
            while recent.Count > maxKept do
                recent.Dequeue() |> ignore
            append line)

    /// An internal error, marked with ERROR in the log file. Called from any thread.
    let error (msg: string) =
        errors.Add msg
        lock recent (fun () -> append (stamp ("ERROR " + msg)))

    /// The latest messages without the errors, oldest first.
    let recentLines () : string list = lock recent (fun () -> List.ofSeq recent)

    /// The latest message or error.
    let latestLine () : string = lock recent (fun () -> latest)
