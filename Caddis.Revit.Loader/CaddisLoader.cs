using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace Caddis.Revit.Loader
{
    /// <summary>
    /// The entry point in Revit 2025 and later, registered in Caddis.addin. Don't rename it.
    /// Loads Caddis.Revit.dll from the folder net8.0 or net10.0 next to the folder of the loader, for the .NET runtime that Revit runs on,
    /// or in the folder named by CADDIS_REVIT_DEV, and passes Revit's calls on to it. The ribbon buttons of Caddis refer to that dll, the loader is only needed at start and shutdown.
    /// </summary>
    public class CaddisLoader : IExternalApplication
    {
        IExternalApplication? caddis;

        /// <summary>Set by Dev/Start-Dev.ps1 for the Revit it starts: a folder with a local build of Caddis.Revit in net8.0 or net10.0,
        /// loaded instead of the installed one.</summary>
        const string DevFolderVariable = "CADDIS_REVIT_DEV";

        /// <summary>The path of Caddis.Revit.dll for the runtime of this process.</summary>
        static string CaddisPath()
        {
            string framework = Environment.Version.Major >= 10 ? "net10.0" : "net8.0";
            string? devFolder = Environment.GetEnvironmentVariable(DevFolderVariable);
            string folder = string.IsNullOrEmpty(devFolder)
                ? Path.GetDirectoryName(Path.GetDirectoryName(typeof(CaddisLoader).Assembly.Location)!)!
                : devFolder;
            return Path.Combine(folder, framework, "Caddis.Revit.dll");
        }

        /// <summary>Loads Caddis.Revit and starts it.</summary>
        public Result OnStartup(UIControlledApplication application)
        {
            string path = CaddisPath();
            try
            {
                // its dependencies, e.g. FSharp.Core, are loaded from its folder
                Assembly assembly = Assembly.LoadFrom(path);
                Type type = assembly.GetType("Caddis.Revit.CaddisApplication", throwOnError: true)!;
                caddis = (IExternalApplication)Activator.CreateInstance(type)!;
            }
            catch (Exception e)
            {
                TaskDialog.Show("Load error", $"Caddis failed to load {path}:{Environment.NewLine}{e.Message}");
                return Result.Failed;
            }
            return caddis.OnStartup(application);
        }

        /// <summary>Shuts Caddis.Revit down.</summary>
        public Result OnShutdown(UIControlledApplication application) =>
            caddis is null ? Result.Succeeded : caddis.OnShutdown(application);
    }
}
