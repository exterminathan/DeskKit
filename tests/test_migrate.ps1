<#
  Tests tools/migrate-launchers.ps1 against a fake tree in %TEMP%. Never touches the real
  shortcuts, scripts, pinned folders, %APPDATA% or %LOCALAPPDATA%: the child runs get
  APPDATA, LOCALAPPDATA and USERPROFILE pointed into the temp tree.
  Run: powershell -NoProfile -ExecutionPolicy Bypass -File tests/test_migrate.ps1 [-Keep]
  Exit 0 = all pass.
#>
param([switch]$Keep)
$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent $PSScriptRoot
$Script = Join-Path $Repo 'tools\migrate-launchers.ps1'
$Root = Join-Path ([System.IO.Path]::GetTempPath()) ("dk-g6-migrate-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:Pass = 0; $script:Fail = 0

function Check([bool]$cond, [string]$what) {
    if ($cond) { $script:Pass++; Write-Output "PASS  $what" } else { $script:Fail++; Write-Output "FAIL  $what" }
}
function MkDir([string]$p) { New-Item -ItemType Directory -Path $p -Force | Out-Null; return $p }

# ---------- fake tree ----------
$FakeAppData = MkDir (Join-Path $Root 'profile\AppData\Roaming')
$FakeLocal = MkDir (Join-Path $Root 'profile\AppData\Local')
$FakeProfile = Join-Path $Root 'profile'
$Shortcuts = MkDir (Join-Path $Root 'Shortcuts')
$Scripts = MkDir (Join-Path $Root 'Scripts')
$Pinned = MkDir (Join-Path $FakeAppData 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar')
$StartMenu = MkDir (Join-Path $FakeAppData 'Microsoft\Windows\Start Menu\Programs')
$TG = MkDir (Join-Path $Root 'TaskbarGroups')
$Svv = MkDir (Join-Path $Root 'Tools\SoundVolumeView')
$OldRS = MkDir (Join-Path $Root 'My Software\RegionShot')
$OldMon = MkDir (Join-Path $FakeProfile 'Documents\GitHub\ClaudeGlobalSetup\tools\monitor')   # under the profile: tests the masking of arguments
$Install = Join-Path $FakeLocal 'Programs\DeskKit'
$BackupRoot = Join-Path $FakeProfile 'DeskKit-migration-backup'

$ico = Join-Path $Repo 'modules\regionshot\icon.ico'
$dummyExe = Join-Path $env:SystemRoot 'System32\whoami.exe'

# Old tools (only files; nothing runs).
Set-Content -LiteralPath (Join-Path $OldRS 'RegionShot.bat') -Value '@echo off' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $Scripts 'cable_listen.pyw') -Value '# old' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $Scripts 'cable_listen.json') -Value '{"source": "CABLE Output"}' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $OldMon 'monitor.ps1') -Value '# old' -Encoding ASCII
Copy-Item $dummyExe (Join-Path $TG 'TaskbarGroups.exe')
MkDir (Join-Path $TG 'config\Tools\icons') | Out-Null
MkDir (Join-Path $TG 'Shortcuts') | Out-Null
Copy-Item $ico (Join-Path $TG 'config\Tools\GroupIcon.ico')
[System.IO.File]::WriteAllBytes((Join-Path $TG 'config\Tools\icons\Doggosync Companion.ico'), [byte[]](0, 0, 1, 0, 9, 9, 9))
Copy-Item $dummyExe (Join-Path $Svv 'SoundVolumeView.exe')
Set-Content -LiteralPath (Join-Path $Svv 'readme.txt') -Value 'NirSoft readme' -Encoding ASCII
MkDir (Join-Path $Svv 'sub') | Out-Null
Set-Content -LiteralPath (Join-Path $Svv 'sub\x.txt') -Value 'nested' -Encoding ASCII

