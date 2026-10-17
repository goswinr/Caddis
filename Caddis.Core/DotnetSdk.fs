namespace Caddis

open System
open System.IO
open System.Text.RegularExpressions

/// Whether '#r "nuget: ..."' can work in a script. F# Interactive restores NuGet packages with 'dotnet msbuild', which needs a .NET SDK.
/// The project it restores targets the framework that runs the session, e.g. net8.0 in Rhino 8, so the SDK must be of that version or newer.
type NuGetSupport = {
    /// The dotnet executable that F# Interactive uses, None if there is none.
    dotnetHost: string option
    /// The versions of the SDKs next to it, e.g. "10.0.401", newest first.
    sdks: string list
    /// The major version of the oldest SDK that can restore packages for the running framework, e.g. 8 on .NET 8.
    /// None on .NET Framework, any SDK can restore for it.
    requiredMajor: int option
    }

module NuGetSupport =

    let private major (version: string) =
        match Int32.TryParse(version.Split('.')[0]) with
        | true, m -> Some m
        | _ -> None

    /// An SDK that can restore packages for the running framework, the newest one.
    let usableSdk (s: NuGetSupport) : string option =
        s.sdks |> List.tryFind (fun v -> match major v, s.requiredMajor with Some m, Some r -> m >= r | Some _, None -> true | None, _ -> false)

    let isAvailable (s: NuGetSupport) = (usableSdk s).IsSome

    /// E.g. ".NET SDK 8 or newer", or ".NET SDK" on .NET Framework.
    let requiredSdk (s: NuGetSupport) =
        match s.requiredMajor with
        | Some m -> $".NET SDK {m} or newer"
        | None -> ".NET SDK"

    /// The page to download the required SDK from.
    let downloadUrl (s: NuGetSupport) =
        match s.requiredMajor with
        | Some m -> $"https://dotnet.microsoft.com/download/dotnet/{m}.0"
        | None -> "https://dotnet.microsoft.com/download"

    /// Why NuGet packages can't be used, None if they can. E.g. "No .NET SDK is installed."
    let problem (s: NuGetSupport) : string option =
        if isAvailable s then None
        else
            match s.dotnetHost, s.sdks with
            | None, _ -> Some "No .NET SDK is installed."
            | Some host, [] -> Some $"No .NET SDK is installed in {Path.GetDirectoryName host}."
            | Some host, newest :: _ ->
                let target = s.requiredMajor |> Option.map (fun m -> $".NET {m}") |> Option.defaultValue ".NET Framework"
                Some $"The newest .NET SDK in {Path.GetDirectoryName host} is {newest}, it can't restore packages for {target}."

    /// For AI agents, in the description of caddis_eval.
    let evalText (s: NuGetSupport) =
        match problem s with
        | None -> "NuGet packages can be used via #r \"nuget: Name\"."
        | Some p -> $"NuGet packages can't be used: #r \"nuget: Name\" needs the {requiredSdk s}. {p} DLL files can be referenced via #r \"path/to/file.dll\"."

    /// Why the person should install the SDK and where to get it, sentence by sentence. None if NuGet packages can be used.
    let installAdviceLines (appName: string) (s: NuGetSupport) : string list option =
        problem s
        |> Option.map (fun p -> [
            $"Scripts can't use NuGet packages, e.g. via #r \"nuget: Name\": F# Interactive downloads them with the {requiredSdk s}."
            p
            "Everything else in Caddis works without it."
            $"To use NuGet packages, install the {requiredSdk s} from {downloadUrl s}."
            $"If Caddis doesn't find it afterwards, restart {appName}."
        ])

    /// Why the person should install the SDK and where to get it. None if NuGet packages can be used.
    let installAdvice (appName: string) (s: NuGetSupport) : string option =
        installAdviceLines appName s |> Option.map (String.concat " ")

    let toJson (s: NuGetSupport) : Text.Json.Nodes.JsonNode =
        Json.obj [
            "available", Json.ofBool (isAvailable s)
            "dotnetHost", (s.dotnetHost |> Json.ofOption Json.ofString)
            "sdks", (Json.ofStrings s.sdks :> Text.Json.Nodes.JsonNode)
            "requiredSdk", Json.ofString (requiredSdk s)
            "problem", (problem s |> Json.ofOption Json.ofString)
            "download", (if isAvailable s then null else Json.ofString (downloadUrl s))
        ]
        :> Text.Json.Nodes.JsonNode


