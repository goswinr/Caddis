using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace Caddis.Revit.Loader
{
    /// <summary>
    /// The entry point in Revit 2025 and later, registered in Caddis.addin. Don't rename it.
    /// Loads Caddis.Revit.dll from the folder net8.0 or net10.0 next to the folder of the loader, for the .NET runtime that Revit runs on,
    /// and passes Revit's calls on to it. The ribbon buttons of Caddis refer to that dll, the loader is only needed at start and shutdown.
    /// </summary>
    public class CaddisLoader : IExternalApplication
    {
        IExternalApplication? caddis;

        /// <summary>The path of Caddis.Revit.dll for the runtime of this process.</summary>
        static string CaddisPath()
        {
            string loaderFolder = Path.GetDirectoryName(typeof(CaddisLoader).Assembly.Location)!;
            string framework = Environment.Version.Major >= 10 ? "net10.0" : "net8.0";
            return Path.Combine(Path.GetDirectoryName(loaderFolder)!, framework, "Caddis.Revit.dll");
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
