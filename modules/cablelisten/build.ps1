# Build dist/<id>-<version>.zip for this module (contract 2.1 and 10.2).
# The zip holds one top folder <id>\ with the payload: module.json, README.md, CHANGELOG.md,
# launch.cmd, icon.ico (if any), src\ and the repo LICENSE. tests, build.ps1 and __pycache__ never ship.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-OutDir <folder>]
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$ModuleDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ModuleDir '..\..')).Path
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }
$mod = Get-Content (Join-Path $ModuleDir 'module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$id = $mod.id; $ver = $mod.version
$zipPath = Join-Path $OutDir "$id-$ver.zip"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$files = @()   # @{ Src = <full path>; Rel = <path inside the top folder> }
foreach ($name in 'module.json', 'README.md', 'CHANGELOG.md', 'launch.cmd') {
  $files += @{ Src = (Join-Path $ModuleDir $name); Rel = $name }
}
if ($mod.icon) { $files += @{ Src = (Join-Path $ModuleDir $mod.icon); Rel = ($mod.icon -replace '\\', '/') } }
$srcRoot = Join-Path $ModuleDir 'src'
Get-ChildItem $srcRoot -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' -and $_.Extension -ne '.pyc' } | ForEach-Object {
  $files += @{ Src = $_.FullName; Rel = 'src/' + $_.FullName.Substring($srcRoot.Length + 1).Replace('\', '/') }
}
$lic = Join-Path $RepoRoot 'LICENSE'
if (Test-Path $lic) { $files += @{ Src = $lic; Rel = 'LICENSE' } }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [IO.File]::Open($zipPath, 'Create')
$zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($f in $files) {
    if (-not (Test-Path $f.Src)) { throw "Missing payload file: $($f.Src)" }
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.Src, "$id/$($f.Rel)", [IO.Compression.CompressionLevel]::Optimal)
  }
} finally { $zip.Dispose(); $fs.Dispose() }
Write-Host "Built $zipPath ($($files.Count) files)"
