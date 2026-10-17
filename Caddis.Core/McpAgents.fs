namespace Caddis

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// The AI agents that the panel sets up, with Caddis as an MCP server for all projects of the user.
type McpAgent =
    | ClaudeCode
    | Codex
    /// Gets Caddis as an extension, see ClaudeDesktop, not with a command line program.
    | ClaudeDesktop

/// Whether an agent reaches this Caddis server.
[<RequireQualifiedAccess>]
type AgentState =
    /// The agent has no MCP server with the name of this app's Caddis, e.g. 'caddis-revit'.
    | NotSetUp
    /// The agent connects to this server.
    | Active
    /// The agent connects to another URL: another instance of the app listens there, or nothing.
    | Inactive of url: string

module McpAgent =

    let all = [ ClaudeCode; Codex; ClaudeDesktop ]

    /// e.g. "Claude Code"
    let name (agent: McpAgent) =
        match agent with
        | ClaudeCode -> "Claude Code"
        | Codex -> "Codex"
        | ClaudeDesktop -> "Claude Desktop"

    /// The agent's command line program. None for Claude Desktop, which installs an extension instead.
    let program (agent: McpAgent) =
        match agent with
        | ClaudeCode -> Some "claude"
        | Codex -> Some "codex"
        | ClaudeDesktop -> None

    /// The arguments that add an MCP server for all projects of the user. None for Claude Desktop.
    let addArgs (agent: McpAgent) (serverName: string) (url: string) =
        match agent with
        | ClaudeCode -> [ "mcp"; "add"; "--transport"; "http"; "--scope"; "user"; serverName; url ]
        | Codex -> [ "mcp"; "add"; serverName; "--url"; url ]
        | ClaudeDesktop -> []

    /// The arguments that remove it. Needed before adding it again to Claude Code, whose 'mcp add' fails if the name exists.
    /// Codex's replaces it. None for Claude Desktop.
    let removeArgs (agent: McpAgent) (serverName: string) =
        match agent with
        | ClaudeCode -> [ "mcp"; "remove"; "--scope"; "user"; serverName ]
        | Codex -> [ "mcp"; "remove"; serverName ]
        | ClaudeDesktop -> []

    /// The command line, as typed in a terminal. "" for Claude Desktop.
    let commandLine (agent: McpAgent) (args: string list) =
        match program agent with
        | Some p -> String.concat " " (p :: args)
        | None -> ""

    /// An environment variable of this process, or of the user if it was set after the app started.
    let private envVar (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" ->
            match Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) with
            | null
            | "" -> None
            | v -> Some v
        | v -> Some v

    /// The variables that move the agents' config files.
    let configVariables = [ "CLAUDE_CONFIG_DIR"; "CODEX_HOME" ]

    /// The file in which the agent keeps the MCP servers for all projects of the user:
    /// ~/.claude.json, or .claude.json in CLAUDE_CONFIG_DIR, and ~/.codex/config.toml, or config.toml in CODEX_HOME.
    /// For Claude Desktop the list of its extensions, see DesktopExtension.installationsPath.
    let configPath (agent: McpAgent) =
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        match agent with
        | ClaudeCode -> Path.Combine(envVar "CLAUDE_CONFIG_DIR" |> Option.defaultValue home, ".claude.json")
        | Codex -> Path.Combine(envVar "CODEX_HOME" |> Option.defaultWith (fun () -> Path.Combine(home, ".codex")), "config.toml")
        | ClaudeDesktop -> DesktopExtension.installationsPath ()

    /// The URL of the MCP server with this name in the text of ~/.claude.json: in its top-level "mcpServers",
    /// where Claude Code keeps those of user scope. "" for a server without URL. Raises a JsonException if the text isn't JSON.
    let claudeUrl (json: string) (serverName: string) : string option =
        use doc = JsonDocument.Parse(json, JsonDocumentOptions(AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip))
        let child (name: string) (e: JsonElement) =
            match e.ValueKind with
            | JsonValueKind.Object ->
                match e.TryGetProperty name with
                | true, v -> Some v
                | _ -> None
            | _ -> None
        doc.RootElement
        |> child "mcpServers"
        |> Option.bind (child serverName)
        |> Option.map (fun server ->
            match child "url" server with
            | Some u when u.ValueKind = JsonValueKind.String -> u.GetString()
            | _ -> "")

    /// The URL of the MCP server with this name in the text of ~/.codex/config.toml: in its table [mcp_servers.<name>].
    /// "" for a server without URL. Reads what Codex writes, a table header and a line url = "...", not all of TOML.
    let codexUrl (toml: string) (serverName: string) : string option =
        let withoutComment (line: string) =
            match line.IndexOf '#' with
            | -1 -> line.Trim()
            | i -> line.Substring(0, i).Trim()
        let headers =
            set [ $"[mcp_servers.{serverName}]"; $"[mcp_servers.\"{serverName}\"]"; $"[mcp_servers.'{serverName}']" ]
        let lines = toml.Split('\n') |> Array.map _.Trim()
        let isHeader (line: string) = headers.Contains((withoutComment line).Replace(" ", "").Replace("\t", ""))
        match Array.tryFindIndex isHeader lines with
        | None -> None
        | Some i ->
            let url (line: string) =
                match line.IndexOf '=' with
                | -1 -> None
                | eq when line.Substring(0, eq).Trim().Trim('"', '\'') = "url" ->
                    let value = line.Substring(eq + 1).Trim()
                    if value.Length > 0 && (value[0] = '"' || value[0] = '\'') then
                        match value.IndexOf(value[0], 1) with
                        | -1 -> None
                        | close -> Some(value.Substring(1, close - 1))
                    else
                        None
                | _ -> None
            // the keys of the table, up to the next table header, e.g. [mcp_servers.caddis-revit.env]
            lines[i + 1 ..]
            |> Array.takeWhile (fun l -> not (l.StartsWith "["))
            |> Array.tryPick url
            |> Option.defaultValue ""
            |> Some

    /// The port of an MCP URL of Caddis on this computer, e.g. 7820 of http://127.0.0.1:7820/mcp. None for other URLs.
    let localPort (url: string) : int option =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, u when u.Scheme = Uri.UriSchemeHttp && u.IsLoopback && u.AbsolutePath.TrimEnd('/') = "/mcp" -> Some u.Port
        | _ -> None

    /// The state of an agent with this URL for the server, None if it has none, for a server that listens on the port.
    let state (registered: string option) (ownPort: int) : AgentState =
        match registered with
        | None -> AgentState.NotSetUp
        | Some url when localPort url = Some ownPort -> AgentState.Active
        | Some url -> AgentState.Inactive url


