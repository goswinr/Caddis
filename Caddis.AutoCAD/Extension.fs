namespace Caddis.AutoCAD

open Autodesk.AutoCAD.Runtime
open Caddis

/// The entry point. AutoCAD calls Initialize on its main thread when it loads Caddis.AutoCAD.dll, via NETLOAD or Caddis.AutoCAD.Loader,
/// and Terminate when it closes.
type CaddisExtension() =

    /// Draws the panel and the ribbon icons again when AutoCAD's theme changes.
    static let onSystemVariableChanged =
        Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventHandler(fun _ e ->
            if e.Name = "COLORTHEME" then
                PanelTheme.refresh ()
                Ribbon.refreshIcons ())

    interface IExtensionApplication with

        member _.Initialize() =
            try
                UiThread.init ()
                // again once AutoCAD is in its message loop, in case it loads Caddis before it has its main window
                let setMainWindow () = AutoCADDialogs.mainWindow <- Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle
                try setMainWindow () with _ -> ()
                UiThread.postLater setMainWindow
                // before Caddis.Core uses System.Text.Json or FCS
                try FsiAssemblyResolver.install ()
                with e -> AutoCADLog.error $"Caddis: putting its assembly resolver first failed: {e.Message}"
                let host = AutoCADHost()
                // its own settings file and port, so that Caddis can run in Rhino, Revit and AutoCAD at the same time
                let store = SettingsStore(SettingsStore.DefaultFolder, "settings-autocad.json", { CaddisSettings.defaults with port = 7830 })
                let server = new CaddisServer(host, store)
                App.server <- Some server
                Autodesk.AutoCAD.ApplicationServices.Core.Application.BeginQuit.Add(fun _ -> App.shutdown ())
                Autodesk.AutoCAD.ApplicationServices.Core.Application.add_SystemVariableChanged onSystemVariableChanged
                App.stateChanged <- Ribbon.update
                try Ribbon.init () with e -> AutoCADLog.error $"Caddis: adding its ribbon tab failed: {e}"
                AutoCADLog.writeLine $"Caddis {Updates.version} is loaded. Its ribbon tab and the command CADDIS start it and change its settings."
                if server.Service.Settings.autoStart then App.start true
                Updates.check ()
            with e ->
                AutoCADLog.error $"Caddis failed to load: {e}"

        member _.Terminate() =
            App.shutdown ()
            Updates.installOnExit ()


/// AutoCAD finds the entry point and the commands through these attributes.
module AssemblyAttributes =

    [<assembly: ExtensionApplication(typeof<CaddisExtension>)>]
    [<assembly: CommandClass(typeof<CaddisCommands>)>]
    do ()
