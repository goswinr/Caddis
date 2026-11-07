## Caddis for AutoCAD 2025 and later

To install, download and run `Caddis.AutoCAD-autocad-Setup.exe` from the Assets.

It registers Caddis with AutoCAD 2025 and later, and with the products based on them, through a bundle that AutoCAD loads when it starts.
AutoCAD 2025 and 2026 run it on .NET 8 as released, or on .NET 10 from an update on, e.g. AutoCAD 2025 from its update 25.0.189.
Caddis loads its build for the runtime of AutoCAD when AutoCAD starts, so it keeps working when AutoCAD is updated.

To use NuGet packages in scripts via `#r "nuget: Name"`, also install the [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) or newer.
Caddis runs without it, only NuGet packages need it.

<details>

<summary>More info on the installation</summary>

No admin rights are needed to install or run it.<br>
Caddis is installed in `C:\Users\{username}\AppData\Local\Caddis.AutoCAD\current`,<br>
and registered with the bundle `%AppData%\Autodesk\ApplicationPlugins\Caddis.bundle`, which has the small loader of Caddis.<br>
An AutoCAD that is running while you install loads Caddis right away. If AutoCAD asks whether to load `Caddis.AutoCAD.Loader.dll`, choose 'Always Load'.

When AutoCAD starts, Caddis checks for a newer version and offers to download it.
It is installed when the last AutoCAD has closed.

The installer is code-signed via Azure Trusted Signing.

See the [Changelog](https://github.com/goswinr/Caddis/blob/main/CHANGELOG.md) for the changes in this release.

This release was created with [Velopack](https://velopack.io/).

</details>
