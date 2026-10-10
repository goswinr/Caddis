<#
.SYNOPSIS
Registers a local build of Caddis.Revit with Revit, for development.

.DESCRIPTION
Writes Caddis.addin to %AppData%\Autodesk\Revit\Addins\<Year>, pointing at the build output.
Revit loads the dll from there at its next start, and locks it while it runs: close Revit before building again.
On the first start Revit asks whether to load the unsigned add-in, choose 'Always Load'.

.EXAMPLE
dotnet build Caddis.Revit -c Release
./Caddis.Revit/install-dev.ps1 -Configuration Release

.EXAMPLE
./Caddis.Revit/install-dev.ps1 -Uninstall
#>
param(
    [string]$Year = "2026",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
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

$dll = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows\Caddis.Revit.dll"
if (-not (Test-Path $dll)) {
    throw "$dll not found. Build it first: dotnet build Caddis.Revit -c $Configuration"
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
