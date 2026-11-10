<#
.SYNOPSIS
  Tests the suite installer. Never installs Region Control and never touches a real DeskKit install.
  1. Builds the installer with what exists (build-suite.ps1) and checks it compiled.
  2. Silent install into a temp dir with every component except regioncontrol.
  3. Checks the files and the uninstaller, then uninstalls silently and checks the dir is clean.
#>
[CmdletBinding()]
param([switch]$SkipBuild, [string]$Root)
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = (Resolve-Path $Root).Path
$dist = Join-Path $Root 'dist'
$fails = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "PASS  $what" -ForegroundColor Green } else { Write-Host "FAIL  $what" -ForegroundColor Red; $fails.Add($what) }
}

if (-not $SkipBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-suite.ps1') -Root $Root
    if ($LASTEXITCODE -ne 0) { Write-Host 'FAIL  build-suite.ps1 failed'; exit 1 }
}
$suite = Get-Content (Join-Path $Root 'suite.json') -Raw | ConvertFrom-Json
$setup = Join-Path $dist "DeskKit-Setup-$($suite.version).exe"
Check (Test-Path $setup) "installer exists: $setup"
if (-not (Test-Path $setup)) { exit 1 }
$sums = Get-Content (Join-Path $dist 'SHA256SUMS.txt') -Raw
Check ($sums -match (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()) 'SHA256SUMS.txt matches the installer'

$stage = Join-Path $dist 'stage'
$ids = @(Get-ChildItem (Join-Path $stage 'modules') -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
$ids = @($ids | Where-Object { $_ -ne 'regioncontrol' })
$hasShell = Test-Path (Join-Path $stage 'DeskKit.exe')
Write-Host "Staged modules to install: $($ids -join ', '); shell: $hasShell"

# Refuse when a real DeskKit install exists: the fixed AppId would overwrite its uninstall entry.
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{60B9D8BF-5C25-489A-B8BA-99D41F974319}_is1'
if ((Test-Path $key) -or (Test-Path ($key -replace 'HKCU:', 'HKLM:'))) {
    Write-Host 'SKIP  a real DeskKit install exists; the silent install test is skipped so it is not overwritten.' -ForegroundColor Yellow
    if ($fails.Count) { exit 1 } else { exit 0 }
}

# The shell tasks write the real HKCU Run value DeskKit and Start Menu / Desktop shortcuts. Refuse if a real one exists.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$lnkStart = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskKit.lnk'
$lnkDesk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeskKit.lnk'
$shellTasks = $false
if ($hasShell) {
    $busy = (Get-ItemProperty $runKey -Name DeskKit -ErrorAction SilentlyContinue) -or (Test-Path $lnkStart) -or (Test-Path $lnkDesk) -or (Get-Process DeskKit -ErrorAction SilentlyContinue)
    if ($busy) { Write-Host 'SKIP  shell tasks: a DeskKit Run value, shortcut or process already exists; not touching it.' -ForegroundColor Yellow }
    else { $shellTasks = $true }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("deskkit-installtest-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$dir = Join-Path $tmp 'DeskKit'
New-Item -ItemType Directory -Force $tmp | Out-Null
$log = Join-Path $tmp 'install.log'
# Defect 11: run the install and every launched exe with a temp APPDATA and LOCALAPPDATA.
function Get-LogStamp([string]$f) { if (Test-Path $f) { $i = Get-Item $f; "$($i.Length)|$($i.LastWriteTimeUtc.Ticks)|$((Get-FileHash $f -Algorithm SHA256).Hash)" } else { 'absent' } }
$realShellLog = Join-Path $env:APPDATA 'DeskKit\shell\shell.log'
$realLogBefore = Get-LogStamp $realShellLog
$savedAppData = $env:APPDATA; $savedLocal = $env:LOCALAPPDATA
$env:APPDATA = Join-Path $tmp 'appdata'; $env:LOCALAPPDATA = Join-Path $tmp 'localappdata'
New-Item -ItemType Directory -Force $env:APPDATA, $env:LOCALAPPDATA | Out-Null
try {
    $comps = $ids -join ','
    $p = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=$dir", "/LOG=$log", "/COMPONENTS=$comps", "/TASKS=$(if ($shellTasks) { 'startup,desktopicon' } else { '' })") -Wait -PassThru
    Check ($p.ExitCode -eq 0) "silent install exit code 0 (got $($p.ExitCode))"
    Check (Test-Path (Join-Path $dir 'suite.json')) 'suite.json installed'
    Check (Test-Path (Join-Path $dir 'LICENSE')) 'LICENSE installed'
    if ($hasShell) { Check (Test-Path (Join-Path $dir 'DeskKit.exe')) 'DeskKit.exe installed' }
    foreach ($id in $ids) {
        $mj = Join-Path $dir "modules\$id\module.json"
        Check (Test-Path $mj) "module $id : module.json installed"
        if (Test-Path $mj) {
            $m = Get-Content $mj -Raw | ConvertFrom-Json
            Check (Test-Path (Join-Path $dir ("modules\$id\" + ($m.entry -replace '/', '\')))) "module $id : entry $($m.entry) installed"
        }
    }
    if ($shellTasks) {
        $exe = Join-Path $dir 'DeskKit.exe'
        $rv = (Get-ItemProperty $runKey -Name DeskKit -ErrorAction SilentlyContinue).DeskKit
        Check ($rv -eq ('"' + $exe + '"')) "login entry HKCU Run DeskKit = quoted install exe (got: $rv)"
        Check (Test-Path $lnkStart) 'Start Menu shortcut exists'
        Check (Test-Path $lnkDesk) 'Desktop shortcut exists'
        $ws = New-Object -ComObject WScript.Shell
        if (Test-Path $lnkStart) { Check ($ws.CreateShortcut($lnkStart).TargetPath -eq $exe) 'Start Menu shortcut targets the installed DeskKit.exe' }
        # Silent setup skips the "start DeskKit" step, so start the installed exe the way that step does and check the shell mutex.
        $proc = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru
        $up = $false
        for ($i = 0; $i -lt 20 -and -not $up; $i++) {
            Start-Sleep -Milliseconds 500
            try { $m = [Threading.Mutex]::OpenExisting('Local\DeskKit.Shell'); $m.Dispose(); $up = $true } catch {}
        }
        Check $up 'installed shell starts and holds Local\DeskKit.Shell'
        if ($proc -and -not $proc.HasExited) {   # stop only the process this test started
            Stop-Process -Id $proc.Id -Force; Start-Sleep -Seconds 1
        }
    }
    Check (-not (Test-Path (Join-Path $dir 'modules\regioncontrol'))) 'regioncontrol not installed'

    # Update support (contract 16): a silent re-install keeps a module the shell updated to a newer version,
    # starts the shell again, and the uninstall removes modules\_update.
    if ($ids.Count -gt 0) {
        $uid = $ids[0]
        $umj = Join-Path $dir "modules\$uid\module.json"
        Set-Content (Join-Path $dir "modules\$uid\NEW.txt") 'added by an update' -Encoding utf8   # uninstall must remove it (defect 8)
        $orig = Get-Content $umj -Raw
        $payloadVer = ($orig | ConvertFrom-Json).version
        Set-Content $umj ($orig -replace '"version"\s*:\s*"[^"]*"', '"version": "99.0.0"') -Encoding utf8 -NoNewline
        New-Item -ItemType Directory -Force (Join-Path $dir 'modules\_update\leftover.new') | Out-Null
        $p2 = Start-Process -FilePath $setup -ArgumentList @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=$dir", "/LOG=$(Join-Path $tmp 'upgrade.log')", "/COMPONENTS=$comps", '/TASKS=') -PassThru
        $p2.WaitForExit()   # not -Wait: that also waits for the shell the installer starts
        Check ($p2.ExitCode -eq 0) "silent upgrade exit code 0 (got $($p2.ExitCode))"
        Check ((Get-Content $umj -Raw | ConvertFrom-Json).version -eq '99.0.0') "upgrade kept newer installed module $uid (payload $payloadVer)"
        if ($hasShell) {
            $up2 = $false
            for ($i = 0; $i -lt 20 -and -not $up2; $i++) {
                Start-Sleep -Milliseconds 500
                try { $m = [Threading.Mutex]::OpenExisting('Local\DeskKit.Shell'); $m.Dispose(); $up2 = $true } catch {}
            }
            Check $up2 'silent upgrade started the shell again'
            Get-Process DeskKit -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
            Start-Sleep -Seconds 1
        }
    }
    $unins = Get-ChildItem $dir -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check ($null -ne $unins) 'uninstaller present'
    if ($unins) {
        # The uninstaller copies itself to temp and exits: the first process returns a meaningless code.
        # Wait for the install dir to go, then read the verdict from the log.
        $ulog = Join-Path $tmp 'uninstall.log'
        Start-Process -FilePath $unins.FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$ulog") -Wait | Out-Null
        $t0 = Get-Date
        while ((Test-Path $dir) -and ((Get-Date) - $t0).TotalSeconds -lt 30) { Start-Sleep -Milliseconds 500 }
        Start-Sleep -Seconds 1
        Check ((Test-Path $ulog) -and ((Get-Content $ulog -Raw) -match 'Uninstallation process succeeded')) 'uninstall log says it succeeded'
        $left = @(Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue)
        Check ($left.Count -eq 0) "install dir is empty after uninstall ($($left.Count) files left: $(($left | ForEach-Object Name) -join ', '))"
        Check (-not (Test-Path (Join-Path $dir 'modules'))) 'uninstall removed the modules folder, including a file an update added'
        Check (-not (Test-Path $key)) 'uninstall registry entry removed'
        if ($shellTasks) {
            Check (-not (Get-ItemProperty $runKey -Name DeskKit -ErrorAction SilentlyContinue)) 'login entry removed by uninstall'
            Check (-not (Test-Path $lnkStart) -and -not (Test-Path $lnkDesk)) 'shortcuts removed by uninstall'
        }
    }
} finally {
    $env:APPDATA = $savedAppData; $env:LOCALAPPDATA = $savedLocal
    Check ((Get-LogStamp $realShellLog) -eq $realLogBefore) 'real %APPDATA%\DeskKit\shell\shell.log did not change'
    if ($shellTasks) {   # never leave a test value or shortcut behind
        Remove-ItemProperty $runKey -Name DeskKit -ErrorAction SilentlyContinue
        Remove-Item $lnkStart, $lnkDesk -ErrorAction SilentlyContinue
    }
    if ($fails.Count -eq 0) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
    else { Write-Host "Logs kept in $tmp" }
}
if ($fails.Count) { Write-Host "`n$($fails.Count) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed." -ForegroundColor Green
