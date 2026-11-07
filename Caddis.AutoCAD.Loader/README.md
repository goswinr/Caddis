# Caddis.AutoCAD.Loader

The plugin that the bundle `Caddis.bundle` loads in AutoCAD 2025 and later. It loads the build of [Caddis.AutoCAD](../Caddis.AutoCAD/README.md)
for the .NET runtime that AutoCAD runs on. The same as [Caddis.Revit.Loader](../Caddis.Revit.Loader/README.md) for Revit.

## Why it exists
AutoCAD 2025 and 2026 run on .NET 8 as released and on .NET 10 from an update on, e.g. AutoCAD 2025 from its update 25.0.189.
So the runtime of an AutoCAD can change with an update, while the bundle in `%AppData%\Autodesk\ApplicationPlugins` stays the same.

Caddis.AutoCAD can't be one dll for both runtimes: it needs FSharp.Compiler.Service and FSharp.Core 8 on .NET 8 and 10 on .NET 10,
and AutoCAD loads only one version of them. So there is a `net8.0` and a `net10.0` build, and the bundle loads the loader, which picks one from `Environment.Version`.

## Why it is so small, and in C#
- It is built for .NET 8, so that it runs on both runtimes.
- It is in C#, so that it loads no FSharp.Core. Otherwise its own FSharp.Core would be loaded before the one of the build it picks, and could be the wrong version.
- It references the API of AutoCAD 2025, the oldest AutoCAD it runs in.
- It only loads Caddis. AutoCAD registers the commands of every assembly loaded into its process and calls its `IExtensionApplication`,
  so `Assembly.LoadFrom` is enough: AutoCAD then calls `Initialize` of Caddis.AutoCAD, and its commands and ribbon tab refer to `Caddis.AutoCAD.dll` itself.
- If a Caddis.AutoCAD is loaded already, e.g. by a second loader or `NETLOAD`, it does nothing.

## Folder layout
The [packaging action](../.github/actions/package-autocad/action.yml) publishes the builds, and the installer puts them in `%LocalAppData%\Caddis.AutoCAD\current`:
```
current/
  loader/    Caddis.AutoCAD.Loader.dll   copied into the bundle by the bootstrapper
  net8.0/    Caddis.AutoCAD.dll          for AutoCAD on .NET 8
  net10.0/   Caddis.AutoCAD.dll          for AutoCAD on .NET 10
```
A bundle names its modules relative to its own folder. So the [bootstrapper](../Caddis.AutoCAD.Bootstrapper/AutoCAD.fs) writes the bundle with a copy of the loader:
```
%AppData%\Autodesk\ApplicationPlugins\Caddis.bundle/
  PackageContents.xml                 loads ./Contents/Caddis.AutoCAD.Loader.dll when AutoCAD starts, from series R25.0 on
  Contents/Caddis.AutoCAD.Loader.dll
  Contents/install-folder.txt         the folder of the installation, e.g. %LocalAppData%\Caddis.AutoCAD\current
```
The loader finds the builds through `install-folder.txt`, or without it in the folder above its own, as in `current` and in the local builds of Start-Dev.
`Caddis.AutoCAD.dll` is loaded with `Assembly.LoadFrom`, so its dependencies, e.g. FSharp.Core, are loaded from its own folder.
If it fails to load, the loader shows the path and the error in a dialog and writes them to `%AppData%\Caddis\autocad.log`.

## Local builds
If the environment variable `CADDIS_AUTOCAD_DEV` is set, the loader loads `Caddis.AutoCAD.dll` from its subfolder `net8.0` or `net10.0` instead.
[Dev/Start-Dev.ps1](../Dev/Start-Dev.ps1) sets it only for the AutoCAD it starts, with a local build in `%LocalAppData%\Caddis.Dev\AutoCAD<year>`,
and loads the local loader with `NETLOAD`, see [Dev/README.md](../Dev/README.md). If Caddis is installed, its loader loads the local build already, and the local loader does nothing.
Caddis.AutoCAD finds no bootstrapper next to that folder, so it doesn't check for updates.

## Don't rename it
AutoCAD knows the bundle by the ProductCode and UpgradeCode in its `PackageContents.xml`, and the module by its path in the bundle.
Renaming the dll changes that path; the bootstrapper writes the bundle again when it installs or updates Caddis.
