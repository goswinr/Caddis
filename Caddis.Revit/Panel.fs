namespace Caddis.Revit

open System
open System.Diagnostics
open System.Reflection
open System.Text
open System.Windows
open System.Windows.Controls
open System.Windows.Data
open System.Windows.Input
open System.Windows.Media
open System.Windows.Media.Imaging
open System.Windows.Threading
open Caddis

/// The images embedded in Caddis.Revit.dll.
module internal Images =

    /// The Caddis logo, decoded at the given width.
    let logo (size: int) : ImageSource =
        use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream "Caddis.Revit.Resources.Caddis.png"
        let img = BitmapImage()
        img.BeginInit()
        img.StreamSource <- stream
        img.DecodePixelWidth <- size
        img.CacheOption <- BitmapCacheOption.OnLoad
        img.EndInit()
        img.Freeze()
        img


/// A row in the list of runs. WPF binds the columns to its properties.
type RunRow = {
    Id: string
    Status: string
    Time: string
    Code: string
    }


/// An optional dockable pane to control the Caddis server and see the recent runs with their output and code, like the panel in Rhino.
/// The ribbon button 'Panel' shows and hides it. Its controls don't use the Revit API, so they work outside of Revit's API context,
/// and show messages in the pane instead of dialogs.
type CaddisPanel() as this =
    inherit UserControl()

    static let repoUrl = "https://github.com/goswinr/Caddis"

    /// Tooltips don't wrap, so they are given line by line.
    static let tip (lines: string list) = String.concat "\n" lines

    static let startTip =
        tip [
            "Start the server, so that AI agents and scripts can run F# code in this Revit."
            "It listens on localhost only, unless 'Listen on the network' is on."
        ]

    static let stopTip = tip [ "Stop the server. Connected AI agents can't reach Revit until it is started again." ]

    static let modeTip =
        tip [
            "UI thread: scripts run in Revit's API context on its UI thread, the Revit API can be used directly,"
            "and each run is one undo step that is rolled back if it fails. Revit is blocked while a script runs,"
            "and a run can't be cancelled."
            ""
            "Background thread: Revit stays usable, runs can be cancelled and time out."
            "Every Revit API call must be wrapped in Caddis.Revit.Script.OnUi(fun () -> ...) or Script.Transact."
            ""
            "Changing it starts a new F# session, values defined by earlier runs are lost."
        ]

    static let mono = FontFamily "Consolas"

    let button (text: string) (toolTip: string) =
        Button(Content = text, ToolTip = toolTip, Padding = Thickness(8.0, 2.0, 8.0, 2.0), Margin = Thickness(0.0, 0.0, 4.0, 4.0))

    let checkBox (text: string) (toolTip: string) =
        CheckBox(Content = text, ToolTip = toolTip, Margin = Thickness(0.0, 0.0, 12.0, 4.0), VerticalAlignment = VerticalAlignment.Center)

    let logo =
        Image(Source = Images.logo 80, Width = 40.0, Height = 40.0, Cursor = Cursors.Hand, ToolTip = $"Caddis on GitHub: {repoUrl}", Margin = Thickness(0.0, 0.0, 8.0, 0.0))

    let status = TextBlock(TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center)

    /// The latest message of Caddis, e.g. that the token was copied.
    let message = TextBlock(TextWrapping = TextWrapping.Wrap, Margin = Thickness(0.0, 2.0, 0.0, 6.0))

    let startStop = button "Start" startTip

    let copyMcp =
        button
            "Copy MCP command"
            (tip [ "Copy the command that adds Caddis to Claude Code, including the token."; "Paste it into a terminal. Only while the server is running." ])

    let copyToken =
        button
            "Copy token"
            (tip [
                "Copy the access token. Every request to Caddis needs it in the header"
                "'Authorization: Bearer <token>'. Keep it private: it allows running any code on this computer."
            ])

    let mode = ComboBox(ToolTip = modeTip, MinWidth = 130.0, Margin = Thickness(0.0, 0.0, 12.0, 4.0))

    let cancel =
        button
            "Cancel run"
            (tip [
                "Cancel the current run. Only runs in background mode can be cancelled."
                "If a run doesn't stop within 5 s, its thread is abandoned and the F# session is reset."
            ])

    let reset =
        button "Reset FSI session" (tip [ "Start a new F# session. All values, functions and types defined by earlier runs are lost."; "Use it if the session is in a bad state." ])

    let autoStart = checkBox "Start with Revit" "Start the server when Revit starts."

    let lan =
        checkBox
            "Listen on the network (LAN)"
            (tip [
                "Make the server reachable from other computers, not only from this one."
                "The token is sent unencrypted over the network, so only use it on a network you trust."
                "Takes effect when the server is started again. On Windows this needs a one-time"
                "'netsh http add urlacl' as administrator, Caddis logs the exact command."
            ])

    let runs = ListView()

    let textView () =
        TextBox(
            IsReadOnly = true,
            FontFamily = mono,
            FontSize = 12.0,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        )

    let output = textView ()
    let codeView = textView ()
    let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 1.0)

    /// The ids and states of the shown runs, to only update the list when something changed.
    let mutable shownRuns = ""

    /// True while the controls are updated from the settings, so that their change events are ignored.
    let mutable updating = false

    let firstCodeLine (code: string) =
        code.Split('\n')
        |> Array.map _.Trim()
        |> Array.tryFind (fun l -> l <> "" && not (l.StartsWith "//"))
        |> Option.defaultValue ""

    let runText (r: Run) =
        let o = r.Outcome
        let sb = StringBuilder()
        let add (title: string) (text: string) =
            if not (String.IsNullOrWhiteSpace text) then
                sb.AppendLine($"--- {title} ---").AppendLine(text.TrimEnd()) |> ignore
        let where = if r.Kind = FSharpCode then r.ScriptPath else r.Code
        sb.AppendLine($"{r.Id}  {RunStatus.toString r.Status}  {int r.Duration.TotalMilliseconds} ms  {where}") |> ignore
        o.message |> Option.iter (add "message")
        add "notes" (r.Notes |> String.concat Environment.NewLine)
        o.value |> Option.iter (fun (t, v) -> add $"value: {t}" v)
        r.Commands
        |> Option.iter (fun c ->
            let ids (name: string) (a: string[]) = if a.Length = 0 then "" else $"{name} ({a.Length}): " + String.concat ", " a
            let changed =
                match c.changedGroups with
                | [] -> ids "changed" c.changed
                | groups -> $"changed ({c.changed.Length}): " + (groups |> List.map (fun (g, n) -> $"{n} {g}") |> String.concat ", ")
            [ ids "created" c.created; (if c.changed.Length = 0 then "" else changed); ids "deleted" c.deleted ]
            |> List.filter ((<>) "")
            |> String.concat Environment.NewLine
            |> add "elements")
        add "stdout" r.StdOut.Text
        add "stderr" r.StdErr.Text
        add "F# Interactive" r.FsiOut.Text
        o.diagnostics
        |> Array.map (fun d -> $"{d.severity} FS{d.errorNumber} line {d.startLine}, column {d.startColumn}: {d.message}")
        |> String.concat Environment.NewLine
        |> add "diagnostics"
        o.exn |> Option.iter (fun e -> add "exception" e.text)
        r.Files |> Array.map _.path |> String.concat Environment.NewLine |> add "files"
        sb.ToString()

    let showDetails () =
        let selectedRun =
            match runs.SelectedItem, App.server with
            | (:? RunRow as row), Some s -> s.Service.Runs.TryGet row.Id
            | _ -> None
        match selectedRun with
        | Some r ->
            output.Text <- runText r
            codeView.Text <- r.Code
        | None ->
            output.Text <- ""
            codeView.Text <- ""

    let refreshRuns (s: CaddisServer) =
        let all = s.Service.Runs.All |> Array.rev
        let signature = all |> Array.map (fun r -> r.Id + RunStatus.toString r.Status) |> String.concat ","
        if signature <> shownRuns then
            shownRuns <- signature
            let selectedId =
                match runs.SelectedItem with
                | :? RunRow as row -> row.Id
                | _ -> null
            let rows =
                all
                |> Array.map (fun r -> {
                    Id = r.Id
                    Status = RunStatus.toString r.Status
                    Time = $"{int r.Duration.TotalMilliseconds} ms"
                    Code = firstCodeLine r.Code
                })
            runs.ItemsSource <- rows
            runs.SelectedItem <- rows |> Array.tryFind (fun row -> row.Id = selectedId) |> Option.map box |> Option.toObj
            showDetails ()

    let refresh () =
        match App.server with
        | None -> status.Text <- "Caddis is not loaded."
        | Some s ->
            let svc = s.Service
            updating <- true
            try
                let running =
                    match svc.Fsi.Current with
                    | Some r -> $", running {r.Id}"
                    | None -> ""
                status.Text <-
                    (if s.IsRunning then $"Listening on {s.Url}" else "Stopped")
                    + $"{Environment.NewLine}F# session: {FsiState.toString svc.Fsi.State}{running}"
                startStop.Content <- if s.IsRunning then "Stop" else "Start"
                startStop.ToolTip <- if s.IsRunning then stopTip else startTip
                copyMcp.IsEnabled <- s.IsRunning
                mode.SelectedIndex <- (match svc.Fsi.Mode with UiThread -> 0 | Background -> 1)
                autoStart.IsChecked <- Nullable svc.Settings.autoStart
                lan.IsChecked <- Nullable svc.Settings.listenOnLan
                cancel.IsEnabled <- svc.Fsi.Current |> Option.exists (fun r -> r.Mode = Background)
                message.Text <- RevitLog.recentLines () |> List.tryLast |> Option.defaultValue ""
                refreshRuns s
                try App.stateChanged () with _ -> ()
            finally
                updating <- false

    do
        PanelTheme.initialize this
        message.SetResourceReference(TextBlock.ForegroundProperty, "Caddis.Muted")
        mode.Items.Add "UI thread" |> ignore
        mode.Items.Add "Background thread" |> ignore

        let gridView = GridView()
        for header, path, width in [ "Run", "Id", 50.0; "Status", "Status", 90.0; "Time", "Time", 70.0; "Code", "Code", 400.0 ] do
            gridView.Columns.Add(GridViewColumn(Header = header, DisplayMemberBinding = Binding path, Width = width))
        runs.View <- gridView

        let row (items: UIElement list) =
            let p = WrapPanel(Orientation = Orientation.Horizontal)
            for i in items do
                p.Children.Add i |> ignore
            p

        let header = DockPanel(Margin = Thickness(0.0, 0.0, 0.0, 6.0))
        DockPanel.SetDock(logo, Dock.Left)
        header.Children.Add logo |> ignore
        header.Children.Add status |> ignore

        let top = StackPanel()
        top.Children.Add header |> ignore
        top.Children.Add(row [ startStop; copyMcp; copyToken ]) |> ignore
        top.Children.Add(row [ TextBlock(Text = "Run scripts on:", ToolTip = modeTip, VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(0.0, 0.0, 6.0, 4.0)); mode; cancel; reset ])
        |> ignore
        top.Children.Add(row [ autoStart; lan ]) |> ignore
        top.Children.Add message |> ignore

        let tabs = TabControl()
        tabs.Items.Add(TabItem(Header = "Output", Content = output)) |> ignore
        tabs.Items.Add(TabItem(Header = "Code", Content = codeView)) |> ignore

        // the run list and the details, with a draggable divider between them
        let root = Grid(Margin = Thickness 6.0)
        for height in [ GridLength.Auto; GridLength(1.0, GridUnitType.Star); GridLength.Auto; GridLength(1.0, GridUnitType.Star) ] do
            root.RowDefinitions.Add(RowDefinition(Height = height))
        let place (e: UIElement) (r: int) =
            Grid.SetRow(e, r)
            root.Children.Add e |> ignore
        place top 0
        place runs 1
        place (GridSplitter(Height = 6.0, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows)) 2
        place tabs 3
        this.Content <- root

        startStop.Click.Add(fun _ ->
            match App.server with
            | Some s when s.IsRunning -> App.stop ()
            | _ -> App.start false
            refresh ())
        logo.MouseUp.Add(fun e ->
            if e.ChangedButton = MouseButton.Left then
                e.Handled <- true
                try Process.Start(ProcessStartInfo(repoUrl, UseShellExecute = true)) |> ignore with _ -> ())
        copyMcp.Click.Add(fun _ -> App.copyMcpCommand (); refresh ())
        copyToken.Click.Add(fun _ -> App.copyToken (); refresh ())
        mode.SelectionChanged.Add(fun _ -> if not updating then App.setMode (if mode.SelectedIndex = 1 then Background else UiThread))
        autoStart.Click.Add(fun _ -> App.setAutoStart (autoStart.IsChecked.GetValueOrDefault()))
        lan.Click.Add(fun _ -> App.setListenOnLan (lan.IsChecked.GetValueOrDefault()); refresh ())
        cancel.Click.Add(fun _ -> App.cancel (); refresh ())
        reset.Click.Add(fun _ -> App.reset (); refresh ())
        runs.SelectionChanged.Add(fun _ -> showDetails ())
        // only while the pane is shown
        timer.Tick.Add(fun _ -> if this.IsVisible then refresh ())
        this.Loaded.Add(fun _ ->
            refresh ()
            timer.Start())
        this.Unloaded.Add(fun _ -> timer.Stop())

    /// The id of the pane in Revit. Don't change it: Revit stores the pane's position and visibility by it.
    static member PaneId = Autodesk.Revit.UI.DockablePaneId(Guid "7b1c58a4-3e0f-4b8e-9a5d-2f6c1d9e8a31")


/// Gives Revit the content of the Caddis pane. Hidden until the ribbon button 'Panel' shows it, then Revit remembers its state.
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
