<#
.SYNOPSIS
Registers a local build of Caddis.Revit with Revit, for development.

.DESCRIPTION
Writes Caddis.addin to %AppData%\Autodesk\Revit\Addins\<Year>, pointing at the build output for the runtime of that Revit:
net48 for Revit 2024, net10.0-windows for a Revit on .NET 10, e.g. 2025.5, 2026.5 or 2027, otherwise net8.0-windows.
The installer writes the same file: this replaces its registration, until its Caddis.Revit.Bootstrapper.exe runs again.
Revit loads the dll from there at its next start, and locks it while it runs: close Revit before building again.
On the first start Revit asks whether to load the unsigned add-in, choose 'Always Load'.

.EXAMPLE
dotnet build Caddis.Revit -c Release
./Caddis.Revit/install-dev.ps1 -Year 2026 -Configuration Release

.EXAMPLE
./Caddis.Revit/install-dev.ps1 -Year 2026 -Uninstall
#>
param(
    [string]$Year = "2026",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    # The build to register. By default the one for the runtime of the Revit in RevitFolder.
    [ValidateSet("", "net48", "net8.0-windows", "net10.0-windows")]
    [string]$Framework = "",
    # The folder of Revit.exe, to find out which .NET it runs on.
    [string]$RevitFolder = "C:\Program Files\Autodesk\Revit $Year",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$addinFile = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year\Caddis.addin"

if ($Uninstall) {
    if (Test-Path $addinFile) {
        Remove-Item $addinFile
        Write-Host "Removed $addinFile"
    } else {
        Write-Host "Caddis is not registered for Revit $Year, there is no $addinFile"
    }
    return
}

if (-not $Framework) {
    if ([int]$Year -lt 2025) {
        $Framework = "net48"
    } else {
        # Revit names the .NET it runs on in its runtimeconfig files, e.g. "tfm": "net10.0" from Revit 2025.5 and 2026.5 on.
        $config = Join-Path $RevitFolder "RevitAPI.runtimeconfig.json"
        if (-not (Test-Path $config)) {
            throw "$config not found. Is Revit $Year installed there? Pass -RevitFolder, or the build with -Framework."
        }
        $tfm = (Get-Content $config -Raw | ConvertFrom-Json).runtimeOptions.tfm
        $Framework = if ([int]($tfm -replace '^net(\d+).*$', '$1') -ge 10) { "net10.0-windows" } else { "net8.0-windows" }
        Write-Host "Revit $Year runs on $tfm."
    }
}

$dll = Join-Path $PSScriptRoot "bin\$Configuration\$Framework\Caddis.Revit.dll"
if (-not (Test-Path $dll)) {
    throw "$dll not found. Build it first: dotnet build Caddis.Revit -c $Configuration"
}

if ((Test-Path $addinFile) -and -not (Get-Content $addinFile -Raw).Contains("<Assembly>$dll</Assembly>")) {
    Write-Host "Replacing $addinFile, it registered:"
    Write-Host ([xml](Get-Content $addinFile -Raw)).RevitAddIns.AddIn.Assembly
}

# Revit identifies the add-in by AddInId, don't change it.
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Caddis</Name>
    <Assembly>$dll</Assembly>
    <FullClassName>Caddis.Revit.CaddisApplication</FullClassName>
    <AddInId>4D0EC386-3A78-491A-95FA-9B5A65CAA89C</AddInId>
    <VendorId>goswinr</VendorId>
    <VendorDescription>Goswin Rothenthal, https://github.com/goswinr/Caddis</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

New-Item -ItemType Directory -Force (Split-Path $addinFile) | Out-Null
Set-Content -Path $addinFile -Value $xml -Encoding UTF8
Write-Host "Wrote $addinFile"
Write-Host "It loads $dll at the next start of Revit $Year."
