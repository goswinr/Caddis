namespace Caddis.Revit.Bootstrapper

open System
open System.IO
open System.Security

/// A message about the registration, shown in the window of the bootstrapper.
type Message =
    | Info of string
    | Done of string
    | Problem of string

/// Registers Caddis with every installed Revit from 2024 on, with a Caddis.addin in the add-in folder of the current user.
/// see https://help.autodesk.com/view/RVT/2026/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Introduction_Add_In_Integration_Add_in_Registration_html
module Revit =

    /// Revit 2024 is the first with Int64 element ids and the dark theme.
    let firstYear = 2024

    /// Revit's installer creates a folder for its version here, for the add-ins of all users.
    let private machineAddins =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.CommonApplicationData, "Autodesk", "Revit", "Addins")

    /// Caddis is installed for the current user only, so it is registered for the current user only.
    /// install-dev.ps1 writes the same file.
    let private userAddins =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "Autodesk", "Revit", "Addins")

    /// Velopack's 'current' folder, with this exe, the loader and a folder for each build of Caddis.Revit. It stays the same across updates.
    let installFolder = AppDomain.CurrentDomain.BaseDirectory

    /// The dll and class Revit loads. Revit 2024 runs on .NET Framework 4.8.
    /// Revit 2025 and later run on .NET 8 or .NET 10, depending on their update, so the loader picks the build when Revit starts.
    let private entryPoint (year: int) =
        if year < 2025 then Path.Combine(installFolder, "net48", "Caddis.Revit.dll"), "Caddis.Revit.CaddisApplication"
        else Path.Combine(installFolder, "loader", "Caddis.Revit.Loader.dll"), "Caddis.Revit.Loader.CaddisLoader"

    /// Revit identifies the add-in by its AddInId, e.g. for the position of its pane, don't change it.
    let private addinXml (dll: string) (className: string) =
        $"""<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Caddis</Name>
    <Assembly>{SecurityElement.Escape dll}</Assembly>
    <FullClassName>{className}</FullClassName>
    <AddInId>4D0EC386-3A78-491A-95FA-9B5A65CAA89C</AddInId>
    <VendorId>goswinr</VendorId>
    <VendorDescription>Goswin Rothenthal, https://github.com/goswinr/Caddis</VendorDescription>
  </AddIn>
</RevitAddIns>
"""

    let private addinFile (year: int) = Path.Combine(userAddins, string year, "Caddis.addin")

    /// True if the .addin file registers this installation, not e.g. a build registered by install-dev.ps1.
    let private isOwn (file: string) =
        try File.ReadAllText(file).IndexOf(SecurityElement.Escape installFolder, StringComparison.OrdinalIgnoreCase) >= 0
        with _ -> false

    /// The versions of the installed Revits, e.g. 2026, from the names of their add-in folders.
    let installedYears () =
        if Directory.Exists machineAddins then
            Directory.GetDirectories machineAddins
            |> Array.choose (fun dir ->
                match Int32.TryParse(Path.GetFileName dir) with
                | true, year when year > 2000 -> Some year
                | _ -> None)
            |> Array.sort
        else
            [||]

    /// Writes Caddis.addin for every installed Revit from 2024 on. Overwrites a registration of a build by install-dev.ps1.
    let register (log: Message -> unit) =
        match installedYears () with
        | [||] -> log (Problem $"No Revit is installed: {machineAddins} has no folder for a Revit version.")
        | years ->
            for year in years do
                if year < firstYear then
                    log (Info $"Revit {year} is installed too, Caddis needs Revit {firstYear} or later.")
                else
                    let file = addinFile year
                    let dll, className = entryPoint year
                    try
                        Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                        File.WriteAllText(file, addinXml dll className, Text.Encoding.UTF8)
                        log (Done $"Registered Caddis with Revit {year} in {file}")
                    with e ->
                        log (Problem $"Registering Caddis with Revit {year} failed: {e.Message}")

    /// Deletes the Caddis.addin files that register this installation.
    let unregister (log: Message -> unit) =
        if Directory.Exists userAddins then
            for dir in Directory.GetDirectories userAddins do
                let file = Path.Combine(dir, "Caddis.addin")
                if File.Exists file && isOwn file then
                    try
                        File.Delete file
                        log (Done $"Deleted {file}")
                    with e ->
                        log (Problem $"Deleting {file} failed: {e.Message}")
