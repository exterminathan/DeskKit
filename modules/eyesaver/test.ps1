# Test the Eye Saver module. Exit 0 = all checks passed.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File test.ps1 [-SkipMonitor]
# The monitor test runs read-only commands only (status) against the real monitor. Use -SkipMonitor to skip it.
# It never changes the monitor. The settings file goes in a throw-away data dir.
param([switch]$SkipMonitor)
$ErrorActionPreference = 'Continue'
$ModuleDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ModuleDir '..\..')).Path
$mod = Get-Content (Join-Path $ModuleDir 'module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$fails = New-Object Collections.Generic.List[string]
function Check($name, [bool]$ok, $detail = '') {
  if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name  $detail"; $fails.Add($name) }
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("dk-" + $mod.id + "-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
  # 1. syntax (PowerShell parse)
  $errs = $null; $tok = $null
  [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $ModuleDir $mod.entry), [ref]$tok, [ref]$errs)
  Check 'powershell syntax' ($errs.Count -eq 0) (($errs | ForEach-Object { $_.Message }) -join '; ')

  # 2. validator
  $out = & python.exe (Join-Path $RepoRoot 'tools\validate-modules.py') --root $RepoRoot 2>&1
  Check 'validate-modules.py' ($LASTEXITCODE -eq 0) ($out -join ' ')

  # 3. zip builds and unpacks
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ModuleDir 'build.ps1') -OutDir (Join-Path $tmp 'dist') | Out-Null
  $zip = Join-Path $tmp "dist\$($mod.id)-$($mod.version).zip"
  Check 'zip builds' (Test-Path $zip)
  $unpack = Join-Path $tmp 'unpacked'
  Expand-Archive -Path $zip -DestinationPath $unpack -Force
  $root = Join-Path $unpack $mod.id
  $need = 'module.json', 'README.md', 'CHANGELOG.md', 'launch.cmd', $mod.entry
  $missing = $need | Where-Object { -not (Test-Path (Join-Path $root $_)) }
  Check 'zip holds the payload' (-not $missing) ($missing -join ', ')
  Check 'zip leaves out build.ps1 and test.ps1' (-not ((Test-Path (Join-Path $root 'build.ps1')) -or (Test-Path (Join-Path $root 'test.ps1'))))

  # 4. no hard-coded path in code or module.json (drive letters, \Users\, C:\Tools)
  $bad = Get-ChildItem $ModuleDir -Recurse -File -Include *.ps1, *.cmd, module.json |
    Where-Object { $_.Name -ne 'test.ps1' -and $_.FullName -notmatch '__pycache__' } |
    Select-String -Pattern '(?i)\b[A-Z]:[\\/]', '(?i)\\Users\\'
  Check 'no hard-coded path' (-not $bad) (($bad | ForEach-Object { "$($_.Filename):$($_.LineNumber)" }) -join ', ')

  # 5. entry runs from the unpacked zip, with a throw-away data dir
  $env:DESKKIT_SHELL = '1'
  $env:DESKKIT_DATA_DIR = Join-Path $tmp 'data'
  $launch = Join-Path $root 'launch.cmd'
  function Run([string[]]$a) {
    $o = Join-Path $tmp 'o.txt'; $e = Join-Path $tmp 'e.txt'
    $p = Start-Process cmd.exe -ArgumentList (@('/c', ('"' + $launch + '"')) + $a) -NoNewWindow -Wait -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
    @{ Code = $p.ExitCode; Out = (Get-Content $o -Raw); Err = (Get-Content $e -Raw) }
  }
  # 5a. bad arguments exit 2 with an ERROR line (no monitor is touched)
  $r = Run @('bogus')
  Check 'bad command exits 2 with ERROR line' ($r.Code -eq 2 -and $r.Err -match 'ERROR:') "exit $($r.Code)"
  $r = Run @('brightness', '150')
  Check 'brightness 150 exits 2' ($r.Code -eq 2) "exit $($r.Code)"
  $r = Run @('eyesaver', 'maybe')
  Check 'eyesaver maybe exits 2' ($r.Code -eq 2) "exit $($r.Code)"
  Check 'default settings.json written with both profiles' ((Test-Path (Join-Path $tmp 'data\settings.json')) -and ((Get-Content (Join-Path $tmp 'data\settings.json') -Raw | ConvertFrom-Json).profiles.on.'0x10' -eq 32))
  # 5b. read-only status on the real monitor
  if ($SkipMonitor) { Write-Host 'SKIP  monitor status (-SkipMonitor)' }
  else {
    $r = Run @('status')
    if ($r.Code -eq 5) { Write-Host 'SKIP  monitor status (no DDC/CI monitor answered; exit 5 with an ERROR line is the right result)'; Check 'exit 5 has ERROR line' ($r.Err -match 'ERROR:') }
    else { Check 'status exits 0 and lists values' ($r.Code -eq 0 -and $r.Out -match '0x10 = ') "exit $($r.Code) $($r.Err)" }
  }
} finally {
  Remove-Item Env:DESKKIT_SHELL, Env:DESKKIT_DATA_DIR -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
if ($fails.Count) { Write-Host "`n$($fails.Count) check(s) failed: $($fails -join '; ')"; exit 1 }
Write-Host "`nAll checks passed."
exit 0
