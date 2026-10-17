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
    SetMode = ignore
    SetAutoStart = ignore
    SetEcho = if name = "Rhino" then Some ignore else None
    SetCancelWaitingCommands = if name = "Rhino" then Some ignore else None
    Cancel = ignore
    Reset = ignore
    OpenUrl = ignore
    PickScriptPath = fun _ _ -> None
    Message = if name = "Revit" then Some(fun () -> "Copied MCP command") else None
    Errors = ErrorLog 20
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
let ``the Errors button shows only while there are errors, and lists them newest first`` () =
    let services = host "Revit"
    let view = PanelView(services)
    let window = Window(Content = view, Width = 600, Height = 600)
    let toggle = view.FindControl<ToggleButton> "ErrorsToggle"
    try
        window.Show()
        view.Refresh()
        Assert.False toggle.IsVisible
        services.Errors.Add "Caddis: first failure"
        services.Errors.Add "Caddis: second failure"
        view.Refresh()
        Assert.True toggle.IsVisible
        Assert.Equal("2 errors", view.FindControl<TextBlock>("ErrorsText").Text)
        Assert.Equal(FontWeight.Bold, view.FindControl<TextBlock>("ErrorsText").FontWeight)
        Assert.False(view.FindControl<Grid>("ErrorsPane").IsVisible)
        Assert.Equal(2, services.Errors.Unseen)
        toggle.IsChecked <- true
        toggle.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        Assert.True(view.FindControl<Grid>("ErrorsPane").IsVisible)
        let text = view.FindControl<TextBox>("ErrorList").Text
        Assert.True(text.IndexOf "second failure" < text.IndexOf "first failure", text)
        Assert.Equal(0, services.Errors.Unseen)
        Assert.Equal(FontWeight.Normal, view.FindControl<TextBlock>("ErrorsText").FontWeight)
        click (view.FindControl<Button> "ClearErrors")
        Assert.False toggle.IsVisible
        Assert.False(view.FindControl<Grid>("ErrorsPane").IsVisible)
        Assert.Empty services.Errors.Lines
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

let runWithId id kind code =
    Run(id, kind, UiThread, code, "example.fsx", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N"), 10000, false)

let run kind code = runWithId "r1" kind code

let finished id kind status code =
    let r = runWithId id kind code
    r.Complete(RunOutcome.ofStatus status) |> ignore
    r

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
        let format = view.FindControl<CheckBox> "FormatCheck"
        format.IsChecked <- Nullable false
        click format
        Assert.Equal(r.Code, editor.Text)
    finally window.Close()
}

