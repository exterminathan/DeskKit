# Build the shell and run its self-test (DeskKit.exe --selftest) against this repo's modules\.
# The self-test uses temp folders for APPDATA, LOCALAPPDATA and a test Run key; it starts only its own
# test modules (shell\tests\fixtures). Exit 0 = all checks passed.
#   powershell -NoProfile -ExecutionPolicy Bypass -File shell/test.ps1 [-NoBuild]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$exe = Join-Path $root 'dist\shell\DeskKit.exe'

if (-not $NoBuild) {
  & (Join-Path $here 'build.ps1')
  if (-not $?) { exit 1 }
}
if (-not (Test-Path $exe)) { Write-Output "FAIL: $exe not found. Run shell/build.ps1."; exit 1 }

$out = [System.IO.Path]::GetTempFileName()
$err = [System.IO.Path]::GetTempFileName()
try {
  $p = Start-Process $exe -ArgumentList @('--selftest', '--modules-dir', "`"$(Join-Path $root 'modules')`"") `
    -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  Get-Content $out
  Get-Content $err | ForEach-Object { Write-Output "stderr: $_" }
  $v = (& $exe --version | Out-String).Trim()
  if ($v -notmatch '^DeskKit \d+\.\d+\.\d+') { Write-Output "FAIL: --version printed '$v'"; exit 1 }
  Write-Output "--version: $v"
  if ($p.ExitCode -ne 0) { Write-Output "FAIL: self-test exit code $($p.ExitCode)"; exit 1 }
  Write-Output 'shell/test.ps1: PASS'
  exit 0
}
finally {
  Remove-Item $out, $err -ErrorAction SilentlyContinue
}
