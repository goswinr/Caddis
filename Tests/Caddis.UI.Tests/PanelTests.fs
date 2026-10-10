module Caddis.UI.Tests.PanelTests

open System
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Headless
open Avalonia.Headless.XUnit
open Avalonia.Interactivity
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Threading
open AvaloniaEdit
open Xunit
open Caddis
open Caddis.UI

type TestAppBuilder =
    static member BuildAvaloniaApp() =
        AppBuilder.Configure<PanelApp>().UseHeadless(AvaloniaHeadlessPlatformOptions())

[<assembly: AvaloniaTestApplication(typeof<TestAppBuilder>)>]
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

let palette dark = {
    IsDark = dark
    Colors = [
        for key in [ "Panel"; "Surface"; "Input"; "Text"; "Disabled"; "Border"; "DisabledBorder"; "Divider"
                     "Button"; "ButtonText"; "Hover"; "Pressed"; "Accent"; "ListHover"; "Selection"; "SelectionText" ] do
            key, (if dark then Colors.Black else Colors.White)
    ]
}

let host name = {
    Name = name
    ModeTip = $"{name} API context"
    CommandLineTitle = if name = "Rhino" then Some "Rhino command line" else None
    ChangeNoun = if name = "Rhino" then "objects" else "elements"
    Server = fun () -> None
    Start = ignore
    Stop = ignore
    CopyMcpCommand = ignore
    CopyToken = ignore
    SetMode = ignore
    SetAutoStart = ignore
    SetListenOnLan = ignore
    SetEcho = if name = "Rhino" then Some ignore else None
    SetCancelWaitingCommands = if name = "Rhino" then Some ignore else None
    Cancel = ignore
    Reset = ignore
    OpenUrl = ignore
    Message = if name = "Revit" then Some(fun () -> "Copied MCP command") else None
    Refreshed = ignore
    Palette = fun () -> palette false
    SubscribeTheme = fun _ -> { new IDisposable with member _.Dispose() = () }
}

let click (button: Button) = button.RaiseEvent(RoutedEventArgs(Button.ClickEvent))

[<AvaloniaTheory>]
[<InlineData("Rhino")>]
[<InlineData("Revit")>]
let ``shared XAML loads and exposes the host's options and actions`` name =
    let mutable starts = 0
    let view = PanelView({ host name with Start = fun () -> starts <- starts + 1 })
    let window = Window(Content = view, Width = 600, Height = 600)
    try
        window.Show()
        view.Refresh()
        Assert.Equal($"Start with {name}", string (view.FindControl<CheckBox>("AutoStart").Content))
        Assert.Equal((name = "Rhino"), view.FindControl<CheckBox>("Echo").IsVisible)
        Assert.Equal((name = "Rhino"), view.FindControl<CheckBox>("CancelWaiting").IsVisible)
        Assert.Equal((name = "Revit"), view.FindControl<TextBlock>("Message").IsVisible)
        Assert.Equal(0, starts)
        click (view.FindControl<Button> "StartStop")
        Assert.Equal(1, starts)
        if name = "Revit" then Assert.Equal("Copied MCP command", view.FindControl<TextBlock>("Message").Text)
        Assert.NotNull(view.FindControl<Image>("LogoImage").Source)
        Assert.NotNull(view.FindControl<TextEditor>("CodeView").SyntaxHighlighting)
    finally window.Close()

[<AvaloniaFact>]
let ``theme is scoped and subscriptions survive detaching and reopening`` () =
    let changed = Event<unit>()
    let mutable dark = false
    let mutable subscriptions = 0
    let services = {
        host "Revit" with
            Palette = fun () -> palette dark
            SubscribeTheme = fun callback ->
                subscriptions <- subscriptions + 1
                let sub = changed.Publish.Subscribe(fun () -> callback ())
                { new IDisposable with member _.Dispose() = sub.Dispose(); subscriptions <- subscriptions - 1 }
    }
    let view = PanelView(services)
    let window = Window(Content = view)
    let applicationTheme = Application.Current.RequestedThemeVariant
    try
        window.Show()
        Assert.Equal(1, subscriptions)
        dark <- true
        changed.Trigger()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(ThemeVariant.Dark, view.FindControl<ThemeVariantScope>("Scope").RequestedThemeVariant)
        Assert.Equal(applicationTheme, Application.Current.RequestedThemeVariant)
        window.Content <- null
        Assert.Equal(0, subscriptions)
        dark <- false
        window.Content <- view
        Assert.Equal(1, subscriptions)
        Assert.Equal(ThemeVariant.Light, view.FindControl<ThemeVariantScope>("Scope").RequestedThemeVariant)
    finally window.Close()
    Assert.Equal(0, subscriptions)

let run kind code =
    Run("r1", kind, UiThread, code, "example.fsx", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N"), 10000, false)

[<AvaloniaFact>]
let ``Revit uses the shared formatted code viewer and can show the original code`` () = task {
    use engineHost = new HeadlessHost(ignore)
    let store = SettingsStore(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N"))
    use server = new CaddisServer(engineHost, store)
    let r = run FSharpCode "let answer=42"
    r.StdOut.Append "Test output"
    r.Complete(RunOutcome.ofStatus Succeeded) |> ignore
    server.Service.Runs.Add r
    let mutable settingsChanges = 0
    let view = PanelView({ host "Revit" with Server = (fun () -> Some server); SetMode = (fun _ -> settingsChanges <- settingsChanges + 1) })
    let window = Window(Content = view, Width = 600, Height = 600)
    try
        window.Show()
        view.Refresh()
        Assert.Equal(0, settingsChanges)
        view.FindControl<ListBox>("Runs").SelectedIndex <- 0
        let editor = view.FindControl<TextEditor> "CodeView"
        Assert.Contains("Test output", view.FindControl<TextBox>("Output").Text)
        let deadline = DateTime.UtcNow.AddSeconds 15
        while not (editor.Text.Contains "let answer = 42") && DateTime.UtcNow < deadline do
            do! Task.Delay 20
        Assert.Contains("let answer = 42", editor.Text)
        let format = view.FindControl<ToggleButton> "FormatToggle"
        format.IsChecked <- Nullable false
        click format
        Assert.Equal(r.Code, editor.Text)
    finally window.Close()
}

[<Theory>]
[<InlineData("Rhino")>]
[<InlineData("Revit")>]
let ``shared output retains command results and grouped element changes`` name =
    let r = run HostCommands "-_Line 0,0,0 1,1,1"
    r.Commands <- Some {
        steps = [ { macro = r.Code; commands = [ "Line", "Success" ]; waitedAt = None } ]
        notRun = []
        created = [| "1" |]
        changed = [| "2"; "3" |]
        changedGroups = if name = "Revit" then [ "Walls", 2 ] else []
        deleted = [||]
        selected = Some [| "1" |]
    }
    r.CommandLine.Append "Length = 12"
    let services = host name
    let text = RunDetails.text services.ChangeNoun services.CommandLineTitle r
    Assert.Contains("Line: Success", text)
    Assert.Contains("selected (1): 1", text)
    if name = "Rhino" then
        Assert.Contains("--- objects ---", text)
        Assert.Contains("changed (2): 2, 3", text)
        Assert.Contains("Length = 12", text)
    else
        Assert.Contains("--- elements ---", text)
        Assert.Contains("changed (2): 2 Walls", text)
        Assert.DoesNotContain("Rhino command line", text)
