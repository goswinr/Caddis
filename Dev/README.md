# Run a local build in Rhino or Revit

The files in this folder build Caddis and start Rhino or Revit with only that build, also when Caddis is installed from Yak or with the Revit installer.
The installed Caddis stays installed: Rhino or Revit started in any other way loads it as before, so there is nothing to switch back.
Double-click one of them, or pin a shortcut to it on the taskbar:

| File | Starts |
|---|---|
| `Rhino8.cmd` | Rhino 8 on .NET 8 |
| `Rhino8NetFx.cmd` | Rhino 8 on .NET Framework |
| `Rhino9.cmd` | Rhino 9 on .NET 10 |
| `Revit2024.cmd` to `Revit2027.cmd` | that Revit |
| `Start-Dev.cmd` | asks which of the installed apps to start, or takes it as argument, e.g. `Start-Dev.cmd Rhino8` |

All of them run [Start-Dev.ps1](Start-Dev.ps1). If something fails, the window stays open and shows why.
- It builds the Debug configuration for the .NET the app runs on, and copies the build to `%LocalAppData%\Caddis.Dev\<app>`.
  The app locks that copy, not `bin`, so you can build again while it runs. To load the new build, close the app and start it with its `.cmd` again.
- From a terminal they take options: `-Configuration Release` builds the Release configuration, `-NoBuild` starts the app with the last build.
  Other arguments are passed on to the app, e.g. `Dev\Rhino8.cmd /scheme=CaddisDev` for a Rhino scheme with its own settings and layout.
- If the script is stopped before it puts the installed Caddis back, its next run does it.

## How the app gets only the local build
- **Rhino** loads it from the folder in the environment variable `RHINO_PACKAGE_DIRS`, set only for the Rhino started here.
  But Rhino would still load the installed Yak package, which has the same plugin id.
  So its folder `%AppData%\McNeel\Rhinoceros\packages\<8.0 or 9.0>\Caddis` is moved to `%AppData%\Caddis\dev\hidden` until Rhino has loaded the local build, a few seconds.
  Then it is moved back, and the path of the plugin that Rhino saved in the registry is put back too, so that the next normal start loads the installed Caddis.
  While another Rhino of the same version runs with the installed Caddis, the folder can't be moved, close that Rhino first.
  Don't hide the package by renaming its `manifest.txt` instead: Rhino then deletes the package.
- **Revit 2025 and later**, with an installed Caddis newer than 0.2.1: the [loader](../Caddis.Revit.Loader/README.md) loads Caddis.Revit from the folder in the environment variable `CADDIS_REVIT_DEV`,
  set only for the Revit started here. Nothing else changes.
- **Revit 2024**, or an installed Caddis up to 0.2.1: `Caddis.addin` in `%AppData%\Autodesk\Revit\Addins\<year>` is replaced by one for the local build
  until Revit has loaded it, then put back. Revit asks whether to load the unsigned add-in, choose `Always Load` or `Load Once`.

To register a local build in Revit for every start instead, see [install-dev.ps1](../Caddis.Revit/README.md#installation-for-development).
