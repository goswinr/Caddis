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

    static let repoUrl = "https://github.com/goswinr/Caddis"

    let logo =
        new ImageView(
            Image = Bitmap.FromResource("Caddis.Rhino.Resources.Caddis.png", typeof<CaddisPanel>.Assembly),
            Size = Size(40, 40),
            Cursor = Cursors.Pointer,
            ToolTip = $"Caddis on GitHub: {repoUrl}"
        )
    /// Tooltips don't wrap, so they are given line by line.
    static let tip (lines: string list) = String.concat "\n" lines

    static let startTip =
        tip [
            "Start the server, so that AI agents and scripts can run F# code and Rhino commands in this Rhino."
            "It listens on localhost only, unless 'Listen on the network' is on."
        ]

    static let stopTip = tip [ "Stop the server. Connected AI agents can't reach Rhino until it is started again." ]

    static let modeTip =
        tip [
            "UI thread: scripts run on Rhino's UI thread. Safest for RhinoCommon,"
            "but Rhino is blocked while a script runs, and a run can't be cancelled."
            ""
            "Background thread: Rhino stays usable, runs can be cancelled and time out."
            "Code that uses the Rhino UI must be wrapped in Caddis.Script.OnUi(fun () -> ...)."
            ""
            "Changing it starts a new F# session, values defined by earlier runs are lost."
        ]

    let status = new Label(Wrap = WrapMode.Word)
    let startStop = new Button(Text = "Start", ToolTip = startTip)
    let copyMcp =
        new Button(
            Text = "Copy MCP command",
            ToolTip =
                tip [
                    "Copy the command that adds Caddis to Claude Code, including the token."
                    "Paste it into a terminal. Only while the server is running."
                ]
        )
    let copyToken =
        new Button(
            Text = "Copy token",
            ToolTip =
                tip [
                    "Copy the access token. Every request to Caddis needs it in the header"
                    "'Authorization: Bearer <token>'. Keep it private: it allows running any code on this computer."
                ]
        )
    let mode = new DropDown(ToolTip = modeTip)
    let autoStart = new CheckBox(Text = "Start with Rhino", ToolTip = "Start the server when Rhino starts.")
    let echo =
        new CheckBox(
            Text = "Echo output to the command line",
            ToolTip = tip [ "Also print the output of runs to the Rhino command line:"; "printfn, F# Interactive's output and errors." ]
        )
    let lan =
        new CheckBox(
            Text = "Listen on the network (LAN)",
            ToolTip =
                tip [
                    "Make the server reachable from other computers, not only from this one."
                    "The token is sent unencrypted over the network, so only use it on a network you trust."
                    "Takes effect when the server is started again. On Windows this needs a one-time"
                    "'netsh http add urlacl' as administrator, Caddis prints the exact command."
                ]
        )
    let cancelWaiting =
        new CheckBox(
            Text = "Cancel commands in scripts that wait for input",
            ToolTip =
                tip [
                    "When an F# script runs a Rhino command, e.g. RhinoApp.RunScript(\"-_Loft\", true),"
                    "and the macro doesn't give all inputs, the command waits at its prompt"
                    "until someone answers it in Rhino, and the run waits with it."
                    ""
                    $"With this on, Caddis cancels such a command after {CaddisSettings.inputTimeoutMs / 1000} s. The script goes on,"
                    "and the result tells the agent at which prompt the command waited."
                    ""
                    "Commands you type in Rhino yourself are never cancelled. In background mode only"
                    "commands started via Caddis.Script.OnUi are watched. Commands run via the MCP tool"
                    "caddis_command are cancelled in any case, unless the agent asks to wait for you."
                ]
        )
    let cancel =
        new Button(
            Text = "Cancel run",
            ToolTip =
                tip [
                    "Cancel the current run. Only runs in background mode can be cancelled."
                    "If a run doesn't stop within 5 s, its thread is abandoned and the F# session is reset."
                ]
        )
    let reset =
        new Button(
            Text = "Reset session",
            ToolTip =
                tip [
                    "Start a new F# session. All values, functions and types defined by earlier runs are lost."
                    "Use it if the session is in a bad state."
                ]
        )
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
        add "notes" (r.Notes |> String.concat Environment.NewLine)
        o.value |> Option.iter (fun (t, v) -> add $"value: {t}" v)
        r.Commands
        |> Option.iter (fun c ->
            add "commands" (CommandReport.stepLines c |> String.concat Environment.NewLine)
            let ids (name: string) (a: string[]) = if a.Length = 0 then "" else $"{name} ({a.Length}): " + String.concat ", " a
            [ ids "created" c.created; ids "changed" c.changed; ids "deleted" c.deleted; c.selected |> Option.map (ids "selected") |> Option.defaultValue "" ]
            |> List.filter ((<>) "")
            |> String.concat Environment.NewLine
            |> add "objects")
        add "stdout" r.StdOut.Text
        add "Rhino command line" r.CommandLine.Text
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
                startStop.ToolTip <- if s.IsRunning then stopTip else startTip
                copyMcp.Enabled <- s.IsRunning
                mode.SelectedIndex <- (match svc.Fsi.Mode with UiThread -> 0 | Background -> 1)
                autoStart.Checked <- Nullable svc.Settings.autoStart
                echo.Checked <- Nullable svc.Settings.echoOutput
                lan.Checked <- Nullable svc.Settings.listenOnLan
                cancelWaiting.Checked <- Nullable svc.Settings.cancelWaitingCommands
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
                    controlRow [| new Label(Text = "Run scripts on:", ToolTip = modeTip); mode; null |] echo
                    controlRow [| cancel; reset; null |] lan
                    controlRow [| null |] cancelWaiting
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
        logo.MouseUp.Add(fun e ->
            if e.Buttons = MouseButtons.Primary then
                e.Handled <- true
                Application.Instance.Open repoUrl)
        copyMcp.Click.Add(fun _ -> App.copyMcpCommand ())
        copyToken.Click.Add(fun _ -> App.copyToken ())
        mode.SelectedIndexChanged.Add(fun _ -> if not updating then App.setMode (if mode.SelectedIndex = 1 then Background else UiThread))
        autoStart.CheckedChanged.Add(fun _ -> if not updating then App.setAutoStart (autoStart.Checked.GetValueOrDefault()))
        echo.CheckedChanged.Add(fun _ -> if not updating then App.setEcho (echo.Checked.GetValueOrDefault()))
        lan.CheckedChanged.Add(fun _ -> if not updating then App.setListenOnLan (lan.Checked.GetValueOrDefault()))
        cancelWaiting.CheckedChanged.Add(fun _ -> if not updating then App.setCancelWaitingCommands (cancelWaiting.Checked.GetValueOrDefault()))
        cancel.Click.Add(fun _ -> App.cancel ())
        reset.Click.Add(fun _ -> App.reset ())
        grid.SelectionChanged.Add(fun _ -> showDetails ())
        timer.Elapsed.Add(fun _ -> refresh ())
        this.LoadComplete.Add(fun _ ->
            refresh ()
            timer.Start())
        this.UnLoad.Add(fun _ -> timer.Stop())
