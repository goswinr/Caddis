namespace Caddis.UI

open System
open System.IO
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Markup.Xaml
open Avalonia.Media
open Avalonia.Media.Immutable
open Avalonia.Styling
open Avalonia.Media.Imaging
open Avalonia.Themes.Fluent
open Avalonia.Threading
open AvaloniaEdit
open Fantomas.Core
open Caddis

/// The icon of a run's status in the list and in the run header: a colored disc with a white mark, an Ellipse and a Path in PanelView.axaml.
/// Green with a check for ok, red with a cross for a compile error and with an exclamation mark for a runtime error,
/// amber with a clock for a timeout, gray with a dash for cancelled or rejected, blue with three dots while running.
module internal StatusIcon =

    let private brush (hex: string) = lazy (ImmutableSolidColorBrush(Color.Parse hex) :> IBrush)
    let private green = brush "#3FA34D"
    let private red = brush "#D13438"
    let private amber = brush "#D69A00"
    let private gray = brush "#8A909B"
    let private blue = brush "#2B88D8"

    /// In the 14 × 14 box of the disc. A line of length 0.1 with round caps is a dot.
    let private geometry (data: string) = lazy (StreamGeometry.Parse data :> Geometry)
    let private check = geometry "M 3.9,7.3 L 6,9.4 L 10.2,4.9"
    let private cross = geometry "M 4.7,4.7 L 9.3,9.3 M 9.3,4.7 L 4.7,9.3"
    let private exclamation = geometry "M 7,3.5 L 7,8 M 7,10.3 L 7,10.4"
    let private clock = geometry "M 7,3.6 L 7,7.2 L 9.3,8.6"
    let private dash = geometry "M 4.2,7 L 9.8,7"
    let private dots = geometry "M 3.9,7 L 4,7 M 6.95,7 L 7.05,7 M 10,7 L 10.1,7"

    /// The color of the disc.
    let fill (s: RunStatus) : IBrush =
        match s with
        | Running -> blue.Force()
        | Succeeded -> green.Force()
        | CompileError
        | RuntimeError -> red.Force()
        | TimedOut -> amber.Force()
        | Cancelled
        | Rejected -> gray.Force()

    /// The white mark on the disc.
    let glyph (s: RunStatus) : Geometry =
        match s with
        | Running -> dots.Force()
        | Succeeded -> check.Force()
        | CompileError -> cross.Force()
        | RuntimeError -> exclamation.Force()
        | TimedOut -> clock.Force()
        | Cancelled
        | Rejected -> dash.Force()

/// A row in the list of runs. The list's item template binds to its properties.
/// They don't change: the list is made again when the status of a run changes.
type RunRow(id: string, status: RunStatus, started: string, time: string, code: string, tip: string) =
    member _.Id = id
    /// The status in words, e.g. "runtime error".
    member _.Status = RunDetails.statusWord status
    /// The color of the status icon's disc.
    member _.StatusFill = StatusIcon.fill status
    /// The white mark on the status icon's disc.
    member _.StatusGlyph = StatusIcon.glyph status
    /// The first line of the code that isn't a comment.
    member _.Code = code
    /// The id, the status in words and the start time.
    member _.Tip = tip
    /// When the run started, to the minute, e.g. "14:52".
    member _.Started = started
    /// The duration, e.g. "140 ms" or "12.2 s", or "running .." while the run is in progress.
    member _.Time = time
    /// True while the run is in progress: its time is shown on yellow.
    member _.IsRunning = (status = Running)

/// The controls of an AI agent in the panel: a dot for its state, its name, and a button to set it up or to connect it to this app.
type internal AgentRow = {
    Agent: McpAgent
    Row: StackPanel
    Dot: Shapes.Ellipse
    State: TextBlock
    Action: Button
    }

