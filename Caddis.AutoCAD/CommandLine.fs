namespace Caddis.AutoCAD

open System
open Autodesk.AutoCAD.EditorInput

/// What AutoCAD's command line shows, for the results of caddis_command.
module internal AutoCADCommandLine =

    /// The lines of the command line history, newest first, on the main thread. AutoCAD keeps the last 400.
    /// Through an internal API of AutoCAD, the public one can't read the command line. Empty if it fails.
    let history () : string[] =
        try Autodesk.AutoCAD.Internal.Utils.GetLastCommandLines(1000, false) |> Array.ofSeq
        with _ -> [||]

    /// The lines that were added to the history since it was 'before', oldest first. Both are newest first, as from history.
    /// Found by the newest lines of before, not by the count of lines: the history is full after 400 lines.
    /// All of after if those lines aren't there anymore, e.g. after more than 400 new lines.
    let added (before: string[]) (after: string[]) : string[] =
        let k = min 6 before.Length
        let matchesAt (o: int) = Seq.forall (fun j -> after[o + j] = before[j]) (seq { 0 .. k - 1 })
        match (if k = 0 then None else Seq.tryFind matchesAt (seq { 0 .. after.Length - k })) with
        | Some o -> Array.rev after[.. o - 1]
        | None -> Array.rev after

    /// The prompt the command line shows, e.g. "Specify next point or [Undo]:", from the system variable LASTPROMPT.
    let prompt () =
        try (string (Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable "LASTPROMPT")).Trim()
        with _ -> ""

    [<Runtime.InteropServices.DllImport("user32.dll")>]
    extern bool private IsWindowEnabled(nativeint hWnd)

    /// True while AutoCAD shows a modal dialog, e.g. of a command, on the main thread. The dialog disables AutoCAD's main window.
    /// Bit 8 of the system variable CMDACTIVE is not set for every dialog, e.g. not for the one of OPTIONS.
    let dialogActive () =
        let mainDisabled =
            try not (IsWindowEnabled Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle)
            with _ -> false
        let cmdActive =
            try
                match Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable "CMDACTIVE" with
                | :? int16 as v -> v &&& 8s <> 0s
                | :? int as v -> v &&& 8 <> 0
                | _ -> false
            with _ ->
                false
        mainDisabled || cmdActive


/// Tells when a prompt of AutoCAD's editor starts to wait for input and when it got it, through all its events
/// PromptingFor* and PromptedFor*, e.g. PromptingForPoint, one pair per kind of input. They fire for the prompts of AutoCAD's own commands too.
/// onPrompt: true when a prompt starts, false when it ended, with the arguments of the event. Prompts can be nested, e.g. a distance prompt asks for a point.
type internal PromptListener(onPrompt: bool -> EventArgs -> unit) =

    /// Set once binding to an event failed, so that the error is logged only once.
    static let mutable failureLogged = false

    member _.Opened(_: obj, e: EventArgs) = onPrompt true e

    member _.Closed(_: obj, e: EventArgs) = onPrompt false e

    /// The message of a prompt from the arguments of a PromptingFor* event, with its options, e.g. "Specify next point or [Undo]:".
    /// Its Options are of a type per kind of input, the message is their Message, or MessageForAdding for a selection.
    static member Message(e: EventArgs) : string =
        try
            match e.GetType().GetProperty "Options" with
            | null -> ""
            | p ->
                match p.GetValue e with
                | null -> ""
                | options ->
                    let t = options.GetType()
                    match t.GetProperty "Message" |> Option.ofObj |> Option.orElse (t.GetProperty "MessageForAdding" |> Option.ofObj) with
                    | Some m -> (string (m.GetValue options)).Trim()
                    | None -> ""
        with _ ->
            ""

    /// Adds the handlers to the events of an editor, one per drawing. They stay for the lifetime of the drawing.
    member this.Attach(ed: Editor) =
        // F# compiles the members of an internal type as internal
        let flags = Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
        let opened = typeof<PromptListener>.GetMethod("Opened", flags)
        let closed = typeof<PromptListener>.GetMethod("Closed", flags)
        let failed = ResizeArray<string>()
        for ev in typeof<Editor>.GetEvents() do
            let handler =
                if ev.Name.StartsWith "PromptingFor" then Some opened
                elif ev.Name.StartsWith "PromptedFor" then Some closed
                else None
            match handler with
            | Some m ->
                // the event arguments derive from EventArgs, so one method fits the delegate of every event
                try ev.AddEventHandler(ed, Delegate.CreateDelegate(ev.EventHandlerType, this, m))
                with e -> failed.Add $"{ev.Name}: {e.Message}"
            | None -> ()
        if failed.Count > 0 && not failureLogged then
            failureLogged <- true
            let events = String.concat ", " failed
            AutoCADLog.error $"Caddis can't watch these prompts of AutoCAD, caddis_command won't cancel their commands after the input timeout: {events}"
