namespace Caddis.Rhino

open System
open System.Runtime.InteropServices
open Eto.Forms

/// A dockable panel to control the Caddis server and see the recent runs and their output.
/// Rhino docks Eto panels, its content is the Avalonia PanelView, which follows Rhino's light, dark or custom theme.
[<Guid("f3b1a864-f96c-4fcc-b7f4-325d0dab262b")>]
type CaddisPanel() as this =
    inherit Panel()

    let timer = new UITimer(Interval = 1.0)

    do
        try
            AvaloniaSetup.ensure ()
            let view = PanelView()
            let host = new AvaloniaHost(view)
            this.Content <- host
            host.SizeChanged.Add(fun _ -> host.SyncDpi())
            timer.Elapsed.Add(fun _ ->
                view.Refresh()
                host.SyncDpi())
            this.LoadComplete.Add(fun _ ->
                view.Refresh()
                timer.Start())
            this.UnLoad.Add(fun _ -> timer.Stop())
        with e ->
            RhinoLog.writeLine $"Caddis: the panel could not start Avalonia: {e}"
            this.Content <-
                new Label(
                    Text = $"The Caddis panel could not be shown: {e.Message}{Environment.NewLine}Use the command 'Caddis' instead.",
                    Wrap = WrapMode.Word
                )