# .bat files copied from the real scripts folder when present (read only), else stand-ins.
$realScripts = Join-Path "$env:SystemDrive\" 'Scripts'
foreach ($b in 'region_shot.bat', 'ytdl.bat') {
    $src = Join-Path $realScripts $b
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $Scripts $b) }
    elseif ($b -eq 'region_shot.bat') { [System.IO.File]::WriteAllText((Join-Path $Scripts $b), "@echo off`r`nset `"APP=D:\Software\RegionShot\RegionShot.bat`"`r`ncall `"%APP%`" %*`r`n") }
    else { [System.IO.File]::WriteAllText((Join-Path $Scripts $b), "@echo off`r`necho ytdl`r`n") }
}

# Shortcuts, made through WScript.Shell.
$sh = New-Object -ComObject WScript.Shell
function New-Lnk([string]$path, [string]$target, [string]$lnkArgs, [string]$work, [string]$icon, [int]$style = 1, [string]$hotkey = '') {
    $l = $sh.CreateShortcut($path)
    $l.TargetPath = $target; $l.Arguments = $lnkArgs; $l.WorkingDirectory = $work
    if ($icon) { $l.IconLocation = $icon }
    $l.WindowStyle = $style
    if ($hotkey) { $l.Hotkey = $hotkey }
    $l.Save()
}
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
New-Lnk (Join-Path $Shortcuts 'Region Shot.lnk') (Join-Path $OldRS 'RegionShot.bat') '' $OldRS (Join-Path $Root 'Software\RegionShot\region_shot.ico,0') 7
New-Lnk (Join-Path $Shortcuts 'Cable Listen.lnk') $dummyExe ('"' + (Join-Path $Scripts 'cable_listen.pyw') + '"') $Scripts "$env:SystemRoot\System32\SndVol.exe,0"
New-Lnk (Join-Path $Shortcuts 'Eye Saver.lnk') $ps ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $OldMon 'monitor.ps1') + '" eyesaver toggle') $Shortcuts "$env:SystemRoot\System32\shell32.dll,255" 1 'Alt+Ctrl+E'
New-Lnk (Join-Path $Shortcuts 'Doggosync Companion.lnk') $dummyExe '' $Root ((Join-Path $TG 'config\Tools\icons\Doggosync Companion.ico') + ',0')
New-Lnk (Join-Path $Shortcuts 'Find.lnk') $dummyExe '"finder.pyw"' $Root "$env:SystemRoot\System32\shell32.dll,23"
New-Lnk (Join-Path $Pinned 'Tools.lnk') (Join-Path $TG 'TaskbarGroups.exe') '"Tools"' "$TG\" ((Join-Path $TG 'config\Tools\GroupIcon.ico') + ',0')
New-Lnk (Join-Path $Pinned 'Notepad.lnk') "$env:SystemRoot\System32\notepad.exe" '' '' ''
New-Lnk (Join-Path $StartMenu 'TaskbarGroups.lnk') (Join-Path $TG 'TaskbarGroups.exe') '' $TG ((Join-Path $TG 'TaskbarGroups.exe') + ',0')
New-Lnk (Join-Path $TG 'Shortcuts\Tools.lnk') (Join-Path $TG 'TaskbarGroups.exe') '"Tools"' "$TG\" ((Join-Path $TG 'config\Tools\GroupIcon.ico') + ',0')

# Fake install: DeskKit.exe plus the module.json files (and the files they name for icons).
function Make-Install([string]$dir, [string[]]$ids) {
    MkDir $dir | Out-Null
    Copy-Item $dummyExe (Join-Path $dir 'DeskKit.exe')
    foreach ($id in $ids) {
        $m = MkDir (Join-Path $dir "modules\$id")
        Copy-Item (Join-Path $Repo "modules\$id\module.json") $m
        if ($id -eq 'regionshot') { Copy-Item $ico $m }
        if ($id -eq 'taskbargroups') { MkDir (Join-Path $m 'bin') | Out-Null; Copy-Item $dummyExe (Join-Path $m 'bin\TaskbarGroups.exe') }
    }
}

