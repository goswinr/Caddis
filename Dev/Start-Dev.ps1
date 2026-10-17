<#
.SYNOPSIS
Starts Rhino or Revit with the local build of Caddis instead of the installed one.

.DESCRIPTION
Builds Caddis for the app, copies the build to %LocalAppData%\Caddis.Dev\<app> and starts the app with only that build.
The app locks the copy, not the build output, so you can build again while it runs. Start it with this script again to load the new build.
The installed Caddis stays installed: Rhino or Revit started in any other way loads it as before.

Rhino loads the local build from RHINO_PACKAGE_DIRS, set only for the Rhino started here.
Rhino would load the installed Yak package instead, it has the same plugin id. So its folder %AppData%\McNeel\Rhinoceros\packages\<version>\Caddis
is moved to %AppData%\Caddis\dev\hidden until Rhino has loaded the local build, then moved back.
The path of the plugin that Rhino saved in the registry is put back too, so that the next normal start loads the installed Caddis.
Don't hide the package by deleting its manifest.txt instead: Rhino then deletes the package.

In Revit 2025 and later, if the installed loader reads CADDIS_REVIT_DEV, that is set for the Revit started here.
Otherwise, e.g. in Revit 2024, Caddis.addin is replaced by one for the local build until Revit has loaded it, then put back.
Revit then asks whether to load the unsigned add-in.

If the script is stopped before it put the installed Caddis back, its next run does it.

.EXAMPLE
./Dev/Start-Dev.ps1
Asks which app to start.

.EXAMPLE
./Dev/Start-Dev.ps1 Rhino8 /scheme=CaddisDev
Builds and starts Rhino 8 on .NET 8, with its settings scheme CaddisDev. Arguments after the app are passed on to it.

