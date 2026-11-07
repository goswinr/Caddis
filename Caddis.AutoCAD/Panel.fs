namespace Caddis.AutoCAD

open System
open System.Diagnostics
open System.Windows
open System.Windows.Controls
open System.Windows.Threading
open Autodesk.AutoCAD.Windows
open Caddis.UI

/// AutoCAD actions and presentation settings for the shared panel.
module private PanelServices =

    /// Windows' save dialog, owned by AutoCAD's active window.
    let pickScriptPath (title: string) (fileName: string) =
        let dialog = Microsoft.Win32.SaveFileDialog(Title = title, FileName = fileName, Filter = "F# script (*.fsx)|*.fsx", DefaultExt = ".fsx")
        if dialog.ShowDialog().GetValueOrDefault() then Some dialog.FileName else None

    let host: PanelHost = {
        Name = "AutoCAD"
        ModeTip = String.concat "\n" [
            "UI thread: scripts run on AutoCAD's main thread with the active drawing locked,"
            "the AutoCAD API can be used directly, and each run that changes the drawing is one undo step."
            "AutoCAD is blocked while a script runs, and a run can't be cancelled."
            ""
            "Background thread: AutoCAD stays usable, runs can be cancelled and time out."
            "Every AutoCAD API call must be wrapped in Caddis.Script.OnUi(fun () -> ...)."
            ""
            "Changing it starts a new F# session, values defined by earlier runs are lost."
        ]
        CommandLineTitle = Some "AutoCAD command line"
        ChangeNoun = "objects"
        Server = fun () -> App.server
        // the panel shows the commands to add Caddis to the agents itself
        Start = fun () -> App.start true
        Stop = App.stop
        SetClipboard = App.trySetClipboard
        SetMode = App.setMode
        SetAutoStart = App.setAutoStart
        SetEcho = Some App.setEcho
        // AutoCAD runs no command that a script starts before the script has ended, see AutoCADHost
        SetCancelWaitingCommands = None
        Cancel = App.cancel
        Reset = App.reset
        OpenUrl = fun url ->
            try Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore with _ -> ()
        PickScriptPath = pickScriptPath
        // Caddis writes its messages to the command line
        Message = None
        Errors = AutoCADLog.errors
        Refreshed = fun () -> try App.stateChanged () with _ -> ()
        Version = Updates.status
        Update = Updates.notice
        VersionTip = String.concat "\n" [
            "When AutoCAD starts, Caddis checks its releases on GitHub and asks whether to download a newer one."
            "A downloaded release is installed when the last AutoCAD has closed."
        ]
        Palette = PanelTheme.palette
        SubscribeTheme = PanelTheme.subscribe
    }

/// The WPF content of the palette. The controls, styles and code viewer live in Caddis.UI.
type internal CaddisPanel() as this =
    inherit UserControl()

    let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 1.0)

    let mutable view: PanelView option = None

    do
        try
            AvaloniaHost.EnsureInitialized()
            let panelView = PanelView(PanelServices.host)
            view <- Some panelView
            let host = new AvaloniaHost(panelView)
            this.Content <- host
            host.SizeChanged.Add(fun _ -> host.SyncDpi())
            timer.Tick.Add(fun _ ->
                if this.IsVisible then
                    panelView.Refresh()
                    host.SyncDpi())
            this.Loaded.Add(fun _ ->
                panelView.Refresh()
                timer.Start())
            this.Unloaded.Add(fun _ -> timer.Stop())
        with e ->
            AutoCADLog.error $"Caddis: the panel could not start Avalonia: {e}"
            this.Content <-
                TextBlock(
                    Text =
                        $"The Caddis panel could not be shown: {e.Message}{Environment.NewLine}Use the Caddis ribbon tab or the command CADDIS instead. While the server runs, Info shows the commands to add Caddis to Claude Code and Codex.",
                    TextWrapping = TextWrapping.Wrap
                )

    /// The shared view, None if Avalonia could not start. For tests in AutoCAD, by reflection.
    member _.View = view

/// The palette set with the panel, created when it is first shown.
module internal Panel =

    /// AutoCAD stores the position, size and docking of the palette set by this id. Keep it across UI changes.
    let private paletteId = Guid "5c3f1f8e-2b6d-4a77-9c1e-8d4b2a6f7e15"

    let mutable private palette: PaletteSet option = None

    /// The content of the palette set, for tests in AutoCAD, by reflection.
    let mutable private content: CaddisPanel option = None

    /// True once AutoCAD has loaded a saved state of the palette set, its size and place.
    let mutable private restored = false

    let private create () =
        // With the command CADDISPANEL, which AutoCAD runs at its start to show the palette set again if it was open when AutoCAD closed.
        let ps = new PaletteSet("Caddis", "CADDISPANEL", paletteId)
        ps.Load.Add(fun _ -> restored <- true)
        ps.Style <- PaletteSetStyles.ShowAutoHideButton ||| PaletteSetStyles.ShowCloseButton ||| PaletteSetStyles.ShowPropertiesMenu
        ps.MinimumSize <- Drawing.Size(320, 400)
        ps.DockEnabled <- DockSides.Left ||| DockSides.Right
        // so that AutoCAD keeps the focus in the palette set when the mouse leaves it, e.g. while someone types in the panel's search box
        ps.KeepFocus <- true
        KeyboardFilter.install ()
        let panel = CaddisPanel()
        content <- Some panel
        ps.AddVisual("Caddis", panel, true) |> ignore
        palette <- Some ps
        ps


    /// Shows the palette set, creating it the first time.
    let show () =
        match palette with
        | Some ps -> if not ps.Visible then ps.Visible <- true
        | None ->
            let ps = create ()
            ps.Visible <- true
            // The first time, AutoCAD shows it floating at the top left in its minimum size. Its size and place are kept from then on.
            if not restored then
                ps.Size <- Drawing.Size(420, 760)
                ps.Dock <- DockSides.Right

    /// Shows or hides the palette set, for the ribbon button.
    let toggle () =
        match palette with
        | Some ps when ps.Visible -> ps.Visible <- false
        | _ -> show ()
