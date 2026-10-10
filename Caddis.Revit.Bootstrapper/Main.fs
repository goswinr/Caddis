namespace Caddis.Revit.Bootstrapper

open System
open System.Diagnostics
open System.Reflection
open System.Windows
open System.Windows.Controls
open System.Windows.Documents
open System.Windows.Media
open Velopack

/// The app that Velopack installs, updates and uninstalls. It registers Caddis with Revit and shows what it did.
/// Caddis itself runs in Revit. It starts this app to check for updates and to install them when the last Revit has closed.
module Main =

    let private version = Assembly.GetExecutingAssembly().GetName().Version.ToString 3

    let private readme = "https://github.com/goswinr/Caddis#revit"

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

    /// Registers Caddis again, to show where, and for a Revit installed since, and shows what it did.
    let private showWindow (updatedTo: string option) =
        let messages = ResizeArray [ Info $"Caddis {version}, an F# interactive host for Revit, controlled via HTTP and MCP." ]
        updatedTo |> Option.iter (fun v -> messages.Add(Done $"Caddis was updated to {v}."))
        Revit.register messages.Add
        messages.Add(Info "Start Revit and use the Caddis tab on its ribbon. If Revit asks whether to load Caddis, choose 'Always Load'.")
        messages.Add(Info $"To register Caddis with a Revit that you install later, start this program again: {Revit.installFolder}Caddis.Revit.Bootstrapper.exe")
        messages.Add(Info "You can close this window now. Read more about Caddis in Revit at:")
        let win =
            Window(
                Title = "Caddis for Revit",
                Width = 820.0,
                Height = 440.0,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = FlowDocumentScrollViewer(Document = document messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
            )
        Application().Run win

    [<EntryPoint; STAThread>]
    let main args =
        try
            // Velopack starts this exe with arguments when it installs, updates or uninstalls Caddis, then Run calls the hook and exits.
            let updatedTo = ref None
            VelopackApp
                .Build()
                .SetAutoApplyOnStartup(false) // Revit may be using Caddis, see Updates.installOnExit
                .OnAfterInstallFastCallback(fun _ -> Revit.register ignore)
                .OnAfterUpdateFastCallback(fun _ -> Revit.register ignore) // e.g. for a Revit installed since
                .OnBeforeUninstallFastCallback(fun _ -> Revit.unregister ignore)
                .OnRestarted(fun v -> updatedTo.Value <- Some(string v)) // by Update.exe, after installing an update
                .Run()
            // Caddis in Revit starts this exe with these arguments, see Caddis.Revit/Updates.fs
            match args with
            | [| "--check-for-update" |] ->
                Updates.check ()
                0
            | [| "--install-update"; revitId |] ->
                Updates.installOnExit (int revitId)
                0
            | _ -> showWindow updatedTo.Value
        with e ->
            MessageBox.Show($"Caddis.Revit.Bootstrapper failed:{Environment.NewLine}{e}", "Caddis for Revit", MessageBoxButton.OK, MessageBoxImage.Error) |> ignore
            1
