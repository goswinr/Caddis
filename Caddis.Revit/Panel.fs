namespace Caddis.Revit

open System
open System.Diagnostics
open System.Windows
open System.Windows.Controls
open System.Windows.Threading
open Caddis.UI

/// Revit actions and presentation settings for the shared panel.
module private PanelServices =

    /// Windows' save dialog, owned by Revit's active window.
    let pickScriptPath (title: string) (fileName: string) =
        let dialog = Microsoft.Win32.SaveFileDialog(Title = title, FileName = fileName, Filter = "F# script (*.fsx)|*.fsx", DefaultExt = ".fsx")
        if dialog.ShowDialog().GetValueOrDefault() then Some dialog.FileName else None

    let host: PanelHost = {
        Name = "Revit"
        ModeTip = String.concat "\n" [
            "UI thread: scripts run in Revit's API context on its UI thread, the Revit API can be used directly,"
            "and each run is one undo step that is rolled back if it fails. Revit is blocked while a script runs,"
            "and a run can't be cancelled."
            ""
            "Background thread: Revit stays usable, runs can be cancelled and time out."
            "Every Revit API call must be wrapped in Caddis.Revit.Script.OnUi(fun () -> ...) or Script.Transact."
            ""
            "Changing it starts a new F# session, values defined by earlier runs are lost."
        ]
        CommandLineTitle = None
        ChangeNoun = "elements"
        Server = fun () -> App.server
        Start = fun () -> App.start false
        Stop = App.stop
        CopyMcpCommand = App.copyMcpCommand
        CopyToken = App.copyToken
        SetMode = App.setMode
        SetAutoStart = App.setAutoStart
        SetListenOnLan = App.setListenOnLan
        SetEcho = None
        SetCancelWaitingCommands = None
        Cancel = App.cancel
        Reset = App.reset
        OpenUrl = fun url ->
            try Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore with _ -> ()
        PickScriptPath = pickScriptPath
        Message = Some(fun () -> RevitLog.recentLines () |> List.tryLast |> Option.defaultValue "")
        Refreshed = fun () -> try App.stateChanged () with _ -> ()
        Palette = PanelTheme.palette
        SubscribeTheme = PanelTheme.subscribe
    }

/// The WPF shell Revit docks. The controls, styles and code viewer live in Caddis.UI.
type CaddisPanel() as this =
    inherit UserControl()

    let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 1.0)

    do
        try
            AvaloniaHost.EnsureInitialized()
            let view = PanelView(PanelServices.host)
            let host = new AvaloniaHost(view)
            this.Content <- host
            // Fill the docking container: an outer WPF margin exposes its black native background.
            host.SizeChanged.Add(fun _ -> host.SyncDpi())
            timer.Tick.Add(fun _ ->
                if this.IsVisible then
                    view.Refresh()
                    host.SyncDpi())
            this.Loaded.Add(fun _ ->
                view.Refresh()
                timer.Start())
            this.Unloaded.Add(fun _ -> timer.Stop())
        with e ->
            RevitLog.write $"Caddis: the panel could not start Avalonia: {e}"
            this.Content <- TextBlock(
                Text = $"The Caddis panel could not be shown: {e.Message}{Environment.NewLine}Use the Caddis ribbon buttons instead.",
                TextWrapping = TextWrapping.Wrap)

    /// Revit stores the pane's position and visibility by this id. Keep it across UI changes.
    static member PaneId = Autodesk.Revit.UI.DockablePaneId(Guid "7b1c58a4-3e0f-4b8e-9a5d-2f6c1d9e8a31")

/// Registers the same optional pane and default docking position as the previous WPF view.
type CaddisPaneProvider() =
    interface Autodesk.Revit.UI.IDockablePaneProvider with
        member _.SetupDockablePane(data: Autodesk.Revit.UI.DockablePaneProviderData) =
            data.FrameworkElement <- CaddisPanel()
            data.VisibleByDefault <- false
            data.InitialState <-
                new Autodesk.Revit.UI.DockablePaneState(
                    DockPosition = Autodesk.Revit.UI.DockPosition.Tabbed,
                    TabBehind = Autodesk.Revit.UI.DockablePanes.BuiltInDockablePanes.ProjectBrowser
                )
