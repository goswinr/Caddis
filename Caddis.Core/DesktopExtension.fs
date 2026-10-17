namespace Caddis

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json.Nodes

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

    /// The folder of Claude Desktop's settings: %APPDATA%\Claude, on Mac ~/Library/Application Support/Claude.
    let settingsFolder () =
        if OperatingSystem.IsMacOS() then
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, "Library", "Application Support", "Claude")
        else
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "Claude")

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
