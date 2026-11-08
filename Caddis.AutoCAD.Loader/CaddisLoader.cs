using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(Caddis.AutoCAD.Loader.CaddisLoader))]

namespace Caddis.AutoCAD.Loader
{
    /// <summary>
    /// The entry point in AutoCAD 2025 and later, loaded by the bundle Caddis.bundle that the installer writes. Don't rename it.
    /// Loads Caddis.AutoCAD.dll from the folder net8.0 or net10.0 of the installation, for the .NET runtime that AutoCAD runs on,
    /// or in the folder named by CADDIS_AUTOCAD_DEV. AutoCAD then registers the commands of Caddis.AutoCAD and calls its Initialize,
    /// as for every assembly loaded into its process that declares them. The loader is only needed at start.
    /// </summary>
    public class CaddisLoader : IExtensionApplication
    {
        /// <summary>Set by Dev/Start-Dev.ps1 for the AutoCAD it starts: a folder with a local build of Caddis.AutoCAD in net8.0 or net10.0,
        /// loaded instead of the installed one.</summary>
        const string DevFolderVariable = "CADDIS_AUTOCAD_DEV";

        /// <summary>The folder with the builds: the one in CADDIS_AUTOCAD_DEV, else the one that the installer wrote into install-folder.txt
        /// next to the copy of the loader in the bundle, else the parent folder of the loader's folder, as in the installation and in Start-Dev.</summary>
        static string BuildsFolder()
        {
            string? devFolder = Environment.GetEnvironmentVariable(DevFolderVariable);
            if (!string.IsNullOrEmpty(devFolder))
                return devFolder;
            string loaderFolder = Path.GetDirectoryName(typeof(CaddisLoader).Assembly.Location)!;
            string installFolderFile = Path.Combine(loaderFolder, "install-folder.txt");
            if (File.Exists(installFolderFile))
                return File.ReadAllText(installFolderFile).Trim();
            return Path.GetDirectoryName(loaderFolder)!;
        }

        /// <summary>The path of Caddis.AutoCAD.dll for the runtime of this process.</summary>
        static string CaddisPath()
        {
            string framework = Environment.Version.Major >= 10 ? "net10.0" : "net8.0";
            return Path.Combine(BuildsFolder(), framework, "Caddis.AutoCAD.dll");
        }

        /// <summary>Writes to %AppData%\Caddis\autocad.log, the log of Caddis.AutoCAD. AutoCAD has no drawing open yet when it loads plugins at its start.</summary>
        static void Log(string message)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Caddis");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "autocad.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ERROR Caddis.AutoCAD.Loader: {message}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>Loads Caddis.AutoCAD, unless a Caddis.AutoCAD is loaded already, e.g. by a second loader or NETLOAD.</summary>
        public void Initialize()
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Caddis.AutoCAD"))
                return;
            string path = CaddisPath();
            try
            {
                // its dependencies, e.g. FSharp.Core, are loaded from its folder
                Assembly.LoadFrom(path);
            }
            catch (System.Exception e)
            {
                string message = $"Caddis for AutoCAD failed to load {path}:{Environment.NewLine}{e.Message}";
                Log(message);
                try { Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowAlertDialog(message); } catch { }
            }
        }

        /// <summary>Caddis.AutoCAD shuts itself down in its own Terminate.</summary>
        public void Terminate() { }
    }
}
