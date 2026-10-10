namespace Caddis

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

/// On which thread F# scripts are evaluated.
type ThreadMode =
    /// On the host's UI thread. Safest for RhinoCommon, but a running script blocks the UI and can't be cancelled.
    | UiThread
    /// On a dedicated background thread. Scripts can be cancelled or timed out and the UI stays responsive.
    | Background

module ThreadMode =

    let toString (m: ThreadMode) =
        match m with
        | UiThread -> "ui"
        | Background -> "background"

    let tryParse (s: string) =
        match (if isNull s then "" else s.Trim().ToLowerInvariant()) with
        | "ui" | "uithread" | "sync" -> Some UiThread
        | "background" | "async" -> Some Background
        | _ -> None


type CaddisSettings = {
    /// The preferred port. If it is taken the next free one is used.
    port: int
    /// Listen on all network interfaces instead of only on localhost.
    listenOnLan: bool
    /// Start the server when the host app starts.
    autoStart: bool
    /// The thread mode a new session starts in.
    threadMode: ThreadMode
    /// Echo script output to the host, e.g. the Rhino command line.
    echoOutput: bool
    /// Cancel host commands that a script starts, e.g. via RhinoApp.RunScript, when they wait for input, see inputTimeoutMs.
    cancelWaitingCommands: bool
    /// Maximum number of characters captured per output stream of a run.
    maxOutputChars: int
    /// Additional arguments for F# Interactive, e.g. "--define:DEBUG".
    extraFsiArgs: string[]
    }

module CaddisSettings =

    /// A command that waits this long for input is cancelled: always in command runs, in F# runs with cancelWaitingCommands.
    let inputTimeoutMs = 3000

    let defaults = {
        port = 7810
        listenOnLan = false
        autoStart = false
        threadMode = UiThread
        echoOutput = true
        cancelWaitingCommands = false
        maxOutputChars = 200_000
        extraFsiArgs = [||]
        }

    let toJson (s: CaddisSettings) : JsonObject =
        Json.obj [
            "port", Json.ofInt s.port
            "listenOnLan", Json.ofBool s.listenOnLan
            "autoStart", Json.ofBool s.autoStart
            "threadMode", Json.ofString (ThreadMode.toString s.threadMode)
            "echoOutput", Json.ofBool s.echoOutput
            "cancelWaitingCommands", Json.ofBool s.cancelWaitingCommands
            "maxOutputChars", Json.ofInt s.maxOutputChars
            "extraFsiArgs", Json.ofStrings s.extraFsiArgs
        ]

    /// Missing or invalid values fall back to the given defaults.
    let ofJson (d: CaddisSettings) (n: JsonNode) : CaddisSettings =
        {
            port = Json.tryInt "port" n |> Option.filter (fun p -> p > 0 && p < 65536) |> Option.defaultValue d.port
            listenOnLan = Json.tryBool "listenOnLan" n |> Option.defaultValue d.listenOnLan
            autoStart = Json.tryBool "autoStart" n |> Option.defaultValue d.autoStart
            threadMode = Json.tryString "threadMode" n |> Option.bind ThreadMode.tryParse |> Option.defaultValue d.threadMode
            echoOutput = Json.tryBool "echoOutput" n |> Option.defaultValue d.echoOutput
            cancelWaitingCommands = Json.tryBool "cancelWaitingCommands" n |> Option.defaultValue d.cancelWaitingCommands
            maxOutputChars = Json.tryInt "maxOutputChars" n |> Option.filter (fun c -> c > 0) |> Option.defaultValue d.maxOutputChars
            extraFsiArgs = Json.tryStringArray "extraFsiArgs" n |> Option.defaultValue d.extraFsiArgs
        }


/// Reads and writes the settings, the auth token and the discovery files, all in one folder.
/// Each host app has its own settings file and defaults, e.g. its own port, the token is shared.
type SettingsStore(folder: string, settingsFileName: string, defaults: CaddisSettings) =

    let settingsPath = Path.Combine(folder, settingsFileName)
    let tokenPath = Path.Combine(folder, "token.txt")
    let instancesFolder = Path.Combine(folder, "instances")

    /// On Mac and Linux make the file readable for the current user only.
    let restrictToUser (path: string) =
        if not (OperatingSystem.IsWindows()) then
            try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()

    /// Publish a complete token atomically, so readers never see an empty or partial replacement.
    let writeToken (overwrite: bool) =
        Directory.CreateDirectory folder |> ignore
        let token = Convert.ToHexString(RandomNumberGenerator.GetBytes 24).ToLowerInvariant()
        let tempPath = Path.Combine(folder, $"token-{Guid.NewGuid():N}.tmp")
        try
            let options = FileStreamOptions(Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None)
            if not (OperatingSystem.IsWindows()) then
                options.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite
            do
                use stream = new FileStream(tempPath, options)
                use writer = new StreamWriter(stream, UTF8Encoding(false))
                writer.Write token
            restrictToUser tempPath
            File.Move(tempPath, tokenPath, overwrite)
            token
        finally
            if File.Exists tempPath then File.Delete tempPath

    /// %AppData%/Caddis on Windows, ~/.config/Caddis on Mac (.NET's ApplicationData folder).
    static member DefaultFolder =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "Caddis")

    /// settings.json with the default settings, used by Rhino.
    new(folder: string) = SettingsStore(folder, "settings.json", CaddisSettings.defaults)

    new() = SettingsStore(SettingsStore.DefaultFolder, "settings.json", CaddisSettings.defaults)

    member _.Folder = folder

    member _.SettingsPath = settingsPath

    member _.TokenPath = tokenPath

    /// Contains one JSON file per running server, named by process id.
    member _.InstancesFolder = instancesFolder

    /// Loads the settings, or writes and returns the defaults if there is no settings file yet.
    /// If the file can't be parsed the defaults are returned and the file is left untouched.
    member this.Load() : CaddisSettings =
        try
            if File.Exists settingsPath then
                File.ReadAllText settingsPath |> Json.parse |> CaddisSettings.ofJson defaults
            else
                this.Save defaults
                defaults
        with _ ->
            defaults

    member _.Save(s: CaddisSettings) =
        Directory.CreateDirectory folder |> ignore
        File.WriteAllText(settingsPath, CaddisSettings.toJson s |> Json.toText)

    /// Reads the shared token without creating or repairing it. Authentication fails closed on read errors.
    member _.ReadToken() : string =
        use stream = new FileStream(tokenPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        use reader = new StreamReader(stream)
        let token = reader.ReadToEnd().Trim()
        if token.Length < 16 then raise (InvalidDataException "The Caddis token file is invalid.")
        token

    /// Creates a new random token and atomically replaces the shared token file.
    member _.NewToken() : string = writeToken true

    /// Returns the saved token, or creates one if there is none.
    member this.GetOrCreateToken() : string =
        if File.Exists tokenPath then
            try this.ReadToken()
            with :? InvalidDataException -> this.NewToken()
        else
            try writeToken false
            // Another instance may have created the token while this one was starting.
            with :? IOException when File.Exists tokenPath -> this.ReadToken()