/// Reads the URLs of the MCP servers in the agents' config files. Again only when a file changed:
/// the panel asks every second, and ~/.claude.json has a hundred KB.
type AgentConfigs(pathOf: McpAgent -> string) =

    let sync = obj ()

    /// By agent and server name: the path, the write time and length of the file, and the URL found in it.
    let cache = Dictionary<McpAgent * string, string * DateTime * int64 * string option>()

    let read (agent: McpAgent) (path: string) (serverName: string) =
        let text () =
            // the agent may be writing it
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            use reader = new StreamReader(stream, Encoding.UTF8)
            reader.ReadToEnd()
        match agent with
        | ClaudeCode -> McpAgent.claudeUrl (text ()) serverName
        | Codex -> McpAgent.codexUrl (text ()) serverName
        | ClaudeDesktop -> DesktopExtension.installedUrl path serverName

    /// The files in their usual places, see McpAgent.configPath.
    new() = AgentConfigs(McpAgent.configPath)

    member _.PathOf(agent: McpAgent) = pathOf agent

    /// Whether the agent is installed, as far as the panel needs to know: Claude Desktop if its settings folder exists.
    /// Claude Code and Codex always count as installed, their Set up says if their program isn't found.
    member _.IsInstalled(agent: McpAgent) =
        match agent with
        | ClaudeDesktop -> Directory.Exists(Path.GetDirectoryName(pathOf agent))
        | ClaudeCode
        | Codex -> true

    /// The URL of the MCP server with this name in the agent's config, None if it has none or there is no config file.
    member _.RegisteredUrl(agent: McpAgent, serverName: string) : string option =
        let path = pathOf agent
        let file = FileInfo path
        if not file.Exists then
            None
        else
            lock sync (fun () ->
                let key = agent, serverName
                match cache.TryGetValue key with
                // Claude Desktop's files are read every time: its extension's settings are in another file, both are small
                | true, (p, time, length, url) when
                    agent <> ClaudeDesktop && p = path && time = file.LastWriteTimeUtc && length = file.Length
                    ->
                    url
                | found, previous ->
                    try
                        let url = read agent path serverName
                        cache[key] <- (path, file.LastWriteTimeUtc, file.Length, url)
                        url
                    with _ ->
                        // e.g. half written: the URL found before, the file is read again next time
                        if found then (let _, _, _, url = previous in url) else None)

    /// The state of the agent, for a server with this name that listens on the port.
    member this.State(agent: McpAgent, serverName: string, ownPort: int) : AgentState =
        McpAgent.state (this.RegisteredUrl(agent, serverName)) ownPort


