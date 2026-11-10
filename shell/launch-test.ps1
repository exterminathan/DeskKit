# Real launch test of the published shell (dist\shell\DeskKit.exe). Run shell/build.ps1 first.
# Everything uses throwaway folders: APPDATA, LOCALAPPDATA, a test Run key, and a temp modules dir holding
# copies of _template (built), eyesaver, regioncontrol's module.json only (no exe: it can never start),
# the shelltest-window and shelltest-echo fixtures, _template-python, and (opt-in) regionshot. It starts only
# its own processes and closes only the shell it started.
#   powershell -NoProfile -ExecutionPolicy Bypass -File shell/launch-test.ps1 [-Keep] [-RegionShot]
# -RegionShot: also start RegionShot (first run builds its venv in the temp LOCALAPPDATA: downloads, minutes).
#   Skipped when a RegionShot copy already runs.
# Never started here (CLAUDE.md "Audio safety"): Cable Listen against real audio devices, and Eye Saver
# actions that change the monitor. Eye Saver runs only its read-only "status" action.
param([switch]$Keep, [switch]$RegionShot)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$exe = Join-Path $root 'dist\shell\DeskKit.exe'
if (-not (Test-Path $exe)) { throw "$exe not found. Run shell/build.ps1 first." }
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
function RunShell([string[]]$shellArgs, [int]$timeoutSec = 30) {
  $out = [System.IO.Path]::GetTempFileName(); $err = [System.IO.Path]::GetTempFileName()
  $quoted = $shellArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }
  $p = Start-Process $exe -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  $null = $p.Handle   # PowerShell 5.1: cache the handle, or ExitCode comes back empty
  if (-not $p.WaitForExit($timeoutSec * 1000)) { $p.Kill(); $code = -1 } else { $code = $p.ExitCode }
  $r = [pscustomobject]@{ Code = $code; Out = (Get-Content $out -Raw); Err = (Get-Content $err -Raw) }
  Remove-Item $out, $err -ErrorAction SilentlyContinue
  return $r
}

if (MutexExists 'Local\DeskKit.Shell') { throw 'A DeskKit shell already runs in this session. Close it first; this test will not touch it.' }

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("deskkit-launch-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$mods = Join-Path $tmp 'modules'
New-Item -ItemType Directory -Force -Path $mods | Out-Null
$env:APPDATA = Join-Path $tmp 'Roaming'
$env:LOCALAPPDATA = Join-Path $tmp 'Local'
$env:DESKKIT_RUN_KEY = 'Software\DeskKit-LaunchTest\Run'
$env:DESKKIT_NO_UI = '1'
$shellLog = Join-Path $env:APPDATA 'DeskKit\shell\shell.log'
$logs = Join-Path $env:APPDATA 'DeskKit\shell\logs'
Write-Output "temp: $tmp"

# ---- Test modules ----
Copy-Item -Recurse (Join-Path $root 'modules\_template') (Join-Path $mods 'template')
Remove-Item -Recurse -Force (Join-Path $mods 'template\bin') -ErrorAction SilentlyContinue
Write-Output 'building the template copy (about 30 s)...'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $mods 'template\build.ps1') | Out-Null
Check 'template copy built' (Test-Path (Join-Path $mods 'template\bin\Hello.exe'))
Copy-Item -Recurse (Join-Path $root 'modules\eyesaver') (Join-Path $mods 'eyesaver')
New-Item -ItemType Directory -Force -Path (Join-Path $mods 'regioncontrol') | Out-Null
Copy-Item (Join-Path $root 'modules\regioncontrol\module.json') (Join-Path $mods 'regioncontrol\module.json')
$st = Join-Path $mods 'shelltest-window'
New-Item -ItemType Directory -Force -Path (Join-Path $st 'src') | Out-Null
Copy-Item (Join-Path $here 'tests\fixtures\fixture.ps1') (Join-Path $st 'src\fixture.ps1')
@'
{
  "schema_version": 1, "id": "shelltest-window", "name": "Shell Test Window", "version": "0.0.1",
  "description": "Launch-test module: holds its mutex until its stop event.", "source": "in-repo", "repo": null,
  "runtime": { "type": "powershell", "min": "5.1" }, "entry": "src/fixture.ps1", "args": ["window"],
  "console": false, "run_mode": "window",
  "instance": { "single": true, "mutex": "Local\\DeskKit.shelltest-window",
    "show_event": "Local\\DeskKit.shelltest-window.show", "stop_event": "Local\\DeskKit.shelltest-window.stop" },
  "actions": [], "autostart": { "default": false, "managed_by": "shell" }, "icon": null,
  "data_dir": "%APPDATA%\\DeskKit\\shelltest-window", "settings_path": "{data_dir}\\settings.json",
  "settings_action": null, "launcher": null, "helpers": [], "licence": "MIT", "homepage": null
}
'@ | Set-Content -Encoding ASCII (Join-Path $st 'module.json')
$stMutex = 'Local\DeskKit.shelltest-window'

