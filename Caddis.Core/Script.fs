namespace Caddis

open System
open System.Threading

module internal ScriptState =

    type Context = {
        run: Run
        invokeOnUi: Func<obj> -> obj
    }

    // A delayed task from an old run must never acquire a later run's identity or UI dispatcher.
    let current = AsyncLocal<Context option>()

    let withContext context f =
        let previous = current.Value
        current.Value <- Some context
        try f () finally current.Value <- previous


/// Members for scripts evaluated by Caddis.
/// Caddis.Core.dll is referenced by every FSI session, so scripts can use e.g. Caddis.Script.OutDir without an #r
[<AbstractClass; Sealed>]
type Script =

    /// The folder for files of the current run.
    /// Files saved here are returned with the result of the run.
    static member OutDir: string = ScriptState.current.Value |> Option.map _.run.OutDir |> Option.defaultValue ""

    /// The id of the current run, e.g. "r12".
    static member RunId: string = ScriptState.current.Value |> Option.map _.run.Id |> Option.defaultValue ""

    /// Runs a function on the UI thread of the host app and returns its result.
    /// Use it in background mode for code that must run on the UI thread, e.g. showing a window or redrawing views.
    /// In UI-thread mode the function just runs directly.
    static member OnUi(f: unit -> 'T) : 'T =
        match ScriptState.current.Value with
        | Some context -> context.invokeOnUi (Func<obj>(fun () -> box (f ()))) |> unbox<'T>
        | None -> invalidOp "Caddis.Script.OnUi can only be used during a Caddis run."