/// Finds and runs the agents' command line programs, e.g. 'claude mcp add ...'.
module AgentCli =

    /// The folders of PATH, and of PATH of the user and machine as they are now: the app may have been started before
    /// the agent was installed. Also the folders the agents install to: apps started from the Mac's Dock don't have them on PATH.
    let searchFolders () : string list =
        let split (value: string) =
            if String.IsNullOrEmpty value then [] else value.Split(Path.PathSeparator) |> List.ofArray
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        [
            yield! split (Environment.GetEnvironmentVariable "PATH")
            if OperatingSystem.IsWindows() then
                yield! split (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User))
                yield! split (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
            Path.Combine(home, ".local", "bin") // Claude Code's installer
            if OperatingSystem.IsWindows() then
                Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "npm")
                Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "Programs", "OpenAI", "Codex", "bin")
            else
                "/opt/homebrew/bin"
                "/usr/local/bin"
        ]
        |> List.map (fun d -> Environment.ExpandEnvironmentVariables(d.Trim().Trim('"')))
        |> List.filter (fun d -> d <> "")
        |> List.distinctBy (fun d -> if OperatingSystem.IsWindows() then d.ToLowerInvariant() else d)

    /// The full path of the program, None if it is in none of the search folders.
    let find (program: string) : string option =
        let names = if OperatingSystem.IsWindows() then [ program + ".exe"; program + ".cmd"; program + ".bat" ] else [ program ]
        searchFolders ()
        |> List.tryPick (fun dir ->
            names
            |> List.tryPick (fun name ->
                try
                    let path = Path.Combine(dir, name)
                    if File.Exists path then Some path else None
                with _ ->
                    None // a PATH entry with invalid characters
            ))

    /// Runs the program without a window and returns its exit code and what it printed. Kills it after a minute.
    /// The arguments must not contain quotes or the special characters of cmd.exe, which runs .cmd files, e.g. those of npm.
    let run (path: string) (args: string list) : Task<int * string> =
        let quote (a: string) = if a = "" || a.IndexOfAny [| ' '; '\t' |] >= 0 then "\"" + a + "\"" else a
        let line = args |> List.map quote |> String.concat " "
        let isBatch =
            OperatingSystem.IsWindows()
            && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        let psi =
            if isBatch then
                let cmd = Environment.GetEnvironmentVariable "ComSpec" |> Option.ofObj |> Option.defaultValue "cmd.exe"
                ProcessStartInfo(cmd, $"/d /s /c \"\"{path}\" {line}\"")
            else
                ProcessStartInfo(path, line)
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.StandardOutputEncoding <- Encoding.UTF8
        psi.StandardErrorEncoding <- Encoding.UTF8
        psi.WorkingDirectory <- Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        // npm's claude.cmd starts node, which may also have been installed after the app started
        psi.EnvironmentVariables["PATH"] <- String.concat (string Path.PathSeparator) (searchFolders ())
        for name in McpAgent.configVariables do
            match Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) with
            | null
            | "" -> ()
            | v when String.IsNullOrEmpty(Environment.GetEnvironmentVariable name) -> psi.EnvironmentVariables[name] <- v
            | _ -> ()
        task {
            use p = Process.Start psi
            p.StandardInput.Close()
            let out = p.StandardOutput.ReadToEndAsync()
            let err = p.StandardError.ReadToEndAsync()
            let! exited = Task.Run<bool>(Func<bool>(fun () -> p.WaitForExit 60000))
            if not exited then
                try p.Kill() with _ -> ()
                return -1, $"'{Path.GetFileName path}' did not finish within a minute."
            else
                let! o = out
                let! e = err
                return p.ExitCode, (o.Trim() + Environment.NewLine + e.Trim()).Trim()
        }
