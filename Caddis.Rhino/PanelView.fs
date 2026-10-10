namespace Caddis.Rhino

open System
open System.Text
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Markup.Xaml
open Avalonia.Media.Imaging
open Avalonia.Themes.Fluent
open Caddis

/// A row in the list of runs. The list's item template binds to its properties.
type RunRow = {
    Id: string
    Status: string
    Time: string
    Code: string
    }

/// The content of the Caddis panel: the server state and settings, the recent runs, and the output and code of the selected run.
/// The layout is in PanelView.axaml and the control themes in PanelStyles.axaml, the colors come from Rhino's theme.
type PanelView() as this =
    inherit UserControl()

    static let repoUrl = "https://github.com/goswinr/Caddis"

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

    do AvaloniaXamlLoader.Load this

    let find name : 'T = this.FindControl<'T> name

    let scope: ThemeVariantScope = find "Scope"
    let logo: Border = find "Logo"
    let logoImage: Image = find "LogoImage"
    let status: TextBlock = find "Status"
    let startStop: Button = find "StartStop"
    let copyMcp: Button = find "CopyMcp"
    let copyToken: Button = find "CopyToken"
    let modeLabel: TextBlock = find "ModeLabel"
    let mode: ComboBox = find "Mode"
    let cancel: Button = find "Cancel"
    let reset: Button = find "Reset"
    let autoStart: CheckBox = find "AutoStart"
    let echo: CheckBox = find "Echo"
    let lan: CheckBox = find "Lan"
    let cancelWaiting: CheckBox = find "CancelWaiting"
    let runs: ListBox = find "Runs"
    let output: TextBox = find "Output"
    let codeView: TextBox = find "CodeView"

    /// The ids and states of the shown runs, to only update the list when something changed.
    let mutable shownRuns = ""

    /// True while the controls are updated from the settings, so that their change events are ignored.
    let mutable updating = false

    /// While attached: the subscription to changes of Rhino's theme.
    let mutable themeChanges: IDisposable option = None

    let applyTheme () = PanelTheme.apply this.Resources scope

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
                ToolTip.SetTip(startStop, (if s.IsRunning then stopTip else startTip))
                copyMcp.IsEnabled <- s.IsRunning
                mode.SelectedIndex <- (match svc.Fsi.Mode with UiThread -> 0 | Background -> 1)
                autoStart.IsChecked <- Nullable svc.Settings.autoStart
                echo.IsChecked <- Nullable svc.Settings.echoOutput
                lan.IsChecked <- Nullable svc.Settings.listenOnLan
                cancelWaiting.IsChecked <- Nullable svc.Settings.cancelWaitingCommands
                cancel.IsEnabled <- svc.Fsi.Current |> Option.exists (fun r -> r.Mode = Background)
                refreshRuns s
            finally
                updating <- false

    /// Copy and Select all, the read-only text has no Cut and Paste.
    let textMenu (box: TextBox) =
        let item (header: string) (action: unit -> unit) =
            let m = MenuItem(Header = header)
            m.Click.Add(fun _ -> action ())
            m
        box.ContextMenu <- ContextMenu(ItemsSource = [ item "Copy" box.Copy; item "Select all" box.SelectAll ])

    do
        // Another plugin started Avalonia with its own application, see AvaloniaSetup.
        if not (Avalonia.Application.Current :? PanelApp) then
            this.Styles.Insert(0, FluentTheme())
        applyTheme ()

        use stream = typeof<PanelView>.Assembly.GetManifestResourceStream "Caddis.Rhino.Resources.Caddis.png"
        logoImage.Source <- Bitmap.DecodeToWidth(stream, 80)

        ToolTip.SetTip(logo, $"Caddis on GitHub: {repoUrl}")
        ToolTip.SetTip(startStop, startTip)
        ToolTip.SetTip(
            copyMcp,
            tip [ "Copy the command that adds Caddis to Claude Code, including the token."; "Paste it into a terminal. Only while the server is running." ]
        )
        ToolTip.SetTip(
            copyToken,
            tip [
                "Copy the access token. Every request to Caddis needs it in the header"
                "'Authorization: Bearer <token>'. Keep it private: it allows running any code on this computer."
            ]
        )
        ToolTip.SetTip(modeLabel, modeTip)
        ToolTip.SetTip(mode, modeTip)
        ToolTip.SetTip(
            cancel,
            tip [
                "Cancel the current run. Only runs in background mode can be cancelled."
                "If a run doesn't stop within 5 s, its thread is abandoned and the F# session is reset."
            ]
        )
        ToolTip.SetTip(
            reset,
            tip [ "Start a new F# session. All values, functions and types defined by earlier runs are lost."; "Use it if the session is in a bad state." ]
        )
        ToolTip.SetTip(autoStart, "Start the server when Rhino starts.")
        ToolTip.SetTip(echo, tip [ "Also print the output of runs to the Rhino command line:"; "printfn, F# Interactive's output and errors." ])
        ToolTip.SetTip(
            lan,
            tip [
                "Make the server reachable from other computers, not only from this one."
                "The token is sent unencrypted over the network, so only use it on a network you trust."
                "Takes effect when the server is started again. On Windows this needs a one-time"
                "'netsh http add urlacl' as administrator, Caddis prints the exact command."
            ]
        )
        ToolTip.SetTip(
            cancelWaiting,
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
        textMenu output
        textMenu codeView

        startStop.Click.Add(fun _ ->
            match App.server with
            | Some s when s.IsRunning -> App.stop ()
            | _ -> App.start ()
            refresh ())
        logo.PointerReleased.Add(fun e ->
            if e.InitialPressMouseButton = MouseButton.Left then
                e.Handled <- true
                Eto.Forms.Application.Instance.Open repoUrl)
        copyMcp.Click.Add(fun _ -> App.copyMcpCommand ())
        copyToken.Click.Add(fun _ -> App.copyToken ())
        mode.SelectionChanged.Add(fun _ -> if not updating then App.setMode (if mode.SelectedIndex = 1 then Background else UiThread))
        // Click is only raised by the user, not when refresh sets IsChecked.
        autoStart.Click.Add(fun _ -> App.setAutoStart (autoStart.IsChecked.GetValueOrDefault()))
        echo.Click.Add(fun _ -> App.setEcho (echo.IsChecked.GetValueOrDefault()))
        lan.Click.Add(fun _ -> App.setListenOnLan (lan.IsChecked.GetValueOrDefault()))
        cancelWaiting.Click.Add(fun _ -> App.setCancelWaitingCommands (cancelWaiting.IsChecked.GetValueOrDefault()))
        cancel.Click.Add(fun _ -> App.cancel ())
        reset.Click.Add(fun _ -> App.reset ())
        runs.SelectionChanged.Add(fun _ -> showDetails ())

    /// Updates the panel from the server's state. Called every second while the panel is shown.
    member _.Refresh() = refresh ()

    override _.OnAttachedToVisualTree(e) =
        base.OnAttachedToVisualTree e
        applyTheme ()
        if themeChanges.IsNone then
            themeChanges <- Some(PanelTheme.subscribe applyTheme)

    override _.OnDetachedFromVisualTree(e) =
        themeChanges |> Option.iter _.Dispose()
        themeChanges <- None
        base.OnDetachedFromVisualTree e
