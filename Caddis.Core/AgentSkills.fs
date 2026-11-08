namespace Caddis

open System
open System.IO

/// Agent skills that NuGet packages ship in skills/<skill>/, e.g. the guide of Rhino.Scripting for AI agents.
/// The files of the packages whose assemblies are loaded are offered as MCP resources, see McpEndpoint.
module AgentSkills =

    /// The URI scheme of the skill files, e.g. caddis-skill://rhino.scripting/0.15.0/rhino.scripting-usage/SKILL.md
    [<Literal>]
    let scheme = "caddis-skill://"

    /// A file of a skill, offered as an MCP resource.
    type SkillFile = {
        uri: string
        name: string
        path: string
        size: int64
        mimeType: string
        }

    /// The folder of the package of an assembly, e.g. C:\...\packages\rhino.scripting\0.15.0 for C:\...\packages\rhino.scripting\0.15.0\lib\net8.0\Rhino.Scripting.dll.
    /// None if the assembly is not in the lib folder of a package, e.g. the assemblies of the host app.
    let packageRootOf (assemblyPath: string) : string option =
        if String.IsNullOrEmpty assemblyPath then
            None
        else
            let framework = Path.GetDirectoryName assemblyPath
            let lib = if isNull framework then null else Path.GetDirectoryName framework
            if isNull lib || not (String.Equals(Path.GetFileName lib, "lib", StringComparison.OrdinalIgnoreCase)) then
                None
            else
                Option.ofObj (Path.GetDirectoryName lib)

    /// The skill files in skills of each package folder, e.g. rhino.scripting-usage/SKILL.md, sorted by skill and file name.
    /// A package folder that can't be read is skipped, so that the other skills are still listed.
    let ofPackageRoots (roots: string seq) : SkillFile list =
        roots
        |> Seq.distinctBy (fun root -> root.ToLowerInvariant())
        |> Seq.collect (fun root ->
            try
                let skillsFolder = Path.Combine(root, "skills")
                if not (Directory.Exists skillsFolder) then
                    Seq.empty
                else
                    let version = Path.GetFileName root
                    let id =
                        match Path.GetDirectoryName root with
                        | null -> ""
                        | parent -> Path.GetFileName parent
                    [
                        for skillFolder in Directory.GetDirectories skillsFolder |> Array.sort do
                            let skill = Path.GetFileName skillFolder
                            for file in Directory.GetFiles skillFolder |> Array.sort do
                                let fileName = Path.GetFileName file
                                yield {
                                    uri = $"{scheme}{id}/{version}/{skill}/{fileName}"
                                    name = $"{id} {version}: {skill}/{fileName}"
                                    path = file
                                    size = FileInfo(file).Length
                                    mimeType = if fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) then "text/markdown" else "text/plain"
                                }
                    ]
                    |> Seq.ofList
            with _ ->
                Seq.empty)
        |> List.ofSeq

    /// The skill files of the packages of the loaded assemblies, e.g. Rhino.Scripting after #r "nuget: Rhino.Scripting.FSharp".
    let loaded () : SkillFile list =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Array.choose (fun a ->
            let path = try (if a.IsDynamic then "" else a.Location) with _ -> ""
            packageRootOf path)
        |> Array.distinct
        |> ofPackageRoots

    /// The skill file with this URI among the loaded packages. None if there is none, so that only listed files can be read.
    let tryFind (uri: string) : SkillFile option =
        loaded () |> List.tryFind (fun f -> f.uri = uri)