/// The content of the Caddis panel: the state of the server, the F# session and the agents, the settings, notices,
/// the recent runs, and the output and code of the selected run.
/// The layout is in PanelView.axaml and the control themes in PanelStyles.axaml, the colors and actions come from the host.
type PanelView(host: PanelHost) as this =
    inherit UserControl()

    static let repoUrl = "https://github.com/goswinr/Caddis"

    /// Below this width the logo is hidden, so that narrow panels, e.g. AutoCAD's palette, keep their width for the status.
    static let narrowWidth = 420.0

    /// Tooltips don't wrap, so they are given line by line.
    static let tip (lines: string list) = String.concat "\n" lines

    /// Writes to the app's log while the server runs.
    let log (msg: string) = host.Server () |> Option.iter (fun s -> s.Service.Host.Log msg)

    let checkDispatcher =
        AvaloniaSetup.createDispatcherCheck log (fun msg ->
            match host.Server () with
            | Some server -> server.Service.Host.LogError msg
            | None -> host.Errors.Add msg)

    let startTip =
        tip [
            $"Start the server, so that AI agents and scripts can run F# code in this {host.Name}."
            "It listens on localhost only."
        ]

    let stopTip = tip [ $"Stop the server. Connected AI agents can't reach {host.Name} until it is started again." ]
    let modeTip = host.ModeTip

    do
        // Another plugin started Avalonia with its own application, see AvaloniaSetup.
        // Added before loading the XAML, AvaloniaEdit's styles in it take resources from the Fluent theme.
        if not (Avalonia.Application.Current :? PanelApp) then
            this.Styles.Add(FluentTheme())
        AvaloniaXamlLoader.Load this

    let find name : 'T = this.FindControl<'T> name

    let scope: ThemeVariantScope = find "Scope"
    let logo: Border = find "Logo"
    let logoImage: Image = find "LogoImage"
    let serverDot: Shapes.Ellipse = find "ServerDot"
    let copyUrl: Button = find "CopyUrl"
    let serverUrl: TextBlock = find "ServerUrl"
    let copyIcon: Shapes.Path = find "CopyIcon"
    let copiedIcon: Shapes.Path = find "CopiedIcon"
    let copiedNote: TextBlock = find "CopiedNote"
    let serverStopped: TextBlock = find "ServerStopped"
    let startStop: Button = find "StartStop"
    let fsiDot: Shapes.Ellipse = find "FsiDot"
    let fsiStatus: TextBlock = find "FsiStatus"
    let cancel: Button = find "Cancel"
    let reset: Button = find "Reset"
    let agentsLabel: TextBlock = find "AgentsLabel"
    let agents: StackPanel = find "Agents"
    let agentNote: TextBlock = find "AgentNote"
    let agentRows =
        [ ClaudeCode, "Claude"; Codex, "Codex"; ClaudeDesktop, "Desktop" ]
        |> List.map (fun (agent, prefix) -> {
            Agent = agent
            Row = find (prefix + "Row")
            Dot = find (prefix + "Dot")
            State = find (prefix + "State")
            Action = find (prefix + "Action")
        })
    let settingsToggle: Primitives.ToggleButton = find "SettingsToggle"
    let settingsSummary: TextBlock = find "SettingsSummary"
    let modeLabel: TextBlock = find "ModeLabel"
    let mode: ComboBox = find "Mode"
    let autoStart: CheckBox = find "AutoStart"
    let echo: CheckBox = find "Echo"
    let cancelWaiting: CheckBox = find "CancelWaiting"
    let version: TextBlock = find "Version"
    let runtimeInfo: TextBlock = find "RuntimeInfo"
    let gitHub: Button = find "GitHub"
    let updateNotice: Border = find "UpdateNotice"
    let updateText: TextBlock = find "UpdateText"
    let sdkNotice: Border = find "SdkNotice"
    let sdkText: TextBlock = find "SdkText"
    let getSdk: Button = find "GetSdk"
    let errorsNotice: Border = find "ErrorsNotice"
    let errorsText: TextBlock = find "ErrorsText"
    let errorsToggle: Primitives.ToggleButton = find "ErrorsToggle"
    let errorList: TextBox = find "ErrorList"
    let clearErrors: Button = find "ClearErrors"
    let runCount: TextBlock = find "RunCount"
    let exportAll: Button = find "ExportAll"
    let runs: ListBox = find "Runs"
    let noRuns: TextBlock = find "NoRuns"
    let runIcon: Panel = find "RunIcon"
    let runIconDisc: Shapes.Ellipse = find "RunIconDisc"
    let runIconGlyph: Shapes.Path = find "RunIconGlyph"
    let runId: TextBlock = find "RunId"
    let runSummary: TextBlock = find "RunSummary"
    let runAgain: Button = find "RunAgain"
    let exportCode: Button = find "ExportCode"
    let output: TextEditor = find "Output"
    let codeView: TextEditor = find "CodeView"
    let formatCheck: CheckBox = find "FormatCheck"
    let message: TextBlock = find "Message"

    /// The context menu of the server's URL: copy it, or the URL that AI agents connect to.
    let copyServerUrl = MenuItem()
    let copyMcpUrl = MenuItem()

    /// The ids and states of the shown runs, to only update the list when something changed.
    let mutable shownRuns = ""

    /// The code of F# runs formatted by Fantomas, None if Fantomas can't parse it. Kept as long as the run.
    let formattedCode = ConditionalWeakTable<Run, Task<string option>>()

    /// True while the controls are updated from the settings, so that their change events are ignored.
    let mutable updating = false

    /// While attached: the subscription to changes of the host's theme.
    let mutable themeChanges: IDisposable option = None

    /// The agent whose button is at work, with the way to cancel it while an activation waits for the other app.
    /// Only one at a time: both agents may connect to the same port.
    let mutable agentWork: (McpAgent * CancellationTokenSource option) option = None

    /// What the last click on an agent's button did, shown below the agents until the time.
    let mutable agentNoteText: (string * DateTime) option = None

    /// Counts the copies to the clipboard, so that only the last one hides its check after a moment.
    let mutable copies = 0

    let applyTheme () =
        let palette = host.Palette ()
        for key, color in palette.Colors do
            this.Resources["Caddis." + key] <- SolidColorBrush(color)
        scope.RequestedThemeVariant <- if palette.IsDark then ThemeVariant.Dark else ThemeVariant.Light
        codeView.SyntaxHighlighting <- Highlighting.fsharp palette.IsDark
        output.SyntaxHighlighting <- Highlighting.output palette.IsDark

    /// Colors a dot by its class: "ok" green, "warn" amber, "run" blue, "" gray.
    let setDot (dot: Shapes.Ellipse) (state: string) =
        for c in [ "ok"; "warn"; "run" ] do
            dot.Classes.Set(c, (c = state))

    let firstCodeLine (code: string) =
        code.Split('\n')
        |> Array.map _.Trim()
        |> Array.tryFind (fun l -> l <> "" && not (l.StartsWith "//"))
        |> Option.defaultValue ""

    let selectedRun () =
        match runs.SelectedItem, host.Server () with
        | (:? RunRow as row), Some s -> s.Service.Runs.TryGet row.Id
        | _ -> None

    /// Formats the code of a run with Fantomas on the thread pool. The first one loads Fantomas and takes up to a second, later ones milliseconds.
    /// When it is done, the code view is updated, in case the run is still selected.
    let rec formatted (r: Run) : Task<string option> =
        formattedCode.GetValue(
            r,
            fun r ->
                let task =
                    async {
                        try
                            let! result = CodeFormatter.FormatDocumentAsync(false, r.Code)
                            return Some result.Code
                        with _ ->
                            return None
                    }
                    |> Async.StartAsTask
                task.ContinueWith(fun (_: Task<string option>) -> Dispatcher.UIThread.Post(fun () -> showCode ())) |> ignore
                task
        )

    /// Shows the code of the selected run, formatted if Formatted is checked.
    /// As it was run until Fantomas is done, or if Fantomas can't parse it.
    and showCode () =
        let code =
            match selectedRun () with
            | Some r when r.Kind = FSharpCode && formatCheck.IsChecked.GetValueOrDefault() ->
                let task = formatted r
                if task.IsCompleted then task.Result |> Option.defaultValue r.Code else r.Code
            | Some r -> r.Code
            | None -> ""
        // Setting the text of the editor moves the caret to the start, so only when other code is shown.
        if codeView.Text <> code then
            codeView.Text <- code
            codeView.ScrollToHome()

    /// The runs that 'Export all' saves, oldest first.
    let exportableRuns () =
        match host.Server () with
        | Some s -> ScriptExport.exportable s.Service.Runs.All
        | None -> [||]

    /// Run again: for a selected F# run, while no run is in progress.
    let updateRunAgain () =
        runAgain.IsEnabled <-
            match selectedRun (), host.Server () with
            | Some r, Some s -> r.Kind = FSharpCode && s.Service.Fsi.Current.IsNone
            | _ -> false

    /// The header above the tabs: the icon of the selected run's status, its id, and its status, duration, start time and thread.
    /// The tooltip names its script and the folder for its files.
    let showRunHeader (selected: Run option) =
        match selected with
        | Some r ->
            runIcon.IsVisible <- true
            runIconDisc.Fill <- StatusIcon.fill r.Status
            runIconGlyph.Data <- StatusIcon.glyph r.Status
            runId.Text <- r.Id
            runSummary.Text <- RunDetails.summary host.Name r
            let where =
                tip [
                    if r.Kind = FSharpCode && not (String.IsNullOrEmpty r.ScriptPath) then $"Script: {r.ScriptPath}"
                    if not (String.IsNullOrEmpty r.OutDir) then $"Folder for its files: {r.OutDir}"
                ]
            ToolTip.SetTip(runId, where)
            ToolTip.SetTip(runSummary, where)
        | None ->
            runIcon.IsVisible <- false
            runId.Text <- ""
            runSummary.Text <- "Select a run to see its output and code."
            ToolTip.SetTip(runId, null)
            ToolTip.SetTip(runSummary, null)

    let showDetails () =
        let selected = selectedRun ()
        updateRunAgain ()
        showRunHeader selected
        let text = selected |> Option.map (RunDetails.text host.ChangeNoun host.CommandLineTitle) |> Option.defaultValue ""
        // Setting the text of the editor moves the caret to the start, so only when other output is shown.
        if output.Text <> text then
            output.Text <- text
            output.ScrollToHome()
        exportCode.IsEnabled <- selected |> Option.exists (fun r -> r.Kind = FSharpCode)
        exportAll.IsEnabled <- exportableRuns().Length > 0
        showCode ()

    /// The code of an F# run as the code view shows it with the given state of Formatted.
    let exportedCode (format: bool) (r: Run) : Task<string> =
        task {
            if format then
                let! f = formatted r
                return f |> Option.defaultValue r.Code
            else
                return r.Code
        }

    /// Asks the host where to save, then saves the text. It is made after the dialog, so that Fantomas has time to format.
    let export (title: string) (fileName: string) (text: unit -> Task<string>) (saved: string -> string) =
        match host.PickScriptPath title fileName with
        | None -> ()
        | Some path ->
            let path = if Path.HasExtension path then path else path + ".fsx"
            task {
                try
                    let! t = text ()
                    File.WriteAllText(path, t)
                    log (saved path)
                with e ->
                    log $"Caddis: could not save '{path}': {e.Message}"
            }
            |> ignore

    let runRow (r: Run) =
        let time = if r.Status = Running then RunDetails.runningTime else RunDetails.duration r.Duration
        RunRow(r.Id, r.Status, RunDetails.startMinute r, time, firstCodeLine r.Code, $"{r.Id}: {RunDetails.statusWord r.Status}, started at {RunDetails.startTime r}")

    let refreshRuns (s: CaddisServer) =
        let all = s.Service.Runs.All |> Array.rev
        runCount.Text <- string all.Length
        noRuns.IsVisible <- all.Length = 0
        let signature = all |> Array.map (fun r -> r.Id + RunStatus.toString r.Status) |> String.concat ","
        if signature <> shownRuns then
            shownRuns <- signature
            let selectedId =
                match runs.SelectedItem with
                | :? RunRow as row -> row.Id
                | _ -> null
            let rows = all |> Array.map runRow
            runs.ItemsSource <- rows
            runs.SelectedItem <- rows |> Array.tryFind (fun row -> row.Id = selectedId) |> Option.map box |> Option.toObj
            showDetails ()
        else
            // The same runs with the same states: only the duration of a running run changed. Its row shows "running ..",
            // the run header counts up. Only the header is updated, because a new list would select the row again
            // and show its details again, every second.
            match selectedRun () with
            | Some r when r.Status = Running -> runSummary.Text <- RunDetails.summary host.Name r
            | _ -> ()

    /// Selects a run in the list, e.g. the one started by Run again.
    let selectRun (id: string) =
        match runs.ItemsSource with
        | :? (RunRow[]) as rows -> runs.SelectedItem <- rows |> Array.tryFind (fun row -> row.Id = id) |> Option.map box |> Option.toObj
        | _ -> ()

    /// The notice while the host has internal errors, in bold for new ones, and their list while it is shown.
    let refreshErrors () =
        match host.Errors.Lines with
        | [] ->
            errorsNotice.IsVisible <- false
            errorsToggle.IsChecked <- false
        | lines ->
            errorsNotice.IsVisible <- true
            errorsText.Text <- if lines.Length = 1 then "1 internal error" else $"{lines.Length} internal errors"
            let shown = errorsToggle.IsChecked.GetValueOrDefault()
            errorsToggle.Content <- if shown then "Hide" else "Show"
            if shown then
                host.Errors.MarkSeen()
                // Setting the text clears the selection, so only when there are other errors.
                let text = lines |> List.rev |> String.concat "\n\n"
                if errorList.Text <> text then errorList.Text <- text
            errorsText.FontWeight <- if host.Errors.Unseen > 0 then FontWeight.Bold else FontWeight.Normal

    /// The commands that set the agent up for this server: Claude Code's 'mcp add' fails if the name exists, so it is removed first.
    /// None for Claude Desktop, which gets an extension.
    let agentCommands (s: CaddisServer) (agent: McpAgent) =
        [
            if agent = ClaudeCode && s.AgentState agent <> AgentState.NotSetUp then
                McpAgent.commandLine agent (McpAgent.removeArgs agent s.Service.Host.Info.mcpServerName)
            if (McpAgent.program agent).IsSome then
                s.AgentCommand agent
        ]

    /// The state of each agent in its dot and its tooltip, and its button: Set up, Activate, or Cancel while an activation waits.
    let refreshAgents (s: CaddisServer) =
        agents.IsVisible <- s.IsRunning
        agentsLabel.IsVisible <- s.IsRunning
        if s.IsRunning then
            let app = host.Name
            let serverName = s.Service.Host.Info.mcpServerName
            for row in agentRows do
                row.Row.IsVisible <- s.AgentConfigs.IsInstalled row.Agent
                let name = McpAgent.name row.Agent
                let state = s.AgentState row.Agent
                let commands = agentCommands s row.Agent
                let isExtension = commands.IsEmpty
                // What Set up does: the commands it runs, or the extension it opens.
                let setUp =
                    if isExtension then
                        [ $"It opens the extension {serverName}.mcpb, Claude Desktop asks to install it."; $"It relays to {s.McpUrl}." ]
                    else
                        [ "It runs:"; yield! commands; "Right-click to copy the command, e.g. to run it in a terminal yourself." ]
                let label, stateTip =
                    match state with
                    | AgentState.Active ->
                        "active",
                        if isExtension then
                            [ $"The extension of {name} relays to Caddis in this {app}, at {s.McpUrl}." ]
                        else
                            [
                                $"{name} connects to Caddis in this {app}, at {s.McpUrl}."
                                $"Sessions of {name} that were open before it was set up see Caddis after a restart."
                            ]
                    | AgentState.NotSetUp when isExtension -> "not set up", [ $"{name} has no extension '{serverName}' turned on." ]
                    | AgentState.NotSetUp -> "not set up", [ $"{name} has no MCP server '{serverName}' for all your projects." ]
                    | AgentState.Inactive url ->
                        let where =
                            match McpAgent.localPort url |> Option.bind s.InstanceOnPort with
                            | Some(other, "") -> [ $"where Caddis in {other} listens." ]
                            | Some(other, title) -> [ $"where Caddis in {other} listens:"; title ]
                            | None -> [ $"where no other Caddis listens." ]
                        "inactive", $"{name} connects to {url}," :: where
                // The state is in the color of the dot and in the tooltip. The button shows only when there is something to do.
                row.State.Text <- name
                let stateTip = tip ($"{name}: {label}" :: stateTip)
                ToolTip.SetTip(row.State, stateTip)
                ToolTip.SetTip(row.Dot, stateTip)
                setDot row.Dot (match state with AgentState.Active -> "ok" | AgentState.Inactive _ -> "warn" | AgentState.NotSetUp -> "")
                match agentWork with
                | Some(agent, cts) when agent = row.Agent ->
                    row.Action.IsVisible <- true
                    row.Action.IsEnabled <- cts.IsSome
                    row.Action.Content <- if cts.IsSome then "Cancel" else "Setting up..."
                    ToolTip.SetTip(row.Action, $"Stop waiting for the other {app}, it keeps the port.")
                | _ ->
                    row.Action.IsVisible <- state <> AgentState.Active
                    row.Action.IsEnabled <- agentWork.IsNone
                    match state with
                    | AgentState.Active -> ()
                    | AgentState.NotSetUp ->
                        row.Action.Content <- "Set up"
                        ToolTip.SetTip(row.Action, tip [ $"Add Caddis in this {app} to {name}, for all your projects."; yield! setUp ])
                    | AgentState.Inactive url ->
                        row.Action.Content <- "Activate"
                        let how =
                            match McpAgent.localPort url with
                            | Some p -> [
                                $"Connect {name} to this {app}: the {app} on port {p} hands it over to this one once it is idle,"
                                "with no run in progress. All agents connected to that port follow."
                                $"If the port can't be taken, e.g. another program uses it, {name} is set up for this {app}:"
                              ]
                            | None -> [ $"Connect {name} to this {app}." ]
                        ToolTip.SetTip(row.Action, tip [ yield! how; yield! setUp ])
            match agentNoteText with
            | Some(note, until) when DateTime.UtcNow < until ->
                agentNote.Text <- note
                agentNote.IsVisible <- true
            | _ -> agentNote.IsVisible <- false

    /// Recommends installing the .NET SDK while scripts can't use NuGet packages without it.
    let refreshSdkNotice () =
        let nuget = DotnetSdk.current ()
        match NuGetSupport.installAdviceLines host.Name nuget with
        | None -> sdkNotice.IsVisible <- false
        | Some lines ->
            sdkNotice.IsVisible <- true
            sdkText.Text <- $"Scripts can't use NuGet packages without the {NuGetSupport.requiredSdk nuget}."
            ToolTip.SetTip(sdkNotice, tip lines)
            ToolTip.SetTip(getSdk, $"Open {NuGetSupport.downloadUrl nuget}")

    /// The server's dot, its URL while it listens, and the button to start or stop it.
    let refreshServer (s: CaddisServer) =
        setDot serverDot (if s.IsRunning then "ok" else "")
        copyUrl.IsVisible <- s.IsRunning
        serverStopped.IsVisible <- not s.IsRunning
        serverStopped.Text <- "Stopped"
        if serverUrl.Text <> s.Url then
            serverUrl.Text <- s.Url
            copyServerUrl.Header <- $"Copy {s.Url}"
            copyMcpUrl.Header <- $"Copy {s.McpUrl}"
            ToolTip.SetTip(
                copyUrl,
                tip [ $"Caddis listens on {s.Url}, AI agents connect to {s.McpUrl}."; $"Click to copy {s.Url}, right-click to copy the URL for agents." ]
            )
        startStop.Content <- if s.IsRunning then "Stop" else "Start"
        ToolTip.SetTip(startStop, (if s.IsRunning then stopTip else startTip))

    /// The session's dot and state, e.g. "Ready · background thread" or "Running r5 · 1.2 s", and Cancel while a run is in progress.
    let refreshSession (svc: CaddisService) =
        let thread =
            match svc.Fsi.Mode with
            | UiThread -> "UI thread"
            | Background -> "background thread"
        match svc.Fsi.Current with
        | Some r ->
            setDot fsiDot "run"
            fsiStatus.Text <- $"Running {r.Id} · {RunDetails.duration r.Duration}"
        | None ->
            let state, dot =
                match svc.Fsi.State with
                | Ready -> "Ready", "ok"
                | Evaluating -> "Running", "run"
                | Initializing -> "Starting", ""
                | NotLoaded -> "Not started", ""
                | Failed _ -> "Failed", "warn"
            setDot fsiDot dot
            fsiStatus.Text <- $"{state} · {thread}"
        ToolTip.SetTip(
            fsiStatus,
            match svc.Fsi.State with
            | Failed e -> tip [ $"The F# session failed: {e}"; ""; modeTip ]
            | _ -> modeTip
        )
        cancel.IsVisible <- svc.Fsi.Current.IsSome
        cancel.IsEnabled <- svc.Fsi.Current |> Option.exists (fun r -> r.Mode = Background)

    let refresh () =
        let note = host.Message |> Option.map (fun get -> get ()) |> Option.defaultValue ""
        message.Text <- note
        ToolTip.SetTip(message, (if String.IsNullOrEmpty note then null else box note))
        refreshErrors ()
        version.Text <- host.Version ()
        match host.Update () with
        | Some text ->
            updateNotice.IsVisible <- true
            updateText.Text <- text
        | None -> updateNotice.IsVisible <- false
        refreshSdkNotice ()
        match host.Server () with
        | None ->
            setDot serverDot ""
            copyUrl.IsVisible <- false
            serverStopped.IsVisible <- true
            serverStopped.Text <- "Caddis is not loaded."
            agents.IsVisible <- false
            agentsLabel.IsVisible <- false
        | Some s ->
            let svc = s.Service
            updating <- true
            try
                refreshServer s
                refreshSession svc
                settingsSummary.Text <- $"Caddis {svc.Version}"
                refreshAgents s
                mode.SelectedIndex <- (match svc.Fsi.Mode with UiThread -> 0 | Background -> 1)
                autoStart.IsChecked <- Nullable svc.Settings.autoStart
                echo.IsChecked <- Nullable svc.Settings.echoOutput
                cancelWaiting.IsChecked <- Nullable svc.Settings.cancelWaitingCommands
                runtimeInfo.Text <- svc.RuntimeInfo
                refreshRuns s
                updateRunAgain ()
                host.Refreshed ()
            finally
                updating <- false

    /// Copy and Select all, the read-only text has no Cut and Paste.
    let textMenu (control: Control) (copy: unit -> unit) (selectAll: unit -> unit) =
        let item (header: string) (action: unit -> unit) =
            let m = MenuItem(Header = header)
            m.Click.Add(fun _ -> action ())
            m
        control.ContextMenu <- ContextMenu(ItemsSource = [ item "Copy" copy; item "Select all" selectAll ])

    /// Puts the text on the clipboard. For a moment the copy icon beside the URL becomes a check with "Copied",
    /// or a note says that another program has the clipboard.
    let copyToClipboard (text: string) =
        let ok = host.SetClipboard text
        copies <- copies + 1
        let copy = copies
        copyIcon.IsVisible <- not ok
        copiedIcon.IsVisible <- ok
        copiedNote.Text <- if ok then "Copied" else "The clipboard is in use, try again."
        copiedNote.IsVisible <- true
        Task
            .Delay(if ok then 1600 else 4000)
            .ContinueWith(fun (_: Task) ->
                Dispatcher.UIThread.Post(fun () ->
                    if copy = copies then
                        copyIcon.IsVisible <- true
                        copiedIcon.IsVisible <- false
                        copiedNote.IsVisible <- false))
        |> ignore

    /// Shows what an agent's button did, below the agents, for some seconds, or until the next note for infinity.
    let noteAgent (seconds: float) (text: string) =
        agentNoteText <- Some(text, (if Double.IsInfinity seconds then DateTime.MaxValue else DateTime.UtcNow.AddSeconds seconds))

    /// Sets the agent up for this server, or connects it to this server, on the thread pool:
    /// the agent's program runs for a second or two, and an activation waits until the other app is idle.
    /// While an activation waits, the button cancels it.
    let agentClicked (s: CaddisServer) (row: AgentRow) =
        let name = McpAgent.name row.Agent
        match agentWork with
        | Some(agent, Some cts) when agent = row.Agent -> cts.Cancel()
        | Some _ -> ()
        | None ->
            let work =
                match s.AgentState row.Agent with
                | AgentState.Active -> None
                | AgentState.NotSetUp -> Some((fun () -> s.SetUpAgent row.Agent), None, $"Setting up {name}...")
                | AgentState.Inactive _ ->
                    let cts = new CancellationTokenSource()
                    let waiting reason =
                        Dispatcher.UIThread.Post(fun () ->
                            if not cts.IsCancellationRequested then
                                noteAgent infinity $"Activating this {host.Name} for {name}, waiting: {reason}"
                                refresh ())
                    Some((fun () -> s.ActivateAgent(row.Agent, waiting, cts.Token)), Some cts, $"Activating this {host.Name} for {name}...")
            match work with
            | None -> ()
            | Some(start, cts, starting) ->
                agentWork <- Some(row.Agent, cts)
                noteAgent infinity starting
                Task
                    .Run<Result<string, string>>(Func<Task<Result<string, string>>>(start))
                    .ContinueWith(
                        Action<Task<Result<string, string>>>(fun t ->
                            Dispatcher.UIThread.Post(fun () ->
                                agentWork <- None
                                cts |> Option.iter _.Dispose()
                                if t.Status = TaskStatus.RanToCompletion then
                                    match t.Result with
                                    | Ok msg -> noteAgent 20.0 msg
                                    | Error msg -> noteAgent 60.0 msg
                                elif t.IsCanceled || (t.Exception.InnerException :? OperationCanceledException) then
                                    noteAgent 20.0 $"Cancelled, {name} connects to the other {host.Name}."
                                else
                                    noteAgent 60.0 $"{name}: {t.Exception.InnerException.Message}"
                                refresh ()))
                    )
                |> ignore
            refresh ()

    /// Puts the commands of the agent's button on the clipboard.
    let copyAgentCommands (s: CaddisServer) (row: AgentRow) =
        let commands = agentCommands s row.Agent
        let what = if commands.Length = 1 then "the command that sets" else "the commands that set"
        if host.SetClipboard(String.concat Environment.NewLine commands) then
            noteAgent 20.0 $"Copied {what} up {McpAgent.name row.Agent} for this {host.Name}, paste it in a terminal."
        else
            noteAgent 20.0 "The clipboard is in use by another program, try again."
        refresh ()

    do
        applyTheme ()
        autoStart.Content <- $"Start with {host.Name}"
        echo.IsVisible <- host.SetEcho.IsSome
        cancelWaiting.IsVisible <- host.SetCancelWaitingCommands.IsSome
        message.IsVisible <- host.Message.IsSome
        noRuns.Text <- $"No runs yet. Code that AI agents run in {host.Name} shows up here."
        showRunHeader None

        use stream = typeof<PanelView>.Assembly.GetManifestResourceStream "Caddis.UI.Resources.Caddis.png"
        logoImage.Source <- Bitmap.DecodeToWidth(stream, 80)

        // In narrow panels the logo is hidden, so that the status keeps the width.
        this.PropertyChanged.Add(fun e ->
            if e.Property = Avalonia.Visual.BoundsProperty then
                logo.IsVisible <- this.Bounds.Width >= narrowWidth)

        ToolTip.SetTip(logo, $"Caddis on GitHub: {repoUrl}")
        ToolTip.SetTip(gitHub, repoUrl)
        ToolTip.SetTip(startStop, startTip)
        ToolTip.SetTip(modeLabel, modeTip)
        ToolTip.SetTip(mode, modeTip)
        ToolTip.SetTip(
            cancel,
            tip [
                "Cancel the current run. Only runs on a background thread can be cancelled,"
                "set 'Run on' under Settings."
                "If a run doesn't stop within 5 s, its thread is abandoned and the F# session is reset."
            ]
        )
        ToolTip.SetTip(
            reset,
            tip [ "Start a new F# session. All values, functions and types defined by earlier runs are lost."; "Use it if the session is in a bad state." ]
        )
        ToolTip.SetTip(errorsText, tip [ "Errors inside Caddis, not in the runs of scripts."; "Agents go on after most of them. The list helps to report a bug." ])
        ToolTip.SetTip(errorsToggle, tip [ "Show or hide the internal errors of Caddis, newest first,"; "e.g. that the F# session could not be created." ])
        ToolTip.SetTip(clearErrors, tip [ "Remove these errors from the list."; "The notice is hidden until the next error." ])
        ToolTip.SetTip(settingsToggle, "Show or hide the settings and the version of Caddis.")
        ToolTip.SetTip(settingsSummary, host.VersionTip)
        ToolTip.SetTip(version, host.VersionTip)
        ToolTip.SetTip(updateNotice, host.VersionTip)
        ToolTip.SetTip(autoStart, $"Start the server when {host.Name} starts.")
        ToolTip.SetTip(echo, tip [ $"Also print the output of runs to the {host.Name} command line:"; "printfn, F# Interactive's output and errors." ])
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
        ToolTip.SetTip(
            formatCheck,
            tip [
                "Show the code formatted with Fantomas."
                "Unchecked: the code as it was run, so that the line numbers match those of the diagnostics"
                "and stack traces in the output. Code that Fantomas can't parse is always shown as it was run."
            ]
        )
        ToolTip.SetTip(
            runAgain,
            tip [
                "Run the code of the selected F# run again, as it was run, also while 'Formatted' is checked."
                "It is a new run in the current F# session, on the thread set under Settings."
                "Only while no other run is in progress."
            ]
        )
        ToolTip.SetTip(exportCode, tip [ "Save the code of the selected F# run to an .fsx file,"; "as shown: formatted while 'Formatted' is checked." ])
        ToolTip.SetTip(
            exportAll,
            tip [
                "Save the code of all successful F# runs in the list to one .fsx file, oldest first."
                "A comment with the run's id and time separates the runs."
                "As shown: formatted while 'Formatted' is checked."
            ]
        )
        textMenu output output.Copy output.SelectAll
        textMenu errorList errorList.Copy errorList.SelectAll
        textMenu codeView codeView.Copy codeView.SelectAll

        startStop.Click.Add(fun _ ->
            match host.Server () with
            | Some s when s.IsRunning -> host.Stop ()
            | _ -> host.Start ()
            refresh ())
        copyUrl.Click.Add(fun _ -> host.Server () |> Option.iter (fun s -> copyToClipboard s.Url))
        copyServerUrl.Click.Add(fun _ -> host.Server () |> Option.iter (fun s -> copyToClipboard s.Url))
        copyMcpUrl.Click.Add(fun _ -> host.Server () |> Option.iter (fun s -> copyToClipboard s.McpUrl))
        copyUrl.ContextMenu <- ContextMenu(ItemsSource = [ copyServerUrl; copyMcpUrl ])
        logo.PointerReleased.Add(fun e ->
            if e.InitialPressMouseButton = MouseButton.Left then
                e.Handled <- true
                host.OpenUrl repoUrl)
        gitHub.Click.Add(fun _ -> host.OpenUrl repoUrl)
        for row in agentRows do
            row.Action.Click.Add(fun _ -> host.Server () |> Option.iter (fun s -> agentClicked s row))
            // Claude Desktop gets an extension, there is no command to copy
            if (McpAgent.program row.Agent).IsSome then
                let copy = MenuItem(Header = "Copy the command")
                copy.Click.Add(fun _ -> host.Server () |> Option.iter (fun s -> copyAgentCommands s row))
                row.Row.ContextMenu <- ContextMenu(ItemsSource = [ copy ])
        getSdk.Click.Add(fun _ -> host.OpenUrl (NuGetSupport.downloadUrl (DotnetSdk.current ())))
        mode.SelectionChanged.Add(fun _ -> if not updating then host.SetMode (if mode.SelectedIndex = 1 then Background else UiThread))
        // Click is only raised by the user, not when refresh sets IsChecked.
        autoStart.Click.Add(fun _ -> host.SetAutoStart (autoStart.IsChecked.GetValueOrDefault()))
        echo.Click.Add(fun _ -> host.SetEcho |> Option.iter (fun set -> set (echo.IsChecked.GetValueOrDefault())))
        cancelWaiting.Click.Add(fun _ -> host.SetCancelWaitingCommands |> Option.iter (fun set -> set (cancelWaiting.IsChecked.GetValueOrDefault())))
        cancel.Click.Add(fun _ -> host.Cancel (); refresh ())
        reset.Click.Add(fun _ -> host.Reset (); refresh ())
        errorsToggle.Click.Add(fun _ -> refreshErrors ())
        clearErrors.Click.Add(fun _ ->
            host.Errors.Clear()
            refreshErrors ())
        runs.SelectionChanged.Add(fun _ -> showDetails ())
        formatCheck.Click.Add(fun _ -> showCode ())
        runAgain.Click.Add(fun _ ->
            match selectedRun (), host.Server () with
            | Some r, Some s when r.Kind = FSharpCode ->
                runAgain.IsEnabled <- false
                // A script file of the user is kept, for its relative #load and #r. Not the script.fsx in the run's own folder.
                let path = if Path.GetDirectoryName r.ScriptPath = r.OutDir then None else Some r.ScriptPath
                let args: EvalArgs = { code = Some r.Code; path = path; timeoutMs = None; waitMs = None; recordUndo = r.RecordUndo }
                // From the thread pool, like the requests of agents: on Rhino's UI thread the run would start inline.
                Task
                    .Run<Run>(Func<Task<Run>>(fun () -> s.Service.StartEval args))
                    .ContinueWith(
                        Action<Task<Run>>(fun t ->
                            Dispatcher.UIThread.Post(fun () ->
                                if t.IsFaulted then
                                    match t.Exception.InnerException with
                                    | BusyException id -> log $"Caddis: run '{id}' is still in progress, cancel it first (only possible in background mode)."
                                    | e -> log $"Caddis: could not run '{r.Id}' again: {e.Message}"
                                refresh ()
                                if t.Status = TaskStatus.RanToCompletion then selectRun t.Result.Id))
                    )
                |> ignore
            | _ -> ())
        exportCode.Click.Add(fun _ ->
            match selectedRun () with
            | Some r when r.Kind = FSharpCode ->
                let format = formatCheck.IsChecked.GetValueOrDefault()
                export $"Export the code of run {r.Id}" $"Caddis-{r.Id}.fsx" (fun () -> exportedCode format r) (fun path ->
                    $"Caddis: saved the code of run {r.Id} to '{path}'.")
            | _ -> ())
        exportAll.Click.Add(fun _ ->
            let all = exportableRuns ()
            if all.Length > 0 then
                let format = formatCheck.IsChecked.GetValueOrDefault()
                let text () =
                    task {
                        let! codes = all |> Array.map (exportedCode format) |> Task.WhenAll
                        return ScriptExport.combine host.Name (List.ofArray (Array.zip all codes))
                    }
                let today = DateTime.Now.ToString "yyyy-MM-dd"
                export "Export all successful runs" $"Caddis-runs-{today}.fsx" text (fun path ->
                    $"Caddis: saved the code of {ScriptExport.count all.Length} to '{path}'."))

    /// Updates the panel from the server's state. Called every second while the panel is shown.
    member _.Refresh() =
        checkDispatcher ()
        refresh ()

    override _.OnAttachedToVisualTree(e) =
        base.OnAttachedToVisualTree e
        applyTheme ()
        if themeChanges.IsNone then
            themeChanges <- Some(host.SubscribeTheme (fun () -> Dispatcher.UIThread.Post applyTheme))

    override _.OnDetachedFromVisualTree(e) =
        themeChanges |> Option.iter _.Dispose()
        themeChanges <- None
        base.OnDetachedFromVisualTree e
