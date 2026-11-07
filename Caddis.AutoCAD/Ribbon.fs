namespace Caddis.AutoCAD

open System
open System.Windows.Controls
open System.Windows.Input
open System.Windows.Threading
open Autodesk.Windows
open Caddis

/// The Caddis tab on AutoCAD's ribbon, made with AdWindows, the library of AutoCAD's ribbon. Like the tab in Revit, see Caddis.Revit/Addin.fs.
/// Its buttons show the state in their text and icon, e.g. 'Start' or 'Stop'. They call the actions of App on AutoCAD's main thread,
/// in the application context, as the panel does.
module internal Ribbon =

    let private tabId = "Caddis.Tab"

    let mutable private tab: RibbonTab = null
    let mutable private startStop: RibbonButton = null
    let mutable private mode: RibbonButton = null
    let mutable private cancel: RibbonButton = null
    let mutable private info: RibbonButton = null
    let mutable private autoStart: RibbonCheckBox = null
    let mutable private echo: RibbonCheckBox = null

    let private infoTip =
        "Shows the state of the server and the F# session, the commands to add Caddis to Claude Code and Codex, and the latest messages of Caddis."

    /// Every button with its current icon, to draw them again when AutoCAD's theme changes.
    let private icons = Collections.Generic.Dictionary<string, RibbonButton * RibbonIcons.Icon>()

    let private setIcon (b: RibbonButton) icon =
        icons[b.Id] <- (b, icon)
        let dark = PanelTheme.isDark ()
        b.LargeImage <- RibbonIcons.draw dark icon 32
        b.Image <- RibbonIcons.draw dark icon 16

    /// Draws the icons in the colors of AutoCAD's current theme. Called when COLORTHEME changes.
    let refreshIcons () =
        for b, icon in Seq.toList icons.Values do
            setIcon b icon

    /// A command for a ribbon item. It only runs while Caddis is loaded.
    let private command (run: unit -> unit) (after: unit -> unit) =
        let canExecuteChanged = Event<EventHandler, EventArgs>()
        { new ICommand with
            [<CLIEvent>]
            member _.CanExecuteChanged = canExecuteChanged.Publish
            member _.CanExecute _ = App.server.IsSome
            member _.Execute _ =
                try run ()
                with e -> AutoCADLog.error $"Caddis: {e}"
                after () }

    /// Synchronizes button texts, icons, availability and checkboxes, and adds the tab again if AutoCAD has rebuilt its ribbon,
    /// e.g. after a change of the workspace. Called on the UI thread by a timer and after the panel refreshed.
    let update () =
        match ComponentManager.Ribbon with
        | null -> ()
        | ribbon when not (isNull tab) && not (ribbon.Tabs.Contains tab) -> ribbon.Tabs.Add tab
        | _ -> ()
        let set (b: RibbonButton) (text: string) icon =
            if not (isNull b) && b.Text <> text then
                b.Text <- text
                setIcon b icon
        let check (c: RibbonCheckBox) value =
            if not (isNull c) && c.IsChecked <> Nullable value then c.IsChecked <- Nullable value
        match App.server with
        | Some s ->
            if s.IsRunning then set startStop "Stop\nlistening" RibbonIcons.stop else set startStop "Start\nlistening" RibbonIcons.start
            set mode (if s.Service.Fsi.Mode = UiThread then "Mode:\non UI thread" else "Mode:\non background thread") RibbonIcons.mode
            check autoStart s.Service.Settings.autoStart
            check echo s.Service.Settings.echoOutput
        | None -> ()
        let canCancel =
            App.server
            |> Option.bind (fun s -> s.Service.Fsi.Current)
            |> Option.exists (fun r -> (r.Mode = Background || r.Kind = HostCommands) && not r.IsFinished && not r.CancelRequested)
        if not (isNull cancel) then
            let icon = if canCancel then RibbonIcons.cancel else RibbonIcons.cancelUnavailable
            if snd icons[cancel.Id] <> icon then setIcon cancel icon
            if cancel.IsEnabled <> canCancel then cancel.IsEnabled <- canCancel
        // Until the Info dialog or the panel has shown them, errors turn the Info button red.
        let errors = AutoCADLog.errors.Unseen
        match errors with
        | 0 -> set info "Info" RibbonIcons.info
        | 1 -> set info "Info:\n1 error" RibbonIcons.infoError
        | n -> set info $"Info:\n{n} errors" RibbonIcons.infoError
        let tip = if errors = 0 then infoTip else $"Caddis had errors, this shows them. {infoTip}"
        if not (isNull info) && string info.ToolTip <> tip then info.ToolTip <- tip

    let private button (id: string) (text: string) (toolTip: string) icon (run: unit -> unit) =
        let b =
            RibbonButton(
                Id = "Caddis." + id,
                Text = text,
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = Orientation.Vertical,
                ToolTip = toolTip,
                CommandHandler = command run update
            )
        setIcon b icon
        b

    let private checkBox (id: string) (text: string) (toolTip: string) (set: bool -> unit) =
        let c = RibbonCheckBox(Id = "Caddis." + id, Text = text, ShowText = true, ShowImage = false, ToolTip = toolTip)
        // Setting IsChecked in update doesn't execute the command, so syncing never writes the settings.
        c.CommandHandler <- command (fun () -> set (c.IsChecked.GetValueOrDefault())) update
        c

    let private panel (title: string) (items: RibbonItem list) =
        let source = RibbonPanelSource(Title = title)
        for item in items do
            source.Items.Add item
        RibbonPanel(Source = source)

    let private create (ribbon: RibbonControl) =
        startStop <-
            button "StartStop" "Start\nlistening" "Starts or stops the Caddis HTTP server with its MCP endpoint for AI agents." RibbonIcons.start (fun () ->
                match App.server with
                | Some s when s.IsRunning -> App.stop ()
                | _ -> App.start false)
        mode <-
            button
                "Mode"
                "Mode:\non UI thread"
                "Switches between running scripts on AutoCAD's main thread and on a background thread. Resets the F# session."
                RibbonIcons.mode
                (fun () ->
                    match App.server with
                    | Some s -> App.setMode (if s.Service.Fsi.Mode = UiThread then Background else UiThread)
                    | None -> ())
        let reset =
            button "Reset" "Reset F#\nSession" "Starts a new F# session, all values defined so far are lost." RibbonIcons.reset App.reset
        cancel <-
            button
                "Cancel"
                "Cancel\nrun"
                "Cancels the current run, only possible in background mode and for AutoCAD commands of caddis_command that wait for input."
                RibbonIcons.cancelUnavailable
                App.cancel
        cancel.IsEnabled <- false
        let panelButton =
            button
                "Panel"
                "Caddis Details\nPanel"
                "Shows or hides the Caddis panel with the server settings and the recent runs, their output and code."
                RibbonIcons.panel
                Panel.toggle
        info <- button "Info" "Info" infoTip RibbonIcons.info App.showInfo
        autoStart <- checkBox "AutoStart" "Start with AutoCAD" "Start the server when AutoCAD starts." App.setAutoStart
        echo <-
            checkBox "EchoOutput" "Echo output" "Also print the output of runs to the AutoCAD command line: printfn, F# Interactive's output and errors." App.setEcho
        let settings = RibbonRowPanel(Id = "Caddis.Settings")
        settings.Items.Add autoStart
        settings.Items.Add(RibbonRowBreak())
        settings.Items.Add echo
        tab <- RibbonTab(Title = "Caddis", Id = tabId)
        // AutoCAD shows every panel's title below its buttons, so the panels group the buttons by what they act on.
        for p in
            [
                panel "Server" [ startStop ]
                panel "F# Session" [ mode; reset; cancel ]
                panel "Status" [ panelButton; info ]
                panel "Settings" [ settings ]
            ] do
            tab.Panels.Add p
        ribbon.Tabs.Add tab
        update ()
        let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 0.5)
        timer.Tick.Add(fun _ -> try update () with e -> AutoCADLog.error $"Caddis: updating the ribbon failed: {e.Message}")
        timer.Start()

    /// Adds the tab to the ribbon. When Caddis loads at AutoCAD's start, the ribbon may not exist yet: then once AdWindows has created it.
    let init () =
        match ComponentManager.Ribbon with
        | null ->
            let handler = ref null
            handler.Value <-
                EventHandler<RibbonItemEventArgs>(fun _ _ ->
                    match ComponentManager.Ribbon with
                    | null -> ()
                    | ribbon ->
                        ComponentManager.ItemInitialized.RemoveHandler handler.Value
                        try create ribbon with e -> AutoCADLog.error $"Caddis: adding its ribbon tab failed: {e}")
            ComponentManager.ItemInitialized.AddHandler handler.Value
        | ribbon -> create ribbon
