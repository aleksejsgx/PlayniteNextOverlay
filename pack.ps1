# Builds nothing. Packs an already-built output folder into a .pext.
# A .pext is a zip archive with extension.yaml at the archive root.
param(
    [string]$BuildDir = "",
    [string]$Destination = ""
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $BuildDir) { $BuildDir = Join-Path $repo "bin\Release" }

$yamlPath = Join-Path $BuildDir "extension.yaml"
if (-not (Test-Path -LiteralPath $yamlPath)) { throw "extension.yaml was not found in $BuildDir" }

$version = "1.0.0"
foreach ($line in Get-Content -LiteralPath $yamlPath) {
    if ($line -match '^\s*Version:\s*(.+)\s*$') { $version = $Matches[1].Trim(); break }
}

if (-not $Destination) {
    $outDir = Join-Path $repo "release"
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    $Destination = Join-Path $outDir ("PlayniteNextOverlay_" + $version + ".pext")
} else {
    $parent = Split-Path -Parent $Destination
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("pno-pack-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Get-ChildItem -LiteralPath $BuildDir -File | Where-Object {
        $_.Extension -ne ".pdb" -and $_.Extension -ne ".xml" -and $_.Name -ne "Playnite.SDK.dll"
    } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage $_.Name)
    }
    $loc = Join-Path $BuildDir "Localization"
    if (Test-Path -LiteralPath $loc) {
        Copy-Item -LiteralPath $loc -Destination (Join-Path $stage "Localization") -Recurse
    }
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $Destination)
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
Write-Output $Destination
