namespace Caddis.AutoCAD

open Autodesk.AutoCAD.EditorInput
open Autodesk.AutoCAD.Runtime
open Caddis

/// The command CADDIS: start and stop the server, change the session and the settings.
/// Scriptable, e.g. '(command "CADDIS" "_Start")'.
type CaddisCommands() =

    static let onOff (b: bool) = if b then "on" else "off"

    /// Asks on which thread scripts run.
    static let askMode (ed: Editor) (current: ThreadMode) =
        let options = PromptKeywordOptions "\nRun scripts on"
        options.Keywords.Add "UIThread"
        options.Keywords.Add "Background"
        options.Keywords.Default <- (match current with UiThread -> "UIThread" | Background -> "Background")
        options.AllowNone <- true
        let r = ed.GetKeywords options
        if r.Status = PromptStatus.OK then
            App.setMode (if r.StringResult = "Background" then Background else UiThread)

    [<CommandMethod("CADDIS")>]
    member _.Caddis() =
        let ed = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument.Editor
        match App.server with
        | None -> ed.WriteMessage "\nCaddis is not loaded."
        | Some server ->
            ed.WriteMessage("\n" + App.info server)
            // the toggles keep the prompt open, all other options end the command
            let mutable finished = false
            while not finished do
                let settings = server.Service.Settings
                let options = PromptKeywordOptions $"\nCaddis, auto start {onOff settings.autoStart}, echo output {onOff settings.echoOutput}"
                options.Keywords.Add(if server.IsRunning then "Stop" else "Start")
                options.Keywords.Add "Mode"
                options.Keywords.Add "Cancel"
                options.Keywords.Add "Reset"
                options.Keywords.Add "AutoStart"
                options.Keywords.Add "EchoOutput"
                options.AllowNone <- true
                let r = ed.GetKeywords options
                if r.Status <> PromptStatus.OK then
                    finished <- true
                else
                    match r.StringResult with
                    | "AutoStart" -> App.setAutoStart (not settings.autoStart)
                    | "EchoOutput" -> App.setEcho (not settings.echoOutput)
                    | keyword ->
                        finished <- true
                        match keyword with
                        | "Start" -> App.start false
                        | "Stop" -> App.stop ()
                        | "Mode" -> askMode ed server.Service.Fsi.Mode
                        | "Cancel" -> App.cancel ()
                        | "Reset" -> App.reset ()
                        | _ -> ()
