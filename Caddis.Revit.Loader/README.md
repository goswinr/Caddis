# Caddis.Revit.Loader

The add-in that `Caddis.addin` registers in Revit 2025 and later. It loads the build of [Caddis.Revit](../Caddis.Revit/README.md)
for the .NET runtime that Revit runs on, and passes Revit's calls on to it.

## Why it exists
Revit 2025 and 2026 run on .NET 8 up to their .4 updates and on .NET 10 from their .5 updates on. Revit 2027 runs on .NET 10.
So the runtime of a Revit can change with an update, while the `.addin` file in `%AppData%\Autodesk\Revit\Addins\<year>` stays the same.

Caddis.Revit can't be one dll for both runtimes: it needs FSharp.Compiler.Service and FSharp.Core 8 on .NET 8 and 10 on .NET 10,
and Revit loads only one version of them. So there is a `net8.0` and a `net10.0` build.
If `Caddis.addin` pointed at one of them, Caddis would fail to load after the update of Revit to the other runtime,
until the bootstrapper rewrote the `.addin` file.

Instead `Caddis.addin` points at the loader. When Revit starts, it picks the build from `Environment.Version`.

## Why it is so small, and in C#
- It is built for .NET 8, so that it runs on both runtimes.
- It is in C#, so that it loads no FSharp.Core. Otherwise its own FSharp.Core would be loaded before the one of the build it picks, and could be the wrong version.
- It references the RevitAPIUI of Revit 2025.0, the oldest Revit it runs in. Revit on .NET fails to load an add-in that references a newer RevitAPI than its own.
- It only starts and shuts down Caddis. The ribbon buttons and the panel of Caddis refer to `Caddis.Revit.dll` itself.

Revit 2024 runs on .NET Framework 4.8, its `Caddis.addin` points at the `net48` build directly.

## Folder layout
The loader expects the builds in sibling folders, as the [packaging action](../.github/actions/package-revit/action.yml) publishes them
and the installer puts them in `%LocalAppData%\Caddis.Revit\current`:
```
current/
  loader/    Caddis.Revit.Loader.dll   registered in Revit 2025 and later
  net8.0/    Caddis.Revit.dll          for Revit on .NET 8
  net10.0/   Caddis.Revit.dll          for Revit on .NET 10
  net48/     Caddis.Revit.dll          registered in Revit 2024
```
`Caddis.Revit.dll` is loaded with `Assembly.LoadFrom`, so its dependencies, e.g. FSharp.Core, are loaded from its own folder.
If it fails to load, the loader shows the path and the error in a dialog.

The development install with [install-dev.ps1](../Caddis.Revit/install-dev.ps1) doesn't use the loader:
it registers the build for the .NET of that Revit directly, as named in Revit's `RevitAPI.runtimeconfig.json`.

## Don't rename it
The [bootstrapper](../Caddis.Revit.Bootstrapper/Revit.fs) writes `Caddis.Revit.Loader.CaddisLoader` in `loader/Caddis.Revit.Loader.dll`
into the `.addin` files. Renaming the class, the namespace, the dll or its folder breaks the add-in until the `.addin` files are written again.
