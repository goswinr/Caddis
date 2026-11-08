module Caddis.Tests.AgentSkillsTests

open System.IO
open Xunit
open Caddis
open Caddis.Tests.Helpers

[<Fact>]
let ``packageRootOf finds the package folder of an assembly in lib`` () =
    let path = Path.Combine("packages", "rhino.scripting", "0.15.0", "lib", "net8.0", "Rhino.Scripting.dll")
    Assert.Equal(Some(Path.Combine("packages", "rhino.scripting", "0.15.0")), AgentSkills.packageRootOf path)

[<Fact>]
let ``packageRootOf ignores assemblies that are not in a lib folder`` () =
    Assert.Equal(None, AgentSkills.packageRootOf (Path.Combine("Rhino 8", "System", "RhinoCommon.dll")))
    Assert.Equal(None, AgentSkills.packageRootOf "")

[<Fact>]
let ``ofPackageRoots lists the files of skills with their URIs`` () =
    let root = Path.Combine(newTempDir (), "rhino.scripting", "0.15.0")
    let skill = Path.Combine(root, "skills", "rhino.scripting-usage")
    Directory.CreateDirectory skill |> ignore
    File.WriteAllText(Path.Combine(skill, "SKILL.md"), "# Skill")
    File.WriteAllText(Path.Combine(skill, "ApiTable.md"), "| table |")
    let withoutSkills = Path.Combine(newTempDir (), "other", "1.0.0")
    Directory.CreateDirectory(Path.Combine(withoutSkills, "lib")) |> ignore
    let files = AgentSkills.ofPackageRoots [ root; withoutSkills ]
    Assert.Equal<string list>(
        [
            "caddis-skill://rhino.scripting/0.15.0/rhino.scripting-usage/ApiTable.md"
            "caddis-skill://rhino.scripting/0.15.0/rhino.scripting-usage/SKILL.md"
        ],
        files |> List.map _.uri
    )
    Assert.Equal("text/markdown", files[0].mimeType)
    Assert.Equal(9L, files[0].size)

[<Fact>]
let ``ofPackageRoots lists nothing for a folder without skills`` () =
    Assert.Empty(AgentSkills.ofPackageRoots [ newTempDir () ])
