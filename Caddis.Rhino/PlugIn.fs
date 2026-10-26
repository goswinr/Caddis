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
        if App.server.IsSome then
            // Loading the .rhp again, e.g. by dragging it into Rhino, calls OnLoad of a second instance
            // before Rhino refuses it because its id is in use. It must not start a second server.
            errorMessage <- "Caddis is already loaded."
            LoadReturnCode.ErrorNoDialog
#if !NETFRAMEWORK
        elif Environment.Version.Major < 8 then
            errorMessage <-
                $"Caddis needs .NET 8, but Rhino is running on {RuntimeInformation.FrameworkDescription}. Use the Rhino command 'SetDotNetRuntime' to switch."
            LoadReturnCode.ErrorShowDialog
#endif
        else
            try
                // before Caddis.Core uses System.Text.Json or FCS, so that on .NET Framework it gets its versions, not Rhino's older ones
                try FsiAssemblyResolver.install ()
                with e -> RhinoLog.error $"Caddis: putting its assembly resolver first failed: {e.Message}"
                RhinoScripting.install ()
                let server = new CaddisServer(RhinoHost(), SettingsStore())
                App.server <- Some server
                RhinoApp.Closing.Add(fun _ ->
                    PanelPlacement.save ()
                    App.shutdown ())
                try
                    Rhino.UI.Panels.RegisterPanel(
                        this, typeof<CaddisPanel>, "Caddis", typeof<CaddisPanel>.Assembly,
                        "Caddis.Rhino.Resources.Caddis.ico", Rhino.UI.PanelType.PerDoc
                    )
                    PanelPlacement.restoreAtStart ()
                with e ->
                    RhinoLog.error $"Caddis: registering the panel failed: {e.Message}"
                Updates.check ()
                if server.Service.Settings.autoStart then App.start ()
                LoadReturnCode.Success
            with e ->
                errorMessage <- $"Caddis failed to load: {e}"
                LoadReturnCode.ErrorShowDialog

    override _.OnShutdown() = App.shutdown ()
