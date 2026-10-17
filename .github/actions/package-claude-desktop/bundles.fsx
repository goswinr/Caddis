// Writes the files of the Claude Desktop extensions of Caddis for Rhino and Revit, one folder each, for 'mcpb pack'.
// Usage: dotnet fsi bundles.fsx <output folder>, after a Release build of Caddis.Core for net10.0.
// The panel makes the same extension with DesktopExtension.writeBundle, then with the tools and instructions of the running Caddis
// and the port it listens on. These have neither, the relay lists the tools once Caddis answers.

#r "../../../Caddis.Core/bin/Release/net10.0/Caddis.Core.dll"

open System.IO
open System.Reflection
open Caddis

let outDir = fsi.CommandLineArgs[1]

let version =
    typeof<DesktopExtension.Bundle>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion.Split('+')[0]

// The MCP names and default ports of RhinoInfo.fs, RevitInfo.fs and the settings of Rhino and Revit.
for serverName, appName, port, platforms in [ "caddis-rhino", "Rhino", 7810, [ "win32"; "darwin" ]; "caddis-revit", "Revit", 7820, [ "win32" ] ] do
    let bundle: DesktopExtension.Bundle = {
        serverName = serverName
        appName = appName
        port = port
        version = version
        platforms = platforms
        instructions = ""
        tools = []
    }
    let dir = Path.Combine(outDir, serverName)
    if Directory.Exists dir then Directory.Delete(dir, true)
    DesktopExtension.writeFolder bundle dir
    printfn $"Wrote the extension {serverName} {version} to {dir}"
