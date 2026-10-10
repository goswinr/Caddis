namespace Caddis.Rhino

open System
open System.Runtime.InteropServices
open Rhino
open Rhino.PlugIns
open Caddis

/// Every .rhp needs exactly one PlugIn class, Rhino creates its instance.
/// It loads at Rhino startup so that the server can start automatically, see the AutoStart setting.
type CaddisPlugIn() =
    inherit PlugIn()

    override _.LoadTime = PlugInLoadTime.AtStartup

    override this.OnLoad(errorMessage) =
        if Environment.Version.Major < 8 then
            errorMessage <-
                $"Caddis needs .NET 8, but Rhino is running on {RuntimeInformation.FrameworkDescription}. Use the Rhino command 'SetDotNetRuntime' to switch."
            LoadReturnCode.ErrorShowDialog
        else
            try
                RhinoScriptingSync.install ()
                let server = new CaddisServer(RhinoHost(), SettingsStore())
                App.server <- Some server
                RhinoApp.Closing.Add(fun _ -> App.shutdown ())
                try
                    Rhino.UI.Panels.RegisterPanel(
                        this, typeof<CaddisPanel>, "Caddis", typeof<CaddisPanel>.Assembly,
                        "Caddis.Rhino.Resources.Caddis.ico", Rhino.UI.PanelType.PerDoc
                    )
                with e ->
                    RhinoLog.writeLine $"Caddis: registering the panel failed: {e.Message}"
                if server.Service.Settings.autoStart then App.start ()
                LoadReturnCode.Success
            with e ->
                errorMessage <- $"Caddis failed to load: {e}"
                LoadReturnCode.ErrorShowDialog

    override _.OnShutdown() = App.shutdown ()
