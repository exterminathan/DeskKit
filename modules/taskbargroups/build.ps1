# Build the Taskbar Groups module (docs/module-contract.md 2.1 and 10.2).
# 1. dotnet publish: one self-contained win-x64 single-file TaskbarGroups.exe into bin\.
# 2. Zip dist\taskbargroups-<version>.zip with one top folder taskbargroups\ holding the payload:
#    module.json, README.md, CHANGELOG.md, bin\ and the repo LICENSE. src\, tests, build.ps1 and
#    test.ps1 never ship.
# Writes only into this module's bin\ (and src\...\obj, src\...\bin for intermediates) and the
# zip folder. It installs nothing and touches no running copy.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-OutDir <zip folder>] [-NoZip]
# Prints the zip path on the last line.
param([string]$OutDir, [switch]$NoZip)
$ErrorActionPreference = 'Stop'
$ModuleDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ModuleDir '..\..')).Path
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }
$mod = Get-Content (Join-Path $ModuleDir 'module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$id = $mod.id; $ver = $mod.version
$project = Join-Path $ModuleDir 'src\TaskbarGroups\TaskbarGroups.csproj'
$bin = Join-Path $ModuleDir 'bin'

# 1. publish into bin\ (cleaned first so no stale file ships)
if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
& dotnet publish $project -nologo -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -p:Version=$ver -o $bin
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
$exe = Join-Path $ModuleDir $mod.entry
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }
Write-Host ("Built {0} {1} into bin\ ({2:N1} MB exe)" -f $mod.name, $ver, ((Get-Item $exe).Length / 1MB))
if ($NoZip) { return }

# 2. zip
$zipPath = Join-Path $OutDir "$id-$ver.zip"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$files = @()   # @{ Src = <full path>; Rel = <path inside the top folder> }
foreach ($name in 'module.json', 'README.md', 'CHANGELOG.md') {
  $files += @{ Src = (Join-Path $ModuleDir $name); Rel = $name }
}
if ($mod.icon) { $files += @{ Src = (Join-Path $ModuleDir $mod.icon); Rel = ($mod.icon -replace '\\', '/') } }
Get-ChildItem $bin -Recurse -File | ForEach-Object {
  $files += @{ Src = $_.FullName; Rel = 'bin/' + $_.FullName.Substring($bin.Length + 1).Replace('\', '/') }
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
Write-Host "Zipped $($files.Count) files"
Write-Output $zipPath
