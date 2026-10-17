namespace Caddis.Revit

open System
open System.Reflection
open Autodesk.Revit.Attributes
open Autodesk.Revit.DB
open Autodesk.Revit.UI
open Caddis

/// The Caddis tab on Revit's ribbon. Its buttons show the state in their text and icon, e.g. 'Start' or 'Stop'.
module internal Ribbon =

    let mutable private startStop: PushButton = null
    let mutable private mode: PushButton = null
    let mutable private cancel: PushButton = null
    let mutable private info: PushButton = null
    let private infoTip = "Shows the state of the server and the F# session, and the latest messages of Caddis."
    /// Every button with its current icon, to draw them again when Revit's theme changes.
    let private icons = Collections.Generic.Dictionary<string, PushButton * RibbonIcons.Icon>()

    let private setIcon (b: PushButton) icon =
        icons[b.Name] <- (b, icon)
        b.LargeImage <- RibbonIcons.draw icon 32
        b.Image <- RibbonIcons.draw icon 16

    /// Draws the icons in the colors of Revit's current theme. Called by Revit's ThemeChanged event.
    let refreshIcons () =
        for b, icon in Seq.toList icons.Values do
            setIcon b icon

    /// Synchronizes button labels, icons, availability and checkboxes. Called on the UI thread from the panel and Revit's Idling event.
    let update () =
        let set (b: PushButton) (text: string) icon =
            if not (isNull b) && b.ItemText <> text then
                b.ItemText <- text
                setIcon b icon
        match App.server with
        | Some s ->
            if s.IsRunning then set startStop "Stop\r\nlistening" RibbonIcons.stop else set startStop "Start\r\nlistening" RibbonIcons.start
            set mode (if s.Service.Fsi.Mode = UiThread then "Mode:\r\non UI thread" else "Mode:\r\non background thread") RibbonIcons.mode
            RibbonSettings.update ()
        | None -> ()
        let canCancel =
            App.server
            |> Option.bind (fun s -> s.Service.Fsi.Current)
            |> Option.exists (fun r -> r.Mode = Background && not r.IsFinished && not r.CancelRequested)
        if not (isNull cancel) then
            let icon = if canCancel then RibbonIcons.cancel else RibbonIcons.cancelUnavailable
            if snd icons[cancel.Name] <> icon then setIcon cancel icon
            if cancel.Enabled <> canCancel then cancel.Enabled <- canCancel
        // Until the Info dialog or the panel has shown them, errors turn the Info button red. Revit can't color a button's text.
        let errors = RevitLog.errors.Unseen
        match errors with
        | 0 -> set info "Info" RibbonIcons.info
        | 1 -> set info "Info:\r\n1 error" RibbonIcons.infoError
        | n -> set info $"Info:\r\n{n} errors" RibbonIcons.infoError
        let tip = if errors = 0 then infoTip else $"Caddis had errors, this shows them. {infoTip}"
        if not (isNull info) && info.ToolTip <> tip then info.ToolTip <- tip

    let create (uiApp: UIControlledApplication) =
        let tab = "Caddis"
        try uiApp.CreateRibbonTab tab with _ -> () // e.g. after a reload, the tab exists
        let assembly = Assembly.GetExecutingAssembly().Location
        let add (panel: RibbonPanel) (name: string) (text: string) (command: string) (toolTip: string) icon =
            let data = new PushButtonData("Caddis" + name, text, assembly, "Caddis.Revit." + command, ToolTip = toolTip)
            let b = panel.AddItem data :?> PushButton
            setIcon b icon
            b
        // Revit shows every panel's title below its buttons, so the panels group the buttons by what they act on.
        let server = uiApp.CreateRibbonPanel(tab, "Server")
        startStop <-
            add server "StartStop" "Start\r\nlistening" "StartStopCommand" "Starts or stops the Caddis HTTP server with its MCP endpoint for AI agents." RibbonIcons.start
        add server "CopyMcp" "Copy MCP\r\nCommand" "CopyMcpCommand" "Copies the command to add Caddis to Claude Code, including the token, to the clipboard." RibbonIcons.copy
        |> ignore
        add server "NewToken" "New\r\ntoken" "NewTokenCommand" "Replaces the token that MCP clients need, the old one stops working." RibbonIcons.newToken
        |> ignore
        let session = uiApp.CreateRibbonPanel(tab, "F# Session")
        mode <-
            add
                session
                "Mode"
                "Mode:\r\non UI thread"
                "ModeCommand"
                "Switches between running scripts on Revit's UI thread in its API context, and on a background thread. Resets the F# session."
                RibbonIcons.mode
        add session "Reset" "Reset F#\r\nSession" "ResetCommand" "Starts a new F# session, all values defined so far are lost." RibbonIcons.reset
        |> ignore
        cancel <-
            add session "Cancel" "Cancel\r\nrun" "CancelCommand" "Cancels the current run, only possible in background mode." RibbonIcons.cancelUnavailable
        cancel.Enabled <- false
        let status = uiApp.CreateRibbonPanel(tab, "Status")
        add status "Panel" "Caddis Details\r\nPanel" "PanelCommand" "Shows or hides the Caddis panel with the server settings and the recent runs, their output and code." RibbonIcons.panel
        |> ignore
        info <- add status "Info" "Info" "InfoCommand" infoTip RibbonIcons.info
        uiApp.CreateRibbonPanel(tab, "Settings") |> ignore
        RibbonSettings.create tab "Settings"


