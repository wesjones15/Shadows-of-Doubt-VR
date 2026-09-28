#Requires -Version 5.1
<#
.SYNOPSIS
    Build SoDVR and package it for Nexus Mods / GitHub Releases.

.PARAMETER Version
    Release version string, e.g. "1.0.0". Used in the output filename.

.EXAMPLE
    .\scripts\pack.ps1 -Version 1.0.0
#>
param(
    [Parameter(Mandatory)]
    [string]$Version,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$Root      = Resolve-Path "$PSScriptRoot\.."
$OutputDir = "$PSScriptRoot"
$TmpDir    = "$OutputDir\_tmp_$Version"
$ZipOut    = "$OutputDir\SoDVR-$Version.zip"

function Step([string]$msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# ── 1. Build ──────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Step "Building SoDVR (Release)"
    dotnet build "$Root\SoDVR\SoDVR.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "SoDVR build failed" }

    Step "Building SoDVR.Preload (Release)"
    dotnet build "$Root\SoDVR.Preload\SoDVR.Preload.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "SoDVR.Preload build failed" }
} else {
    Write-Host "`nSkipping build - using existing DLLs from bin/Release." -ForegroundColor Yellow
}

# ── 2. Locate build outputs ───────────────────────────────────────────────────
$SoDVRDll   = "$Root\SoDVR\bin\Release\net6.0\SoDVR.dll"
$PreloadDll = "$Root\SoDVR.Preload\bin\Release\net6.0\SoDVR.Preload.dll"
$LoaderDll  = "$Root\RuntimeDeps\Native\openxr_loader.dll"
$ConfigFile = "$Root\SoDVR\com.sodvr.mod.cfg"

foreach ($f in @($SoDVRDll, $PreloadDll, $LoaderDll, $ConfigFile)) {
    if (-not (Test-Path $f)) { throw "Required file not found: $f" }
}

# ── 3. Assemble staging tree ──────────────────────────────────────────────────
Step "Assembling package"

if (Test-Path $TmpDir) { Remove-Item $TmpDir -Recurse -Force }
New-Item -ItemType Directory -Path "$TmpDir\BepInEx\plugins" -Force | Out-Null
New-Item -ItemType Directory -Path "$TmpDir\BepInEx\config" -Force | Out-Null
New-Item -ItemType Directory -Path "$TmpDir\BepInEx\patchers\SoDVR\RuntimeDeps\Native" -Force | Out-Null

Copy-Item $SoDVRDll   "$TmpDir\BepInEx\plugins\SoDVR.dll"
Copy-Item $PreloadDll "$TmpDir\BepInEx\patchers\SoDVR\SoDVR.Preload.dll"
Copy-Item $LoaderDll  "$TmpDir\BepInEx\patchers\SoDVR\RuntimeDeps\Native\openxr_loader.dll"
Copy-Item $ConfigFile "$TmpDir\BepInEx\config\com.sodvr.mod.cfg"
Copy-Item "$Root\README.md" "$TmpDir\README.md"
Copy-Item "$Root\CHANGELOG.md" "$TmpDir\CHANGELOG.md"
Copy-Item "$Root\LICENSE" "$TmpDir\LICENSE"
Copy-Item "$Root\licenses" "$TmpDir\licenses" -Recurse

# ── 4. Zip ────────────────────────────────────────────────────────────────────
if (Test-Path $ZipOut) { Remove-Item $ZipOut -Force }
# Not Compress-Archive: on PowerShell 5.1 it writes entry paths with backslashes, which other
# extractors and mod managers take as part of the file name instead of folders.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($ZipOut, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $TmpDir -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($TmpDir.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry) | Out-Null
    }
} finally { $zip.Dispose() }

Remove-Item $TmpDir -Recurse -Force

Step "Done"
Write-Host ""
Write-Host "Package: $ZipOut" -ForegroundColor Green
Write-Host ""
Write-Host "Upload to Nexus Mods: https://www.nexusmods.com/shadowsofdoubt"
Write-Host "Create GitHub release:"
Write-Host "  gh release create v$Version $ZipOut $SoDVRDll $ConfigFile --title 'SoDVR v$Version'"
