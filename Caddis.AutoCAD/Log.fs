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

    let private append (line: string) =
        lock sync (fun () ->
            try
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.AppendAllText(path, DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss" + " " + line + Environment.NewLine)
            with _ ->
                ())

    /// Writes text to the command line of the active drawing, from any thread. Lost if no drawing is open.
    /// Starts a new line itself if the text starts with a line break, as AutoCAD's own messages do.
    let write (s: string) =
        UiThread.post (fun () ->
            match Application.DocumentManager.MdiActiveDocument with
            | null -> ()
            | doc -> doc.Editor.WriteMessage s)

    /// A message of Caddis, on its own line on the command line and in the log file. Called from any thread.
    let writeLine (s: string) =
        append s
        write ("\n" + s)

    /// An internal error, marked with ERROR in the log file. Called from any thread.
    let error (s: string) =
        errors.Add s
        append ("ERROR " + s)
        write ("\n" + s)