/// The commands of the ribbon buttons. Revit runs them in API context.
[<AbstractClass>]
type CaddisCommand() =

    abstract Run: unit -> unit

    interface IExternalCommand with
        member this.Execute(commandData: ExternalCommandData, message: byref<string>, _elements: ElementSet) : Result =
            try
                ApiContext.enter commandData.Application (fun () ->
                    this.Run()
                    Ribbon.update ())
                Result.Succeeded
            with e ->
                RevitLog.error $"Caddis: {e}"
                message <- e.Message
                Result.Failed

[<Transaction(TransactionMode.Manual)>]
type StartStopCommand() =
    inherit CaddisCommand()

    override _.Run() =
        match App.server with
        | Some s when s.IsRunning -> App.stop ()
        | _ -> App.start false

[<Transaction(TransactionMode.Manual)>]
type CopyMcpCommand() =
    inherit CaddisCommand()
    override _.Run() = App.copyMcpCommand ()

[<Transaction(TransactionMode.Manual)>]
type ModeCommand() =
    inherit CaddisCommand()

    override _.Run() =
        match App.server with
        | Some s -> App.setMode (if s.Service.Fsi.Mode = UiThread then Background else UiThread)
        | None -> ()

[<Transaction(TransactionMode.Manual)>]
type ResetCommand() =
    inherit CaddisCommand()
    override _.Run() = App.reset ()

[<Transaction(TransactionMode.Manual)>]
type CancelCommand() =
    inherit CaddisCommand()
    override _.Run() = App.cancel ()

[<Transaction(TransactionMode.Manual)>]
type AutoStartCommand() =
    inherit CaddisCommand()

    override _.Run() =
        match App.server with
        | Some s -> App.setAutoStart (not s.Service.Settings.autoStart)
        | None -> ()

[<Transaction(TransactionMode.Manual)>]
type InfoCommand() =
    inherit CaddisCommand()
    override _.Run() = App.showInfo ()

[<Transaction(TransactionMode.Manual)>]
type NewTokenCommand() =
    inherit CaddisCommand()
    override _.Run() = App.newToken ()

[<Transaction(TransactionMode.Manual)>]
type PanelCommand() =
    inherit CaddisCommand()

    override _.Run() =
        let pane = ApiContext.uiApplication().GetDockablePane CaddisPanel.PaneId
        if pane.IsShown() then pane.Hide() else pane.Show()


/// The entry point, registered in Caddis.addin. Don't rename it.
type CaddisApplication() =

    let themeChanged =
        EventHandler<Autodesk.Revit.UI.Events.ThemeChangedEventArgs>(fun _ _ ->
            PanelTheme.refresh ()
            Ribbon.refreshIcons ())
    // Panel timers stop when hidden; idling also catches settings and mode changes made over HTTP.
    let idling = EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs>(fun _ _ -> Ribbon.update ())

    interface IExternalApplication with

        member _.OnStartup(uiApp: UIControlledApplication) : Result =
            try
                // before Caddis.Core uses System.Text.Json or FCS, so that on .NET Framework it gets its versions, not Revit's older ones
                try FsiAssemblyResolver.install ()
                with e -> RevitLog.error $"Caddis: putting its assembly resolver first failed: {e.Message}"
                ApiContext.init ()
                RevitDialogs.mainWindow <- uiApp.MainWindowHandle
                let host = RevitHost(uiApp.ControlledApplication)
                // its own settings file and port, so that Caddis can run in Rhino and Revit at the same time
                let store = SettingsStore(SettingsStore.DefaultFolder, "settings-revit.json", { CaddisSettings.defaults with port = 7820 })
                let server = new CaddisServer(host, store)
                App.server <- Some server
                Ribbon.create uiApp
                App.stateChanged <- Ribbon.update
                uiApp.ThemeChanged.AddHandler themeChanged
                uiApp.Idling.AddHandler idling
                try uiApp.RegisterDockablePane(CaddisPanel.PaneId, "Caddis", CaddisPaneProvider())
                with e -> RevitLog.error $"Caddis: registering the panel failed: {e.Message}"
                if server.Service.Settings.autoStart then App.start true
                Ribbon.update ()
                Updates.check ()
                Result.Succeeded
            with e ->
                RevitLog.error $"Caddis failed to load: {e}"
                TaskDialog.Show("Load error", $"Caddis failed to load:{Environment.NewLine}{e.Message}") |> ignore
                Result.Failed

        member _.OnShutdown(uiApp: UIControlledApplication) : Result =
            uiApp.ThemeChanged.RemoveHandler themeChanged
            uiApp.Idling.RemoveHandler idling
            App.shutdown ()
            ApiContext.shutdown ()
            Updates.installOnExit ()
            Result.Succeeded
