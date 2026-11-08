namespace Caddis.AutoCAD.Bootstrapper

open System
open System.IO
open System.Security

/// A message about the registration, shown in the window of the bootstrapper.
type Message =
    | Info of string
    | Done of string
    | Problem of string

/// Registers Caddis with every AutoCAD from 2025 on, with the bundle Caddis.bundle in the plugin folder of the current user.
/// AutoCAD loads the bundles in that folder when it starts, and a bundle that appears there while it runs.
/// See 'PackageContents.xml Format Reference' in the ObjectARX and Managed .NET developer's guide of AutoCAD.
module AutoCAD =

    /// AutoCAD 2025 is the first on .NET 8; AutoCAD 2024 and older run on .NET Framework.
    let firstYear = 2025

    /// The plugin folder of the current user. Caddis is installed for the current user only.
    let private userPlugins =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "Autodesk", "ApplicationPlugins")

    let bundleFolder = Path.Combine(userPlugins, "Caddis.bundle")

    /// Velopack's 'current' folder, with this exe, the loader and a folder for each build of Caddis.AutoCAD. It stays the same across updates.
    let installFolder = AppDomain.CurrentDomain.BaseDirectory

    /// The loader and the file with the install folder in the bundle. Bundles name their modules relative to their folder,
    /// so the loader is copied into it, and finds the builds through the file, see Caddis.AutoCAD.Loader/CaddisLoader.cs.
    let private contents = Path.Combine(bundleFolder, "Contents")
    let private loaderFile = Path.Combine(contents, "Caddis.AutoCAD.Loader.dll")
    let private installFolderFile = Path.Combine(contents, "install-folder.txt")

    let private version = Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString 3

    /// AutoCAD identifies the bundle by its ProductCode and UpgradeCode, don't change them.
    /// For every AutoCAD-based product from AutoCAD 2025, series R25.0, on. The loader reports it if a later AutoCAD can't load Caddis.
    let private packageContents () =
        $"""<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" ProductType="Application" Name="Caddis"
  Description="Hosts a persistent F# Interactive session inside AutoCAD, controlled through HTTP and MCP." Author="Goswin Rothenthal" AppVersion="{version}"
  ProductCode="{{B6E4C0F1-7A2D-4E5B-9C3F-1D8A6E2B4C70}}" UpgradeCode="{{3F9A1C2E-5B7D-4A6E-8C0F-2E4D6B8A1C93}}"
  HelpFile="https://github.com/goswinr/Caddis/blob/main/Caddis.AutoCAD/README.md">
  <CompanyDetails Name="Goswin Rothenthal" Url="https://github.com/goswinr/Caddis" />
  <Components Description="Caddis for AutoCAD 2025 and later">
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R25.0" />
    <ComponentEntry AppName="Caddis" Version="{version}" ModuleName="./Contents/Caddis.AutoCAD.Loader.dll"
      AppDescription="Loads the build of Caddis for the .NET that AutoCAD runs on." LoadOnAutoCADStartup="True" />
  </Components>
</ApplicationPackage>
"""

    /// True if the bundle registers this installation, not e.g. one in another folder.
    let private isOwn () =
        try File.ReadAllText(installFolderFile).Trim().Equals(installFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        with _ -> false

    /// The versions of the installed AutoCADs, e.g. 2025, from the names of their folders in Program Files.
    let installedYears () =
        let autodesk = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ProgramFiles, "Autodesk")
        if Directory.Exists autodesk then
            Directory.GetDirectories(autodesk, "AutoCAD 20*")
            |> Array.choose (fun dir ->
                match Int32.TryParse(Path.GetFileName(dir).Substring "AutoCAD ".Length) with
                | true, year when File.Exists(Path.Combine(dir, "acad.exe")) -> Some year
                | _ -> None)
            |> Array.sort
        else
            [||]

    /// Writes the bundle. Overwrites the bundle of another installation. A running AutoCAD locks the loader, then the old one stays until the next time.
    let register (log: Message -> unit) =
        try
            Directory.CreateDirectory contents |> ignore
            let loader = Path.Combine(installFolder, "loader", "Caddis.AutoCAD.Loader.dll")
            try File.Copy(loader, loaderFile, true)
            with :? IOException when File.Exists loaderFile ->
                log (Info $"AutoCAD is running and uses {loaderFile}, it is replaced the next time this program runs while no AutoCAD is running.")
            File.WriteAllText(installFolderFile, installFolder.TrimEnd('\\'))
            File.WriteAllText(Path.Combine(bundleFolder, "PackageContents.xml"), packageContents (), Text.Encoding.UTF8)
            log (Done $"Registered Caddis with AutoCAD in {bundleFolder}")
            match installedYears () with
            | [||] -> log (Problem "No AutoCAD is installed: there is no folder 'Autodesk\\AutoCAD <year>' with acad.exe in Program Files.")
            | years ->
                for year in years do
                    if year < firstYear then log (Info $"AutoCAD {year} is installed too, Caddis needs AutoCAD {firstYear} or later.")
                    else log (Done $"AutoCAD {year} loads Caddis when it starts.")
        with e ->
            log (Problem $"Registering Caddis with AutoCAD in {bundleFolder} failed: {e.Message}")

    /// Deletes the bundle if it registers this installation.
    let unregister (log: Message -> unit) =
        if Directory.Exists bundleFolder && isOwn () then
            try
                Directory.Delete(bundleFolder, true)
                log (Done $"Deleted {bundleFolder}")
            with e ->
                log (Problem $"Deleting {bundleFolder} failed: {e.Message}")
