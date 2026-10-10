namespace Caddis.Revit

open System
open Autodesk.Revit.DB
open Autodesk.Revit.UI

/// Members for scripts evaluated by Caddis in Revit. Caddis.Revit.dll is referenced by every F# session.
/// Includes the members of Caddis.Script, so that 'open Caddis.Revit' is enough.
[<AbstractClass; Sealed>]
type Script =

    static member private NotInApiContext =
        "The Revit API can only be used in Revit's API context: in 'ui' mode directly, in background mode inside Caddis.Revit.Script.OnUi(fun () -> ...) or Script.Transact."

    /// The folder for files of the current run. Files saved here are returned with the result of the run.
    static member OutDir: string = global.Caddis.Script.OutDir

    /// The id of the current run, e.g. "r12".
    static member RunId: string = global.Caddis.Script.RunId

    /// Runs a function in Revit's API context on the UI thread and returns its result.
    /// Needed in background mode for every call of the Revit API. In 'ui' mode the function just runs directly.
    static member OnUi(f: unit -> 'T) : 'T = global.Caddis.Script.OnUi f

    /// The UIApplication of Revit. Only in Revit's API context, see OnUi.
    static member UiApp: UIApplication =
        if not (ApiContext.isActive ()) then raise (InvalidOperationException Script.NotInApiContext)
        match ApiContext.uiApplication () with
        | null -> raise (InvalidOperationException "Revit's UIApplication is not known yet.")
        | app -> app

    /// The active UIDocument, e.g. for the selection. Only in Revit's API context, see OnUi.
    static member UiDoc: UIDocument =
        match Script.UiApp.ActiveUIDocument with
        | null -> raise (InvalidOperationException "No document is open in Revit.")
        | d -> d

    /// The active Document. Only in Revit's API context, see OnUi.
    static member Doc: Document = Script.UiDoc.Document

    /// Runs a function in a transaction on the active document and commits it, also in background mode.
    /// Raises an exception if Revit did not commit it, e.g. because of an error that Revit reported for the changes.
    static member Transact(name: string, f: Document -> 'T) : 'T =
        Script.OnUi(fun () ->
            let doc = Script.Doc
            use t = new Transaction(doc, name)
            match t.Start() with
            | TransactionStatus.Started -> ()
            | s -> failwith $"The transaction '{name}' did not start, its status is '{s}'."
            try
                let result = f doc
                match t.Commit() with
                | TransactionStatus.Committed -> result
                | s -> failwith $"Revit did not commit the transaction '{name}', its status is '{s}'. The notes of the run show why."
            finally
                if t.GetStatus() = TransactionStatus.Started then t.RollBack() |> ignore)

    /// Runs a function in a transaction named 'Caddis' on the active document and commits it, also in background mode.
    static member Transact(f: Document -> 'T) : 'T = Script.Transact("Caddis", f)
