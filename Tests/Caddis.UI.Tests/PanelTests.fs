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
open Avalonia.VisualTree
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
    CommandLineTitle = if name = "Revit" then None else Some $"{name} command line"
    ChangeNoun = if name = "Revit" then "elements" else "objects"
    Server = fun () -> None
    Start = ignore
    Stop = ignore
    SetClipboard = fun _ -> true
    SetMode = ignore
    SetAutoStart = ignore
    SetEcho = if name = "Revit" then None else Some ignore
    SetCancelWaitingCommands = if name = "Rhino" then Some ignore else None
    Cancel = ignore
    Reset = ignore
    OpenUrl = ignore
    PickScriptPath = fun _ _ -> None
    Message = if name = "Revit" then Some(fun () -> "Copied MCP command") else None
    Errors = ErrorLog 20
    Refreshed = ignore
    Version = fun () -> $"You're using Caddis 1.2.3 in {name}."
    Update = fun () -> None
    VersionTip = "Updates"
    Palette = fun () -> palette false
    SubscribeTheme = fun _ -> { new IDisposable with member _.Dispose() = () }
}

let click (button: Button) = button.RaiseEvent(RoutedEventArgs(Button.ClickEvent))

[<AvaloniaTheory>]
[<InlineData("Rhino")>]
[<InlineData("Revit")>]
[<InlineData("AutoCAD")>]
let ``shared XAML loads and exposes the host's options and actions`` name =
    let mutable starts = 0
    let view = PanelView({ host name with Start = fun () -> starts <- starts + 1 })
    let window = Window(Content = view, Width = 600, Height = 600)
    try
        window.Show()
        view.Refresh()
        Assert.Equal($"Start with {name}", string (view.FindControl<CheckBox>("AutoStart").Content))
        Assert.Equal((name <> "Revit"), view.FindControl<CheckBox>("Echo").IsVisible)
        Assert.Contains($"{name} command line", string (ToolTip.GetTip(view.FindControl<CheckBox>("Echo"))))
        Assert.Equal((name = "Rhino"), view.FindControl<CheckBox>("CancelWaiting").IsVisible)
        Assert.Equal((name = "Revit"), view.FindControl<TextBlock>("Message").IsVisible)
        Assert.Equal(0, starts)
        click (view.FindControl<Button> "StartStop")
        Assert.Equal(1, starts)
        if name = "Revit" then Assert.Equal("Copied MCP command", view.FindControl<TextBlock>("Message").Text)
        Assert.Equal($"You're using Caddis 1.2.3 in {name}.", view.FindControl<TextBlock>("Version").Text)
        Assert.NotNull(view.FindControl<Image>("LogoImage").Source)
        Assert.NotNull(view.FindControl<TextEditor>("CodeView").SyntaxHighlighting)
    finally window.Close()

[<AvaloniaFact>]
let ``the update notice shows only while the host has one`` () =
    let mutable update = None
    let view = PanelView({ host "Rhino" with Update = fun () -> update })
    let window = Window(Content = view, Width = 600, Height = 600)
    let notice = view.FindControl<Border> "UpdateNotice"
    try
        window.Show()
        view.Refresh()
        Assert.False notice.IsVisible
        update <- Some "Caddis 1.2.4 is installed, Rhino loads it when it restarts. You're using 1.2.3."
        view.Refresh()
        Assert.True notice.IsVisible
        Assert.Equal(update.Value, view.FindControl<TextBlock>("UpdateText").Text)
        update <- None
        view.Refresh()
        Assert.False notice.IsVisible
    finally window.Close()

[<AvaloniaFact>]
let ``the errors notice shows only while there are internal errors, and lists them newest first`` () =
    let services = host "Revit"
    let view = PanelView(services)
    let window = Window(Content = view, Width = 600, Height = 600)
    let notice = view.FindControl<Border> "ErrorsNotice"
    let toggle = view.FindControl<ToggleButton> "ErrorsToggle"
    try
        window.Show()
        view.Refresh()
        Assert.False notice.IsVisible
        services.Errors.Add "Caddis: first failure"
        services.Errors.Add "Caddis: second failure"
        view.Refresh()
        Assert.True notice.IsVisible
        // amber, not red like a failed run
        Assert.Contains("warning", notice.Classes)
        Assert.Equal("2 internal errors", view.FindControl<TextBlock>("ErrorsText").Text)
        Assert.Equal("Show", string toggle.Content)
        Assert.Equal(FontWeight.Bold, view.FindControl<TextBlock>("ErrorsText").FontWeight)
        Assert.False(view.FindControl<Grid>("ErrorsPane").IsVisible)
        Assert.Equal(2, services.Errors.Unseen)
        toggle.IsChecked <- true
        toggle.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        Assert.True(view.FindControl<Grid>("ErrorsPane").IsVisible)
        Assert.Equal("Hide", string toggle.Content)
        let text = view.FindControl<TextBox>("ErrorList").Text
        Assert.True(text.IndexOf "second failure" < text.IndexOf "first failure", text)
        Assert.Equal(0, services.Errors.Unseen)
        Assert.Equal(FontWeight.Normal, view.FindControl<TextBlock>("ErrorsText").FontWeight)
        click (view.FindControl<Button> "ClearErrors")
        Assert.False notice.IsVisible
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

