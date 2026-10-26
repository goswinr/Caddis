module Caddis.Tests.NuGetTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Caddis
open Caddis.Tests.Helpers

let private exe = if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"

/// A folder like an installation of .NET with these SDKs, and its dotnet executable.
let private fakeDotnet (sdks: string list) =
    let dir = newTempDir ()
    let host = Path.Combine(dir, exe)
    File.WriteAllText(host, "")
    for v in sdks do
        let sdk = Directory.CreateDirectory(Path.Combine(dir, "sdk", v)).FullName
        File.WriteAllText(Path.Combine(sdk, "dotnet.dll"), "")
    host

/// Runs f while F# Interactive and Caddis use the given dotnet executable.
let private withHost (host: string) (f: unit -> 'T) : 'T =
    let prev = Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
    Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host)
    DotnetSdk.clearCache ()
    try f ()
    finally
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", prev)
        DotnetSdk.clearCache ()

#if NETFRAMEWORK
let private runtimeMajor = None
#else
let private runtimeMajor = Some Environment.Version.Major
#endif

/// An SDK that can restore packages for this runtime.
let private usableSdk = match runtimeMajor with Some m -> $"{m}.0.100" | None -> "8.0.100"

[<Fact>]
let ``finds the SDKs next to the dotnet executable, newest first`` () =
    let host = fakeDotnet [ "8.0.100"; "10.0.100-rc.2.25502.107"; "10.0.100"; "9.0.300" ]
    // a folder without dotnet.dll is not an SDK, e.g. one left by an uninstaller
    Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName host, "sdk", "11.0.100")) |> ignore
    let s = withHost host DotnetSdk.check
    Assert.Equal(Some host, s.dotnetHost)
    Assert.Equal<string list>([ "10.0.100"; "10.0.100-rc.2.25502.107"; "9.0.300"; "8.0.100" ], s.sdks)
    Assert.Equal(runtimeMajor, s.requiredMajor)

[<Fact>]
let ``NuGet is available with an SDK of the runtime's version or newer`` () =
    let s = { dotnetHost = Some "dotnet"; sdks = [ "10.0.401"; "8.0.100" ]; requiredMajor = Some 8 }
    Assert.Equal(Some "10.0.401", NuGetSupport.usableSdk s)
    Assert.True(NuGetSupport.isAvailable s)
    Assert.Equal(None, NuGetSupport.problem s)
    Assert.Equal(None, NuGetSupport.installAdvice "Rhino" s)
    Assert.Equal("NuGet packages can be used via #r \"nuget: Name\".", NuGetSupport.evalText s)

[<Fact>]
let ``NuGet is not available with only older SDKs`` () =
    let s = { dotnetHost = Some(Path.Combine("dotnet", exe)); sdks = [ "8.0.403" ]; requiredMajor = Some 10 }
    Assert.False(NuGetSupport.isAvailable s)
    Assert.Equal(Some $"The newest .NET SDK in dotnet is 8.0.403, it can't restore packages for .NET 10.", NuGetSupport.problem s)
    let advice = (NuGetSupport.installAdvice "Revit" s).Value
    Assert.Contains("install the .NET SDK 10 or newer from https://dotnet.microsoft.com/download/dotnet/10.0", advice)
    Assert.Contains("restart Revit", advice)
    Assert.StartsWith("NuGet packages can't be used: #r \"nuget: Name\" needs the .NET SDK 10 or newer.", NuGetSupport.evalText s)

[<Fact>]
let ``NuGet is not available without dotnet or SDK`` () =
    let none = { dotnetHost = None; sdks = []; requiredMajor = Some 8 }
    Assert.Equal(Some "No .NET SDK is installed.", NuGetSupport.problem none)
    let onlyRuntime = { none with dotnetHost = Some(Path.Combine("dotnet", exe)) }
    Assert.Equal(Some "No .NET SDK is installed in dotnet.", NuGetSupport.problem onlyRuntime)
    // on .NET Framework any SDK can restore packages
    let netfx = { none with requiredMajor = None }
    Assert.Equal("https://dotnet.microsoft.com/download", NuGetSupport.downloadUrl netfx)
    Assert.True(NuGetSupport.isAvailable { netfx with sdks = [ "6.0.100" ] })

