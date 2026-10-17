<#
.SYNOPSIS
Generates the logo assets used by this repository from logo.png.

.DESCRIPTION
Checks that ImageMagick is installed through Winget (and installs it when absent),
then creates a 128px PNG, a multi-resolution favicon, and a centered repository card.
ImageMagick replaces existing output files.
#>

[CmdletBinding()]
param()

# Stop immediately when a command or validation step fails.
$ErrorActionPreference = 'Stop'

# Always operate beside this script so it can be run from any working directory.
$packageId = 'ImageMagick.ImageMagick'
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $scriptDirectory

# Return true only when the exact Winget package is installed.
function Test-WingetPackageInstalled {
    $wingetOutput = & winget list --id $packageId --exact --accept-source-agreements 2>&1

    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    return (($wingetOutput | Out-String) -match [regex]::Escape($packageId))
}

# Run ImageMagick and turn a non-zero exit code into a useful PowerShell error.
function Invoke-Magick {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $magickExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ImageMagick failed while generating $($Arguments[-1])."
    }
}

# Winget is used to validate the ImageMagick dependency and install it when necessary.
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'Winget is required to validate or install ImageMagick, but it was not found.'
}

if (-not (Test-WingetPackageInstalled)) {
    Write-Host "Installing $packageId..."
    & winget install --id $packageId --exact --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "Winget could not install $packageId."
    }

    if (-not (Test-WingetPackageInstalled)) {
        throw "$packageId was not detected after installation."
    }
}

# ImageMagick's Winget package normally exposes magick.exe on PATH.
# Fall back to its Program Files installation folder when this session has an outdated PATH.
$magickCommand = Get-Command magick -ErrorAction SilentlyContinue
if ($magickCommand) {
    $magickExecutable = $magickCommand.Source
}
else {
    $installedMagick = Get-ChildItem -Path (Join-Path $env:ProgramFiles 'ImageMagick-*\magick.exe') -File -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $installedMagick) {
        throw 'ImageMagick is installed but magick.exe was not found on PATH or in Program Files.'
    }

    $magickExecutable = $installedMagick.FullName
    Write-Host "Using ImageMagick at $magickExecutable."
}

# Validate the source asset before attempting to create any outputs.
$logoPath = Join-Path $scriptDirectory 'logo.png'
if (-not (Test-Path -LiteralPath $logoPath -PathType Leaf)) {
    throw "logo.png was not found in $scriptDirectory."
}

$logo128Path = Join-Path $scriptDirectory 'logo128.png'
$faviconPath = Join-Path $scriptDirectory 'favicon.ico'
$repoCardPath = Join-Path $scriptDirectory 'repoCard.png'

Write-Host 'Processing logo.png...'

# Create a 128 by 128 PNG logo.
Write-Host 'Generating logo128.png...'
Invoke-Magick -Arguments @($logoPath, '-resize', '128x128', $logo128Path)

# Create an ICO containing 256, 64, 48, 32, and 16 pixel variants.
Write-Host 'Generating favicon.ico...'
Invoke-Magick -Arguments @($logoPath, '-define', 'icon:auto-resize=256,64,48,32,16', $faviconPath)

# Create a transparent 1280 by 640 repository card with a centered 640px logo.
Write-Host 'Generating repoCard.png...'
Invoke-Magick -Arguments @($logoPath, '-resize', '640x640', '-background', 'none', '-gravity', 'center', '-extent', '1280x640', $repoCardPath)

Write-Host 'Done! All images generated successfully.'