[<AvaloniaFact>]
let ``while the server runs, its URL copies on a click and the agents show where they are set up and their latest request`` () = task {
    use engineHost = new HeadlessHost(ignore)
    let store = SettingsStore(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N"))
    store.Save { CaddisSettings.defaults with port = 27910 }
    use server = new CaddisServer(engineHost, store)
    let dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N")).FullName
    let path agent = Path.Combine(dir, (match agent with ClaudeCode -> ".claude.json" | Codex -> "config.toml" | ClaudeDesktop -> Path.Combine("Claude", "extensions-installations.json")))
    server.AgentConfigs <- AgentConfigs path
    let copied = ref ""
    let view = PanelView({ host "Revit" with Server = (fun () -> Some server); SetClipboard = fun text -> copied.Value <- text; true })
    let window = Window(Content = view, Width = 600, Height = 600)
    let state name = view.FindControl<TextBlock>(name + "State").Text
    let action name = view.FindControl<Button>(name + "Action")
    let tipOf (c: Control) = string (ToolTip.GetTip c)
    let dotOf name = view.FindControl<Shapes.Ellipse>(name + "Dot").Classes
    try
        window.Show()
        view.Refresh()
        Assert.False(view.FindControl<StackPanel>("Agents").IsVisible)
        match server.Start() with
        | Ok _ -> ()
        | Error e -> failwith e
        // the F# session that Start creates in the background, before the next test creates its own
        do! server.Service.Fsi.Initialize()
        File.WriteAllText(path ClaudeCode, """{"mcpServers":{"caddis":{"type":"http","url":""" + "\"" + server.McpUrl + "\"}}}")
        File.WriteAllText(path Codex, "[mcp_servers.caddis]\nurl = \"http://127.0.0.1:27919/mcp\"\n")
        view.Refresh()
        // The server's URL, which a click copies, and the right-click menu, which also has the URL for agents.
        let copyUrl = view.FindControl<Button> "CopyUrl"
        Assert.True copyUrl.IsVisible
        Assert.Equal(server.Url, view.FindControl<TextBlock>("ServerUrl").Text)
        Assert.Contains("ok", view.FindControl<Shapes.Ellipse>("ServerDot").Classes)
        click copyUrl
        Assert.Equal(server.Url, copied.Value)
        Assert.True(view.FindControl<TextBlock>("CopiedNote").IsVisible)
        Assert.Equal("Copied", view.FindControl<TextBlock>("CopiedNote").Text)
        Assert.True(view.FindControl<Shapes.Path>("CopiedIcon").IsVisible)
        let urlMenu = copyUrl.ContextMenu.ItemsSource |> Seq.cast<MenuItem> |> Seq.toList
        Assert.Equal($"Copy {server.McpUrl}", string urlMenu[1].Header)
        urlMenu[1].RaiseEvent(RoutedEventArgs(MenuItem.ClickEvent))
        Assert.Equal(server.McpUrl, copied.Value)
        Assert.True(view.FindControl<StackPanel>("Agents").IsVisible)
        Assert.Equal("Claude Code", state "Claude")
        Assert.Contains("ok", dotOf "Claude")
        Assert.False((action "Claude").IsVisible)
        Assert.Contains("Claude Code: set up", tipOf (view.FindControl<TextBlock> "ClaudeState"))
        Assert.Contains(server.McpUrl, tipOf (view.FindControl<TextBlock> "ClaudeState"))
        // no request yet: nothing beside the name
        let latest = view.FindControl<TextBlock> "ClaudeLatest"
        Assert.False latest.IsVisible
        Assert.Contains("No request from Claude Code to this Revit yet.", tipOf (view.FindControl<TextBlock> "ClaudeState"))
        Assert.Contains("No MCP requests yet.", tipOf copyUrl)
        // Claude Code starts a session: how long ago beside its name, the request in the tooltips
        use client = new System.Net.Http.HttpClient()
        let initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"claude-code","version":"2.0.0"}}}"""
        use content = new System.Net.Http.StringContent(initialize, System.Text.Encoding.UTF8, "application/json")
        let! resp = client.PostAsync(server.McpUrl, content)
        resp.EnsureSuccessStatusCode() |> ignore
        view.Refresh()
        Assert.True latest.IsVisible
        Assert.Equal("just now", latest.Text)
        Assert.Contains("Latest request to this Revit: initialize at", tipOf (view.FindControl<TextBlock> "ClaudeState"))
        Assert.Contains("Claude Code: initialize, just now", tipOf copyUrl)
        Assert.False((view.FindControl<TextBlock> "CodexLatest").IsVisible)
        Assert.Contains("warn", dotOf "Codex")
        Assert.Contains("Codex: set up elsewhere", tipOf (view.FindControl<TextBlock> "CodexState"))
        Assert.Equal("Switch here", string (action "Codex").Content)
        Assert.Contains("http://127.0.0.1:27919/mcp", tipOf (view.FindControl<TextBlock> "CodexState"))
        // the tooltip of Switch here says how the port is taken over, and what Set up does if it can't be
        Assert.Contains("It asks the Caddis on port 27919 to hand the port over: POST http://127.0.0.1:27919/release.", tipOf (action "Codex"))
        Assert.Contains(server.CodexMcpCommand, tipOf (action "Codex"))
        File.Delete(path Codex)
        view.Refresh()
        Assert.DoesNotContain("ok", dotOf "Codex")
        Assert.DoesNotContain("warn", dotOf "Codex")
        Assert.Contains("Codex: not set up", tipOf (view.FindControl<TextBlock> "CodexState"))
        Assert.Equal("Set up", string (action "Codex").Content)
        // the tooltip of Set up says which commands it runs and which file Codex writes
        Assert.Contains("  " + server.CodexMcpCommand, tipOf (action "Codex"))
        Assert.Contains($"[mcp_servers.caddis] in {path Codex}", tipOf (action "Codex"))
        Assert.Contains("Right-click to copy the commands", tipOf (action "Codex"))
        // a right-click on the row copies the command
        let menu = view.FindControl<StackPanel>("CodexRow").ContextMenu
        let copy = menu.ItemsSource |> Seq.cast<MenuItem> |> Seq.head
        copy.RaiseEvent(RoutedEventArgs(MenuItem.ClickEvent))
        Assert.Equal(server.CodexMcpCommand, copied.Value)
        Assert.Contains("Copied the command", view.FindControl<TextBlock>("AgentNote").Text)
        // Claude Desktop only once it is installed, it gets an extension
        Assert.False(view.FindControl<StackPanel>("DesktopRow").IsVisible)
        Directory.CreateDirectory(Path.GetDirectoryName(path ClaudeDesktop)) |> ignore
        view.Refresh()
        Assert.True(view.FindControl<StackPanel>("DesktopRow").IsVisible)
        Assert.Contains("Claude Desktop: not set up", tipOf (view.FindControl<TextBlock> "DesktopState"))
        Assert.Contains("caddis.mcpb:", tipOf (action "Desktop"))
        Assert.Contains("runs relay.js with its own Node", tipOf (action "Desktop"))
        Assert.DoesNotContain("Right-click", tipOf (action "Desktop"))
        Assert.Null(view.FindControl<StackPanel>("DesktopRow").ContextMenu)
        let extension = Json.obj [ "id", Json.ofString "x"; "manifest", DesktopExtension.manifest (server.DesktopBundle()) ]
        File.WriteAllText(path ClaudeDesktop, Json.toText (Json.obj [ "extensions", Json.obj [ "x", extension ] ]))
        view.Refresh()
        Assert.Contains("ok", dotOf "Desktop")
        Assert.False((action "Desktop").IsVisible)
        server.Stop()
        view.Refresh()
        Assert.False(view.FindControl<StackPanel>("Agents").IsVisible)
        Assert.False copyUrl.IsVisible
        Assert.Equal("Stopped", view.FindControl<TextBlock>("ServerStopped").Text)
    finally window.Close()
}

[<AvaloniaFact>]
let ``recommends the .NET SDK while scripts can't use NuGet packages`` () =
    let prev = Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
    let setHost (path: string) =
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", path)
        DotnetSdk.clearCache ()
    // a dotnet executable without SDKs next to it, like on a PC with only the .NET runtime
    let dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N")).FullName
    let fake = Path.Combine(dir, (if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"))
    File.WriteAllText(fake, "")
    let mutable opened = []
    let view = PanelView({ host "Rhino" with OpenUrl = fun url -> opened <- opened @ [ url ] })
    let window = Window(Content = view, Width = 600, Height = 600)
    try
        window.Show()
        setHost fake
        view.Refresh()
        Assert.True(view.FindControl<Border>("SdkNotice").IsVisible)
        Assert.Equal($"Scripts can't use NuGet packages without the .NET SDK {Environment.Version.Major} or newer.", view.FindControl<TextBlock>("SdkText").Text)
        click (view.FindControl<Button> "GetSdk")
        Assert.Equal<string list>([ $"https://dotnet.microsoft.com/download/dotnet/{Environment.Version.Major}.0" ], opened)
        // the SDK was installed
        let sdk = Directory.CreateDirectory(Path.Combine(dir, "sdk", $"{Environment.Version.Major}.0.100")).FullName
        File.WriteAllText(Path.Combine(sdk, "dotnet.dll"), "")
        DotnetSdk.clearCache ()
        view.Refresh()
        Assert.False(view.FindControl<Border>("SdkNotice").IsVisible)
    finally
        window.Close()
        setHost prev

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
        Assert.Contains("Test output", view.FindControl<TextEditor>("Output").Text)
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

[<AvaloniaFact>]
let ``a running run shows its start time and running .. on yellow in its row, which stays selected while the header counts up`` () = task {
    use engineHost = new HeadlessHost(ignore)
    let store = SettingsStore(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N"))
    use server = new CaddisServer(engineHost, store)
    let first = finished "r1" FSharpCode Succeeded "let a = 1"
    server.Service.Runs.Add first
    let running = runWithId "r2" FSharpCode "let b = 2"
    server.Service.Runs.Add running
    let view = PanelView({ host "Rhino" with Server = (fun () -> Some server) })
    let window = Window(Content = view, Width = 600, Height = 600)
    try
        window.Show()
        view.Refresh()
        let runs = view.FindControl<ListBox> "Runs"
        runs.SelectedIndex <- 0 // newest first: the running run
        window.UpdateLayout()
        let row = runs.SelectedItem :?> RunRow
        let shown (index: int) =
            runs.ContainerFromIndex(index).GetVisualDescendants()
            |> Seq.choose (function :? TextBlock as t when not (isNull t.Text) -> Some t.Text | _ -> None)
            |> List.ofSeq
        let timeBox (index: int) =
            runs.ContainerFromIndex(index).GetVisualDescendants() |> Seq.pick (function :? Border as b when b.Classes.Contains "runTime" -> Some b | _ -> None)
        Assert.True row.IsRunning
        Assert.Contains("running ..", shown 0)
        Assert.Contains(RunDetails.startMinute running, shown 0)
        Assert.True((timeBox 0).Classes.Contains "running")
        Assert.NotNull((timeBox 0).Background)
        // The finished run: its start time and duration, not on yellow.
        Assert.Contains(RunDetails.startMinute first, shown 1)
        Assert.Contains(shown 1, fun t -> t.EndsWith " ms")
        Assert.False((timeBox 1).Classes.Contains "running")
        Assert.Null((timeBox 1).Background)
        let summary = view.FindControl<TextBlock> "RunSummary"
        let before = summary.Text
        do! Task.Delay 50
        view.Refresh()
        Assert.Same(row, runs.SelectedItem)
        Assert.NotEqual<string>(before, summary.Text)
        // When its status changes, the list is made again, and the run stays selected.
        running.Complete(RunOutcome.ofStatus Succeeded) |> ignore
        view.Refresh()
        Assert.NotSame(row, runs.SelectedItem)
        let done' = runs.SelectedItem :?> RunRow
        Assert.Equal("r2", done'.Id)
        Assert.False done'.IsRunning
        Assert.EndsWith(" ms", done'.Time)
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

[<Fact>]
let ``the output of a failed run starts with what went wrong, and has no summary line`` () =
    let failedRun = runWithId "r7" FSharpCode "let first = slabs[0].Geometry :?> Brep"
    failedRun.StdOut.Append "92 slabs on layer Slabs"
    let exn = { typeName = "System.InvalidCastException"; message = "Unable to cast"; text = "System.InvalidCastException: Unable to cast\n   at main@() in script.fsx:line 3"; scriptLine = Some 3 }
    failedRun.Complete { RunOutcome.ofStatus RuntimeError with exn = Some exn } |> ignore
    let text = RunDetails.text "objects" (Some "Rhino command line") failedRun
    Assert.StartsWith("--- exception ---", text)
    Assert.True(text.IndexOf "--- exception ---" < text.IndexOf "--- stdout ---", text)
    Assert.DoesNotContain("example.fsx", text)
    let ok = finished "r8" FSharpCode Succeeded "let y = 2"
    ok.StdOut.Append "fine"
    Assert.StartsWith("--- stdout ---", RunDetails.text "objects" None ok)

[<Fact>]
let ``durations read at a glance`` () =
    Assert.Equal("140 ms", RunDetails.duration (TimeSpan.FromMilliseconds 140.0))
    Assert.Equal("12.2 s", RunDetails.duration (TimeSpan.FromMilliseconds 12175.0))
    Assert.Equal("2 min 5 s", RunDetails.duration (TimeSpan.FromSeconds 125.0))
    Assert.Equal("runtime error", RunDetails.statusWord RuntimeError)

[<AvaloniaFact>]
let ``the run header names the selected run, and the list shows the status as an icon`` () =
    use engineHost = new HeadlessHost(ignore)
    let store = SettingsStore(Path.Combine(Path.GetTempPath(), "CaddisPanelTests", Guid.NewGuid().ToString "N"))
    use server = new CaddisServer(engineHost, store)
    let view = PanelView({ host "Rhino" with Server = (fun () -> Some server) })
    let window = Window(Content = view, Width = 600, Height = 600)
    try
        window.Show()
        view.Refresh()
        Assert.Equal("0", view.FindControl<TextBlock>("RunCount").Text)
        Assert.True(view.FindControl<TextBlock>("NoRuns").IsVisible)
        Assert.Equal($"Caddis {server.Service.Version}", view.FindControl<TextBlock>("SettingsSummary").Text)
        server.Service.Runs.Add(finished "r1" FSharpCode Succeeded "let a = 1")
        server.Service.Runs.Add(finished "r2" FSharpCode CompileError "// a comment\nlet b =")
        view.Refresh()
        Assert.Equal("2", view.FindControl<TextBlock>("RunCount").Text)
        Assert.False(view.FindControl<TextBlock>("NoRuns").IsVisible)
        Assert.Equal("Select a run to see its output and code.", view.FindControl<TextBlock>("RunSummary").Text)
        Assert.False(view.FindControl<Panel>("RunIcon").IsVisible)
        let runs = view.FindControl<ListBox> "Runs"
        runs.SelectedIndex <- 0 // newest first: the compile error
        let rows = runs.ItemsSource :?> RunRow[]
        Assert.Equal("let b =", rows[0].Code)
        Assert.Equal("compile error", rows[0].Status)
        Assert.NotEqual<IBrush>(rows[0].StatusFill, rows[1].StatusFill)
        Assert.NotEqual<Geometry>(rows[0].StatusGlyph, rows[1].StatusGlyph)
        Assert.True(view.FindControl<Panel>("RunIcon").IsVisible)
        Assert.Equal("r2", view.FindControl<TextBlock>("RunId").Text)
        let summary = view.FindControl<TextBlock>("RunSummary").Text
        Assert.StartsWith("compile error · ", summary)
        Assert.EndsWith(" · UI thread", summary)
        Assert.Contains("example.fsx", string (ToolTip.GetTip(view.FindControl<TextBlock>("RunSummary"))))
    finally window.Close()

[<AvaloniaFact>]
let ``narrow panels hide the logo, so that the status keeps the width`` () =
    let view = PanelView(host "AutoCAD")
    let window = Window(Content = view, Width = 600, Height = 600)
    let logo = view.FindControl<Border> "Logo"
    try
        window.Show()
        Dispatcher.UIThread.RunJobs()
        Assert.True logo.IsVisible
        window.Width <- 320.0
        Dispatcher.UIThread.RunJobs()
        window.UpdateLayout()
        Assert.False logo.IsVisible
        window.Width <- 500.0
        Dispatcher.UIThread.RunJobs()
        window.UpdateLayout()
        Assert.True logo.IsVisible
    finally window.Close()