.EXAMPLE
./Dev/Start-Dev.ps1 Revit2026 -Configuration Release -NoBuild
#>
param(
    # Rhino8, Rhino8NetFx for Rhino 8 on .NET Framework, Rhino9, or Revit and its year, e.g. Revit2026. Without it the script asks.
    [Parameter(Position = 0)]
    [string]$App = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    # Starts the app with the last build, without building.
    [switch]$NoBuild,
    # How long to wait for the app to load Caddis, before the installed Caddis is put back anyway.
    [int]$TimeoutSeconds = 300,
    # Passed on to the app.
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs = @()
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$stageRoot = Join-Path $env:LOCALAPPDATA "Caddis.Dev"
# On the same drive as Rhino's packages and Revit's add-ins, so that moving them there is a rename.
$hiddenRoot = Join-Path $env:APPDATA "Caddis\dev\hidden"
$revitDevVariable = "CADDIS_REVIT_DEV" # see Caddis.Revit.Loader/CaddisLoader.cs

# True if another process has the file open, e.g. Rhino or Revit has loaded the assembly.
function Test-Locked([string]$file) {
    if (-not (Test-Path -LiteralPath $file)) { return $false }
    try {
        [IO.File]::Open($file, "Open", "ReadWrite", "None").Dispose()
        return $false
    } catch [IO.IOException] {
        return $true
    }
}

# Deletes the folder of an app in the hidden folder, and the hidden folder, if they are empty.
function Remove-EmptyHidden([string]$folder) {
    foreach ($f in $folder, $hiddenRoot, (Split-Path $hiddenRoot)) {
        try { [IO.Directory]::Delete($f) } catch { return }
    }
}

function Get-RhinoPackage([int]$major) { Join-Path $env:APPDATA "McNeel\Rhinoceros\packages\$major.0\Caddis" }
function Get-HiddenRhino([int]$major) { Join-Path $hiddenRoot "Rhino $major" }
function Get-RevitAddin([int]$year) { Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Caddis.addin" }
function Get-HiddenRevitAddin([int]$year) { Join-Path $hiddenRoot "Revit $year" }

# Rhino saves here the path of the .rhp it registered, by the plugin id in Caddis.Rhino/AssemblyInfo.fs.
# A normal start after a start with the local build would find the installed package at another path than saved, and then load no Caddis,
# only its next start does. So the saved path is put back once Rhino has loaded the local build. Rhino doesn't save it again when it closes.
function Get-RhinoPlugInKey([int]$major) { "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-Ins\5507c750-f992-4009-b079-263a062279fb\PlugIn" }

# Moves the installed Yak package out of Rhino's package folder. Returns false if Caddis isn't installed in this Rhino.
function Hide-RhinoPackage([int]$major) {
    $package = Get-RhinoPackage $major
    if (-not (Test-Path -LiteralPath $package)) { return $false }
    $hidden = Get-HiddenRhino $major
    New-Item -ItemType Directory -Force $hidden | Out-Null
    $fileName = (Get-ItemProperty (Get-RhinoPlugInKey $major) -ErrorAction SilentlyContinue).FileName
    if ($fileName) { Set-Content -LiteralPath (Join-Path $hidden "PlugInFileName.txt") -Value $fileName }
    try {
        [IO.Directory]::Move($package, (Join-Path $hidden "Caddis"))
    } catch {
        throw "Can't move the installed Caddis aside, is Rhino $major running? Close it first. $package : $($_.Exception.Message)"
    }
    Write-Host "Moved the installed Caddis aside until Rhino $major has loaded the local build: $package"
    return $true
}

function Restore-RhinoPackage([int]$major) {
    $hidden = Get-HiddenRhino $major
    if (-not (Test-Path -LiteralPath $hidden)) { return }
    $hiddenPackage = Join-Path $hidden "Caddis"
    $package = Get-RhinoPackage $major
    if (Test-Path -LiteralPath $hiddenPackage) {
        if (Test-Path -LiteralPath $package) {
            Write-Warning "Caddis was installed in Rhino $major again meanwhile. The package moved aside before is still in $hiddenPackage, delete it."
        } else {
            [IO.Directory]::Move($hiddenPackage, $package)
            Write-Host "Put the installed Caddis back: $package"
        }
    }
    $savedFileName = Join-Path $hidden "PlugInFileName.txt"
    if (Test-Path -LiteralPath $savedFileName) {
        $key = Get-RhinoPlugInKey $major
        if (Test-Path -LiteralPath $key) { Set-ItemProperty -LiteralPath $key -Name FileName -Value (Get-Content -LiteralPath $savedFileName -Raw).Trim() }
        Remove-Item -LiteralPath $savedFileName
    }
    Remove-EmptyHidden $hidden
}

# Replaces Caddis.addin with one for the local build. The installed one is kept in the hidden folder, which also marks that there was none.
function Set-DevAddin($target) {
    $addin = Get-RevitAddin $target.Year
    $hidden = Get-HiddenRevitAddin $target.Year
    New-Item -ItemType Directory -Force $hidden | Out-Null
    if (Test-Path -LiteralPath $addin) {
        Move-Item -LiteralPath $addin -Destination (Join-Path $hidden "Caddis.addin")
    } else {
        New-Item -ItemType Directory -Force (Split-Path $addin) | Out-Null
    }
    # As written by Caddis.Revit.Bootstrapper and install-dev.ps1. Revit identifies the add-in by AddInId, don't change it.
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Caddis</Name>
    <Assembly>$([Security.SecurityElement]::Escape($target.Loaded))</Assembly>
    <FullClassName>Caddis.Revit.CaddisApplication</FullClassName>
    <AddInId>4D0EC386-3A78-491A-95FA-9B5A65CAA89C</AddInId>
    <VendorId>goswinr</VendorId>
    <VendorDescription>Goswin Rothenthal, https://github.com/goswinr/Caddis</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    [IO.File]::WriteAllText($addin, $xml, [Text.Encoding]::UTF8)
    Write-Host "Registered the local build in Revit $($target.Year) until it has loaded it: $addin"
}

function Restore-RevitAddin([int]$year) {
    $hidden = Get-HiddenRevitAddin $year
    if (-not (Test-Path -LiteralPath $hidden)) { return }
    $addin = Get-RevitAddin $year
    $backup = Join-Path $hidden "Caddis.addin"
    $exists = Test-Path -LiteralPath $addin
    $isDev = $exists -and (Get-Content -LiteralPath $addin -Raw).Contains([Security.SecurityElement]::Escape($stageRoot))
    if ($exists -and -not $isDev) {
        Write-Host "Caddis.addin of Revit $year was written again meanwhile, e.g. by the installer, it stays: $addin"
    } elseif (Test-Path -LiteralPath $backup) {
        Move-Item -LiteralPath $backup -Destination $addin -Force
        Write-Host "Put the installed Caddis back in Revit ${year}: $addin"
    } elseif ($isDev) {
        Remove-Item -LiteralPath $addin
        Write-Host "Removed the local build from Revit ${year}, Caddis wasn't registered there before: $addin"
    }
    Get-ChildItem -LiteralPath $hidden | Remove-Item -Force
    Remove-EmptyHidden $hidden
}

# Puts back what an earlier run moved aside, if it was stopped before it could.
function Restore-Installed {
    if (-not (Test-Path -LiteralPath $hiddenRoot)) { return }
    foreach ($dir in Get-ChildItem -LiteralPath $hiddenRoot -Directory) {
        if ($dir.Name -match '^Rhino (\d+)$') { Restore-RhinoPackage ([int]$Matches[1]) }
        elseif ($dir.Name -match '^Revit (\d+)$') { Restore-RevitAddin ([int]$Matches[1]) }
    }
}

# True if Caddis.addin registers an installed loader that reads CADDIS_REVIT_DEV.
function Test-LoaderReadsDevFolder([string]$addin) {
    if (-not (Test-Path -LiteralPath $addin)) { return $false }
    $assembly = ([xml](Get-Content -LiteralPath $addin -Raw)).RevitAddIns.AddIn.Assembly
    if (-not $assembly -or -not $assembly.EndsWith("Caddis.Revit.Loader.dll") -or -not (Test-Path -LiteralPath $assembly)) { return $false }
    # string literals are stored in UTF-16 in a dll, at an even or an odd offset
    $bytes = [IO.File]::ReadAllBytes($assembly)
    $utf16 = [Text.Encoding]::Unicode
    return $utf16.GetString($bytes).Contains($revitDevVariable) -or $utf16.GetString($bytes, 1, $bytes.Length - 1).Contains($revitDevVariable)
}

function New-RhinoTarget([string]$name, [int]$major, [string]$exe, [string]$tfm, [string]$runtime, [string[]]$arguments) {
    $stage = Join-Path $stageRoot $name
    [pscustomobject]@{
        Name = $name; Kind = "Rhino"; Label = "Rhino $major on $runtime"; Major = $major; Exe = $exe; Arguments = $arguments
        Project = "Caddis.Rhino"; Tfm = $tfm
        StageBuild = $stage # Rhino uses the .rhp at the top of a folder in RHINO_PACKAGE_DIRS
        Loaded = Join-Path $stage "Caddis.Rhino.rhp"
    }
}

function New-RevitTarget([int]$year) {
    $folder = Join-Path $env:ProgramFiles "Autodesk\Revit $year"
    if ($year -lt 2025) {
        $framework = "net48"; $runtime = ".NET Framework"
    } else {
        # e.g. "tfm": "net10.0" from Revit 2025.5 and 2026.5 on, see install-dev.ps1
        $config = Join-Path $folder "RevitAPI.runtimeconfig.json"
        if (-not (Test-Path -LiteralPath $config)) { return }
        $tfm = (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).runtimeOptions.tfm
        $major = [int]($tfm -replace '^net(\d+).*$', '$1')
        $framework = if ($major -ge 10) { "net10.0" } else { "net8.0" }
        $runtime = ".NET $major"
    }
    $stage = Join-Path $stageRoot "Revit$year"
    # the folder names of the installer, which the loader expects, see Caddis.Revit.Loader/README.md
    $stageBuild = Join-Path $stage $framework
    [pscustomobject]@{
        Name = "Revit$year"; Kind = "Revit"; Label = "Revit $year on $runtime"; Year = $year
        Exe = Join-Path $folder "Revit.exe"; Arguments = @()
        Project = "Caddis.Revit"; Tfm = $(if ($framework -eq "net48") { "net48" } else { "$framework-windows" })
        Stage = $stage; StageBuild = $stageBuild
        Loaded = Join-Path $stageBuild "Caddis.Revit.dll"
    }
}

# The installed Rhinos and Revits that Caddis supports.
function Get-Targets {
    foreach ($major in 8, 9) {
        $exe = (Get-ItemProperty "HKLM:\SOFTWARE\McNeel\Rhinoceros\$major.0\Install" -ErrorAction SilentlyContinue).ExePath
        if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { continue }
        if ($major -eq 8) {
            New-RhinoTarget "Rhino8" 8 $exe "net8.0" ".NET 8" @("/netcore")
            New-RhinoTarget "Rhino8NetFx" 8 $exe "net48" ".NET Framework" @("/netfx")
        } else {
            New-RhinoTarget "Rhino9" 9 $exe "net10.0" ".NET 10" @()
        }
    }
    Get-ChildItem (Join-Path $env:ProgramFiles "Autodesk") -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^Revit (\d{4})$' -and [int]$Matches[1] -ge 2024 -and (Test-Path (Join-Path $_.FullName "Revit.exe")) } |
        ForEach-Object { New-RevitTarget ([int]($_.Name -replace '^Revit ', '')) }
}

function Select-Target {
    $targets = @(Get-Targets)
    if ($targets.Count -eq 0) { throw "Found no Rhino 8 or 9 and no Revit from 2024 on." }
    if ($App) {
        $target = $targets | Where-Object Name -eq $App
        if (-not $target) { throw "Unknown app '$App'. Installed are: $($targets.Name -join ', ')" }
        return $target
    }
    Write-Host "Start which app with the local build of Caddis?"
    for ($i = 0; $i -lt $targets.Count; $i++) {
        Write-Host ("  {0}  {1,-12} {2}" -f ($i + 1), $targets[$i].Name, $targets[$i].Label)
    }
    $choice = 0
    if (-not [int]::TryParse((Read-Host "Number"), [ref]$choice) -or $choice -lt 1 -or $choice -gt $targets.Count) { throw "No app chosen." }
    return $targets[$choice - 1]
}

# Copies the build output, deleting files that are not in it anymore.
function Copy-Build($target) {
    $output = Join-Path $repo "$($target.Project)\bin\$Configuration\$($target.Tfm)"
    if (-not (Test-Path -LiteralPath $output)) { throw "$output not found, build it first: dotnet build $($target.Project) -c $Configuration -f $($target.Tfm)" }
    robocopy $output $target.StageBuild /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Host
    if ($LASTEXITCODE -ge 8) { throw "Copying $output to $($target.StageBuild) failed." }
    $global:LASTEXITCODE = 0
}

# Starts the app with these environment variables, only for its process.
function Start-App($target, [hashtable]$environment) {
    $saved = @{}
    foreach ($name in $environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $environment[$name])
    }
    try {
        $arguments = @(@($target.Arguments) + @($AppArgs) | Where-Object { $_ })
        if ($arguments.Count -gt 0) {
            Start-Process -FilePath $target.Exe -ArgumentList $arguments -PassThru
        } else {
            Start-Process -FilePath $target.Exe -PassThru
        }
    } finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}

# Waits until the app has loaded the local build, it then locks the file.
function Wait-ForLoad($process, $target) {
    Write-Host "Waiting for $($target.Label) to load the local build, at most $TimeoutSeconds s ..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Locked $target.Loaded) {
            Write-Host "$($target.Label) loaded Caddis from $($target.StageBuild)"
            return
        }
        if ($process.HasExited) {
            Write-Warning "$($target.Label) exited before it loaded Caddis."
            return
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Warning "$($target.Label) didn't load Caddis within $TimeoutSeconds s."
}

function Start-Rhino($target) {
    $hidden = Hide-RhinoPackage $target.Major
    try {
        $process = Start-App $target @{ RHINO_PACKAGE_DIRS = $target.StageBuild }
        Write-Host "Started $($target.Label), process $($process.Id)."
        if ($hidden) { Wait-ForLoad $process $target }
    } finally {
        if ($hidden) { Restore-RhinoPackage $target.Major }
    }
}

function Start-Revit($target) {
    if ($target.Year -ge 2025 -and (Test-LoaderReadsDevFolder (Get-RevitAddin $target.Year))) {
        $process = Start-App $target @{ $revitDevVariable = $target.Stage }
        Write-Host "Started $($target.Label), process $($process.Id). The installed loader loads Caddis from $($target.StageBuild)"
        return
    }
    Set-DevAddin $target
    try {
        $process = Start-App $target @{}
        Write-Host "Started $($target.Label), process $($process.Id). If it asks whether to load the unsigned add-in Caddis, choose 'Always Load' or 'Load Once'."
        Wait-ForLoad $process $target
    } finally {
        Restore-RevitAddin $target.Year
    }
}

# One run at a time, so that one doesn't put back what another has just moved aside.
$mutex = New-Object Threading.Mutex($false, "Local\Caddis.Start-Dev")
try {
    if (-not $mutex.WaitOne(0)) {
        Write-Host "Waiting for another run of Start-Dev to finish ..."
        [void]$mutex.WaitOne()
    }
} catch [Threading.AbandonedMutexException] {
    # an earlier run was stopped, Restore-Installed puts back what it moved aside
}
try {
    Restore-Installed
    $target = Select-Target
    if (Test-Locked $target.Loaded) { throw "A $($target.Label) still runs with the local build from $($target.StageBuild), close it first." }
    if (-not $NoBuild) {
        Write-Host "dotnet build $($target.Project) -c $Configuration -f $($target.Tfm)"
        dotnet build (Join-Path $repo $target.Project) -c $Configuration -f $target.Tfm -nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "The build failed." }
    }
    Copy-Build $target
    if ($target.Kind -eq "Rhino") { Start-Rhino $target } else { Start-Revit $target }
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