[<AvaloniaFact>]
let ``Run again runs the code of the selected F# run as a new run and selects it`` () = task {
    use engineHost = new HeadlessHost(ignore)
    let store = SettingsStore(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N"))
    use server = new CaddisServer(engineHost, store)
    let outDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N")).FullName
    // Ids that the registry doesn't give, it counts from r1.
    let original = Run("x1", FSharpCode, UiThread, "let again = 6 * 7", Path.Combine(outDir, "script.fsx"), outDir, 10000, false)
    original.Complete(RunOutcome.ofStatus Succeeded) |> ignore
    server.Service.Runs.Add original
    server.Service.Runs.Add(finished "x2" HostCommands Succeeded "-_Line 0,0,0 1,1,1")
    let view = PanelView({ host "Revit" with Server = (fun () -> Some server) })
    let window = Window(Content = view, Width = 600, Height = 600)
    let selectedId (runs: ListBox) = match runs.SelectedItem with :? RunRow as row -> row.Id | _ -> ""
    try
        window.Show()
        view.Refresh()
        let runAgain = view.FindControl<Button> "RunAgain"
        let runs = view.FindControl<ListBox> "Runs"
        Assert.False runAgain.IsEnabled
        runs.SelectedIndex <- 0 // newest first: the command run
        Assert.False runAgain.IsEnabled
        runs.SelectedIndex <- 1
        Assert.True runAgain.IsEnabled
        click runAgain
        let deadline = DateTime.UtcNow.AddSeconds 60
        while not ((selectedId runs).StartsWith "r") && DateTime.UtcNow < deadline do
            do! Task.Delay 20
        Assert.Equal("r1", selectedId runs)
        let again = server.Service.Runs.TryGet("r1").Value
        Assert.Equal(original.Code, again.Code)
        Assert.NotEqual<string>(original.ScriptPath, again.ScriptPath)
        do! again.Completion
        Assert.Equal(Succeeded, again.Status)
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

[<Fact>]
let ``Export all keeps only the successful F# runs, each after a comment with its id`` () =
    let runs = [|
        finished "r1" FSharpCode Succeeded "let a = 1\nlet b = a + 1\n"
        finished "r2" FSharpCode CompileError "let c ="
        finished "c3" HostCommands Succeeded "-_Line 0,0,0 1,1,1"
        finished "r4" FSharpCode Succeeded "printfn \"%d\" b"
    |]
    let exported = ScriptExport.exportable runs
    Assert.Equal<string[]>([| "r1"; "r4" |], exported |> Array.map _.Id)
    let script = ScriptExport.combine "Rhino" [ for r in exported -> r, r.Code ]
    let lines = script.Split([| Environment.NewLine |], StringSplitOptions.None)
    Assert.StartsWith("// 2 successful F# runs in Rhino", lines[0])
    Assert.StartsWith("// ---- run r1, started ", ScriptExport.separator exported[0])
    let first = Array.IndexOf(lines, ScriptExport.separator exported[0])
    let second = Array.IndexOf(lines, ScriptExport.separator exported[1])
    Assert.True(0 < first && first < second)
    Assert.Equal<string[]>([| "let a = 1"; "let b = a + 1"; "" |], lines[first + 1 .. second - 1])
    Assert.Equal("printfn \"%d\" b", lines[second + 1])
    Assert.DoesNotContain("let c =", script)
    Assert.DoesNotContain("\n", script.Replace(Environment.NewLine, ""))

[<AvaloniaFact>]
let ``Export saves the code of the selected run and Export all the successful F# runs`` () = task {
    let messages = Collections.Concurrent.ConcurrentQueue<string>()
    use engineHost = new HeadlessHost(messages.Enqueue)
    let store = SettingsStore(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N"))
    use server = new CaddisServer(engineHost, store)
    for r in [ finished "r1" FSharpCode Succeeded "let answer=42"
               finished "r2" FSharpCode CompileError "let broken ="
               finished "c3" HostCommands Succeeded "-_Line 0,0,0 1,1,1" ] do
        server.Service.Runs.Add r
    let dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N")).FullName
    let mutable suggested = []
    // Without the extension, as if typed in the dialog: .fsx is added.
    let pick _ (fileName: string) =
        suggested <- suggested @ [ fileName ]
        Some(Path.Combine(dir, Path.GetFileNameWithoutExtension fileName))
    let saved (fileName: string) = task {
        let deadline = DateTime.UtcNow.AddSeconds 15
        while not (messages |> Seq.exists (fun m -> m.Contains fileName)) && DateTime.UtcNow < deadline do
            do! Task.Delay 20
        return File.ReadAllText(Path.Combine(dir, fileName))
    }
    let view = PanelView({ host "Rhino" with Server = (fun () -> Some server); PickScriptPath = pick })
    let window = Window(Content = view, Width = 600, Height = 600)
    try
        window.Show()
        let exportCode = view.FindControl<Button> "ExportCode"
        let exportAll = view.FindControl<Button> "ExportAll"
        Assert.False exportAll.IsEnabled
        view.Refresh()
        Assert.True exportAll.IsEnabled
        Assert.False exportCode.IsEnabled
        let runs = view.FindControl<ListBox> "Runs"
        runs.SelectedIndex <- 0 // newest first: the command run
        Assert.False exportCode.IsEnabled
        runs.SelectedIndex <- 2
        Assert.True exportCode.IsEnabled
        click exportCode
        let! single = saved "Caddis-r1.fsx"
        Assert.Equal("let answer = 42", single.Trim())
        view.FindControl<CheckBox>("FormatCheck").IsChecked <- Nullable false
        click exportAll
        Assert.Equal(2, suggested.Length)
        Assert.StartsWith("Caddis-runs-", suggested[1])
        let! all = saved suggested[1]
        Assert.Contains("// 1 successful F# run in Rhino", all)
        Assert.Contains("// ---- run r1, started ", all)
        Assert.Contains("let answer=42", all)
        Assert.DoesNotContain("broken", all)
        Assert.DoesNotContain("-_Line", all)
    finally window.Close()
}
