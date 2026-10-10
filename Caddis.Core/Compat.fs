namespace Caddis

#if NETFRAMEWORK
open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography

/// The members of .NET Core that Caddis uses, for .NET Framework.
[<AutoOpen>]
module Compat =

    type String with
        member s.Contains(value: string, comparison: StringComparison) = s.IndexOf(value, comparison) >= 0

    type Environment with
        static member ProcessId = Process.GetCurrentProcess().Id

    type Convert with
        static member ToHexString(bytes: byte[]) = BitConverter.ToString(bytes).Replace("-", "")

    type RandomNumberGenerator with
        static member GetBytes(count: int) =
            let bytes = Array.zeroCreate<byte> count
            use rng = RandomNumberGenerator.Create()
            rng.GetBytes bytes
            bytes

    type Path with
        static member GetRelativePath(relativeTo: string, path: string) =
            let dir = Path.GetFullPath relativeTo
            let dir = if dir.EndsWith(string Path.DirectorySeparatorChar) then dir else dir + string Path.DirectorySeparatorChar
            let relative = Uri(dir).MakeRelativeUri(Uri(Path.GetFullPath path))
            Uri.UnescapeDataString(relative.ToString()).Replace('/', Path.DirectorySeparatorChar)

    type File with
        static member Move(source: string, destination: string, overwrite: bool) =
            if overwrite && File.Exists destination then File.Replace(source, destination, null)
            else File.Move(source, destination)

    type CryptographicOperations =
        static member FixedTimeEquals(left: ReadOnlySpan<byte>, right: ReadOnlySpan<byte>) =
            let mutable diff = left.Length ^^^ right.Length
            for i in 0 .. min left.Length right.Length - 1 do
                diff <- diff ||| (int left[i] ^^^ int right[i])
            diff = 0


/// Settings for F# Interactive on .NET Framework.
module NetFramework =

    /// .NET Framework shows line numbers from portable PDBs, like the ones of --multiemit+, only in apps that target 4.7.2 or later.
    /// Rhino.exe doesn't say which version it targets, so it gets the old behavior. This turns it on for the whole process.
    /// The value is cached on first use, so the cache is reset too.
    let showPortablePdbLineNumbers () =
        AppContext.SetSwitch("Switch.System.Diagnostics.IgnorePortablePDBsInStackTraces", false)
        match typeof<obj>.Assembly.GetType "System.AppContextSwitches" with
        | null -> ()
        | t ->
            match t.GetField("_ignorePortablePDBsInStackTraces", Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Static) with
            | null -> ()
            | f -> f.SetValue(null, 0)

#nowarn "44" // LegacyReferenceResolver is obsolete, but the only way to set where FCS looks for the .NET Framework

    /// To type-check code FCS looks for System.dll and the other default references of a script in the reference assemblies of the
    /// .NET Framework SDK, e.g. C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8, that most PCs don't have.
    /// This makes it use the folder of the runtime instead, e.g. C:\Windows\Microsoft.NET\Framework64\v4.0.30319, like evaluation does.
    /// FSI resolves with simple resolution, in the folders only, so Resolve is not used.
    let referenceResolver =
        let runtimeDir = Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('\\', '/')
        FSharp.Compiler.CodeAnalysis.LegacyReferenceResolver(
            { new FSharp.Compiler.CodeAnalysis.ILegacyReferenceResolver with
                member _.HighestInstalledNetFrameworkVersion() = Path.GetFileName runtimeDir
                member _.DotNetFrameworkReferenceAssembliesRootDirectory = Path.GetDirectoryName runtimeDir
                member _.Resolve(_, _, _, _, _, _, _, _, _, _) = [||]
            }
        )
#endif
