namespace Caddis

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Runtime.InteropServices
open System.Text
open System.Text.Json.Nodes
open Microsoft.Win32

/// Caddis as an extension of Claude Desktop, an MCP Bundle (.mcpb): a zip of a manifest, relay.js and an icon.
/// Claude Desktop runs local MCP servers over stdio only, and reaches the URLs of its connectors from Anthropic's servers,
/// which can't reach this computer. So Claude Desktop runs relay.js with its own Node, and it relays to the MCP endpoint of Caddis.
/// https://github.com/modelcontextprotocol/mcpb/blob/main/MANIFEST.md
module DesktopExtension =

    /// What a bundle is made for.
    type Bundle = {
        /// The MCP name of the app's Caddis, e.g. "caddis-rhino", also the name of the extension.
        serverName: string
        /// e.g. "Rhino"
        appName: string
        /// The port of Caddis, the default of the extension's setting.
        port: int
        /// The version of Caddis, also of the extension.
        version: string
        /// The platforms of the app, "win32" and "darwin".
        platforms: string list
        /// The instructions of Caddis, shown while it isn't running. Empty for bundles made without a running Caddis.
        instructions: string
        /// The tools of Caddis as in tools/list, listed while it isn't running. Empty for bundles made without a running Caddis.
        tools: JsonNode list
        }

    let private repo = "https://github.com/goswinr/Caddis"

    let private resource (name: string) =
        use stream = typeof<Bundle>.Assembly.GetManifestResourceStream $"Caddis.Core.ClaudeDesktop.{name}"
        use ms = new MemoryStream()
        stream.CopyTo ms
        ms.ToArray()

    /// The first sentence, for the tools in the manifest, which Claude Desktop shows before installing.
    let private firstSentence (text: string) =
        match text.IndexOf ". " with
        | -1 -> text
        | i -> text.Substring(0, i + 1)

    let manifest (b: Bundle) : JsonObject =
        let str = Json.ofString
        Json.obj [
            "manifest_version", str "0.3"
            "name", str b.serverName
            "display_name", str $"Caddis for {b.appName}"
            "version", str b.version
            "description", str $"Run F# scripts inside a running {b.appName}: the agent writes the code, Caddis runs it in {b.appName} and returns what happened."
            "long_description",
            str (
                String.concat "\n\n" [
                    $"Caddis hosts an F# Interactive session inside {b.appName}, with an MCP endpoint on this computer. This extension relays Claude Desktop to it."
                    $"It needs Caddis in {b.appName}: install it as described on [GitHub]({repo}), start {b.appName}, and the server in the Caddis panel."
                    "Set the port to the one of the Caddis panel if it isn't the default."
                ]
            )
            "author", Json.obj [ "name", str "Goswin Rothenthal"; "url", str repo ]
            "repository", Json.obj [ "type", str "git"; "url", str repo ]
            "homepage", str repo
            "support", str (repo + "/issues")
            "icon", str "icon.png"
            "license", str "MIT"
            "keywords", Json.ofStrings [ "fsharp"; "scripting"; "cad"; b.appName.ToLowerInvariant() ]
            "server",
            Json.obj [
                "type", str "node"
                "entry_point", str "server/relay.js"
                "mcp_config",
                Json.obj [
                    "command", str "node"
                    "args", Json.ofStrings [ "${__dirname}/server/relay.js"; "${user_config.port}"; b.appName ]
                    "env", Json.obj []
                ]
            ]
            "user_config",
            Json.obj [
                "port",
                Json.obj [
                    "type", str "number"
                    "title", str $"Port of Caddis in {b.appName}"
                    "description", str $"The port where Caddis in {b.appName} listens, the Caddis panel shows it."
                    "default", Json.ofInt b.port
                    "min", Json.ofInt 1
                    "max", Json.ofInt 65535
                    "required", Json.ofBool true
                ]
            ]
            if not b.tools.IsEmpty then
                "tools",
                Json.arr (
                    b.tools
                    |> List.map (fun t ->
                        Json.obj [
                            "name", str (Json.tryString "name" t |> Option.defaultValue "")
                            "description", str (Json.tryString "description" t |> Option.defaultValue "" |> firstSentence)
                        ]
                        :> JsonNode)
                )
            "tools_generated", Json.ofBool true
            "compatibility", Json.obj [ "platforms", Json.ofStrings b.platforms; "runtimes", Json.obj [ "node", str ">=18.0.0" ] ]
        ]

    /// The files of the bundle, by their path in it.
    let files (b: Bundle) : (string * byte[]) list =
        let offer =
            Json.obj [
                "name", Json.ofString b.serverName
                "version", Json.ofString b.version
                "instructions", Json.ofString b.instructions
                "tools", Json.arr (b.tools |> List.map _.DeepClone())
            ]
        let utf8 (n: JsonNode) = Encoding.UTF8.GetBytes(Json.toText n)
        [
            "manifest.json", utf8 (manifest b)
            "server/relay.js", resource "relay.js"
            "server/caddis.json", utf8 offer
            "icon.png", resource "icon.png"
        ]

    /// Writes the files of the bundle to a folder, e.g. for 'mcpb pack'.
    let writeFolder (b: Bundle) (dir: string) =
        for name, bytes in files b do
            let path = Path.Combine(dir, name)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllBytes(path, bytes)

    /// Writes the bundle as an .mcpb file, which Claude Desktop installs when it is opened.
    let writeBundle (b: Bundle) (path: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
        use fs = new FileStream(path, FileMode.Create)
        use zip = new ZipArchive(fs, ZipArchiveMode.Create)
        for name, bytes in files b do
            use entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open()
            entry.Write(bytes, 0, bytes.Length)

    /// The package family of Claude Desktop for Windows from the Microsoft Store or an MSIX file.
    let packageFamily = "Claude_pzs8sxrjxfjjc"

    /// The program of Claude Desktop from its installer for Windows, in the folder of local app data, which starts its latest version.
    let installerProgram (localAppData: string) =
        Path.Combine(localAppData, "AnthropicClaude", "claude.exe")

    /// The program of Claude Desktop from its installer for Windows, %LOCALAPPDATA%\AnthropicClaude\claude.exe.
    /// None if it isn't installed so, and on Mac.
    let program () =
        let path = installerProgram (Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData)
        if OperatingSystem.IsWindows() && File.Exists path then Some path else None

    /// The id of Claude Desktop as the app of its MSIX package, e.g. "Claude_pzs8sxrjxfjjc!Claude", by which Windows starts it.
    /// Windows lists the packages of the user in the registry, each with a key for each of its apps. None if there is no such package, and on Mac.
    let packagedAppId () =
        if not (OperatingSystem.IsWindows()) then
            None
        else
            try
                use packages =
                    Registry.CurrentUser.OpenSubKey
                        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"
                let name, publisherId = let i = packageFamily.IndexOf '_' in packageFamily.Substring(0, i), packageFamily.Substring(i + 1)
                // full names are e.g. Claude_1.2.3.0_x64__pzs8sxrjxfjjc
                packages.GetSubKeyNames()
                |> Array.filter (fun n -> n.StartsWith(name + "_") && n.EndsWith("_" + publisherId))
                |> Array.tryPick (fun n ->
                    use package = packages.OpenSubKey n
                    package.GetSubKeyNames() |> Array.tryHead |> Option.map (fun app -> $"{packageFamily}!{app}"))
            with _ ->
                None

    [<ComImport; Guid("2e941141-7f97-4756-ba1d-9decde894a3d"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IApplicationActivationManager =
        abstract ActivateApplication:
            [<MarshalAs(UnmanagedType.LPWStr)>] appUserModelId: string *
            [<MarshalAs(UnmanagedType.LPWStr)>] arguments: string *
            options: int *
            [<Out>] processId: byref<uint32> ->
                unit

    /// Starts the app of an MSIX package with these arguments. Its program can't be started directly,
    /// it would run outside of its package, with other folders for its data, and not pass the arguments on to the running app.
    let private activate (appId: string) (arguments: string) =
        let manager =
            Activator.CreateInstance(Type.GetTypeFromCLSID(Guid "45BA127D-10A8-46EA-8AB7-56EA9078943C")) :?> IApplicationActivationManager
        try
            let mutable pid = 0u
            manager.ActivateApplication(appId, arguments, 0, &pid)
        finally
            Marshal.ReleaseComObject manager |> ignore

    /// Opens the bundle in Claude Desktop, which asks the person to install it.
    /// Claude Desktop for Windows, from its installer or its MSIX package, isn't the app for .mcpb files, so Windows would ask which app to open it with.
    /// So it starts Claude Desktop with the file, which passes it on to the running Claude Desktop.
    let openBundle (path: string) =
        match program (), packagedAppId () with
        | Some exe, _ -> Process.Start(ProcessStartInfo(exe, $"\"{path}\"", UseShellExecute = false)) |> ignore
        | None, Some appId -> activate appId $"\"{path}\""
        | None, None -> Process.Start(ProcessStartInfo(path, UseShellExecute = true)) |> ignore

    /// The folder of Claude Desktop's settings on Windows, in these folders of the user's local and roaming app data: Claude in the roaming one.
    /// Claude Desktop from its MSIX package has it in its package's folder instead, where Windows redirects its roaming app data,
    /// unless Claude Desktop from the installer is there too.
    let windowsSettingsFolder (localAppData: string) (roamingAppData: string) =
        let packaged = Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Roaming", "Claude")
        if not (File.Exists(installerProgram localAppData)) && Directory.Exists packaged then
            packaged
        else
            Path.Combine(roamingAppData, "Claude")

    /// The folder of Claude Desktop's settings: %APPDATA%\Claude, or the one of its MSIX package, on Mac ~/Library/Application Support/Claude.
    let settingsFolder () =
        if OperatingSystem.IsMacOS() then
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, "Library", "Application Support", "Claude")
        else
            windowsSettingsFolder
                (Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData)
                (Environment.GetFolderPath Environment.SpecialFolder.ApplicationData)

    /// The file in which Claude Desktop lists the installed extensions, each with its manifest.
    let installationsPath () =
        Path.Combine(settingsFolder (), "extensions-installations.json")

    let private parse (path: string) : JsonNode =
        // Claude Desktop may be writing it
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        use reader = new StreamReader(stream, Encoding.UTF8)
        Json.parse (reader.ReadToEnd())

    /// The URL to which the installed extension of this name relays, None if there is none, or it is turned off.
    /// Claude Desktop keeps the manifest of each extension in extensions-installations.json, and whether it is turned on and
    /// the settings the person changed in 'Claude Extensions Settings/<id>.json': there the port is under userConfig.
    /// Raises a JsonException if a file isn't JSON, e.g. half written.
    let installedUrl (installationsPath: string) (serverName: string) : string option =
        let extensions =
            match Json.prop "extensions" (parse installationsPath) with
            | Some(:? JsonObject as o) -> List.ofSeq o
            | _ -> []
        extensions
        |> List.tryFind (fun e -> Json.prop "manifest" e.Value |> Option.bind (Json.tryString "name") = Some serverName)
        |> Option.bind (fun e ->
            let id = Json.tryString "id" e.Value |> Option.defaultValue e.Key
            let settingsPath = Path.Combine(Path.GetDirectoryName installationsPath, "Claude Extensions Settings", id + ".json")
            let settings = if File.Exists settingsPath then parse settingsPath else null
            if Json.tryBool "isEnabled" settings = Some false then
                None
            else
                let configured = Json.prop "userConfig" settings |> Option.bind (Json.tryInt "port")
                let byDefault =
                    Json.prop "manifest" e.Value
                    |> Option.bind (Json.prop "user_config")
                    |> Option.bind (Json.prop "port")
                    |> Option.bind (Json.tryInt "default")
                match configured |> Option.orElse byDefault with
                | Some port -> Some $"http://127.0.0.1:{port}/mcp"
                | None -> Some "")
