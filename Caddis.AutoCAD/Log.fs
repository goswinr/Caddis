namespace Caddis.AutoCAD

open System
open System.IO
open Autodesk.AutoCAD.ApplicationServices
open Caddis

/// Messages of Caddis itself, on the AutoCAD command line and in a log file.
/// The log file also has the messages written while no drawing is open, e.g. when Caddis loads at AutoCAD's start.
/// Errors are kept apart from the other messages, so that many messages don't push them out.
module AutoCADLog =

    /// The latest errors, for the panel, which also shows them after the command line has scrolled on.
    let errors = ErrorLog 20

    /// %AppData%/Caddis/autocad.log, started anew when it is larger than 1 MB.
    let path = Path.Combine(SettingsStore.DefaultFolder, "autocad.log")

    let private sync = obj ()

    do
        try
            if File.Exists path && FileInfo(path).Length > 1_000_000L then File.Delete path
        with _ ->
            ()

    /// The latest messages without the errors, for the Info dialog.
    let private recent = Collections.Generic.Queue<string>()

    let private stamp (line: string) = DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss" + " " + line

    /// Called under the lock.
    let private appendToFile (line: string) =
        try
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.AppendAllText(path, line + Environment.NewLine)
        with _ ->
            ()

    /// The lines that write put on the command line since startRecording, by their text without trailing spaces, with how often.
    /// So that the lines of an F# run's result leave out what Caddis wrote itself, e.g. its echo of the run's output.
    let private written = Collections.Generic.Dictionary<string, int>()
    let mutable private recording = false

    /// The last line that write put on the command line. It stays the current line until something starts a new line.
    let mutable private lastWritten = ""

    /// The newest lines of the command line history right after the last write since startRecording, newest first. Set on the main thread.
    /// If the history still ends with them, nothing started a new line since, so the current line is still the one that Caddis wrote.
    let mutable private historyAfterWrite: string[] option = None

    /// The newest lines of the command line history, through an internal API of AutoCAD. Empty if it fails.
    let private newestLines () =
        try Autodesk.AutoCAD.Internal.Utils.GetLastCommandLines(3, false) |> Array.ofSeq
        with _ -> [||]

    /// True if the history still ends as it did right after the last line that Caddis wrote since startRecording, on the main thread.
    /// Then the current line is still that line, nothing printed after it. False if Caddis wrote nothing since.
    let wroteLast () =
        match lock written (fun () -> historyAfterWrite) with
        | Some lines -> newestLines () = lines
        | None -> false

    /// The last line that Caddis wrote to the command line, without trailing spaces. AutoCAD or a script may have written more since.
    let lastLine () = lock written (fun () -> lastWritten.TrimEnd())

    /// Starts counting the lines that write puts on the command line, from zero.
    let startRecording () =
        lock written (fun () ->
            written.Clear()
            historyAfterWrite <- None
            recording <- true)

    /// Stops counting, and returns how often write put each line on the command line since startRecording.
    let stopRecording () : Collections.Generic.Dictionary<string, int> =
        lock written (fun () ->
            recording <- false
            Collections.Generic.Dictionary<string, int>(written))

    /// Writes text to the command line of the active drawing, from any thread. Lost if no drawing is open.
    /// Starts a new line itself if the text starts with a line break, as AutoCAD's own messages do.
    let write (s: string) =
        lock written (fun () ->
            // the text before the first line break continues the current line, the others are lines of their own
            let lines = s.Replace("\r\n", "\n").Split('\n')
            lastWritten <- if lines.Length = 1 then lastWritten + lines[0] else lines[lines.Length - 1]
            if recording then
                for line in lines |> Array.skip 1 do
                    let key = line.TrimEnd()
                    if key <> "" then
                        written[key] <- (match written.TryGetValue key with | true, n -> n + 1 | _ -> 1))
        UiThread.post (fun () ->
            match Application.DocumentManager.MdiActiveDocument with
            | null -> ()
            | doc ->
                doc.Editor.WriteMessage s
                if recording then
                    let lines = newestLines ()
                    lock written (fun () -> historyAfterWrite <- Some lines))

    /// A message of Caddis, on its own line on the command line and in the log file. Called from any thread.
    let writeLine (s: string) =
        let line = stamp s
        lock sync (fun () ->
            recent.Enqueue line
            while recent.Count > 30 do
                recent.Dequeue() |> ignore
            appendToFile line)
        write ("\n" + s)

    /// An internal error, marked with ERROR in the log file. Called from any thread.
    let error (s: string) =
        errors.Add s
        lock sync (fun () -> appendToFile (stamp ("ERROR " + s)))
        write ("\n" + s)

    /// The latest messages without the errors, oldest first, with their time.
    let recentLines () : string list = lock sync (fun () -> List.ofSeq recent)
