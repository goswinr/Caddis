namespace Caddis.Rhino

open System
open System.Runtime.InteropServices
open Eto.Forms
open Caddis.UI

/// Rhino actions and presentation settings for the shared panel.
module private PanelServices =

    /// Eto's save dialog, native on Windows and Mac, in front of Rhino's main window.
    let pickScriptPath (title: string) (fileName: string) =
        use dialog = new SaveFileDialog(Title = title, FileName = fileName)
        dialog.Filters.Add(FileFilter("F# script", ".fsx"))
        if dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) = DialogResult.Ok then Some dialog.FileName else None

    let host: PanelHost = {
        Name = "Rhino"
        ModeTip = String.concat "\n" [
            "UI thread: scripts run on Rhino's UI thread. Safest for RhinoCommon,"
            "but Rhino is blocked while a script runs, and a run can't be cancelled."
            ""
            "Background thread: Rhino stays usable, runs can be cancelled and time out."
            "Code that uses the Rhino UI must be wrapped in Caddis.Script.OnUi(fun () -> ...)."
            ""
            "Changing it starts a new F# session, values defined by earlier runs are lost."
        ]
        CommandLineTitle = Some "Rhino command line"
        ChangeNoun = "objects"
        Server = fun () -> App.server
        Start = App.start
        Stop = App.stop
        SetClipboard = App.trySetClipboard
        SetMode = App.setMode
        SetAutoStart = App.setAutoStart
        SetEcho = Some App.setEcho
        SetCancelWaitingCommands = Some App.setCancelWaitingCommands
        Cancel = App.cancel
        Reset = App.reset
        OpenUrl = fun url -> Application.Instance.Open url
        PickScriptPath = pickScriptPath
        Message = None
        Errors = RhinoLog.errors
        Refreshed = ignore
        Version = Updates.status
        Update = Updates.notice
        VersionTip = String.concat "\n" [
            "When Rhino starts, Caddis checks its releases on Yak, the package server of Rhino."
            "Rhino's Package Manager installs and updates Caddis, search for Caddis there."
            "Rhino loads an update when it restarts."
        ]
        Palette = PanelTheme.palette
        SubscribeTheme = PanelTheme.subscribe
    }

/// A dockable panel to control the Caddis server and see the recent runs and their output.
/// Rhino docks Eto panels, its content is the Avalonia PanelView, which follows Rhino's light, dark or custom theme.
/// Rhino keeps the panel's place in its layout by this id, and Caddis too, see PanelPlacement.
[<Guid(PanelPlacement.PanelId)>]
type CaddisPanel() as this =
    inherit Panel()

    let timer = new UITimer(Interval = 1.0)

    do
        try
            RhinoAvaloniaSetup.ensure ()
            let view = PanelView(PanelServices.host)
            let host = new AvaloniaHost(view)
            this.Content <- host
            PanelPlacement.panelHandle <- fun () -> host.Handle
            host.SizeChanged.Add(fun _ -> host.SyncDpi())
            timer.Elapsed.Add(fun _ ->
                view.Refresh()
                host.SyncDpi()
                PanelPlacement.shown ())
            this.LoadComplete.Add(fun _ ->
                view.Refresh()
                timer.Start())
            this.UnLoad.Add(fun _ -> timer.Stop())
        with e ->
            RhinoLog.error $"Caddis: the panel could not start Avalonia: {e}"
            this.Content <-
                new Label(
                    Text =
                        $"The Caddis panel could not be shown: {e.Message}{Environment.NewLine}Use the command 'Caddis' instead. When it starts the server, it prints the commands to add Caddis to Claude Code and Codex.",
                    Wrap = WrapMode.Word
                )

    // Rhino calls PanelHidden when the user closes the panel, and PanelClosing when Rhino exits.
    interface Rhino.UI.IPanel with
        member _.PanelShown(_, _) = ()
        member _.PanelHidden(_, _) = PanelPlacement.hiding ()
        member _.PanelClosing(_, _) = PanelPlacement.hiding ()