[<Fact>]
let ``recognizes NuGet references in code`` () =
    Assert.True(DotnetSdk.referencesNuGet "#r \"nuget: FSharp.Data\"")
    Assert.True(DotnetSdk.referencesNuGet "let x = 1\n  #r @\"nuget:Newtonsoft.Json, 13.0.3\"\nx")
    Assert.False(DotnetSdk.referencesNuGet "#r \"System.Xml.dll\"")
    Assert.False(DotnetSdk.referencesNuGet "// #r is not used here: \"nuget: X\"")

[<Fact>]
let ``status reports whether NuGet packages can be used`` () =
    let svc = newService ()
    let nuget = svc.Status([])["nuget"]
    Assert.NotNull nuget
    Assert.Equal(NuGetSupport.isAvailable (DotnetSdk.current ()), nuget["available"].GetValue<bool>())

[<Fact>]
let ``MCP tells the agent whether NuGet packages can be used`` () =
    let evalDescription () =
        let _, body = HttpTests.mcp """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""
        (HttpTests.at [ "result"; "tools" ] (JsonNode.Parse body)).AsArray()
        |> Seq.find (fun t -> HttpTests.str [ "name" ] t = "caddis_eval")
        |> HttpTests.str [ "description" ]
    let instructions () =
        let _, body =
            HttpTests.mcp """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""
        HttpTests.str [ "result"; "instructions" ] (JsonNode.Parse body)
    withHost (fakeDotnet [ usableSdk ]) (fun () ->
        Assert.EndsWith("NuGet packages can be used via #r \"nuget: Name\".", evalDescription ())
        Assert.DoesNotContain("NuGet", instructions ()))
    withHost (fakeDotnet []) (fun () ->
        Assert.Contains("NuGet packages can't be used", evalDescription ())
        Assert.Contains("If a task needs a NuGet package, tell the person in Headless: Scripts can't use NuGet packages", instructions ())
        let status = JsonNode.Parse(snd (HttpTests.send HttpTests.client.Value (new Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Get, "/status"))))
        Assert.False(HttpTests.flag [ "nuget"; "available" ] status)
        Assert.StartsWith("https://dotnet.microsoft.com/download", HttpTests.str [ "nuget"; "download" ] status))

[<Fact>]
let ``MCP suggests the host's NuGet packages only if they can be used`` () =
    let instructions () =
        HostTests.str [ "result"; "instructions" ] (HostTests.mcp """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
    let packages = "The NuGet package OtherApp.Scripting has the API of OtherApp."
    withHost (fakeDotnet [ usableSdk ]) (fun () ->
        Assert.Equal("Caddis runs F# code inside OtherApp.\n" + packages, instructions ())
        // and Claude Desktop's extension while the app isn't running
        Assert.EndsWith(packages, HostTests.server.Value.DesktopBundle().instructions))
    withHost (fakeDotnet []) (fun () ->
        let text = instructions ()
        Assert.DoesNotContain(packages, text)
        Assert.Contains("If a task needs a NuGet package, tell the person in OtherApp: Scripts can't use NuGet packages", text))

[<Fact>]
let ``a run with a NuGet reference explains a missing SDK`` () =
    // a dotnet executable without SDKs next to it, like on a PC with only the .NET runtime
    let real = DotnetSdk.findHost ()
    Assert.True(real.IsSome, "This test needs dotnet.")
    let host = Path.Combine(newTempDir (), exe)
    File.Copy(real.Value, host)
    withHost host (fun () ->
        let svc = newService ()
        // not a real package: one that was restored before, or is next to the test assembly like Newtonsoft.Json, would work without the SDK
        let run = eval svc "#r \"nuget: Caddis.Tests.NotOnNuGet\"\n1 + 1"
        Assert.NotEqual(Succeeded, run.Status)
        let note = run.Notes |> List.tryFind (fun n -> n.Contains "NuGet")
        Assert.True(note.IsSome, $"No note about NuGet in: {run.Notes}, status {RunStatus.toString run.Status}")
        // FSI only says e.g. "Error: [...\host\fxr] does not exist"
        Assert.Contains("No .NET SDK is installed in", note.Value))
