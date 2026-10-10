namespace Caddis.Revit

open System
open System.Windows.Input
open Autodesk.Windows

/// RevitAPIUI doesn't expose checkboxes; AdWindows supplies Revit's native, theme-aware controls.
/// These commands only update Caddis settings and don't call the Revit document API.
module internal RibbonSettings =

    let mutable private autoStart: RibbonCheckBox = null
    let mutable private lan: RibbonCheckBox = null

    let update () =
        let set (control: RibbonCheckBox) value =
            if not (isNull control) && control.IsChecked <> Nullable value then
                control.IsChecked <- Nullable value
        match App.server with
        | Some s ->
            set autoStart s.Service.Settings.autoStart
            set lan s.Service.Settings.listenOnLan
        | None -> ()

    let private checkBox id text toolTip (set: bool -> unit) =
        let control = RibbonCheckBox(Id = id, Text = text, ShowText = true, ShowImage = false, ToolTip = toolTip)
        let canExecuteChanged = Event<EventHandler, EventArgs>()
        control.CommandHandler <-
            { new ICommand with
                [<CLIEvent>]
                member _.CanExecuteChanged = canExecuteChanged.Publish
                member _.CanExecute _ = App.server.IsSome
                member _.Execute _ =
                    try set (control.IsChecked.GetValueOrDefault())
                    with e -> RevitLog.write $"Caddis: {e.Message}"
                    // Setting IsChecked doesn't execute the command, so syncing never writes settings.
                    update () }
        control

    /// The panel was created through RevitAPIUI; only its settings row uses AdWindows.
    let create tabName panelTitle =
        let tab = ComponentManager.Ribbon.Tabs |> Seq.find (fun tab -> tab.Id = tabName)
        let panel = tab.Panels |> Seq.find (fun panel -> panel.Source.Title = panelTitle)
        autoStart <- checkBox "CaddisAutoStartCheckBox" "Start with Revit" "Start the server when Revit starts." App.setAutoStart
        lan <-
            checkBox
                "CaddisListenOnLanCheckBox"
                "Listen on the network (LAN)"
                "Allow connections from other computers. The token is sent unencrypted; use only on a trusted network. Stop and start the server to apply changes."
                App.setListenOnLan
        let row = RibbonRowPanel(Id = "CaddisSettings")
        row.Items.Add autoStart
        row.Items.Add(RibbonRowBreak())
        row.Items.Add lan
        panel.Source.Items.Add row
        update ()
