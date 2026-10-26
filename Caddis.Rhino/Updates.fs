namespace Caddis.Rhino

open System
open System.IO
open System.Net.Http
open System.Reflection
open System.Text.Json.Nodes
open System.Threading.Tasks
open Rhino
open Caddis

/// Updates of Caddis come from Yak, McNeel's package server, and Rhino's Package Manager installs them.
/// Caddis only tells about them: at Rhino's start it asks Yak for the newest release for this Rhino,
/// and the package's folder shows whether the Package Manager has installed a newer one, which Rhino loads when it restarts.
module internal Updates =

    /// The version of this build, e.g. 0.3.1, from the changelog.
    let version =
        let a = Assembly.GetExecutingAssembly()
        match a.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> a.GetName().Version.ToString 3
        | v -> v.InformationalVersion.Split('+')[0]

    /// The manifest.txt of the package, with the version that Rhino loads at its start, e.g. in
    /// %AppData%/McNeel/Rhinoceros/packages/8.0/Caddis, above the folder of each version and its runtime folders, e.g. 0.3.1/net8.0.
    /// None for a build not installed by the Package Manager, e.g. one started by Dev/Rhino8.cmd.
    let private manifest =
        try
            let runtimeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            let path = Path.Combine(runtimeDir, "..", "..", "manifest.txt") |> Path.GetFullPath
            if File.Exists path then Some path else None
        with _ ->
            None

    let private installed = manifest.IsSome

    /// The version in the manifest and the time the file was written. Read again when the file changes.
    let mutable private lastManifest: (DateTime * string) option = None

    /// The version that the Package Manager has installed, the one Rhino loads at its next start.
    let private installedVersion () =
        match manifest with
        | None -> None
        | Some path ->
            try
                let written = File.GetLastWriteTimeUtc path
                match lastManifest with
                | Some(time, v) when time = written -> Some v
                | _ ->
                    let v = File.ReadAllText(path).Trim()
                    lastManifest <- Some(written, v)
                    Some v
            with _ ->
                lastManifest |> Option.map snd

    /// The result of the check at Rhino's start: the newest release for this Rhino, or why the check failed. None until it is done.
    let mutable private latest: Result<string, string> option = None

    /// Whether a distribution of a release is for this Rhino, by its tag: rh8_19 is for Rhino 8.19 and later Rhino 8s.
    let private isForThisRhino (tag: string) =
        let v = RhinoApp.Version
        match tag.TrimStart('r', 'h').Split('_') with
        | [| major |] -> major = string v.Major
        | [| major; minor |] ->
            match Int32.TryParse minor with
            | true, m -> major = string v.Major && m <= v.Minor
            | _ -> false
        | _ -> false

    /// The newest stable release on Yak with a distribution for this Rhino, from the JSON of https://yak.rhino3d.com/versions/caddis.
    let private newestRelease (json: string) =
        let array (name: string) (node: JsonNode) =
            match Json.prop name node with
            | Some(:? JsonArray as a) -> Seq.toList a
            | _ -> []
        match Json.parse json with
        | :? JsonArray as releases ->
            releases
            |> Seq.filter (fun r ->
                Json.tryBool "prerelease" r <> Some true
                && array "distributions" r |> List.exists (fun d -> Json.tryString "rhino_version" d |> Option.exists isForThisRhino))
            |> Seq.choose (Json.tryString "version")
            |> Seq.fold (fun newest v -> if ReleaseVersion.isNewer v newest then v else newest) version
        | _ -> failwith "Yak's answer is not a list of versions."

    /// At Rhino's start, on the thread pool: asks Yak for the newest release for this Rhino.
    let check () =
        if installed then
            Task.Run(fun () ->
                task {
                    try
                        use client = new HttpClient(Timeout = TimeSpan.FromSeconds 20.0)
                        let! json = client.GetStringAsync "https://yak.rhino3d.com/versions/caddis"
                        latest <- Some(Ok(newestRelease json))
                    with e ->
                        // not on the command line, e.g. offline it would fail at every start; the settings in the panel show it
                        latest <- Some(Error e.Message)
                }
                :> Task)
            |> ignore

    /// Whether a newer Caddis is installed or available. None if not, or if Caddis was not installed by the Package Manager.
    let notice () =
        match installedVersion (), latest with
        | Some i, _ when ReleaseVersion.isNewer i version ->
            Some $"Caddis {i} is installed, Rhino loads it when it restarts. You're using {version}."
        | _, Some(Ok l) when ReleaseVersion.isNewer l version ->
            Some $"Caddis {l} is available, you're using {version}. Update it in Rhino's Package Manager, Rhino loads it when it restarts."
        | _ -> None

    /// The version of Caddis and whether a newer one is installed or available.
    let status () =
        if not installed then
            $"You're using Caddis {version}. Rhino's Package Manager did not install it, so Caddis doesn't check for updates."
        else
            match notice (), latest with
            | Some n, _ -> n
            | None, Some(Ok _) -> $"You're using the latest version of Caddis, {version}."
            | None, Some(Error e) -> $"You're using Caddis {version}. Checking for updates failed: {e}"
            | None, None -> $"You're using Caddis {version}."
