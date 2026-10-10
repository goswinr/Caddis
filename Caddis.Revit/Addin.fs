namespace Caddis.Revit

open System
open System.Reflection
open Autodesk.Revit.Attributes
open Autodesk.Revit.DB
open Autodesk.Revit.UI
open Caddis

/// The Caddis tab on Revit's ribbon. Its buttons show the state in their text, e.g. 'Start' or 'Stop'.
module internal Ribbon =

    let mutable private startStop: PushButton = null
    let mutable private mode: PushButton = null
    let mutable private autoStart: PushButton = null

    /// Sets the texts of the buttons to the current state, if they changed. On the main thread, also from the panel.
    let update () =
        let set (b: PushButton) (text: string) = if not (isNull b) && b.ItemText <> text then b.ItemText <- text
        match App.server with
        | Some s ->
            set startStop (if s.IsRunning then "Stop" else "Start")
            set mode (if s.Service.Fsi.Mode = UiThread then "Mode: UI" else "Mode: Background")
            set autoStart (if s.Service.Settings.autoStart then "Auto start: on" else "Auto start: off")
        | None -> ()

    let create (uiApp: UIControlledApplication) =
        let tab = "Caddis"
        try uiApp.CreateRibbonTab tab with _ -> () // e.g. after a reload, the tab exists
        let panel = uiApp.CreateRibbonPanel(tab, "Caddis")
        let assembly = Assembly.GetExecutingAssembly().Location
        let button (name: string) (text: string) (command: string) (toolTip: string) =
            new PushButtonData("Caddis" + name, text, assembly, "Caddis.Revit." + command, ToolTip = toolTip)
        let large (d: PushButtonData) =
            d.LargeImage <- Images.logo 32
            d.Image <- Images.logo 16
            d
        startStop <-
            panel.AddItem(
                button "StartStop" "Start" "StartStopCommand" "Starts or stops the Caddis HTTP server with its MCP endpoint for AI agents."
                |> large
            )
            :?> PushButton
        panel.AddItem(
            button "CopyMcp" "Copy MCP" "CopyMcpCommand" "Copies the command to add Caddis to Claude Code, including the token, to the clipboard."
            |> large
        )
        |> ignore
        panel.AddItem(
            button "Panel" "Panel" "PanelCommand" "Shows or hides the Caddis panel with the server settings and the recent runs, their output and code."
            |> large
        )
        |> ignore
        panel.AddSeparator()
        let stacked1 =
            panel.AddStackedItems(
                button "Mode" "Mode: UI" "ModeCommand" "Switches between running scripts on Revit's UI thread in its API context, and on a background thread. Resets the F# session.",
                button "Reset" "Reset F#" "ResetCommand" "Starts a new F# session, all values defined so far are lost.",
                button "Cancel" "Cancel run" "CancelCommand" "Cancels the current run, only possible in background mode."
            )
        mode <- stacked1[0] :?> PushButton
        let stacked2 =
            panel.AddStackedItems(
                button "AutoStart" "Auto start: off" "AutoStartCommand" "Starts the server when Revit starts.",
                button "Info" "Info" "InfoCommand" "Shows the state of the server and the F# session, and the latest messages of Caddis.",
                button "NewToken" "New token" "NewTokenCommand" "Replaces the token that MCP clients need, the old one stops working."
            )
        autoStart <- stacked2[0] :?> PushButton


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
                RevitLog.write $"Caddis: {e}"
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

    interface IExternalApplication with

        member _.OnStartup(uiApp: UIControlledApplication) : Result =
            try
                ApiContext.init ()
                RevitDialogs.mainWindow <- uiApp.MainWindowHandle
                let host = RevitHost(uiApp.ControlledApplication)
                // its own settings file and port, so that Caddis can run in Rhino and Revit at the same time
                let store = SettingsStore(SettingsStore.DefaultFolder, "settings-revit.json", { CaddisSettings.defaults with port = 7820 })
                let server = new CaddisServer(host, store)
                App.server <- Some server
                Ribbon.create uiApp
                App.stateChanged <- Ribbon.update
                try uiApp.RegisterDockablePane(CaddisPanel.PaneId, "Caddis", CaddisPaneProvider())
                with e -> RevitLog.write $"Caddis: registering the panel failed: {e.Message}"
                if server.Service.Settings.autoStart then App.start true
                Ribbon.update ()
                Result.Succeeded
            with e ->
                RevitLog.write $"Caddis failed to load: {e}"
                TaskDialog.Show("Load error", $"Caddis failed to load:{Environment.NewLine}{e.Message}") |> ignore
                Result.Failed

        member _.OnShutdown(_uiApp: UIControlledApplication) : Result =
            App.shutdown ()
            ApiContext.shutdown ()
            Result.Succeeded