# Echo fixture: prints every argument it gets (for --run <id> -- <args>).
$se = Join-Path $mods 'shelltest-echo'
New-Item -ItemType Directory -Force -Path (Join-Path $se 'src') | Out-Null
Copy-Item (Join-Path $here 'tests\fixtures\echo.ps1') (Join-Path $se 'src\echo.ps1')
$j = Get-Content (Join-Path $st 'module.json') -Raw | ConvertFrom-Json
$j.id = 'shelltest-echo'; $j.name = 'Shell Test Echo'; $j.description = 'Launch-test module: prints its arguments.'
$j.entry = 'src/echo.ps1'; $j.args = @('fixed'); $j.run_mode = 'oneshot'
$j.instance = [pscustomobject]@{ single = $false; mutex = $null; show_event = $null; stop_event = $null }
$j.data_dir = '%APPDATA%\DeskKit\shelltest-echo'
$j | ConvertTo-Json -Depth 5 | Set-Content -Encoding ASCII (Join-Path $se 'module.json')

# Python: the template, and RegionShot (opt-in). Cable Listen is never started (CLAUDE.md "Audio safety").
$pythonFound = [bool]((Get-Command py.exe -ErrorAction SilentlyContinue) -or (Get-Command python.exe -ErrorAction SilentlyContinue | Where-Object { $_.Source -notmatch 'WindowsApps' }))
Copy-Item -Recurse (Join-Path $root 'modules\_template-python') (Join-Path $mods 'template-python')
$rsSkip = $null
if (-not $RegionShot) { $rsSkip = 'not requested (-RegionShot)' }
elseif (-not $pythonFound) { $rsSkip = 'Python not found' }
elseif (MutexExists 'Local\DeskKit.regionshot') { $rsSkip = 'a RegionShot copy already holds its mutex' }
elseif (Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'region_shot\.py|RegionShot\.bat' -and $_.ProcessId -ne $PID }) { $rsSkip = 'an old RegionShot copy runs' }
if (-not $rsSkip) {
  Copy-Item -Recurse (Join-Path $root 'modules\regionshot') (Join-Path $mods 'regionshot')
  $env:DESKKIT_NO_MIGRATE = '1'   # never read the user's old RegionShot registry settings
}

