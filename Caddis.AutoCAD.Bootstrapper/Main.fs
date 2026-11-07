namespace Caddis.AutoCAD.Bootstrapper

open System
open System.Diagnostics
open System.Reflection
open System.Windows
open System.Windows.Controls
open System.Windows.Documents
open System.Windows.Media
open System.Windows.Threading
open Velopack

/// The app that Velopack installs, updates and uninstalls. It registers Caddis with AutoCAD and shows what it did.
/// Caddis itself runs in AutoCAD. It starts this app to check for updates and to install them when the last AutoCAD has closed.
/// A copy of Caddis.Revit.Bootstrapper/Main.fs for AutoCAD.
module Main =

    let private version = Assembly.GetExecutingAssembly().GetName().Version.ToString 3

    let private readme = "https://github.com/goswinr/Caddis/blob/main/Caddis.AutoCAD/README.md"

    /// The messages as a read-only document, problems in red, with a link to the README.
    let private document (messages: Message seq) =
        let doc = FlowDocument(PagePadding = Thickness 16.0, FontFamily = FontFamily "Segoe UI", FontSize = 13.0)
        let add (brush: Brush) (inline': Inline) =
            doc.Blocks.Add(Paragraph(inline', Foreground = brush, Margin = Thickness(0.0, 0.0, 0.0, 8.0)))
        for m in messages do
            match m with
            | Info text -> add Brushes.Black (Run text)
            | Done text -> add Brushes.DarkGreen (Run text)
            | Problem text -> add Brushes.Red (Run text)
        let link = Hyperlink(Run readme, NavigateUri = Uri readme)
        link.RequestNavigate.Add(fun e -> Process.Start(ProcessStartInfo(e.Uri.AbsoluteUri, UseShellExecute = true)) |> ignore)
        add Brushes.Black link
        doc

    /// A button that counts down the seconds until the window closes. Clicking it keeps the window open.
    let private autoClose (win: Window) (seconds: int) =
        let left = ref seconds
        let text () = $"Keep open (closing in {left.Value} s)"
        let button = Button(Content = text (), Padding = Thickness(12.0, 4.0, 12.0, 4.0), Margin = Thickness 8.0, HorizontalAlignment = HorizontalAlignment.Right)
        let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 1.0)
        timer.Tick.Add(fun _ ->
            left.Value <- left.Value - 1
            if left.Value > 0 then
                button.Content <- text ()
            else
                timer.Stop()
                win.Close())
        button.Click.Add(fun _ ->
            timer.Stop()
            button.Visibility <- Visibility.Collapsed)
        win.Loaded.Add(fun _ -> timer.Start())
        button

    /// Registers Caddis again, to show where, and shows what it did.
    let private showWindow (updatedTo: string option) =
        let messages = ResizeArray [ Info $"Caddis {version}, an F# interactive host for AutoCAD, controlled via HTTP and MCP." ]
        updatedTo |> Option.iter (fun v -> messages.Add(Done $"Caddis was updated to {v}."))
        AutoCAD.register messages.Add
        messages.Add(Info "Start AutoCAD and use the Caddis tab on its ribbon, or its command CADDIS. A running AutoCAD loads Caddis right away.")
        messages.Add(Info "If AutoCAD asks whether to load Caddis.AutoCAD.Loader.dll, choose 'Always Load'.")
        messages.Add(Info "You can close this window now. Read more about Caddis in AutoCAD at:")
        let win = Window(Title = "Caddis for AutoCAD", Width = 820.0, Height = 480.0, WindowStartupLocation = WindowStartupLocation.CenterScreen)
        let button = autoClose win 30
        DockPanel.SetDock(button, Dock.Bottom)
        let panel = DockPanel()
        panel.Children.Add button |> ignore
        panel.Children.Add(FlowDocumentScrollViewer(Document = document messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto)) |> ignore
        win.Content <- panel
        Application().Run win

    [<EntryPoint; STAThread>]
    let main args =
        try
            // Velopack starts this exe with arguments when it installs, updates or uninstalls Caddis, then Run calls the hook and exits.
            let updatedTo = ref None
            VelopackApp
                .Build()
                .SetAutoApplyOnStartup(false) // AutoCAD may be using Caddis, see Updates.installOnExit
                .OnAfterInstallFastCallback(fun _ -> AutoCAD.register ignore)
                .OnAfterUpdateFastCallback(fun _ -> AutoCAD.register ignore) // e.g. for a new loader
                .OnBeforeUninstallFastCallback(fun _ -> AutoCAD.unregister ignore)
                .OnRestarted(fun v -> updatedTo.Value <- Some(string v)) // by Update.exe, after installing an update
                .Run()
            // Caddis in AutoCAD starts this exe with these arguments, see Caddis.AutoCAD/Updates.fs
            match args with
            | [| "--check-for-update" |] ->
                Updates.check ()
                0
            | [| "--install-update"; autocadId |] ->
                Updates.installOnExit (int autocadId)
                0
            | _ -> showWindow updatedTo.Value
        with e ->
            Dialog.error $"Caddis.AutoCAD.Bootstrapper failed:{Environment.NewLine}{e}"
            1
