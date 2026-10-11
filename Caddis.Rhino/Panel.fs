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
        CopyMcpCommand = App.copyMcpCommand
        CopyToken = App.copyToken
        SetMode = App.setMode
        SetAutoStart = App.setAutoStart
        SetListenOnLan = App.setListenOnLan
        SetEcho = Some App.setEcho
        SetCancelWaitingCommands = Some App.setCancelWaitingCommands
        Cancel = App.cancel
        Reset = App.reset
        OpenUrl = fun url -> Application.Instance.Open url
        PickScriptPath = pickScriptPath
        Message = None
        Refreshed = ignore
        Palette = PanelTheme.palette
        SubscribeTheme = PanelTheme.subscribe
    }

/// A dockable panel to control the Caddis server and see the recent runs and their output.
/// Rhino docks Eto panels, its content is the Avalonia PanelView, which follows Rhino's light, dark or custom theme.
[<Guid("f3b1a864-f96c-4fcc-b7f4-325d0dab262b")>]
type CaddisPanel() as this =
    inherit Panel()

    let timer = new UITimer(Interval = 1.0)

    do
        try
            RhinoAvaloniaSetup.ensure ()
            let view = PanelView(PanelServices.host)
            let host = new AvaloniaHost(view)
            this.Content <- host
            host.SizeChanged.Add(fun _ -> host.SyncDpi())
            timer.Elapsed.Add(fun _ ->
                view.Refresh()
                host.SyncDpi())
            this.LoadComplete.Add(fun _ ->
                view.Refresh()
                timer.Start())
            this.UnLoad.Add(fun _ -> timer.Stop())
        with e ->
            RhinoLog.writeLine $"Caddis: the panel could not start Avalonia: {e}"
            this.Content <-
                new Label(
                    Text = $"The Caddis panel could not be shown: {e.Message}{Environment.NewLine}Use the command 'Caddis' instead.",
                    Wrap = WrapMode.Word
                )