/// Finds the .NET SDKs that F# Interactive uses to restore NuGet packages, without starting a process.
module DotnetSdk =

    let private exe = if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"

    let private fileExists (path: string) =
        try File.Exists path with _ -> false

    /// The dotnet executable, found in the same places and order as FCS does, see getDotnetHostPath in
    /// https://github.com/dotnet/fsharp/blob/main/src/Compiler/Facilities/CompilerLocation.fs
    let findHost () : string option =
        let candidates: (unit -> string option) list = [
            fun () -> Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" |> Option.ofObj
            // the runtime that runs this process, e.g. C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.21\System.Private.CoreLib.dll
            fun () -> Some(Path.GetFullPath(Path.Combine(Path.GetDirectoryName typeof<int>.Assembly.Location, "..", "..", "..", exe)))
            fun () ->
                match Environment.GetEnvironmentVariable "PATH" with
                | null -> None
                | path ->
                    path.Split([| Path.PathSeparator |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.tryPick (fun dir ->
                        try
                            let f = Path.Combine(dir.Trim().Trim('"'), exe)
                            if fileExists f then Some f else None
                        with _ -> None)
            fun () ->
                if OperatingSystem.IsWindows() then
                    match Environment.GetEnvironmentVariable "ProgramW6432" with
                    | null
                    | "" -> Some(Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ProgramFiles, "dotnet", exe))
                    | pf -> Some(Path.Combine(pf, "dotnet", exe))
                else None
        ]
        candidates
        |> List.tryPick (fun c ->
            try c () |> Option.filter fileExists
            with _ -> None)

    /// The versions of the SDKs next to a dotnet executable, in its folder 'sdk', newest first.
    let sdksOf (host: string) : string list =
        try
            let dir = Path.Combine(Path.GetDirectoryName host, "sdk")
            if not (Directory.Exists dir) then []
            else
                Directory.GetDirectories dir
                |> Array.filter (fun d -> fileExists (Path.Combine(d, "dotnet.dll")))
                |> Array.map Path.GetFileName
                |> Array.choose (fun name ->
                    match Version.TryParse(name.Split('-')[0]) with
                    | true, v -> Some(v, name)
                    | _ -> None)
                // a release before its previews, e.g. 10.0.100 before 10.0.100-rc.2
                |> Array.sortByDescending (fun (v, name) -> v, not (name.Contains "-"), name)
                |> Array.map snd
                |> List.ofArray
        with _ -> []

    let check () : NuGetSupport =
        let host = findHost ()
        {
            dotnetHost = host
            sdks = host |> Option.map sdksOf |> Option.defaultValue []
#if NETFRAMEWORK
            requiredMajor = None
#else
            requiredMajor = Some Environment.Version.Major
#endif
        }

    let private cacheTime = TimeSpan.FromSeconds 10.0

    let mutable private cached: (DateTime * NuGetSupport) option = None

    /// Checked again after 10 s, so that an SDK installed while the host app runs is found.
    let current () : NuGetSupport =
        match cached with
        | Some(time, s) when DateTime.UtcNow - time < cacheTime -> s
        | _ ->
            let s = check ()
            cached <- Some(DateTime.UtcNow, s)
            s

    /// The next call of current checks again.
    let clearCache () = cached <- None

    /// Whether code references a NuGet package, e.g. '#r "nuget: FSharp.Data"'.
    let referencesNuGet (code: string) =
        not (isNull code) && Regex.IsMatch(code, """^\s*#r\s+@?"nuget\s*:""", RegexOptions.Multiline ||| RegexOptions.IgnoreCase)
