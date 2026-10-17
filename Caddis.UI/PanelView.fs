namespace Caddis.UI

open System
open System.IO
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Markup.Xaml
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Media.Imaging
open Avalonia.Themes.Fluent
open Avalonia.Threading
open AvaloniaEdit
open Fantomas.Core
open Caddis

/// A row in the list of runs. The list's item template binds to its properties.
type RunRow = {
    Id: string
    Status: string
    Time: string
    Code: string
    }

/// The content of the Caddis panel: the server state and settings, the recent runs, and the output and code of the selected run.
/// The layout is in PanelView.axaml and the control themes in PanelStyles.axaml, the colors and actions come from the host.
type PanelView(host: PanelHost) as this =
    inherit UserControl()

    static let repoUrl = "https://github.com/goswinr/Caddis"

    /// Tooltips don't wrap, so they are given line by line.
    static let tip (lines: string list) = String.concat "\n" lines

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
    let serverStatus: TextBlock = find "ServerStatus"
    let fsiStatus: TextBlock = find "FsiStatus"
    let message: TextBlock = find "Message"
    let startStop: Button = find "StartStop"
    let copyMcp: Button = find "CopyMcp"
    let modeLabel: TextBlock = find "ModeLabel"
    let mode: ComboBox = find "Mode"
    let cancel: Button = find "Cancel"
    let reset: Button = find "Reset"
    let settingsToggle: Primitives.ToggleButton = find "SettingsToggle"
    let errorsToggle: Primitives.ToggleButton = find "ErrorsToggle"
    let errorsText: TextBlock = find "ErrorsText"
    let errorList: TextBox = find "ErrorList"
    let clearErrors: Button = find "ClearErrors"
    let autoStart: CheckBox = find "AutoStart"
    let echo: CheckBox = find "Echo"
    let cancelWaiting: CheckBox = find "CancelWaiting"
    let runtimeInfo: TextBlock = find "RuntimeInfo"
    let runs: ListBox = find "Runs"
    let output: TextBox = find "Output"
    let codeView: TextEditor = find "CodeView"
    let formatCheck: CheckBox = find "FormatCheck"
    let runAgain: Button = find "RunAgain"
    let exportCode: Button = find "ExportCode"
    let exportAll: Button = find "ExportAll"

    /// The ids and states of the shown runs, to only update the list when something changed.
    let mutable shownRuns = ""

    /// The code of F# runs formatted by Fantomas, None if Fantomas can't parse it. Kept as long as the run.
    let formattedCode = ConditionalWeakTable<Run, Task<string option>>()

    /// True while the controls are updated from the settings, so that their change events are ignored.
    let mutable updating = false

    /// While attached: the subscription to changes of the host's theme.
    let mutable themeChanges: IDisposable option = None

    let applyTheme () =
        let palette = host.Palette ()
        for key, color in palette.Colors do
            this.Resources["Caddis." + key] <- SolidColorBrush(color)
        scope.RequestedThemeVariant <- if palette.IsDark then ThemeVariant.Dark else ThemeVariant.Light
        codeView.SyntaxHighlighting <- FSharpHighlighting.get palette.IsDark

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

    let showDetails () =
        let selected = selectedRun ()
        updateRunAgain ()
        output.Text <- selected |> Option.map (RunDetails.text host.ChangeNoun host.CommandLineTitle) |> Option.defaultValue ""
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

    let log (msg: string) = host.Server () |> Option.iter (fun s -> s.Service.Host.Log msg)

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

    /// Selects a run in the list, e.g. the one started by Run again.
    let selectRun (id: string) =
        match runs.ItemsSource with
        | :? (RunRow[]) as rows -> runs.SelectedItem <- rows |> Array.tryFind (fun row -> row.Id = id) |> Option.map box |> Option.toObj
        | _ -> ()

    /// The Errors button while the host has errors, in bold for new ones, and their list while it is shown.
    let refreshErrors () =
        match host.Errors.Lines with
        | [] ->
            errorsToggle.IsVisible <- false
            errorsToggle.IsChecked <- false
        | lines ->
            errorsToggle.IsVisible <- true
            errorsText.Text <- if lines.Length = 1 then "1 error" else $"{lines.Length} errors"
            if errorsToggle.IsChecked.GetValueOrDefault() then
                host.Errors.MarkSeen()
                // Setting the text clears the selection, so only when there are other errors.
                let text = lines |> List.rev |> String.concat "\n\n"
                if errorList.Text <> text then errorList.Text <- text
            errorsText.FontWeight <- if host.Errors.Unseen > 0 then FontWeight.Bold else FontWeight.Normal

    let refresh () =
        message.Text <- host.Message |> Option.map (fun get -> get ()) |> Option.defaultValue ""
        refreshErrors ()
        match host.Server () with
        | None -> serverStatus.Text <- "Caddis is not loaded."
        | Some s ->
            let svc = s.Service
            updating <- true
            try
                let running =
                    match svc.Fsi.Current with
                    | Some r -> $", running {r.Id}"
                    | None -> ""
                let thread =
                    match svc.Fsi.Mode with
                    | UiThread -> "UI thread"
                    | Background -> "background thread"
                serverStatus.Text <- if s.IsRunning then $"Listening on {s.Url}" else "Server stopped"
                // The thread mode is shown here too, its dropdown is in the folded settings.
                fsiStatus.Text <- $"F# session ({thread}): {FsiState.toString svc.Fsi.State}{running}"
                startStop.Content <- if s.IsRunning then "Stop" else "Start"
                ToolTip.SetTip(startStop, (if s.IsRunning then stopTip else startTip))
                copyMcp.IsEnabled <- s.IsRunning
                mode.SelectedIndex <- (match svc.Fsi.Mode with UiThread -> 0 | Background -> 1)
                autoStart.IsChecked <- Nullable svc.Settings.autoStart
                echo.IsChecked <- Nullable svc.Settings.echoOutput
                cancelWaiting.IsChecked <- Nullable svc.Settings.cancelWaitingCommands
                runtimeInfo.Text <- svc.RuntimeInfo
                cancel.IsEnabled <- svc.Fsi.Current |> Option.exists (fun r -> r.Mode = Background)
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

    do
        applyTheme ()
        autoStart.Content <- $"Start with {host.Name}"
        echo.IsVisible <- host.SetEcho.IsSome
        cancelWaiting.IsVisible <- host.SetCancelWaitingCommands.IsSome
        message.IsVisible <- host.Message.IsSome

        use stream = typeof<PanelView>.Assembly.GetManifestResourceStream "Caddis.UI.Resources.Caddis.png"
        logoImage.Source <- Bitmap.DecodeToWidth(stream, 80)

        ToolTip.SetTip(logo, $"Caddis on GitHub: {repoUrl}")
        ToolTip.SetTip(startStop, startTip)
        ToolTip.SetTip(
            copyMcp,
            tip [ "Copy the command that adds Caddis to Claude Code."; "Paste it into a terminal. Only while the server is running." ]
        )
        ToolTip.SetTip(modeLabel, modeTip)
        ToolTip.SetTip(mode, modeTip)
        ToolTip.SetTip(
            cancel,
            tip [
                "Cancel the current run. Only runs on a background thread can be cancelled,"
                "set 'Run scripts on' under Settings."
                "If a run doesn't stop within 5 s, its thread is abandoned and the F# session is reset."
            ]
        )
        ToolTip.SetTip(
            reset,
            tip [ "Start a new F# session. All values, functions and types defined by earlier runs are lost."; "Use it if the session is in a bad state." ]
        )
        ToolTip.SetTip(settingsToggle, "Show or hide the settings.")
        ToolTip.SetTip(errorsToggle, tip [ "Show or hide the internal errors of Caddis,"; "e.g. that the F# session could not be created." ])
        ToolTip.SetTip(clearErrors, tip [ "Remove these errors from the list."; "The Errors button is hidden until the next error." ])
        ToolTip.SetTip(fsiStatus, modeTip)
        ToolTip.SetTip(autoStart, $"Start the server when {host.Name} starts.")
        ToolTip.SetTip(echo, tip [ "Also print the output of runs to the Rhino command line:"; "printfn, F# Interactive's output and errors." ])
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
        logo.PointerReleased.Add(fun e ->
            if e.InitialPressMouseButton = MouseButton.Left then
                e.Handled <- true
                host.OpenUrl repoUrl)
        copyMcp.Click.Add(fun _ -> host.CopyMcpCommand (); refresh ())
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
    member _.Refresh() = refresh ()

    override _.OnAttachedToVisualTree(e) =
        base.OnAttachedToVisualTree e
        applyTheme ()
        if themeChanges.IsNone then
            themeChanges <- Some(host.SubscribeTheme (fun () -> Dispatcher.UIThread.Post applyTheme))

    override _.OnDetachedFromVisualTree(e) =
        themeChanges |> Option.iter _.Dispose()
        themeChanges <- None
        base.OnDetachedFromVisualTree e
