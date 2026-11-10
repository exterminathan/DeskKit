# Prepare one in-repo module release (docs/module-contract.md 10.2 and 16.11). It builds the zip, checks it,
# writes SHA256SUMS.txt and the release notes, and PRINTS the gh command. It runs nothing against GitHub:
# a push or a release needs Nate's word in the session.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish-release.ps1 -Module <id> [-SkipValidate]
# Output: dist\release\<id>-<version>\ with <id>-<version>.zip, SHA256SUMS.txt and NOTES.md.
param(
  [Parameter(Mandatory = $true)][string]$Module,
  [switch]$SkipValidate
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
function Fail([string]$msg) { Write-Output "ERROR: $msg"; exit 1 }

if ($Module -notmatch '^[a-z][a-z0-9-]{1,31}$') { Fail "'$Module' is not a module id." }
$mdir = Join-Path $root "modules\$Module"
$mfile = Join-Path $mdir 'module.json'
if (-not (Test-Path $mfile)) { Fail "modules\$Module\module.json not found." }
$m = Get-Content $mfile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($m.source -ne 'in-repo') { Fail "$Module is a $($m.source) module. It releases from its own repo ($($m.repo)); change DeskKit's pin with contract 10.3." }
$ver = $m.version
$suite = Get-Content (Join-Path $root 'suite.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$entry = $suite.modules | Where-Object { $_.id -eq $Module }
if (-not $entry) { Fail "suite.json has no entry for $Module." }
if ($entry.version -ne $ver) { Fail "suite.json says $Module $($entry.version), module.json says $ver. Make them equal." }
if (-not $suite.repo -or $suite.repo -notmatch '^https://github\.com/([^/]+/[^/]+)$') { Fail 'suite.json has no GitHub repo (contract 16.1).' }
$repo = $Matches[1]

$changelog = Join-Path $mdir 'CHANGELOG.md'
if (-not (Test-Path $changelog)) { Fail "modules\$Module\CHANGELOG.md not found." }
$lines = Get-Content $changelog -Encoding UTF8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "^##\s+$([regex]::Escape($ver))(\s|$)") { $start = $i; break } }
if ($start -lt 0) { Fail "CHANGELOG.md has no '## $ver (YYYY-MM-DD)' heading." }
$notes = @()
for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^##\s'; $i++) { $notes += $lines[$i] }

if (-not $SkipValidate) {
  $py = (Get-Command py.exe -ErrorAction SilentlyContinue).Source
  $pyArgs = @('-3')
  if (-not $py) { $py = (Get-Command python.exe -ErrorAction SilentlyContinue | Where-Object { $_.Source -notmatch 'WindowsApps' } | Select-Object -First 1).Source; $pyArgs = @() }
  if (-not $py) { Fail 'Python not found for tools/validate-modules.py (or pass -SkipValidate).' }
  & $py @pyArgs (Join-Path $root 'tools\validate-modules.py') --root $root
  if ($LASTEXITCODE -ne 0) { Fail 'tools/validate-modules.py failed.' }
}

# Build with the module's own build.ps1 (with -OutDir when it takes one; else it writes dist\<id>-<ver>.zip).
$build = Join-Path $mdir 'build.ps1'
if (-not (Test-Path $build)) { Fail "modules\$Module\build.ps1 not found." }
$out = Join-Path $root "dist\release\$Module-$ver"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$zipName = "$Module-$ver.zip"
$zip = Join-Path $out $zipName
$takesOutDir = (Get-Command $build).Parameters.ContainsKey('OutDir')
if ($takesOutDir) { & powershell -NoProfile -ExecutionPolicy Bypass -File $build -OutDir $out }
else { & powershell -NoProfile -ExecutionPolicy Bypass -File $build }
if ($LASTEXITCODE -ne 0) { Fail "build.ps1 failed with exit code $LASTEXITCODE." }
if (-not $takesOutDir) {
  $built = Join-Path $root "dist\$zipName"
  if (-not (Test-Path $built)) { Fail "build.ps1 did not make dist\$zipName." }
  Copy-Item $built $zip
}
if (-not (Test-Path $zip)) { Fail "build.ps1 did not make $zip." }

# Check the zip: one top folder <id>\ with module.json at the release version (contract 10.2).
$za = [IO.Compression.ZipFile]::OpenRead($zip)
try {
  $names = $za.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }
  $bad = $names | Where-Object { -not $_.StartsWith("$Module/") }
  if ($bad) { Fail "the zip has files outside the top folder $Module/: $($bad | Select-Object -First 3)" }
  $mj = $za.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq "$Module/module.json" } | Select-Object -First 1
  if (-not $mj) { Fail "the zip has no $Module/module.json." }
  $reader = New-Object IO.StreamReader($mj.Open())
  try { $zm = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
  if ($zm.version -ne $ver) { Fail "the zip's module.json says $($zm.version), not $ver." }
  $entryPath = "$Module/" + ($m.entry -replace '\\', '/')
  if ($names -notcontains $entryPath) { Fail "the zip has no entry file $entryPath." }
  foreach ($never in 'build.ps1', 'tests/') { if ($names | Where-Object { $_ -eq "$Module/$never" -or $_.StartsWith("$Module/$never") }) { Fail "the zip ships $never (contract 2.1)." } }
}
finally { $za.Dispose() }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$sums = Join-Path $out 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sums, "$hash  $zipName`n", (New-Object System.Text.UTF8Encoding($false)))
$notesFile = Join-Path $out 'NOTES.md'
[IO.File]::WriteAllText($notesFile, (($notes -join "`n").Trim() + "`n"), (New-Object System.Text.UTF8Encoding($false)))

$tag = "$Module/v$ver"
$title = "$($m.name) $ver"
$pre = if ($ver -match '-') { ' --prerelease' } else { '' }
$rel = $out.Substring($root.Length + 1)
Write-Output ''
Write-Output "Release files in $out"
Write-Output "  $zipName  sha256 $hash"
Write-Output '  SHA256SUMS.txt, NOTES.md'
Write-Output ''
Write-Output 'Nothing was sent to GitHub. With Nate''s word in the session, run from the repo root:'
Write-Output ''
Write-Output "gh release create `"$tag`" `"$rel\$zipName`" `"$rel\SHA256SUMS.txt`" --repo $repo --title `"$title`" --notes-file `"$rel\NOTES.md`"$pre"
exit 0
