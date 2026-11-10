# Test the Power Display scaffold. Exit 0 = all checks passed.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File test.ps1
# Builds the module, runs the validator, starts it with a temp data dir, then stops it with its
# stop event (contract 7.2, 7.3). It never touches a running copy of any module.
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
$proc = $null
try {
  # 1. build
  $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ModuleDir 'build.ps1') 2>&1
  $buildCode = $LASTEXITCODE
  $exe = Join-Path $ModuleDir $mod.entry
  Check 'build.ps1 builds the exe' ((Test-Path $exe) -and $buildCode -eq 0) ($out | Select-Object -Last 3 | Out-String)

  # 2. validator
  $out = & python.exe (Join-Path $RepoRoot 'tools\validate-modules.py') --root $RepoRoot 2>&1
  Check 'validate-modules.py' ($LASTEXITCODE -eq 0) ($out -join ' ')

  # 3. do not run next to a real copy
  $mutexName = $mod.instance.mutex
  $m = $null
  if ([Threading.Mutex]::TryOpenExisting($mutexName, [ref]$m)) {
    $m.Dispose()
    Check 'no real copy is running' $false "a copy holds $mutexName; start/stop checks skipped, it is not touched"
    throw 'skip'
  }

  # 4. start with a throw-away data dir
  $data = Join-Path $tmp 'data'
  $env:DESKKIT_DATA_DIR = $data
  $env:DESKKIT_SHELL = '1'
  $proc = Start-Process -FilePath $exe -PassThru
  $up = $false
  for ($i = 0; $i -lt 40 -and -not $up; $i++) {
    Start-Sleep -Milliseconds 250
    $m = $null
    if ([Threading.Mutex]::TryOpenExisting($mutexName, [ref]$m)) { $up = $true; $m.Dispose() }
  }
  Check 'starts and holds its mutex' $up
  $log = Join-Path $data ("logs\" + $mod.id + ".log")
  Check 'logs the scaffold line' ((Test-Path $log) -and ((Get-Content $log -Raw) -match 'scaffold, no features yet')) $log

  # 5. a second start hands over and exits 0
  $second = Start-Process -FilePath $exe -PassThru -Wait
  Check 'second copy exits 0' ($second.ExitCode -eq 0) "exit $($second.ExitCode)"
  Check 'first copy still runs after the second start' (-not $proc.HasExited)

  # 6. stop event: exit 0 within 5 s
  $ev = [Threading.EventWaitHandle]::OpenExisting($mod.instance.stop_event)
  [void]$ev.Set(); $ev.Dispose()
  $stopped = $proc.WaitForExit(5000)
  Check 'stop event ends it within 5 s' $stopped
  if ($stopped) { Check 'exit code 0' ($proc.ExitCode -eq 0) "exit $($proc.ExitCode)" }
} catch {
  if ("$_" -ne 'skip') { Check 'test ran without an exception' $false "$_" }
} finally {
  # Stop only the process this test started.
  if ($proc -and -not $proc.HasExited) { $proc.Kill($true) }
  Remove-Item Env:\DESKKIT_DATA_DIR, Env:\DESKKIT_SHELL -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
if ($fails.Count -gt 0) { Write-Host "`n$($fails.Count) check(s) failed."; exit 1 }
Write-Host "`nAll checks passed."
exit 0