$shell = $null
try {
  # ---- Start the tray ----
  $shell = Start-Process $exe -ArgumentList @('--modules-dir', "`"$mods`"", '--show') -PassThru
  $shellPid = $shell.Id
  Check 'tray: log says the tray icon was created' (WaitFor { (ReadShared $shellLog) -match "\[$shellPid\] tray icon created" } 20) (ReadShared $shellLog)
  Check 'tray: process is running' (-not $shell.HasExited)
  Check 'tray: shell mutex exists' (MutexExists 'Local\DeskKit.Shell')

  $second = RunShell @('--modules-dir', $mods) 15
  Check 'single instance: a second copy exits at once with 0' ($second.Code -eq 0) "code $($second.Code)"
  Check 'single instance: the first copy still runs' (-not $shell.HasExited)

  # ---- Main window through UI Automation ----
  $rootEl = [Windows.Automation.AutomationElement]::RootElement
  $winCond = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $shellPid)),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'DeskKit')))
  $win = $null
  [void](WaitFor { $script:win = $rootEl.FindFirst('Children', $winCond); $null -ne $script:win } 15)
  Check 'window: main window is open' ($null -ne $win)
  function ById([string]$id) {
    $win.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
  }
  function Item([string]$name) {
    $win.FindFirst('Descendants', (New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))))
  }
  function Click([string]$id) { (ById $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  function SelectRow([string]$name) { (Item $name).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300 }

  if ($win) {
    $items = $win.FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition(
      [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))) | ForEach-Object { $_.Current.Name }
    Write-Output "window rows: $($items -join ', ')"
    foreach ($n in 'Hello', 'Hello (Python)', 'Eye Saver', 'Snap Layouts', 'Shell Test Window', 'Shell Test Echo') { Check "window: row '$n'" ($items -contains $n) }
    SelectRow 'Snap Layouts'
    $problem = (ById 'lblProblem').Current.Name
    Check 'window: Snap Layouts shows a clear problem line' ($problem -match 'Not installed') $problem

    # Tray-driven start and stop of the window fixture (the shell owns this process).
    SelectRow 'Shell Test Window'
    Check 'window: main button says Open' ((ById 'btnMain').Current.Name -eq 'Open') (ById 'btnMain').Current.Name
    Click 'btnMain'
    Check 'tray start: fixture mutex appears' (WaitFor { MutexExists $stMutex } 20)
    Check 'tray start: shell log records the start' (WaitFor { (ReadShared $shellLog) -match 'start shelltest-window' } 5)
    Start-Sleep -Seconds 2.5
    Check 'tray start: main button now says Close' ((ById 'btnMain').Current.Name -eq 'Close') (ById 'btnMain').Current.Name
    Click 'btnStop'
    Check 'tray stop: fixture mutex is gone' (WaitFor { -not (MutexExists $stMutex) } 15)
    Check 'tray stop: fixture saw its stop event' (WaitFor { (ReadShared (Join-Path $logs 'shelltest-window.log')) -match 'stop received' } 5)

    # Tray-driven run of the template (oneshot, console:true).
    SelectRow 'Hello'
    Click 'btnMain'
    Check 'tray run: template wrote its settings in the throwaway data dir' (WaitFor { Test-Path (Join-Path $env:APPDATA 'DeskKit\template\settings.json') } 20)
    Remove-Item (Join-Path $env:APPDATA 'DeskKit\template') -Recurse -Force -ErrorAction SilentlyContinue

    # Autostart: tick the checkbox for the fixture; the shell login entry turns on with it.
    SelectRow 'Shell Test Window'
    (ById 'chkAutostart').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 500
    $state = ReadShared (Join-Path $env:APPDATA 'DeskKit\shell\state.json')
    Check 'autostart: state.json records the module on' ($state -match '"shelltest-window"' -and $state -match '"autostart": true') $state
    $runVal = (Get-ItemProperty -Path "HKCU:\$($env:DESKKIT_RUN_KEY)" -Name DeskKit -ErrorAction SilentlyContinue).DeskKit
    Check 'autostart: shell Run value DeskKit written (test key)' ($runVal -like "*DeskKit.exe*--modules-dir*") $runVal

    # Real Python window modules, started and stopped from the main window (the shell owns the process).
    function TrayStartStop([string]$row, [string]$id, [string]$mutex, [int]$startSec) {
      SelectRow $row
      Click 'btnMain'
      $up = WaitFor { MutexExists $mutex } $startSec
      Check "${id}: started from the main window (mutex appears)" $up (ReadShared (Join-Path $logs "$id.log"))
      if ($up) {
        Start-Sleep -Seconds 3
        Check "${id}: state says running" ((ById 'lblTitle').Current.Name -match ': running$') (ById 'lblTitle').Current.Name
        Click 'btnStop'
        Check "${id}: stopped from the main window (mutex gone)" (WaitFor { -not (MutexExists $mutex) } 15)
      }
      $log = ReadShared (Join-Path $logs "$id.log")
      Check "${id}: no ERROR line in its log" ($log -notmatch '(?m)^ERROR:') $log
    }
    if ($rsSkip) { Write-Output "SKIP  regionshot: $rsSkip" }
    else {
      Write-Output 'regionshot: first start builds its venv in the temp LOCALAPPDATA (can take minutes)...'
      TrayStartStop 'RegionShot' 'regionshot' 'Local\DeskKit.regionshot' 900
    }
  }

  # ---- Command line ----
  $list = RunShell @('--list', '--modules-dir', $mods)
  Write-Output $list.Out
  Check '--list: exit 0' ($list.Code -eq 0)
  foreach ($id in 'template', 'eyesaver', 'regioncontrol', 'shelltest-window') { Check "--list: row $id" ($list.Out -match "(?m)^$id\s") }

  $r = RunShell @('--run', 'template', '--modules-dir', $mods)
  Check '--run template: exit 0 at once' ($r.Code -eq 0) "code $($r.Code) $($r.Err)"
  Check '--run template: module ran (settings.json in throwaway data dir)' (WaitFor { Test-Path (Join-Path $env:APPDATA 'DeskKit\template\settings.json') } 20)

  $r = RunShell @('--run', 'eyesaver', '--action', 'status', '--modules-dir', $mods)
  Check '--run eyesaver --action status: exit 0 at once' ($r.Code -eq 0) "code $($r.Code) $($r.Err)"
  $esLog = Join-Path $logs 'eyesaver.log'
  $esDone = WaitFor { $t = ReadShared $esLog; ($t -match 'start status') -and (($t -match '0xD6') -or ($t -match 'ERROR')) } 40
  Check 'eyesaver: status output captured in its log' $esDone
  Write-Output "---- eyesaver.log ----"; Write-Output (ReadShared $esLog); Write-Output "----------------------"

  $r = RunShell @('--run', 'nosuch', '--modules-dir', $mods)
  Check '--run unknown id: exit 1 with an ERROR line' ($r.Code -eq 1 -and $r.Err -match 'ERROR: DeskKit has no module') "code $($r.Code) $($r.Err)"
  $r = RunShell @('--run', 'eyesaver', '--action', 'nosuch', '--modules-dir', $mods)
  Check '--run unknown action: exit 1' ($r.Code -eq 1 -and $r.Err -match 'has no action') "code $($r.Code) $($r.Err)"

  $r = RunShell @('--run', 'shelltest-window', '--modules-dir', $mods)
  Check '--run shelltest-window: exit 0' ($r.Code -eq 0)
  Check '--run shelltest-window: mutex appears' (WaitFor { MutexExists $stMutex } 20)
  $r = RunShell @('--stop', 'shelltest-window', '--modules-dir', $mods)
  Check '--stop shelltest-window: exit 0 and the mutex is gone' ($r.Code -eq 0 -and -not (MutexExists $stMutex)) "code $($r.Code) $($r.Err)"

  # ---- --run <id> -- <args> (pass-through) ----
  $echoLog = Join-Path $logs 'shelltest-echo.log'
  $r = RunShell @('--modules-dir', $mods, '--run', 'shelltest-echo', '--', 'Tools', 'two words', '-flag', '{data_dir}', '--list')
  Check '--run shelltest-echo -- ...: exit 0' ($r.Code -eq 0) "code $($r.Code) $($r.Err)"
  [void](WaitFor { (ReadShared $echoLog) -match 'argc=' } 20)
  $got = @((ReadShared $echoLog) -split "`r?`n" | Where-Object { $_ -match '^argv\[' } | ForEach-Object { $_ -replace '^argv\[\d+\]=<(.*)>$', '$1' })
  Check '--run -- : module args then extra args, each exact' (($got -join '|') -eq 'fixed|Tools|two words|-flag|{data_dir}|--list') ($got -join '|')
  $r = RunShell @('--modules-dir', $mods, '--run', 'shelltest-echo', '--action', 'x', '--', 'y')
  Check '--run with --action and -- : refused with exit 2' ($r.Code -eq 2 -and $r.Err -match 'do not combine') "code $($r.Code) $($r.Err)"

  # ---- Python through the shell ----
  if ($pythonFound) {
    $tp = Join-Path $env:APPDATA 'DeskKit\template-python\settings.json'
    Check '--list: template-python status ok' ($list.Out -match '(?m)^template-python\s.*\sok\s*$') $list.Out
    $r = RunShell @('--run', 'template-python', '--modules-dir', $mods)
    Check '--run template-python: exit 0' ($r.Code -eq 0) "code $($r.Code) $($r.Err)"
    Check '--run template-python: it ran (settings.json in throwaway data dir)' (WaitFor { Test-Path $tp } 30)
    $r = RunShell @('--run', 'template-python', '--modules-dir', $mods, '--', 'Nate')
    Check '--run template-python -- Nate: exit 0 and logged' ($r.Code -eq 0 -and (WaitFor { (ReadShared (Join-Path $logs 'template-python.log')) -match 'start Nate ===' } 5)) "code $($r.Code)"
    $savedPath = $env:PATH
    try {
      $env:PATH = Join-Path $env:SystemRoot 'System32'   # no Python, no py launcher
      $msg = 'Python 3.11 or newer not found. Install it from https://www.python.org/downloads/windows/ and try again.'
      $l2 = RunShell @('--list', '--modules-dir', $mods)
      Check 'no Python: --list shows the exact message for template-python' ($l2.Out -match ('(?m)^template-python\s.*' + [regex]::Escape($msg))) $l2.Out
      $r = RunShell @('--run', 'template-python', '--modules-dir', $mods)
      Check 'no Python: --run exits 4 with the exact ERROR line' ($r.Code -eq 4 -and $r.Err.Trim() -eq "ERROR: $msg") "code $($r.Code) err $($r.Err)"
    }
    finally { $env:PATH = $savedPath }
  }
  else { Write-Output 'SKIP  Python tests: Python not found on this PC' }
  # ---- Close the shell, restart it: the autostart module starts ----
  $r = RunShell @('--exit')
  Check '--exit: exit 0' ($r.Code -eq 0)
  Check '--exit: the shell we started has exited' ($shell.WaitForExit(10000))
  Check 'exit: the shell mutex is gone' (-not (MutexExists 'Local\DeskKit.Shell'))

  $shell = Start-Process $exe -ArgumentList @('--modules-dir', "`"$mods`"") -PassThru
  Check 'autostart: restarted shell starts shelltest-window' (WaitFor { MutexExists $stMutex } 25) (ReadShared $shellLog)
  Check 'autostart: shell log records it' ((ReadShared $shellLog) -match 'autostart shelltest-window: started')
  $r = RunShell @('--stop', 'shelltest-window', '--modules-dir', $mods)
  Check 'autostart: module stopped again' ($r.Code -eq 0 -and -not (MutexExists $stMutex))
  $r = RunShell @('--exit')
  Check 'final: the shell we started has exited' ($shell.WaitForExit(10000))
}
finally {
  if ($shell -and -not $shell.HasExited) { Write-Output "cleanup: closing the test shell (pid $($shell.Id))"; $shell.Kill() }
  # Any process still running from the temp modules dir was started by this test: stop it.
  Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($tmp) } | ForEach-Object {
    Write-Output "cleanup: stopping leftover test process $($_.ProcessId) $($_.Name)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
  }
  Remove-Item -Path 'HKCU:\Software\DeskKit-LaunchTest' -Recurse -ErrorAction SilentlyContinue
  if (-not $Keep) { Start-Sleep -Milliseconds 500; Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}
Write-Output "$($script:pass) passed, $($script:fail) failed"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
