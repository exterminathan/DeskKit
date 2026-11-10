# Builds dist\regionshot-<version>.zip at the repo root (docs/module-contract.md 10.2).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File modules/regionshot/build.ps1
# Prints the zip path on the last line.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$module = $PSScriptRoot
$repo = Split-Path (Split-Path $module -Parent) -Parent
$info = Get-Content (Join-Path $module 'module.json') -Raw | ConvertFrom-Json
$id = $info.id
$version = $info.version

$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "$id-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# Payload: module.json README.md CHANGELOG.md launch.cmd icon.ico src\ and LICENSE.
$files = @()
foreach ($name in 'module.json', 'README.md', 'CHANGELOG.md', 'launch.cmd', 'icon.ico') {
    $files += [pscustomobject]@{ Path = Join-Path $module $name; Entry = "$id/$name" }
}
$license = Join-Path $repo 'LICENSE'
if (Test-Path $license) { $files += [pscustomobject]@{ Path = $license; Entry = "$id/LICENSE" } }
$src = Join-Path $module 'src'
Get-ChildItem $src -Recurse -File | Where-Object {
    $_.FullName -notmatch '\\__pycache__\\' -and $_.Extension -notin '.pyc', '.log'
} | ForEach-Object {
    $rel = $_.FullName.Substring($module.Length + 1).Replace('\', '/')
    $files += [pscustomobject]@{ Path = $_.FullName; Entry = "$id/$rel" }
}
foreach ($f in $files) { if (-not (Test-Path $f.Path)) { throw "Missing payload file: $($f.Path)" } }

$stream = [System.IO.File]::Open($zip, 'Create')
try {
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    foreach ($f in $files) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $f.Path, $f.Entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
    $archive.Dispose()
} finally { $stream.Dispose() }

Write-Output "Built $($files.Count) files."
Write-Output $zip
