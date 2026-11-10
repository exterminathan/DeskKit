# Update tests (contract 16) of the published shell (dist\shell\DeskKit.exe) against a LOCAL fake feed
# (shell\tests\fake_feed.py on 127.0.0.1). Never the internet, never the real GitHub repos.
# Everything uses throwaway folders: APPDATA, LOCALAPPDATA, a temp install dir (suite.json + modules\), a test
# Run key and a test Region Control uninstall key under HKCU\Software\DeskKit-UpdateTest.
# Modules in the temp install dir: "updtest" (a window fixture: shell\tests\fixtures\fixture.ps1), a copy of the
# eyesaver payload (never started: CLAUDE.md "Audio safety"), and regioncontrol's module.json with a FAKE exe
# (text file, never started; its mutex renamed so the user's real Region Control is never seen or touched).
# The last part starts the real tray with --show and clicks Update in the main window (UI Automation).
#   powershell -NoProfile -ExecutionPolicy Bypass -File shell/update-test.ps1 [-NoBuild] [-Keep]
param([switch]$NoBuild, [switch]$Keep)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$exe = Join-Path $root 'dist\shell\DeskKit.exe'
if (-not $NoBuild) { & (Join-Path $here 'build.ps1'); if (-not $?) { exit 1 } }
if (-not (Test-Path $exe)) { throw "$exe not found. Run shell/build.ps1 first." }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$script:pass = 0; $script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { $script:pass++; Write-Output "PASS  $name" }
  else { $script:fail++; Write-Output "FAIL  $name$(if ($detail) { "  -> $detail" })" }
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
  $until = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $until) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 250 }
  return [bool](& $cond)
}
function MutexExists([string]$name) {
  $m = $null
  if ([System.Threading.Mutex]::TryOpenExisting($name, [ref]$m)) { $m.Dispose(); return $true }
  return $false
}
function ReadShared([string]$file) {
  if (-not (Test-Path $file)) { return '' }
  $fs = [System.IO.File]::Open($file, 'Open', 'Read', 'ReadWrite, Delete')
  try { (New-Object System.IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
}
function RunShell([string[]]$shellArgs, [int]$timeoutSec = 60) {
  $out = [System.IO.Path]::GetTempFileName(); $err = [System.IO.Path]::GetTempFileName()
  $quoted = $shellArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }
  $p = Start-Process $exe -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  $null = $p.Handle
  if (-not $p.WaitForExit($timeoutSec * 1000)) { $p.Kill(); $code = -1 } else { $code = $p.ExitCode }
  $r = [pscustomobject]@{ Code = $code; Out = [string](Get-Content $out -Raw); Err = [string](Get-Content $err -Raw) }
  Remove-Item $out, $err -ErrorAction SilentlyContinue
  return $r
}
function Version([string]$moduleDir) {
  $f = Join-Path $moduleDir 'module.json'
  if (-not (Test-Path $f)) { return '(missing)' }
  return (Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json).version
}
function DirHash([string]$dir) {
  if (-not (Test-Path $dir)) { return '(missing)' }
  $lines = Get-ChildItem $dir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $_.FullName.Substring($dir.Length) + '=' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
  return (($lines -join "`n").GetHashCode()).ToString() + "/$(@($lines).Count)"
}
function Sha([string]$file) { (Get-FileHash $file -Algorithm SHA256).Hash.ToLower() }
function ZipDir([string]$folder, [string]$zip) {
  if (Test-Path $zip) { Remove-Item $zip -Force }
  [IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
}
function WriteJson($obj, [string]$file) {
  $json = ConvertTo-Json -InputObject $obj -Depth 12
  [IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding($false)))
}

if (MutexExists 'Local\DeskKit.updtest') { throw 'Local\DeskKit.updtest exists: a previous test copy still runs.' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("deskkit-update-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$install = Join-Path $tmp 'install'
$mods = Join-Path $install 'modules'
$base = Join-Path $tmp 'base'          # clean copies of the installed modules
$work = Join-Path $tmp 'work'
$feed = Join-Path $tmp 'feed'
$assets = Join-Path $feed 'assets'
New-Item -ItemType Directory -Force -Path $mods, $base, $work, $assets | Out-Null
Copy-Item (Join-Path $root 'suite.json') (Join-Path $install 'suite.json')
$env:APPDATA = Join-Path $tmp 'Roaming'
$env:LOCALAPPDATA = Join-Path $tmp 'Local'
$env:DESKKIT_RUN_KEY = 'Software\DeskKit-UpdateTest\Run'
$env:DESKKIT_NO_UI = '1'
$env:DESKKIT_RC_UNINSTALL_KEY = 'Software\DeskKit-UpdateTest\NoRegionControl'
Remove-Item Env:DESKKIT_UPDATE_FAIL_AT, Env:DESKKIT_UPDATE_NO_RUN, Env:DESKKIT_UPDATE_FIRST_DELAY -ErrorAction SilentlyContinue
$shellLog = Join-Path $env:APPDATA 'DeskKit\shell\shell.log'
$stateFile = Join-Path $env:APPDATA 'DeskKit\shell\state.json'
$updatesDir = Join-Path $env:LOCALAPPDATA 'DeskKit\updates'
$suiteRepo = ((Get-Content (Join-Path $install 'suite.json') -Raw | ConvertFrom-Json).repo -replace '^https://github.com/', '')
$rcRepo = 'exterminathan/region-control'
Write-Output "update-test: temp $tmp, suite repo $suiteRepo"

# ---------------- Fixture modules ----------------
function UpdtestManifest([string]$version) {
  [ordered]@{
    schema_version = 1; id = 'updtest'; name = 'Update Test'; version = $version
    description = 'Update test fixture (window mode).'; source = 'in-repo'; repo = $null
    runtime = [ordered]@{ type = 'powershell'; min = '5.1' }; entry = 'src/fixture.ps1'; args = @('window'); console = $false
    run_mode = 'window'
    instance = [ordered]@{ single = $true; mutex = 'Local\DeskKit.updtest'; show_event = 'Local\DeskKit.updtest.show'; stop_event = 'Local\DeskKit.updtest.stop' }
    actions = @(); autostart = [ordered]@{ default = $false; managed_by = 'shell' }; icon = $null
    data_dir = '%APPDATA%\DeskKit\updtest'; settings_path = $null; settings_action = $null; launcher = $null
    helpers = @(); licence = 'MIT'; homepage = $null
  }
}
function MakeUpdtest([string]$parent, [string]$version, [switch]$Marker) {
  $d = Join-Path $parent 'updtest'
  New-Item -ItemType Directory -Force -Path (Join-Path $d 'src') | Out-Null
  WriteJson (UpdtestManifest $version) (Join-Path $d 'module.json')
  Copy-Item (Join-Path $here 'tests\fixtures\fixture.ps1') (Join-Path $d 'src\fixture.ps1')
  if ($Marker) { Set-Content (Join-Path $d 'NEW.txt') "version $version" }
  return $d
}
[void](MakeUpdtest $base '0.1.0')
foreach ($v in '0.2.0', '0.3.0-beta.1') {
  $p = Join-Path $work "updtest-$v"
  [void](MakeUpdtest $p $v -Marker)
  ZipDir (Join-Path $p 'updtest') (Join-Path $assets "updtest-$v.zip")
}

# Eye Saver: the real payload from its own build.ps1 (never started here).
$esBuild = Join-Path $work 'eyesaver-build'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'modules\eyesaver\build.ps1') -OutDir $esBuild | Out-Null
$esZip = Get-ChildItem $esBuild -Filter 'eyesaver-*.zip' | Select-Object -First 1
if (-not $esZip) { throw 'modules/eyesaver/build.ps1 made no zip.' }
[IO.Compression.ZipFile]::ExtractToDirectory($esZip.FullName, $base)
$esOld = Version (Join-Path $base 'eyesaver')
$esv = [version]$esOld
$esNew = "$($esv.Major).$($esv.Minor + 1).0"
$esNewDir = Join-Path $work "eyesaver-$esNew"
[IO.Compression.ZipFile]::ExtractToDirectory($esZip.FullName, $esNewDir)
$mf = Join-Path $esNewDir 'eyesaver\module.json'
$txt = [IO.File]::ReadAllText($mf) -replace ('"version":\s*"' + [regex]::Escape($esOld) + '"'), ('"version": "' + $esNew + '"')
[IO.File]::WriteAllText($mf, $txt, (New-Object System.Text.UTF8Encoding($false)))
Set-Content (Join-Path $esNewDir 'eyesaver\NEW.txt') "version $esNew"
ZipDir (Join-Path $esNewDir 'eyesaver') (Join-Path $assets "eyesaver-$esNew.zip")

# Region Control: module.json + a fake exe (text). Its mutex is renamed so the real Region Control is never seen.
$rcBase = Join-Path $base 'regioncontrol'
New-Item -ItemType Directory -Force -Path (Join-Path $rcBase 'bin') | Out-Null
$rcJson = [IO.File]::ReadAllText((Join-Path $root 'modules\regioncontrol\module.json')).Replace('Local\\RegionControl.Single', 'Local\\DeskKit.UpdateTest.RC')
[IO.File]::WriteAllText((Join-Path $rcBase 'module.json'), $rcJson, (New-Object System.Text.UTF8Encoding($false)))
Set-Content (Join-Path $rcBase 'bin\RegionControl.exe') 'FAKE Region Control exe 0.1.1 (never started)'
$rcOld = Version $rcBase
Set-Content (Join-Path $assets 'RegionControl-Portable-0.1.2.exe') 'FAKE Region Control exe 0.1.2 (never started)'
Set-Content (Join-Path $assets 'DeskKit-Setup-9.0.0.exe') 'FAKE DeskKit installer (never started)'

function ResetInstall([switch]$NoRegionControl) {
  Get-ChildItem $mods -Force | Remove-Item -Recurse -Force
  Copy-Item -Recurse (Join-Path $base 'updtest') (Join-Path $mods 'updtest')
  Copy-Item -Recurse (Join-Path $base 'eyesaver') (Join-Path $mods 'eyesaver')
  if (-not $NoRegionControl) { Copy-Item -Recurse $rcBase (Join-Path $mods 'regioncontrol') }
}

# ---------------- Feed helpers ----------------
function Asset([string]$name, [string]$file, [string]$digest = 'auto') {
  if (-not $file) { $file = $name }
  $a = [ordered]@{ name = $name; browser_download_url = "{base}/assets/$file" }
  if ($digest -eq 'auto') { $p = Join-Path $assets $file; if (Test-Path $p) { $a.digest = 'sha256:' + (Sha $p) } }
  elseif ($digest) { $a.digest = $digest }
  return $a
}
function Rel([string]$tag, $assetList, [bool]$pre = $false) {
  [ordered]@{ tag_name = $tag; draft = $false; prerelease = $pre; html_url = "https://example.invalid/releases/$tag"; assets = @($assetList) }
}
function SetFeed([hashtable]$repos) { WriteJson ([ordered]@{ repos = $repos }) (Join-Path $feed 'feed.json') }
function StandardFeed {
  SetFeed @{
    $suiteRepo = @(
      (Rel 'updtest/v0.2.0' @((Asset 'updtest-0.2.0.zip'))),
      (Rel 'updtest/v0.1.0' @()),
      (Rel "eyesaver/v$esNew" @((Asset "eyesaver-$esNew.zip"))),
      (Rel 'v0.1.0' @())
    )
    $rcRepo = @((Rel 'v0.1.2' @((Asset 'RegionControl-Portable-0.1.2.exe')) $true), (Rel 'v0.1.1' @() $true))
  }
}
function RowOf([string]$out, [string]$id) { ($out -split "`r?`n" | Where-Object { $_ -match "^$([regex]::Escape($id))\s" } | Select-Object -First 1) }
function ResultOf([string]$id) {
  if (-not (Test-Path $stateFile)) { return $null }
  $s = Get-Content $stateFile -Raw | ConvertFrom-Json
  if ($s.updates -and $s.updates.results) { return $s.updates.results.$id }
  return $null
}

# ---------------- Start the fake feed ----------------
$py = (Get-Command py.exe -ErrorAction SilentlyContinue).Source
$pyArgs = @('-3')
if (-not $py) { $py = (Get-Command python.exe -ErrorAction SilentlyContinue | Where-Object { $_.Source -notmatch 'WindowsApps' } | Select-Object -First 1).Source; $pyArgs = @() }
if (-not $py) { throw 'Python not found: the fake feed needs it.' }
$portFile = Join-Path $feed 'port.txt'
StandardFeed
$server = Start-Process $py -ArgumentList ($pyArgs + @('-I', "`"$(Join-Path $here 'tests\fake_feed.py')`"", '--root', "`"$feed`"", '--port-file', "`"$portFile`"")) -PassThru -WindowStyle Hidden
$shell = $null
try {
  if (-not (WaitFor { Test-Path $portFile } 15)) { throw 'The fake feed did not start.' }
  $port = (Get-Content $portFile -Raw).Trim()
  $feedBase = "http://127.0.0.1:$port"
  $env:DESKKIT_FEED_BASE = $feedBase
  Write-Output "fake feed: $feedBase (pid $($server.Id))"

  # Data dir of updtest: must survive every update unchanged.
  $dataDir = Join-Path $env:APPDATA 'DeskKit\updtest'
  New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
  Set-Content (Join-Path $dataDir 'settings.json') '{ "keep": "me" }'
  $dataHash = DirHash $dataDir

  # ---- 1. Newer version found, same version ignored, pinned and shell rows ----
  ResetInstall
  $r = RunShell @('--check-updates', '--modules-dir', $mods)
  Check '--check-updates: exit 0' ($r.Code -eq 0) "$($r.Code) $($r.Err)"
  Check '--check-updates: header row' ($r.Out -match 'ID\s+INSTALLED\s+AVAILABLE\s+STATE\s+NOTE') $r.Out
  Check 'newer version found: updtest 0.1.0 -> 0.2.0' ((RowOf $r.Out 'updtest') -match '0\.1\.0\s+0\.2\.0\s+update') (RowOf $r.Out 'updtest')
  Check "newer version found: eyesaver $esOld -> $esNew" ((RowOf $r.Out 'eyesaver') -match "$([regex]::Escape($esNew))\s+update") (RowOf $r.Out 'eyesaver')
  Check 'pinned: regioncontrol 0.1.2 found (GitHub pre-release flag, plain version)' ((RowOf $r.Out 'regioncontrol') -match '0\.1\.2\s+update') (RowOf $r.Out 'regioncontrol')
  Check 'same version ignored: shell v0.1.0 is current' ((RowOf $r.Out 'shell') -match '\s+current') (RowOf $r.Out 'shell')
  Check '--check-updates installs nothing' ((Version (Join-Path $mods 'updtest')) -eq '0.1.0' -and -not (Test-Path (Join-Path $mods 'updtest\NEW.txt')))

  SetFeed @{ $suiteRepo = @((Rel 'updtest/v0.1.0' @((Asset 'updtest-0.2.0.zip'))), (Rel 'updtest/v0.0.9' @())); $rcRepo = @() }
  $r = RunShell @('--check-updates', '--modules-dir', $mods)
  Check 'same version ignored: updtest 0.1.0 in the feed is current' ((RowOf $r.Out 'updtest') -match '0\.1\.0\s+-\s+current') (RowOf $r.Out 'updtest')

  # ---- 2. ETag cache: the second identical check gets 304 ----
  $reqLog = Join-Path $feed 'requests.log'
  Remove-Item $reqLog -ErrorAction SilentlyContinue
  $r = RunShell @('--check-updates', '--modules-dir', $mods)
  Check 'ETag: repeat check sends If-None-Match and gets 304' ((Get-Content $reqLog -Raw) -match '304 /repos/') (Get-Content $reqLog -Raw)

  # ---- 3. Pre-release rules ----
  SetFeed @{ $suiteRepo = @((Rel 'updtest/v0.3.0-beta.1' @((Asset 'updtest-0.3.0-beta.1.zip')) $true), (Rel 'updtest/v0.2.0' @((Asset 'updtest-0.2.0.zip')))); $rcRepo = @() }
  $r = RunShell @('--check-updates', '--modules-dir', $mods)
  Check 'pre-release: skipped by default (0.2.0 offered, not 0.3.0-beta.1)' ((RowOf $r.Out 'updtest') -match '0\.1\.0\s+0\.2\.0\s+update') (RowOf $r.Out 'updtest')
  $state = if (Test-Path $stateFile) { Get-Content $stateFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
  if (-not $state.updates) { $state | Add-Member -NotePropertyName updates -NotePropertyValue ([pscustomobject]@{}) }
  $state.updates | Add-Member -NotePropertyName include_prereleases -NotePropertyValue $true -Force
  WriteJson $state $stateFile
  $r = RunShell @('--check-updates', '--modules-dir', $mods)
  Check 'pre-release: offered when include_prereleases is on' ((RowOf $r.Out 'updtest') -match '0\.3\.0-beta\.1\s+update') (RowOf $r.Out 'updtest')
  $state.updates.include_prereleases = $false
  WriteJson $state $stateFile

  # ---- 4. SHA256 mismatch refused ----
  ResetInstall
  $wrong = 'sha256:' + ('0' * 64)
  SetFeed @{ $suiteRepo = @((Rel 'updtest/v0.2.0' @((Asset 'updtest-0.2.0.zip' '' $wrong)))); $rcRepo = @() }
  $r = RunShell @('--update', 'updtest', '--modules-dir', $mods)
  Check 'SHA256 mismatch: exit 1' ($r.Code -eq 1) "$($r.Code) $($r.Out) $($r.Err)"
  Check 'SHA256 mismatch: message says checksum' ($r.Err -match 'did not match its checksum') $r.Err
  Check 'SHA256 mismatch: downloaded file deleted' (-not (Test-Path (Join-Path $updatesDir 'updtest-0.2.0')))
  Check 'SHA256 mismatch: module untouched' ((Version (Join-Path $mods 'updtest')) -eq '0.1.0' -and -not (Test-Path (Join-Path $mods 'updtest\NEW.txt')))
  Check 'SHA256 mismatch: recorded as failed' ((ResultOf 'updtest').result -eq 'failed')

  # SHA256SUMS.txt that disagrees with the asset digest: refused.
  $sums = Join-Path $assets 'bad-SHA256SUMS.txt'
  Set-Content $sums "$('1' * 64)  updtest-0.2.0.zip" -Encoding ASCII
  SetFeed @{ $suiteRepo = @((Rel 'updtest/v0.2.0' @((Asset 'updtest-0.2.0.zip'), (Asset 'SHA256SUMS.txt' 'bad-SHA256SUMS.txt' '')))); $rcRepo = @() }
  $r = RunShell @('--update', 'updtest', '--modules-dir', $mods)
  Check 'SHA256SUMS disagrees with the digest: refused, untouched' ($r.Code -eq 1 -and (Version (Join-Path $mods 'updtest')) -eq '0.1.0') "$($r.Code) $($r.Err)"
  # No checksum at all: refused.
  SetFeed @{ $suiteRepo = @((Rel 'updtest/v0.2.0' @((Asset 'updtest-0.2.0.zip' '' '')))); $rcRepo = @() }
  $r = RunShell @('--update', 'updtest', '--modules-dir', $mods)
  Check 'no checksum: refused, untouched' ($r.Code -eq 1 -and $r.Err -match 'no checksum' -and (Version (Join-Path $mods 'updtest')) -eq '0.1.0') "$($r.Code) $($r.Err)"

  # ---- 5. Download failure ----
  SetFeed @{ $suiteRepo = @((Rel 'updtest/v0.2.0' @((Asset 'updtest-0.2.0.zip' 'missing-file.zip' ('sha256:' + (Sha (Join-Path $assets 'updtest-0.2.0.zip'))))))); $rcRepo = @() }
  $r = RunShell @('--update', 'updtest', '--modules-dir', $mods)
  Check 'download failure: exit 1' ($r.Code -eq 1) "$($r.Code) $($r.Err)"
  Check 'download failure: message' ($r.Err -match 'download of Update Test 0\.2\.0 failed') $r.Err
  Check 'download failure: module untouched' ((Version (Join-Path $mods 'updtest')) -eq '0.1.0')

  # ---- 5b. Failed staging leaves nothing behind (defect 10) ----
  $slip = Join-Path $assets 'slip.zip'
  if (Test-Path $slip) { Remove-Item $slip -Force }
  $zfs = [IO.File]::Open($slip, 'Create')
  $za = New-Object IO.Compression.ZipArchive($zfs, [IO.Compression.ZipArchiveMode]::Create)
  foreach ($en in 'updtest/module.json', '../escaped.txt') {
    $ze = $za.CreateEntry($en); $zw = New-Object IO.StreamWriter($ze.Open()); $zw.Write('x'); $zw.Dispose()
  }
  $za.Dispose(); $zfs.Dispose()
  SetFeed @{ $suiteRepo = @((Rel 'updtest/v0.2.0' @((Asset 'updtest-0.2.0.zip' 'slip.zip')))); $rcRepo = @() }
  ResetInstall
  $r = RunShell @('--update', 'updtest', '--modules-dir', $mods)
  Check 'failed staging (zip-slip): exit 1, nothing changed' ($r.Code -eq 1 -and $r.Err -match 'could not be prepared' -and (Version (Join-Path $mods 'updtest')) -eq '0.1.0') "$($r.Code) $($r.Err)"
  Check 'failed staging: no _update\updtest.extract left' (-not (Test-Path (Join-Path $mods '_update\updtest.extract'))) ((Get-ChildItem (Join-Path $mods '_update') -ErrorAction SilentlyContinue | ForEach-Object Name) -join ',')
  Check 'failed staging: empty _update folder removed' (-not (Test-Path (Join-Path $mods '_update')))
  Check 'failed staging: nothing written outside modules' (-not (Test-Path (Join-Path $install 'escaped.txt')) -and -not (Test-Path (Join-Path $mods 'escaped.txt')))
  StandardFeed

  # ---- 6. Rollback when the swap fails midway ----
  StandardFeed
  $env:DESKKIT_UPDATE_FAIL_AT = 'swap-in'
  $before = DirHash (Join-Path $mods 'updtest')
  $r = RunShell @('--update', 'updtest', '--modules-dir', $mods)
  Remove-Item Env:DESKKIT_UPDATE_FAIL_AT
  Check 'rollback: exit 1' ($r.Code -eq 1) "$($r.Code) $($r.Err)"
  Check 'rollback: old version put back, files identical' ((DirHash (Join-Path $mods 'updtest')) -eq $before) $r.Err
  Check 'rollback: recorded as rolled-back' ((ResultOf 'updtest').result -eq 'rolled-back') ((ResultOf 'updtest') | Out-String)
  Check 'rollback: no staging left in modules\_update' (-not (Test-Path (Join-Path $mods '_update\updtest.new')))

  # ---- 7. Locked file: the swap cannot start, nothing changes ----
  $lockFile = Join-Path $mods 'updtest\src\fixture.ps1'
  $lock = [IO.File]::Open($lockFile, 'Open', 'Read', 'Read')
  try { $r = RunShell @('--update', 'updtest', '--modules-dir', $mods) } finally { $lock.Dispose() }
  Check 'locked file: exit 1, file in use' ($r.Code -eq 1 -and $r.Err -match 'file is in use') "$($r.Code) $($r.Err)"
  Check 'locked file: module untouched' ((DirHash (Join-Path $mods 'updtest')) -eq $before)

  # ---- 8. Module running: the user declines, then agrees ----
  $r = RunShell @('--run', 'updtest', '--modules-dir', $mods)
  Check 'running: fixture started' ($r.Code -eq 0 -and (WaitFor { MutexExists 'Local\DeskKit.updtest' } 20)) "$($r.Code) $($r.Err)"
  $r = RunShell @('--update', 'updtest', '--modules-dir', $mods)
  Check 'running + decline: exit 3' ($r.Code -eq 3) "$($r.Code) $($r.Out) $($r.Err)"
  Check 'running + decline: module untouched' ((DirHash (Join-Path $mods 'updtest')) -eq $before)
  Check 'running + decline: module still runs' (MutexExists 'Local\DeskKit.updtest')
  Check 'running + decline: recorded as declined' ((ResultOf 'updtest').result -eq 'declined')
  $r = RunShell @('--update', 'updtest', '--yes', '--modules-dir', $mods)
  Check 'running + agree: exit 0' ($r.Code -eq 0) "$($r.Code) $($r.Out) $($r.Err)"
  Check 'running + agree: updated to 0.2.0' ((Version (Join-Path $mods 'updtest')) -eq '0.2.0' -and (Test-Path (Join-Path $mods 'updtest\NEW.txt')))
  Check 'running + agree: stopped by its stop event' ((ReadShared $shellLog) -match 'stop updtest: step 1, stop event set')
  Check 'running + agree: started again' (WaitFor { MutexExists 'Local\DeskKit.updtest' } 20) $r.Out
  Check 'swap success: modules\_update cleaned' (-not (Test-Path (Join-Path $mods '_update\updtest.old')) -and -not (Test-Path (Join-Path $mods '_update\updtest.new')))
  Check 'swap success: download removed' (-not (Test-Path (Join-Path $updatesDir 'updtest-0.2.0')))
  Check 'swap success: recorded in state.json' ((ResultOf 'updtest').result -eq 'updated' -and (ResultOf 'updtest').to -eq '0.2.0')
  Check 'swap success: shell.log has the update line' ((ReadShared $shellLog) -match 'update updtest: updated 0\.1\.0 -> 0\.2\.0')
  $r = RunShell @('--stop', 'updtest', '--modules-dir', $mods)
  Check 'running: test copy stopped again' ($r.Code -eq 0 -and (WaitFor { -not (MutexExists 'Local\DeskKit.updtest') } 10))
  Check 'data dir untouched by every update' ((DirHash $dataDir) -eq $dataHash)

  # ---- 8b. SHA256SUMS.txt only (no asset digest), as tools/publish-release.ps1 writes it ----
  ResetInstall
  $goodSums = Join-Path $assets 'good-SHA256SUMS.txt'
  [IO.File]::WriteAllText($goodSums, "$(Sha (Join-Path $assets 'updtest-0.2.0.zip'))  updtest-0.2.0.zip`n", (New-Object System.Text.UTF8Encoding($false)))
  SetFeed @{ $suiteRepo = @((Rel 'updtest/v0.2.0' @((Asset 'updtest-0.2.0.zip' '' ''), (Asset 'SHA256SUMS.txt' 'good-SHA256SUMS.txt' '')))); $rcRepo = @() }
  $r = RunShell @('--update', 'updtest', '--modules-dir', $mods)
  Check 'SHA256SUMS.txt only: verified and updated' ($r.Code -eq 0 -and (Version (Join-Path $mods 'updtest')) -eq '0.2.0') "$($r.Code) $($r.Out) $($r.Err)"

  # ---- 9. --update all --yes: in-repo zip, real eyesaver payload, pinned exe ----
  ResetInstall
  StandardFeed
  $r = RunShell @('--update', 'all', '--yes', '--modules-dir', $mods)
  Check '--update all --yes: exit 0' ($r.Code -eq 0) "$($r.Code) $($r.Out) $($r.Err)"
  Check '--update all: updtest 0.2.0' ((Version (Join-Path $mods 'updtest')) -eq '0.2.0')
  Check "--update all: eyesaver $esNew (real payload)" ((Version (Join-Path $mods 'eyesaver')) -eq $esNew -and (Test-Path (Join-Path $mods 'eyesaver\src\monitor.ps1')) -and (Test-Path (Join-Path $mods 'eyesaver\NEW.txt')))
  Check '--update all: regioncontrol module.json 0.1.2' ((Version (Join-Path $mods 'regioncontrol')) -eq '0.1.2') $r.Out
  Check '--update all: regioncontrol exe swapped (fake, never started)' ((Get-Content (Join-Path $mods 'regioncontrol\bin\RegionControl.exe') -Raw) -match '0\.1\.2')
  Check '--update all: regioncontrol module.json keeps its other keys' ((Get-Content (Join-Path $mods 'regioncontrol\module.json') -Raw | ConvertFrom-Json).repo -eq 'https://github.com/exterminathan/region-control')
  $r = RunShell @('--update', 'all', '--yes', '--modules-dir', $mods)
  Check '--update all again: nothing to do, exit 0' ($r.Code -eq 0 -and $r.Out -match 'up to date') "$($r.Code) $($r.Out)"
  $r = RunShell @('--update', 'nosuch', '--modules-dir', $mods)
  Check '--update unknown id: exit 2' ($r.Code -eq 2) "$($r.Code)"

  # ---- 10. Region Control installed separately: notify only, never writes ----
  ResetInstall -NoRegionControl
  $fakeProg = Join-Path $tmp 'FakeProgramFiles\Region Control'
  New-Item -ItemType Directory -Force -Path $fakeProg | Out-Null
  Set-Content (Join-Path $fakeProg 'RegionControl.exe') 'FAKE separate install (never started)'
  $progHash = DirHash $fakeProg
  $rcKey = 'HKCU:\Software\DeskKit-UpdateTest\RegionControl'
  New-Item -Path $rcKey -Force | Out-Null
  New-ItemProperty -Path $rcKey -Name DisplayVersion -Value '0.1.1' -Force | Out-Null
  New-ItemProperty -Path $rcKey -Name InstallLocation -Value $fakeProg -Force | Out-Null
  $env:DESKKIT_RC_UNINSTALL_KEY = 'Software\DeskKit-UpdateTest\RegionControl'
  $r = RunShell @('--check-updates', '--modules-dir', $mods)
  Check 'separate Region Control: notify-only row' ((RowOf $r.Out 'regioncontrol') -match '0\.1\.1\s+0\.1\.2\s+notify-only') (RowOf $r.Out 'regioncontrol')
  $r = RunShell @('--update', 'regioncontrol', '--yes', '--modules-dir', $mods)
  Check 'separate Region Control: --update exits 3 (notify only)' ($r.Code -eq 3) "$($r.Code) $($r.Out) $($r.Err)"
  Check 'separate Region Control: message names the release page' ($r.Out -match 'example\.invalid/releases/v0\.1\.2') $r.Out
  Check 'separate Region Control: its folder is untouched' ((DirHash $fakeProg) -eq $progHash)
  Check 'separate Region Control: nothing downloaded' (-not (Test-Path (Join-Path $updatesDir 'regioncontrol-0.1.2')))
  Check 'separate Region Control: not added to modules\' (-not (Test-Path (Join-Path $mods 'regioncontrol')))
  $env:DESKKIT_RC_UNINSTALL_KEY = 'Software\DeskKit-UpdateTest\NoRegionControl'

  # ---- 11. Source folder (development): notify only, read-only check of the repo ----
  $r = RunShell @('--check-updates', '--modules-dir', (Join-Path $root 'modules'))
  Check 'source folder (repo modules\, build.ps1 present): eyesaver is notify-only' ((RowOf $r.Out 'eyesaver') -match 'notify-only') (RowOf $r.Out 'eyesaver')
  Check 'source folder: repo regioncontrol (no build.ps1) is notify-only too' ((RowOf $r.Out 'regioncontrol') -match 'notify-only') (RowOf $r.Out 'regioncontrol')
  Check 'source folder: nothing written to the repo (no modules\_update)' (-not (Test-Path (Join-Path $root 'modules\_update')))

  # ---- 12. The shell's own update: download and check the installer, never run it here ----
  SetFeed @{ $suiteRepo = @((Rel 'v9.0.0' @((Asset 'DeskKit-Setup-9.0.0.exe')))); $rcRepo = @() }
  $env:DESKKIT_UPDATE_NO_RUN = '1'
  $r = RunShell @('--update', 'shell', '--modules-dir', $mods)
  Remove-Item Env:DESKKIT_UPDATE_NO_RUN
  Check '--update shell: installer downloaded and checked, not started' ($r.Code -eq 0 -and $r.Out -match 'Downloaded and checked' -and (Test-Path (Join-Path $updatesDir 'shell-9.0.0\DeskKit-Setup-9.0.0.exe'))) "$($r.Code) $($r.Out) $($r.Err)"

  # ---- 13. Offline: the check fails silently with a log line ----
  $env:DESKKIT_FEED_BASE = 'http://127.0.0.1:9'
  $logBefore = (ReadShared $shellLog).Length
  $r = RunShell @('--check-updates', '--modules-dir', $mods)
  Check 'offline: --check-updates still exits 0' ($r.Code -eq 0) "$($r.Code) $($r.Err)"
  Check 'offline: rows say error' ((RowOf $r.Out 'updtest') -match '\serror\s') (RowOf $r.Out 'updtest')
  Check 'offline: no ERROR on stderr (silent)' (-not ($r.Err -match 'ERROR')) $r.Err
  Check 'offline: one log line names the failure' ((ReadShared $shellLog).Substring($logBefore) -match 'updates: check failed for') ((ReadShared $shellLog).Substring($logBefore))
  $env:DESKKIT_FEED_BASE = $feedBase

  # ---- 14. Real launch: the tray finds the update and the Update button installs it ----
  # Another DeskKit shell (not ours) may hold Local\DeskKit.Shell: wait for it, never touch it.
  if (-not (WaitFor { -not (MutexExists 'Local\DeskKit.Shell') } 300)) {
    Check 'tray: no other DeskKit shell runs (waited 5 min; this test never closes it)' $false
    throw 'skip-tray'
  }
  ResetInstall -NoRegionControl
  StandardFeed
  $env:DESKKIT_UPDATE_FIRST_DELAY = '1'
  $shell = Start-Process $exe -ArgumentList @('--modules-dir', "`"$mods`"", '--show') -PassThru
  $shellPid = $shell.Id
  Check 'tray: started' (WaitFor { (ReadShared $shellLog) -match "\[$shellPid\] tray icon created" } 20)
  Check 'tray: automatic check ran after the first delay' (WaitFor { (ReadShared $shellLog) -match "\[$shellPid\] updates: checked" } 30)
  $rootEl = [Windows.Automation.AutomationElement]::RootElement
  $winCond = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $shellPid)),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'DeskKit')))
  $win = $null
  [void](WaitFor { $script:win = $rootEl.FindFirst('Children', $winCond); $null -ne $script:win } 15)
  Check 'tray: main window open' ($null -ne $win)
  if ($win) {
    function ById([string]$id) { $win.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) }
    $item = $win.FindFirst('Descendants', (New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Eye Saver')))))
    Check 'window: Eye Saver row present' ($null -ne $item)
    if ($item) {
      $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
      $btn = ById 'btnUpdate'
      Check 'window: Update button enabled for Eye Saver' (WaitFor { $b = ById 'btnUpdate'; $b -and $b.Current.IsEnabled } 10)
      $status = ById 'lblUpdateStatus'
      Check 'window: status line says an update is available' ($status -and $status.Current.Name -match "Update available: $([regex]::Escape($esNew))") $(if ($status) { $status.Current.Name })
      (ById 'btnUpdate').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
      Check "real launch: Update click installs eyesaver $esNew in the temp install dir" (WaitFor { (Version (Join-Path $mods 'eyesaver')) -eq $esNew } 30) (Version (Join-Path $mods 'eyesaver'))
      Check 'real launch: status line reports the result' (WaitFor { $s = ById 'lblUpdateStatus'; $s -and $s.Current.Name -match 'was updated' } 10) $(if (ById 'lblUpdateStatus') { (ById 'lblUpdateStatus').Current.Name })
      Check 'real launch: the payload is complete' ((Test-Path (Join-Path $mods 'eyesaver\src\monitor.ps1')) -and (Test-Path (Join-Path $mods 'eyesaver\launch.cmd')))
    }
  }
  $r = RunShell @('--exit')
  Check 'tray: --exit closes the test shell' ($r.Code -eq 0 -and $shell.WaitForExit(10000))
  Remove-Item Env:DESKKIT_UPDATE_FIRST_DELAY -ErrorAction SilentlyContinue
}
catch { if ("$_" -ne 'skip-tray') { throw } }
finally {
  if ($shell -and -not $shell.HasExited) { Write-Output "cleanup: closing the test shell (pid $($shell.Id))"; $shell.Kill() }
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
  Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($tmp) } | ForEach-Object {
    Write-Output "cleanup: stopping leftover test process $($_.ProcessId) $($_.Name)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
  }
  Remove-Item -Path 'HKCU:\Software\DeskKit-UpdateTest' -Recurse -ErrorAction SilentlyContinue
  if (-not $Keep) { Start-Sleep -Milliseconds 500; Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
  else { Write-Output "kept: $tmp" }
}
Write-Output "$($script:pass) passed, $($script:fail) failed"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