# ---------- helpers ----------
function Snapshot([string[]]$dirs) {
    $h = @{}
    foreach ($d in $dirs) {
        if (-not (Test-Path -LiteralPath $d)) { continue }
        foreach ($f in Get-ChildItem -LiteralPath $d -Recurse -File -Force) {
            if ($f.FullName.StartsWith($BackupRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }
            # powershell.exe itself writes a startup cache under LOCALAPPDATA: not the script's doing.
            if ($f.FullName -like '*\Microsoft\Windows\PowerShell\*') { continue }
            $h[$f.FullName] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash + '|' + $f.Length
        }
    }
    return $h
}
function Same-Snapshot($a, $b) {
    if ($a.Count -ne $b.Count) { return $false }
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k) -or $b[$k] -ne $a[$k]) { return $false } }
    return $true
}
function Diff-Snapshot($a, $b) {
    $d = @()
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { $d += "gone: $k" } elseif ($b[$k] -ne $a[$k]) { $d += "changed: $k" } }
    foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k)) { $d += "new: $k" } }
    return $d
}

function Run-Migrate([string[]]$extra, [string]$installDir = $Install) {
    $saved = @{ APPDATA = $env:APPDATA; LOCALAPPDATA = $env:LOCALAPPDATA; USERPROFILE = $env:USERPROFILE }
    $env:APPDATA = $FakeAppData; $env:LOCALAPPDATA = $FakeLocal; $env:USERPROFILE = $FakeProfile
    try {
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script,
            '-InstallDir', $installDir, '-ShortcutsDir', $Shortcuts, '-ScriptsDir', $Scripts,
            '-PinnedDir', "$Pinned,$StartMenu", '-SoundVolumeViewDir', $Svv, '-BackupRoot', $BackupRoot) + $extra
        $text = & powershell.exe @argList 2>&1 | Out-String
        $code = $LASTEXITCODE
    } finally {
        $env:APPDATA = $saved.APPDATA; $env:LOCALAPPDATA = $saved.LOCALAPPDATA; $env:USERPROFILE = $saved.USERPROFILE
    }
    return [pscustomobject]@{ Code = $code; Text = $text }
}
function Lnk([string]$p) { return $sh.CreateShortcut($p) }

$Watch = @($Shortcuts, $Scripts, $Pinned, $StartMenu, $TG, $Svv, $FakeAppData, $FakeLocal)
$IconsDir = Join-Path $FakeAppData 'DeskKit\taskbargroups\icons'
$HelperDir = Join-Path $FakeLocal 'DeskKit\helpers\soundvolumeview'
$Exe = Join-Path $Install 'DeskKit.exe'

