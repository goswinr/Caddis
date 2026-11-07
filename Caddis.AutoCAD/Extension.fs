namespace Caddis.AutoCAD

open Autodesk.AutoCAD.Runtime
open Caddis

/// The entry point. AutoCAD calls Initialize on its main thread when it loads Caddis.AutoCAD.dll, e.g. via NETLOAD, and Terminate when it closes.
type CaddisExtension() =

    interface IExtensionApplication with

        member _.Initialize() =
            try
                UiThread.init ()
                // before Caddis.Core uses System.Text.Json or FCS
                try FsiAssemblyResolver.install ()
                with e -> AutoCADLog.error $"Caddis: putting its assembly resolver first failed: {e.Message}"
                let host = AutoCADHost()
                // its own settings file and port, so that Caddis can run in Rhino, Revit and AutoCAD at the same time
                let store = SettingsStore(SettingsStore.DefaultFolder, "settings-autocad.json", { CaddisSettings.defaults with port = 7830 })
                let server = new CaddisServer(host, store)
                App.server <- Some server
                Autodesk.AutoCAD.ApplicationServices.Core.Application.BeginQuit.Add(fun _ -> App.shutdown ())
                AutoCADLog.writeLine $"Caddis {server.Service.Version} is loaded. The command CADDIS starts it and changes its settings."
                if server.Service.Settings.autoStart then App.start true
            with e ->
                AutoCADLog.error $"Caddis failed to load: {e}"

        member _.Terminate() = App.shutdown ()


/// AutoCAD finds the entry point and the commands through these attributes.
module AssemblyAttributes =

    [<assembly: ExtensionApplication(typeof<CaddisExtension>)>]
    [<assembly: CommandClass(typeof<CaddisCommands>)>]
    do ()
