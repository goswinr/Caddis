## Caddis for Revit 2024 to 2027

To install, download and run `Caddis.Revit-win-Setup.exe` from the Assets.

It registers Caddis with every installed Revit from 2024 on.
Revit 2024 runs it on .NET Framework 4.8, Revit 2025 and 2026 on .NET 8, or on .NET 10 from their .5 updates on, Revit 2027 on .NET 10.
Caddis loads its build for the runtime of Revit when Revit starts, so it keeps working when Revit is updated.

To use NuGet packages in scripts via `#r "nuget: Name"`, also install the [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) or newer.
Caddis runs without it, only NuGet packages need it.

For Rhino, install Caddis with Rhino's Package Manager instead.

<details>

<summary>More info on the installation</summary>

No admin rights are needed to install or run it.<br>
Caddis is installed in `C:\Users\{username}\AppData\Local\Caddis.Revit\current`,<br>
and registered with a `Caddis.addin` in `%AppData%\Autodesk\Revit\Addins\{year}` for each Revit.<br>
For a Revit that you install later, start `Caddis.Revit.Bootstrapper.exe` in that folder again.

When Revit starts, Caddis checks for a newer version and offers to download it.
It is installed when the last Revit has closed.

The installer is code-signed via Azure Trusted Signing.

See the [Changelog](https://github.com/goswinr/Caddis/blob/main/CHANGELOG.md) for the changes in this release.

This release was created with [Velopack](https://velopack.io/).

</details>
