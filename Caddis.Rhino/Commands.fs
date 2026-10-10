namespace Caddis.Rhino

open Rhino
open Rhino.Input
open Rhino.Input.Custom
open Caddis

/// The 'Caddis' command: start and stop the server, open the panel, change settings.
/// Scriptable, e.g. '-Caddis Start'.
type CaddisCommand() =
    inherit Commands.Command()

    override _.EnglishName = "Caddis"

    override _.RunCommand(_doc, _mode) =
        match App.server with
        | None ->
            RhinoApp.WriteLine "Caddis is not loaded."
            Commands.Result.Failure
        | Some server ->
            RhinoApp.WriteLine(App.info server)
            let settings = server.Service.Settings
            use go = new GetOption()
            go.SetCommandPrompt "Caddis"
            go.AcceptNothing true
            let mutable autoStart = new OptionToggle(settings.autoStart, "Off", "On")
            let mutable echo = new OptionToggle(settings.echoOutput, "Off", "On")
            let mutable cancelWaiting = new OptionToggle(settings.cancelWaitingCommands, "Off", "On")
            let mutable lan = new OptionToggle(settings.listenOnLan, "Off", "On")
            let iStartStop = go.AddOption(if server.IsRunning then "Stop" else "Start")
            let iPanel = go.AddOption "Panel"
            let iCopy = go.AddOption "CopyMcpCommand"
            let iMode = go.AddOptionList("Mode", [| "UIThread"; "Background" |], (match server.Service.Fsi.Mode with UiThread -> 0 | Background -> 1))
            let iCancel = go.AddOption "Cancel"
            let iReset = go.AddOption "Reset"
            let iAutoStart = go.AddOptionToggle("AutoStart", &autoStart)
            let iEcho = go.AddOptionToggle("EchoOutput", &echo)
            let iCancelWaiting = go.AddOptionToggle("CancelWaitingCommands", &cancelWaiting)
            let iLan = go.AddOptionToggle("ListenOnLan", &lan)
            let iNewToken = go.AddOption "NewToken"

            // the toggles keep the prompt open, all other options end the command
            let mutable result = None
            while result.IsNone do
                match go.Get() with
                | GetResult.Option ->
                    let i = go.OptionIndex()
                    if i = iAutoStart then App.setAutoStart autoStart.CurrentValue
                    elif i = iEcho then App.setEcho echo.CurrentValue
                    elif i = iCancelWaiting then App.setCancelWaitingCommands cancelWaiting.CurrentValue
                    elif i = iLan then App.setListenOnLan lan.CurrentValue
                    else
                        if i = iStartStop then (if server.IsRunning then App.stop () else App.start ())
                        elif i = iPanel then Rhino.UI.Panels.OpenPanel typeof<CaddisPanel>.GUID
                        elif i = iCopy then App.copyMcpCommand ()
                        elif i = iMode then App.setMode (if go.Option().CurrentListOptionIndex = 1 then Background else UiThread)
                        elif i = iCancel then App.cancel ()
                        elif i = iReset then App.reset ()
                        elif i = iNewToken then App.newToken ()
                        result <- Some Commands.Result.Success
                | GetResult.Nothing -> result <- Some Commands.Result.Success
                | _ -> result <- Some Commands.Result.Cancel
            result.Value
