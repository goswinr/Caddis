namespace Caddis.Revit

open System
open System.Collections.Generic
open System.IO
open Caddis

/// Messages of Caddis itself. Revit has no command line, so they go to a log file
/// and the latest ones are kept for the Info button.
module RevitLog =

    let private maxKept = 30

    let private recent = Queue<string>()

    /// %AppData%/Caddis/revit.log, started anew when it is larger than 1 MB.
    let path = Path.Combine(SettingsStore.DefaultFolder, "revit.log")

    do
        try
            if File.Exists path && FileInfo(path).Length > 1_000_000L then File.Delete path
        with _ ->
            ()

    /// Called from any thread.
    let write (msg: string) =
        let line = DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss" + " " + msg
        lock recent (fun () ->
            recent.Enqueue line
            while recent.Count > maxKept do
                recent.Dequeue() |> ignore
            try
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.AppendAllText(path, line + Environment.NewLine)
            with _ ->
                ())

    /// The latest messages, oldest first.
    let recentLines () : string list = lock recent (fun () -> List.ofSeq recent)