try {
    # ---------- 1. dry run with no install: nothing changes ----------
    $before = Snapshot $Watch
    $tgBefore = Snapshot @($TG)
    $r = Run-Migrate @()
    Check ($r.Code -eq 0) "dry run exits 0 (got $($r.Code))"
    Check (Same-Snapshot $before (Snapshot $Watch)) 'dry run changes no file, byte for byte'
    Check (-not (Test-Path -LiteralPath $BackupRoot)) 'dry run makes no backup folder'
    Check ($r.Text -match 'DRY RUN') 'dry run says DRY RUN'
    Check ($r.Text -match 'NOT FOUND') 'dry run warns that DeskKit.exe is missing'
    foreach ($exp in 'CHANGE\s+regionshot\s+lnk', 'CHANGE\s+cablelisten\s+lnk', 'CHANGE\s+eyesaver\s+lnk', 'CHANGE\s+taskbargroups\s+icon', 'CHANGE\s+regionshot\s+bat', 'SKIP\s+taskbargroups\s+lnk', 'LEFT ALONE\s+taskbargroups', 'CHANGE\s+cablelisten\s+helper', 'CHANGE\s+cablelisten\s+settings') {
        Check ($r.Text -match $exp) "dry run lists /$exp/"
    }
    Check ($r.Text -match '--run taskbargroups -- Tools') 'dry run shows the Tools group arguments'
    Check ($r.Text -match '--run eyesaver --action toggle') 'dry run shows the Eye Saver toggle action'
    Check ($r.Text -match 'Find\.lnk') 'dry run lists Find.lnk as not matched'
    Check ($r.Text -notmatch [regex]::Escape($FakeProfile)) 'dry run masks the profile path'

    # ---------- 2. refusals ----------
    $r = Run-Migrate @('-Apply')
    Check ($r.Code -eq 2) "-Apply refuses with no DeskKit.exe (exit $($r.Code))"
    Check ($r.Text -match 'DeskKit\.exe not found') '-Apply says DeskKit.exe not found'
    Check (Same-Snapshot $before (Snapshot $Watch)) 'refusal changes nothing'
    Check (-not (Test-Path -LiteralPath $BackupRoot)) 'refusal makes no backup folder'

    $partial = Join-Path $Root 'PartialInstall'
    Make-Install $partial @('regionshot', 'eyesaver', 'taskbargroups')
    $r = Run-Migrate @('-Apply') $partial
    Check ($r.Code -eq 2 -and $r.Text -match 'not installed.*cablelisten') "-Apply refuses when a module is missing (exit $($r.Code))"
    Check (Same-Snapshot $before (Snapshot $Watch)) 'missing-module refusal changes nothing'

    $r = Run-Migrate @('-Only', 'nosuchmodule')
    Check ($r.Code -eq 2) "-Only with an unknown id is refused (exit $($r.Code))"

    # ---------- 3. -Only filter (dry run) ----------
    Make-Install $Install @('regionshot', 'cablelisten', 'eyesaver', 'taskbargroups')
    $before = Snapshot $Watch
    $r = Run-Migrate @('-Only', 'eyesaver,regionshot')
    Check ($r.Code -eq 0 -and $r.Text -match 'eyesaver' -and $r.Text -match 'regionshot\s+bat' -and $r.Text -notmatch 'CHANGE\s+cablelisten' -and $r.Text -cnotmatch 'taskbargroups\s') '-Only limits the rows to the named modules'

    # ---------- 4. -Apply without -IncludeTaskbarPins ----------
    $pinBefore = Snapshot @($Pinned, $StartMenu)
    $r = Run-Migrate @('-Apply')
    Check ($r.Code -eq 0) "-Apply exits 0 (got $($r.Code))"
    if ($r.Code -ne 0) { Write-Output $r.Text }
    Check (Same-Snapshot $pinBefore (Snapshot @($Pinned, $StartMenu))) '-Apply without -IncludeTaskbarPins leaves pinned and Start Menu entries'
    Check (Same-Snapshot $tgBefore (Snapshot @($TG))) 'old TaskbarGroups folder unchanged'

    $l = Lnk (Join-Path $Shortcuts 'Region Shot.lnk')
    Check ($l.TargetPath -ieq $Exe) 'Region Shot.lnk targets DeskKit.exe'
    Check ($l.Arguments -eq '--run regionshot') "Region Shot.lnk arguments ($($l.Arguments))"
    Check ($l.WorkingDirectory -ieq $Install) 'Region Shot.lnk working folder is the install dir'
    Check ($l.IconLocation -ieq ((Join-Path $Install 'modules\regionshot\icon.ico') + ',0')) "Region Shot.lnk icon is the module icon.ico ($($l.IconLocation))"
    Check ($l.WindowStyle -eq 7) 'Region Shot.lnk keeps its window style'

    $l = Lnk (Join-Path $Shortcuts 'Cable Listen.lnk')
    Check ($l.TargetPath -ieq $Exe -and $l.Arguments -eq '--run cablelisten') "Cable Listen.lnk -> DeskKit.exe --run cablelisten ($($l.Arguments))"
    Check ($l.IconLocation -ieq "$env:SystemRoot\System32\SndVol.exe,0") "Cable Listen.lnk keeps its old icon ($($l.IconLocation))"

    $l = Lnk (Join-Path $Shortcuts 'Eye Saver.lnk')
    Check ($l.TargetPath -ieq $Exe -and $l.Arguments -eq '--run eyesaver --action toggle') "Eye Saver.lnk -> --run eyesaver --action toggle ($($l.Arguments))"
    Check ($l.IconLocation -ieq "$env:SystemRoot\System32\shell32.dll,255") "Eye Saver.lnk keeps its old icon ($($l.IconLocation))"
    Check ($l.Hotkey -eq 'Alt+Ctrl+E') "Eye Saver.lnk keeps its hotkey ($($l.Hotkey))"

    $l = Lnk (Join-Path $Shortcuts 'Doggosync Companion.lnk')
    $dogCopy = Join-Path $IconsDir 'Doggosync Companion.ico'
    Check ($l.TargetPath -ieq $dummyExe) 'Doggosync Companion.lnk keeps its target'
    Check ($l.IconLocation -ieq "$dogCopy,0") "Doggosync Companion.lnk icon points at the copy ($($l.IconLocation))"
    Check ((Test-Path -LiteralPath $dogCopy) -and (Get-FileHash $dogCopy).Hash -eq (Get-FileHash (Join-Path $TG 'config\Tools\icons\Doggosync Companion.ico')).Hash) 'Doggosync icon copy is byte-identical'

    $l = Lnk (Join-Path $Shortcuts 'Find.lnk')
    Check ($l.TargetPath -ieq $dummyExe -and $l.Arguments -eq '"finder.pyw"') 'Find.lnk (not matched) unchanged'

    $batPath = Join-Path $Scripts 'region_shot.bat'
    $bat = [System.IO.File]::ReadAllBytes($batPath)
    $expBat = [System.Text.Encoding]::ASCII.GetBytes("@echo off`r`n`"%LOCALAPPDATA%\Programs\DeskKit\DeskKit.exe`" --run regionshot -- %*`r`n")
    Check ([Convert]::ToBase64String($bat) -eq [Convert]::ToBase64String($expBat)) 'region_shot.bat is the 2-line CRLF forwarder'
    Check ((Get-FileHash (Join-Path $Scripts 'ytdl.bat')).Hash -eq ($before[(Join-Path $Scripts 'ytdl.bat')] -split '\|')[0]) 'ytdl.bat (not matched) unchanged'

    $helperOk = (Test-Path -LiteralPath (Join-Path $HelperDir 'SoundVolumeView.exe')) -and (Test-Path -LiteralPath (Join-Path $HelperDir 'sub\x.txt'))
    if ($helperOk) { foreach ($f in Get-ChildItem -LiteralPath $Svv -Recurse -File) { $to = Join-Path $HelperDir $f.FullName.Substring($Svv.Length + 1); if ((Get-FileHash $to).Hash -ne (Get-FileHash $f.FullName).Hash) { $helperOk = $false } } }
    Check $helperOk 'SoundVolumeView folder copied whole and unchanged'
    $cj = Join-Path $FakeAppData 'DeskKit\cablelisten\cable_listen.json'
    Check ((Test-Path -LiteralPath $cj) -and (Get-FileHash $cj).Hash -eq (Get-FileHash (Join-Path $Scripts 'cable_listen.json')).Hash) 'cable_listen.json copied to the Cable Listen data dir'

    $backups = @(Get-ChildItem -LiteralPath $BackupRoot -Directory)
    Check ($backups.Count -eq 1) "one backup folder made ($($backups.Count))"
    $b1 = $backups[0].FullName
    Check ($backups[0].Name -match '^\d{8}-\d{6}$') "backup folder is named yyyyMMdd-HHmmss ($($backups[0].Name))"
    Check (Test-Path -LiteralPath (Join-Path $b1 'restore.ps1')) 'backup has restore.ps1'
    $relRS = (Join-Path $Shortcuts 'Region Shot.lnk') -replace '^([A-Za-z]):', '$1'
    Check (Test-Path -LiteralPath (Join-Path $b1 $relRS)) 'backup keeps the folder structure'
    Check ((Get-FileHash (Join-Path $b1 $relRS)).Hash -eq ($before[(Join-Path $Shortcuts 'Region Shot.lnk')] -split '\|')[0]) 'backup copy equals the original byte for byte'

    # ---------- 5. -Apply -IncludeTaskbarPins ----------
    Start-Sleep -Seconds 1   # a new backup folder needs a new timestamp
    $r = Run-Migrate @('-Apply', '-IncludeTaskbarPins')
    Check ($r.Code -eq 0) "-Apply -IncludeTaskbarPins exits 0 (got $($r.Code))"
    $l = Lnk (Join-Path $Pinned 'Tools.lnk')
    Check ($l.TargetPath -ieq $Exe -and $l.Arguments -eq '--run taskbargroups -- Tools') "pinned Tools.lnk -> --run taskbargroups -- Tools ($($l.Arguments))"
    Check ($l.IconLocation -ieq ((Join-Path $IconsDir 'GroupIcon.ico') + ',0')) "pinned Tools.lnk icon points at the copy ($($l.IconLocation))"
    Check ((Get-FileHash (Join-Path $IconsDir 'GroupIcon.ico')).Hash -eq (Get-FileHash (Join-Path $TG 'config\Tools\GroupIcon.ico')).Hash) 'GroupIcon copy is byte-identical'
    $l = Lnk (Join-Path $StartMenu 'TaskbarGroups.lnk')
    Check ($l.TargetPath -ieq $Exe -and $l.Arguments -eq '--run taskbargroups') "Start Menu TaskbarGroups.lnk -> --run taskbargroups ($($l.Arguments))"
    Check ($l.IconLocation -ieq ((Join-Path $Install 'modules\taskbargroups\bin\TaskbarGroups.exe') + ',0')) "Start Menu icon is the installed TaskbarGroups.exe ($($l.IconLocation))"
    $l = Lnk (Join-Path $Pinned 'Notepad.lnk')
    Check ($l.Arguments -eq '' -and $l.TargetPath -match 'notepad') 'unrelated pinned shortcut unchanged'
    Check (Same-Snapshot $tgBefore (Snapshot @($TG))) 'old TaskbarGroups folder still unchanged (Shortcuts\Tools.lnk left alone)'
    $backups = @(Get-ChildItem -LiteralPath $BackupRoot -Directory | Sort-Object Name)
    Check ($backups.Count -eq 2) "second backup folder made ($($backups.Count))"
    $b2 = $backups[1].FullName

    # ---------- 6. -Apply again: nothing changes ----------
    $afterApply = Snapshot $Watch
    Start-Sleep -Seconds 1
    $r = Run-Migrate @('-Apply', '-IncludeTaskbarPins')
    Check ($r.Code -eq 0 -and $r.Text -match 'Nothing to change') "third -Apply finds nothing to change (exit $($r.Code))"
    Check (Same-Snapshot $afterApply (Snapshot $Watch)) 'third -Apply changes no file'
    Check (@(Get-ChildItem -LiteralPath $BackupRoot -Directory).Count -eq 2) 'third -Apply makes no backup folder'
    Check ($r.Text -match 'DONE\s+regionshot\s+lnk' -and $r.Text -match 'DONE\s+cablelisten\s+helper') 'third run reports DONE rows'

    # ---------- 7. restore.ps1, newest first: back to the original bytes ----------
    foreach ($b in @($b2, $b1)) {
        $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $b 'restore.ps1') -RemoveCreated 2>&1 | Out-String
        Check ($LASTEXITCODE -eq 0) "restore.ps1 in $(Split-Path -Leaf $b) exits 0"
    }
    $restored = Snapshot $Watch
    $diff = Diff-Snapshot $before $restored
    Check ($diff.Count -eq 0) "after restore every file equals the original byte for byte ($($diff.Count) differences)"
    if ($diff.Count) { $diff | Select-Object -First 10 | ForEach-Object { Write-Output "      $_" } }
    Check (-not (Test-Path -LiteralPath $HelperDir)) 'restore -RemoveCreated removes the helper copy'
    Check (-not (Test-Path -LiteralPath (Join-Path $IconsDir 'GroupIcon.ico'))) 'restore -RemoveCreated removes the icon copies'

    # ---------- 8. helper target already there: not copied again ----------
    MkDir $HelperDir | Out-Null
    Copy-Item $dummyExe (Join-Path $HelperDir 'SoundVolumeView.exe')
    $r = Run-Migrate @('-Only', 'cablelisten')
    Check ($r.Text -match 'DONE\s+cablelisten\s+helper') 'helper already present: reported DONE, not copied'
} finally {
    if ($Keep) { Write-Output "Kept: $Root" } else { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output ''
Write-Output "$($script:Pass) pass, $($script:Fail) fail"
if ($script:Fail) { exit 1 }
exit 0
