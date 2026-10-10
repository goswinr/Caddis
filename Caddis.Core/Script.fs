namespace Caddis

open System

module internal ScriptState =

    let mutable runId = ""

    let mutable outDir = ""

    /// Set by FsiHost to the host's InvokeOnUi
    let mutable invokeOnUi: Func<obj> -> obj = fun f -> f.Invoke()


/// Members for scripts evaluated by Caddis.
/// Caddis.Core.dll is referenced by every FSI session, so scripts can use e.g. Caddis.Script.OutDir without an #r
[<AbstractClass; Sealed>]
type Script =

    /// The folder for files of the current run.
    /// Files saved here are returned with the result of the run.
    static member OutDir: string = ScriptState.outDir

    /// The id of the current run, e.g. "r12".
    static member RunId: string = ScriptState.runId

    /// Runs a function on the UI thread of the host app and returns its result.
    /// Use it in background mode for code that must run on the UI thread, e.g. showing a window or redrawing views.
    /// In UI-thread mode the function just runs directly.
    static member OnUi(f: unit -> 'T) : 'T =
        ScriptState.invokeOnUi (Func<obj>(fun () -> box (f ()))) |> unbox<'T>
