namespace Caddis.Rhino

open System
open System.Runtime.InteropServices
open System.Text
open Eto.Drawing
open Eto.Forms
open Caddis

/// A row in the list of runs.
type RunRow = {
    Id: string
    Status: string
    Duration: string
    FirstLine: string
    }

/// A dockable panel to control the Caddis server and see the recent runs and their output.
[<Guid("f3b1a864-f96c-4fcc-b7f4-325d0dab262b")>]
type CaddisPanel() as this =
    inherit Panel()

    let logo =
        new ImageView(
            Image = Bitmap.FromResource("Caddis.Rhino.Resources.Caddis.png", typeof<CaddisPanel>.Assembly),
            Size = Size(40, 40),
            ToolTip = "Caddis"
        )
    let status = new Label(Wrap = WrapMode.Word)
    let startStop = new Button(Text = "Start")
    let copyMcp = new Button(Text = "Copy MCP command")
    let copyToken = new Button(Text = "Copy token")
    let mode = new DropDown()
    let autoStart = new CheckBox(Text = "Start with Rhino")
    let echo = new CheckBox(Text = "Echo output to the command line")
    let lan = new CheckBox(Text = "Listen on the network (LAN)")
    let cancel = new Button(Text = "Cancel run")
    let reset = new Button(Text = "Reset session")
    let grid = new GridView(ShowHeader = true, AllowMultipleSelection = false)
    let details = new TextArea(ReadOnly = true, Wrap = false, Font = Fonts.Monospace 9.0f)
    let codeView = new TextArea(ReadOnly = true, Wrap = false, Font = Fonts.Monospace 9.0f)
    let timer = new UITimer(Interval = 1.0)

    /// The ids and states of the shown runs, to only update the grid when something changed.
    let mutable shownRuns = ""

    /// True while the controls are updated from the settings, so that their change events are ignored.
    let mutable updating = false

    let column (header: string) (get: RunRow -> string) =
        new GridColumn(
            HeaderText = header,
            DataCell = new TextBoxCell(Binding = (Binding.Delegate<RunRow, string>(Func<RunRow, string> get) :> IIndirectBinding<string>))
        )

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
        sb.AppendLine($"{r.Id}  {RunStatus.toString r.Status}  {int r.Duration.TotalMilliseconds} ms  {r.ScriptPath}") |> ignore
        o.message |> Option.iter (add "message")
        o.value |> Option.iter (fun (t, v) -> add $"value: {t}" v)
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
            match grid.SelectedItem, App.server with
            | (:? RunRow as row), Some s -> s.Service.Runs.TryGet row.Id
            | _ -> None
        match selectedRun with
        | Some r ->
            details.Text <- runText r
            codeView.Text <- r.Code
        | None ->
            details.Text <- ""
            codeView.Text <- ""

    let refreshRuns (s: CaddisServer) =
        let runs = s.Service.Runs.All |> Array.rev
        let signature = runs |> Array.map (fun r -> r.Id + RunStatus.toString r.Status) |> String.concat ","
        if signature <> shownRuns then
            shownRuns <- signature
            let selectedId =
                match grid.SelectedItem with
                | :? RunRow as row -> row.Id
                | _ -> null
            let rows =
                runs
                |> Array.map (fun r -> {
                    Id = r.Id
                    Status = RunStatus.toString r.Status
                    Duration = $"{int r.Duration.TotalMilliseconds} ms"
                    FirstLine = firstCodeLine r.Code
                })
            grid.DataStore <- (rows |> Array.map box :> seq<obj>)
            match rows |> Array.tryFindIndex (fun row -> row.Id = selectedId) with
            | Some i -> grid.SelectedRow <- i
            | None -> ()
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
                startStop.Text <- if s.IsRunning then "Stop" else "Start"
                copyMcp.Enabled <- s.IsRunning
                mode.SelectedIndex <- (match svc.Fsi.Mode with UiThread -> 0 | Background -> 1)
                autoStart.Checked <- Nullable svc.Settings.autoStart
                echo.Checked <- Nullable svc.Settings.echoOutput
                lan.Checked <- Nullable svc.Settings.listenOnLan
                cancel.Enabled <- svc.Fsi.Current |> Option.exists (fun r -> r.Mode = Background)
                refreshRuns s
            finally
                updating <- false

    do
        mode.Items.Add "UI thread"
        mode.Items.Add "Background thread"
        grid.Columns.Add(column "Run" _.Id)
        grid.Columns.Add(column "Status" _.Status)
        grid.Columns.Add(column "Time" _.Duration)
        grid.Columns.Add(column "Code" _.FirstLine)

        let detailPage (title: string) (view: Control) =
            let content = new DynamicLayout(Padding = Padding 0)
            content.Add(view, xscale = true, yscale = true) |> ignore
            new TabPage(Text = title, Padding = Padding 0, Content = content)

        let detailTabs = new TabControl()
        detailTabs.Pages.Add(detailPage "Output" details)
        detailTabs.Pages.Add(detailPage "Code" codeView)

        let runSplitter =
            new Splitter(
                Orientation = Orientation.Vertical,
                FixedPanel = SplitterFixedPanel.None,
                Panel1 = grid,
                Panel2 = detailTabs,
                RelativePosition = 0.5,
                SplitterWidth = 6
            )

        let controlRow (controls: Control[]) (checkbox: CheckBox) =
            let actions = new DynamicLayout(DefaultSpacing = Size(4, 4))
            actions.AddSeparateRow(controls) |> ignore
            new TableRow(
                new TableCell(actions, false),
                new TableCell(checkbox, false),
                new TableCell(null, true)
            )

        let controls =
            new TableLayout(
                [|
                    controlRow [| startStop; copyMcp; copyToken; null |] autoStart
                    controlRow [| new Label(Text = "Run scripts on:"); mode; null |] echo
                    controlRow [| cancel; reset; null |] lan
                |],
                Spacing = Size(8, 4),
                Padding = Padding 0
            )

        let layout = new DynamicLayout(DefaultSpacing = Size(4, 4), Padding = Padding 0)
        let header = new DynamicLayout(DefaultSpacing = Size(8, 4))
        header.BeginHorizontal() |> ignore
        header.Add(logo, xscale = false) |> ignore
        header.Add(status, xscale = true) |> ignore
        header.EndHorizontal() |> ignore
        layout.AddRow(header) |> ignore
        layout.AddRow(controls) |> ignore
        layout.Add(runSplitter, xscale = true, yscale = true) |> ignore
        this.Content <- layout

        startStop.Click.Add(fun _ ->
            match App.server with
            | Some s when s.IsRunning -> App.stop ()
            | _ -> App.start ()
            refresh ())
        copyMcp.Click.Add(fun _ -> App.copyMcpCommand ())
        copyToken.Click.Add(fun _ -> App.copyToken ())
        mode.SelectedIndexChanged.Add(fun _ -> if not updating then App.setMode (if mode.SelectedIndex = 1 then Background else UiThread))
        autoStart.CheckedChanged.Add(fun _ -> if not updating then App.setAutoStart (autoStart.Checked.GetValueOrDefault()))
        echo.CheckedChanged.Add(fun _ -> if not updating then App.setEcho (echo.Checked.GetValueOrDefault()))
        lan.CheckedChanged.Add(fun _ -> if not updating then App.setListenOnLan (lan.Checked.GetValueOrDefault()))
        cancel.Click.Add(fun _ -> App.cancel ())
        reset.Click.Add(fun _ -> App.reset ())
        grid.SelectionChanged.Add(fun _ -> showDetails ())
        timer.Elapsed.Add(fun _ -> refresh ())
        this.LoadComplete.Add(fun _ ->
            refresh ()
            timer.Start())
        this.UnLoad.Add(fun _ -> timer.Stop())
