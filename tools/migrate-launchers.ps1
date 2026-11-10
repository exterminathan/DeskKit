<#
.SYNOPSIS
  Repoints the old tools' shortcuts and PATH scripts to the installed DeskKit.

.DESCRIPTION
  Spec: docs/module-contract.md section 13.

  Dry run by default: prints every file it would change (current and new target,
  arguments, working folder and icon) and changes nothing. -Apply writes the changes,
  after it backs up every file it touches to
  %USERPROFILE%\DeskKit-migration-backup\<yyyyMMdd-HHmmss>\ with a restore.ps1.

  Pinned taskbar and Start Menu entries change only with -IncludeTaskbarPins.
  It never changes anything inside the old TaskbarGroups folder or any old tool folder.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/migrate-launchers.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/migrate-launchers.ps1 -Apply -IncludeTaskbarPins
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/migrate-launchers.ps1 -Only regionshot,eyesaver -Apply

  Exit codes: 0 = done (or dry run), 1 = a change failed (see the output), 2 = refused (bad parameter or missing install).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\DeskKit'),
    [string]$ShortcutsDir = (Join-Path "$env:SystemDrive\" 'Shortcuts'),
    [string]$ScriptsDir = (Join-Path "$env:SystemDrive\" 'Scripts'),
    [string[]]$PinnedDir = @(
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')
    ),
    # Old TaskbarGroups folder. Empty = take it from the folder of any shortcut that targets TaskbarGroups.exe.
    [string]$TaskbarGroupsDir = '',
    # Old SoundVolumeView folder that Cable Listen used. Copied, unchanged, to the DeskKit helpers folder.
    [string]$SoundVolumeViewDir = (Join-Path "$env:SystemDrive\" 'Tools\SoundVolumeView'),
    [string]$BackupRoot = (Join-Path $env:USERPROFILE 'DeskKit-migration-backup'),
    [string[]]$Only = @(),
    [switch]$Apply,
    [switch]$IncludeTaskbarPins
)

$ErrorActionPreference = 'Stop'
$KnownIds = @('regionshot', 'cablelisten', 'eyesaver', 'taskbargroups')

# ---------- parameters ----------

# -Only a,b arrives as one string through "powershell -File": split on commas.
$OnlyIds = @()
foreach ($o in $Only) { foreach ($p in ($o -split ',')) { $t = $p.Trim().ToLowerInvariant(); if ($t) { $OnlyIds += $t } } }
foreach ($id in $OnlyIds) {
    if ($KnownIds -notcontains $id) {
        Write-Output "ERROR: -Only '$id' is not a module this script migrates. Use: $($KnownIds -join ', ')."
        exit 2
    }
}
function In-Scope([string]$id) { return ($OnlyIds.Count -eq 0) -or ($OnlyIds -contains $id) }

function Full([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return '' }
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($p)).TrimEnd('\')
}
function Is-Under([string]$path, [string]$dir) {
    if (-not $path -or -not $dir) { return $false }
    $p = Full $path; $d = (Full $dir) + '\'
    return $p.StartsWith($d, [StringComparison]::OrdinalIgnoreCase)
}
function Same-Path([string]$a, [string]$b) {
    if (-not $a -or -not $b) { return $false }
    return [string]::Equals((Full $a), (Full $b), [StringComparison]::OrdinalIgnoreCase)
}

$InstallDir = Full $InstallDir
$DeskKitExe = Join-Path $InstallDir 'DeskKit.exe'
# Several pinned folders: an array, or one comma-separated string (the form "powershell -File" passes).
$PinnedDirs = @($PinnedDir | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { Full $_ })
$RepoModules = Join-Path (Split-Path -Parent $PSScriptRoot) 'modules'
$IconsDest = Join-Path $env:APPDATA 'DeskKit\taskbargroups\icons'
$HelperDest = Join-Path $env:LOCALAPPDATA 'DeskKit\helpers\soundvolumeview'
$CableData = Join-Path $env:APPDATA 'DeskKit\cablelisten'

# Show paths with %LOCALAPPDATA%, %APPDATA% and %USERPROFILE% so the output names no user.
function Show([string]$p) {
    if (-not $p) { return $p }
    foreach ($v in 'LOCALAPPDATA', 'APPDATA', 'USERPROFILE') {
        $root = [Environment]::GetEnvironmentVariable($v)
        if ($root) {
            $root = $root.TrimEnd('\')
            # Anywhere in the text (arguments hold paths too), only at a path boundary.
            $p = [regex]::Replace($p, [regex]::Escape($root) + '(?=\\|"|$|\s|,)', "%$v%", 'IgnoreCase')
        }
    }
    return $p
}

# ---------- module facts (installed copy first, the repo copy as a fallback for the dry run) ----------

$ModuleInfo = @{}
foreach ($id in $KnownIds) {
    $installed = Join-Path $InstallDir "modules\$id\module.json"
    $repo = Join-Path $RepoModules "$id\module.json"
    $info = @{ Installed = (Test-Path -LiteralPath $installed); Icon = $null; EntryLeaf = $null }
    $src = $null
    if ($info.Installed) { $src = $installed } elseif (Test-Path -LiteralPath $repo) { $src = $repo }
    if ($src) {
        $j = Get-Content -LiteralPath $src -Raw -Encoding UTF8 | ConvertFrom-Json
        $srcDir = Split-Path -Parent $src
        if ($j.icon -and (Test-Path -LiteralPath (Join-Path $srcDir $j.icon))) {
            $info.Icon = (Join-Path $InstallDir "modules\$id\$($j.icon)") -replace '/', '\'
        }
        if ($j.entry) {
            $info.EntryLeaf = Split-Path -Leaf $j.entry
            $info.Entry = (Join-Path $InstallDir "modules\$id\$($j.entry)") -replace '/', '\'
        }
    }
    $ModuleInfo[$id] = $info
}

# ---------- read launch points ----------

$Shell = New-Object -ComObject WScript.Shell
function Read-Lnk([string]$path) {
    $l = $Shell.CreateShortcut($path)
    return [pscustomobject]@{
        Target = [string]$l.TargetPath; Args = [string]$l.Arguments
        Work = [string]$l.WorkingDirectory; Icon = [string]$l.IconLocation
    }
}
function Icon-Path([string]$loc) {
    if (-not $loc) { return '' }
    $i = $loc.LastIndexOf(',')
    if ($i -ge 0 -and $loc.Substring($i + 1).Trim() -match '^-?\d+$') { return $loc.Substring(0, $i).Trim() }
    return $loc.Trim()
}
function Icon-Index([string]$loc) {
    $i = $loc.LastIndexOf(',')
    if ($i -ge 0 -and $loc.Substring($i + 1).Trim() -match '^-?\d+$') { return $loc.Substring($i + 1).Trim() }
    return '0'
}

$Folders = @()
if ($ShortcutsDir) { $Folders += [pscustomobject]@{ Dir = (Full $ShortcutsDir); Pinned = $false } }
foreach ($d in $PinnedDirs) { $Folders += [pscustomobject]@{ Dir = $d; Pinned = $true } }

$Links = @()
foreach ($f in $Folders) {
    if (-not (Test-Path -LiteralPath $f.Dir -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $f.Dir -Filter '*.lnk' -File | Sort-Object Name) {
        $Links += [pscustomobject]@{ Path = $file.FullName; Pinned = $f.Pinned; Lnk = (Read-Lnk $file.FullName) }
    }
}

if (-not $TaskbarGroupsDir) {
    foreach ($l in $Links) {
        if ($l.Lnk.Target -and (Split-Path -Leaf $l.Lnk.Target) -ieq 'TaskbarGroups.exe') { $TaskbarGroupsDir = Split-Path -Parent $l.Lnk.Target; break }
    }
}
if ($TaskbarGroupsDir) { $TaskbarGroupsDir = Full $TaskbarGroupsDir }

# ---------- plan ----------

$Plan = New-Object System.Collections.Generic.List[object]
$NotMatched = New-Object System.Collections.Generic.List[string]
$PlannedIconCopies = @{}   # source full path -> destination

function Plan-IconCopy([string]$src) {
    $key = (Full $src).ToLowerInvariant()
    if ($PlannedIconCopies.ContainsKey($key)) { return $PlannedIconCopies[$key] }
    $name = Split-Path -Leaf $src
    $base = [System.IO.Path]::GetFileNameWithoutExtension($name); $ext = [System.IO.Path]::GetExtension($name)
    $dest = Join-Path $IconsDest $name
    $srcHash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
    $taken = @($PlannedIconCopies.Values | ForEach-Object { $_.Dest.ToLowerInvariant() })
    for ($i = 2; ; $i++) {
        $exists = Test-Path -LiteralPath $dest
        if ($exists -and (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -eq $srcHash) { break }
        if (-not $exists -and ($taken -notcontains $dest.ToLowerInvariant())) { break }
        $dest = Join-Path $IconsDest "$base-$i$ext"
    }
    $copy = [pscustomobject]@{ Source = (Full $src); Dest = $dest; Needed = -not (Test-Path -LiteralPath $dest) }
    $PlannedIconCopies[$key] = $copy
    return $copy
}

# Decide the new icon of a shortcut. Returns @{ Icon; Copy; Note }.
function New-Icon([string]$id, [string]$oldLoc) {
    $r = @{ Icon = $oldLoc; Copy = $null; Note = '' }
    $oldPath = Icon-Path $oldLoc
    $idx = Icon-Index $oldLoc
    if ($id -and $ModuleInfo[$id].Icon) { $r.Icon = "$($ModuleInfo[$id].Icon),0"; $r.Note = 'module icon.ico'; return $r }
    if (-not $oldPath) { $r.Note = 'no icon before: Windows shows the DeskKit.exe icon'; return $r }
    $oldFull = Full $oldPath
    if ($TaskbarGroupsDir -and (Is-Under $oldFull $TaskbarGroupsDir)) {
        $tg = $ModuleInfo['taskbargroups']
        if ($tg.EntryLeaf -and ((Split-Path -Leaf $oldFull) -ieq $tg.EntryLeaf)) {
            $r.Icon = "$($tg.Entry),$idx"; $r.Note = 'icon of the installed TaskbarGroups.exe'; return $r
        }
        if (Test-Path -LiteralPath $oldFull) {
            $c = Plan-IconCopy $oldFull
            $r.Icon = "$($c.Dest),$idx"; $r.Copy = $c
            if ($c.Needed) { $r.Note = 'icon copied out of the old TaskbarGroups folder' } else { $r.Note = 'icon copy already there' }
            return $r
        }
        $r.Icon = "$DeskKitExe,0"; $r.Note = 'old icon missing: DeskKit.exe icon'; return $r
    }
    if (-not $id) { return $r }
    if (-not (Test-Path -LiteralPath $oldFull)) { $r.Icon = "$DeskKitExe,0"; $r.Note = 'old icon missing: DeskKit.exe icon'; return $r }
    $r.Note = 'old icon kept (module has no icon.ico)'
    return $r
}

function Quote-Arg([string]$a) { if ($a -match '[\s"]' -or $a -eq '') { return '"' + ($a -replace '"', '\"') + '"' } return $a }

function Add-Row($row) {
    if ($row.Status -eq 'change' -and $row.Pinned -and -not $IncludeTaskbarPins) {
        $row.Status = 'skip'; $row.Note = (@($row.Note, 'pinned or Start Menu entry: needs -IncludeTaskbarPins') | Where-Object { $_ }) -join '; '
    }
    if ($row.Status -eq 'change' -and $row.Module -and -not $ModuleInfo[$row.Module].Installed -and $row.Kind -ne 'icon') {
        $row.Note = (@($row.Note, 'module not installed yet') | Where-Object { $_ }) -join '; '
    }
    $Plan.Add($row)
}

foreach ($l in $Links) {
    $k = $l.Lnk
    $leaf = ''; if ($k.Target) { $leaf = Split-Path -Leaf $k.Target }
    $id = $null; $newArgs = $null

    if ($leaf -ieq 'DeskKit.exe' -and $k.Args -match '^\s*--run\s+([a-z0-9-]+)') {
        $did = $Matches[1]
        if (In-Scope $did) {
            Add-Row ([pscustomobject]@{ Kind = 'lnk'; Module = $did; Path = $l.Path; Pinned = $l.Pinned; Status = 'done'; Old = $k; New = $null; Copy = $null; Note = 'already points at DeskKit.exe' })
        }
        continue
    }
    if ($k.Target -match 'RegionShot\.bat' -or $k.Args -match 'RegionShot\.bat') { $id = 'regionshot'; $newArgs = '--run regionshot' }
    elseif ($k.Args -match 'cable_listen\.pyw') { $id = 'cablelisten'; $newArgs = '--run cablelisten' }
    elseif ($k.Args -match 'monitor\.ps1' -and $k.Args -match 'eyesaver') { $id = 'eyesaver'; $newArgs = '--run eyesaver --action toggle' }
    elseif ($leaf -ieq 'TaskbarGroups.exe') {
        $id = 'taskbargroups'
        $group = $k.Args.Trim().Trim('"').Trim()
        if ($group) { $newArgs = '--run taskbargroups -- ' + (Quote-Arg $group) } else { $newArgs = '--run taskbargroups' }
    }

    if ($id) {
        if (-not (In-Scope $id)) { continue }
        $ic = New-Icon $id $k.Icon
        $new = [pscustomobject]@{ Target = $DeskKitExe; Args = $newArgs; Work = $InstallDir; Icon = $ic.Icon }
        Add-Row ([pscustomobject]@{ Kind = 'lnk'; Module = $id; Path = $l.Path; Pinned = $l.Pinned; Status = 'change'; Old = $k; New = $new; Copy = $ic.Copy; Note = $ic.Note })
        continue
    }

    # Not a module launch point, but its icon lives in the old TaskbarGroups folder.
    $oldIconPath = Icon-Path $k.Icon
    if ($TaskbarGroupsDir -and $oldIconPath -and (Is-Under $oldIconPath $TaskbarGroupsDir)) {
        if (-not (In-Scope 'taskbargroups')) { continue }
        $ic = New-Icon $null $k.Icon
        $new = [pscustomobject]@{ Target = $k.Target; Args = $k.Args; Work = $k.Work; Icon = $ic.Icon }
        Add-Row ([pscustomobject]@{ Kind = 'icon'; Module = 'taskbargroups'; Path = $l.Path; Pinned = $l.Pinned; Status = 'change'; Old = $k; New = $new; Copy = $ic.Copy; Note = "icon only; $($ic.Note)" })
        continue
    }
    $NotMatched.Add($l.Path)
}

# Shortcuts inside the old TaskbarGroups folder: listed, never changed.
if ($TaskbarGroupsDir -and (In-Scope 'taskbargroups')) {
    $tgShortcuts = Join-Path $TaskbarGroupsDir 'Shortcuts'
    if (Test-Path -LiteralPath $tgShortcuts -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $tgShortcuts -Filter '*.lnk' -File | Sort-Object Name) {
            Add-Row ([pscustomobject]@{ Kind = 'lnk'; Module = 'taskbargroups'; Path = $file.FullName; Pinned = $false; Status = 'left alone'; Old = (Read-Lnk $file.FullName); New = $null; Copy = $null; Note = 'inside the old TaskbarGroups folder: never changed' })
        }
    }
}

# PATH forwarders (.bat / .cmd).
$DefaultInstall = Full (Join-Path $env:LOCALAPPDATA 'Programs\DeskKit')
if (Same-Path $InstallDir $DefaultInstall) { $BatExe = '%LOCALAPPDATA%\Programs\DeskKit\DeskKit.exe' } else { $BatExe = $DeskKitExe }
function Bat-Text([string]$id) { return "@echo off`r`n`"$BatExe`" --run $id -- %*`r`n" }
$BatEncoding = [System.Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)

if ($ScriptsDir -and (Test-Path -LiteralPath $ScriptsDir -PathType Container)) {
    foreach ($file in Get-ChildItem -LiteralPath $ScriptsDir -File | Where-Object { $_.Extension -in '.bat', '.cmd' } | Sort-Object Name) {
        $text = [System.IO.File]::ReadAllText($file.FullName, $BatEncoding)
        $old = [pscustomobject]@{ Target = ''; Args = ''; Work = ''; Icon = '' }
        if ($text -match 'DeskKit\.exe"?\s+--run\s+([a-z0-9-]+)') {
            $did = $Matches[1]
            if (In-Scope $did) { Add-Row ([pscustomobject]@{ Kind = 'bat'; Module = $did; Path = $file.FullName; Pinned = $false; Status = 'done'; Old = $old; New = $null; Copy = $null; Note = 'already forwards to DeskKit.exe' }) }
            continue
        }
        $id = $null
        if ($text -match 'RegionShot\.bat') { $id = 'regionshot' }
        if (-not $id) { $NotMatched.Add($file.FullName); continue }
        if (-not (In-Scope $id)) { continue }
        $m = [regex]::Match($text, '(?im)^\s*(?:set\s+"?APP=([^"\r\n]*)"?|call\s+"?([^"\r\n]*RegionShot\.bat)"?)')
        $oldTarget = ''; if ($m.Success) { $oldTarget = ($m.Groups[1].Value + $m.Groups[2].Value).Trim() }
        $note = ''
        if ($oldTarget -and -not (Test-Path -LiteralPath $oldTarget)) { $note = "old target does not exist today" }
        $old = [pscustomobject]@{ Target = $oldTarget; Args = '%*'; Work = ''; Icon = '' }
        $new = [pscustomobject]@{ Target = $BatExe; Args = "--run $id -- %*"; Work = ''; Icon = '' }
        Add-Row ([pscustomobject]@{ Kind = 'bat'; Module = $id; Path = $file.FullName; Pinned = $false; Status = 'change'; Old = $old; New = $new; Copy = $null; Note = $note })
    }
}

# Cable Listen: the helper folder and the old settings file.
$Creations = New-Object System.Collections.Generic.List[object]
if (In-Scope 'cablelisten') {
    $svvExe = Join-Path $SoundVolumeViewDir 'SoundVolumeView.exe'
    if ($SoundVolumeViewDir -and (Test-Path -LiteralPath $svvExe)) {
        if (Test-Path -LiteralPath (Join-Path $HelperDest 'SoundVolumeView.exe')) {
            $Creations.Add([pscustomobject]@{ Kind = 'helper'; Module = 'cablelisten'; Source = (Full $SoundVolumeViewDir); Dest = $HelperDest; Status = 'done'; Note = 'helper already in the DeskKit helpers folder' })
        } elseif ((Test-Path -LiteralPath $HelperDest) -and @(Get-ChildItem -LiteralPath $HelperDest -Force).Count -gt 0) {
            $Creations.Add([pscustomobject]@{ Kind = 'helper'; Module = 'cablelisten'; Source = (Full $SoundVolumeViewDir); Dest = $HelperDest; Status = 'skip'; Note = 'target folder exists without SoundVolumeView.exe: copy by hand' })
        } else {
            $n = @(Get-ChildItem -LiteralPath $SoundVolumeViewDir -Recurse -File -Force).Count
            $Creations.Add([pscustomobject]@{ Kind = 'helper'; Module = 'cablelisten'; Source = (Full $SoundVolumeViewDir); Dest = $HelperDest; Status = 'change'; Note = "copy all $n files unchanged (NirSoft freeware terms)" })
        }
    }
    # Old settings: next to the old cable_listen.pyw, else in the scripts folder.
    $oldJson = $null
    foreach ($row in $Plan) {
        if ($row.Module -eq 'cablelisten' -and $row.Old.Args -match '"?([^"]*cable_listen\.pyw)"?') {
            $cand = Join-Path (Split-Path -Parent $Matches[1]) 'cable_listen.json'
            if (Test-Path -LiteralPath $cand) { $oldJson = $cand; break }
        }
    }
    if (-not $oldJson -and $ScriptsDir) { $cand = Join-Path $ScriptsDir 'cable_listen.json'; if (Test-Path -LiteralPath $cand) { $oldJson = $cand } }
    if ($oldJson) {
        $destJson = Join-Path $CableData 'cable_listen.json'
        if ((Test-Path -LiteralPath $destJson) -or (Test-Path -LiteralPath (Join-Path $CableData 'settings.json'))) {
            $Creations.Add([pscustomobject]@{ Kind = 'settings'; Module = 'cablelisten'; Source = (Full $oldJson); Dest = $destJson; Status = 'done'; Note = 'Cable Listen already has settings' })
        } else {
            $Creations.Add([pscustomobject]@{ Kind = 'settings'; Module = 'cablelisten'; Source = (Full $oldJson); Dest = $destJson; Status = 'change'; Note = 'module turns it into settings.json on first run' })
        }
    }
}

# ---------- report ----------

$out = New-Object System.Collections.Generic.List[string]
if ($Apply) { $out.Add('DeskKit launcher migration: APPLY') } else { $out.Add('DeskKit launcher migration: DRY RUN. Nothing was changed. Run again with -Apply to change files.') }
$exeState = 'found'; if (-not (Test-Path -LiteralPath $DeskKitExe)) { $exeState = 'NOT FOUND (-Apply refuses to run)' }
$out.Add("Install dir:        $(Show $InstallDir)  (DeskKit.exe $exeState)")
$out.Add("Shortcuts dir:      $(Show (Full $ShortcutsDir))")
$out.Add("Scripts dir:        $(Show (Full $ScriptsDir))")
foreach ($d in $PinnedDirs) { $out.Add("Pinned dir:         $(Show $d)") }
$tgShow = '(none found)'; if ($TaskbarGroupsDir) { $tgShow = Show $TaskbarGroupsDir }
$out.Add("Old TaskbarGroups:  $tgShow  (read only)")
$pinState = 'no: pinned and Start Menu rows are skipped'; if ($IncludeTaskbarPins) { $pinState = 'yes' }
$out.Add("Edit pins:          $pinState")
if ($OnlyIds.Count) { $out.Add("Only:               $($OnlyIds -join ', ')") }
$out.Add('')

$rows = @($Plan.ToArray()) + @($Creations.ToArray())
$out.Add(('{0,-4} {1,-11} {2,-14} {3,-9} {4}' -f '#', 'ACTION', 'MODULE', 'KIND', 'FILE'))
$n = 0
foreach ($r in $rows) {
    $n++
    $path = $r.Path; if (-not $path) { $path = $r.Dest }
    $out.Add(('{0,-4} {1,-11} {2,-14} {3,-9} {4}' -f $n, $r.Status.ToUpperInvariant(), $r.Module, $r.Kind, (Show $path)))
}
$out.Add('')

function Fmt-Args([string]$a) { if ($a) { return $a } return '(none)' }
$n = 0
foreach ($r in $rows) {
    $n++
    if ($r.Kind -in 'helper', 'settings') {
        $out.Add("[$n] $($r.Status.ToUpperInvariant())  $($r.Module)  $($r.Kind)")
        $out.Add("      copy from: $(Show $r.Source)")
        $out.Add("      copy to:   $(Show $r.Dest)")
        if ($r.Note) { $out.Add("      note:      $($r.Note)") }
        $out.Add('')
        continue
    }
    $out.Add("[$n] $($r.Status.ToUpperInvariant())  $($r.Module)  $(Show $r.Path)")
    $o = $r.Old
    if ($r.Kind -eq 'bat') {
        if ($r.New) {
            $out.Add("      now: forwards to $(Show $o.Target) %*")
            $out.Add("      new: `"$($r.New.Target)`" $($r.New.Args)  (2 lines, CRLF)")
        }
    } else {
        $out.Add("      now  target: $(Show $o.Target)")
        $out.Add("           args:   $(Fmt-Args (Show $o.Args))")
        $out.Add("           folder: $(Show $o.Work)")
        $out.Add("           icon:   $(Show $o.Icon)")
        if ($r.New) {
            $out.Add("      new  target: $(Show $r.New.Target)")
            $out.Add("           args:   $(Fmt-Args (Show $r.New.Args))")
            $out.Add("           folder: $(Show $r.New.Work)")
            $out.Add("           icon:   $(Show $r.New.Icon)")
        }
        if ($r.Copy -and $r.Copy.Needed) { $out.Add("      icon copy: $(Show $r.Copy.Source) -> $(Show $r.Copy.Dest)") }
    }
    if ($r.Note) { $out.Add("      note: $($r.Note)") }
    $out.Add('')
}

$out.Add("Not matched (left alone): $($NotMatched.Count)")
foreach ($p in $NotMatched) { $out.Add("  $(Show $p)") }
$out.Add('')
$changes = @($rows | Where-Object { $_.Status -eq 'change' })
$out.Add("Summary: $($changes.Count) to change, $(@($rows | Where-Object { $_.Status -eq 'done' }).Count) already done, $(@($rows | Where-Object { $_.Status -eq 'skip' }).Count) skipped, $(@($rows | Where-Object { $_.Status -eq 'left alone' }).Count) left alone, $($NotMatched.Count) not matched.")
$out | ForEach-Object { Write-Output $_ }

if (-not $Apply) { exit 0 }

# ---------- apply ----------

if (-not (Test-Path -LiteralPath $DeskKitExe)) {
    Write-Output "ERROR: DeskKit.exe not found in $(Show $InstallDir). Install DeskKit first, or pass -InstallDir. Nothing was changed."
    exit 2
}
$missing = @($changes | Where-Object { $_.Kind -in 'lnk', 'bat' -and -not $ModuleInfo[$_.Module].Installed } | ForEach-Object { $_.Module } | Sort-Object -Unique)
if ($missing.Count) {
    Write-Output "ERROR: these modules are not installed in $(Show $InstallDir): $($missing -join ', '). Install them, or leave them out with -Only. Nothing was changed."
    exit 2
}
if ($changes.Count -eq 0) { Write-Output 'Nothing to change. No backup made.'; exit 0 }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$BackupDir = Join-Path $BackupRoot $stamp
for ($i = 2; Test-Path -LiteralPath $BackupDir; $i++) { $BackupDir = Join-Path $BackupRoot "$stamp-$i" }
New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null

function Backup-Rel([string]$full) {
    if ($full -match '^([A-Za-z]):\\(.*)$') { return "$($Matches[1])\$($Matches[2])" }
    if ($full -match '^\\\\(.*)$') { return "UNC\$($Matches[1])" }
    return $full.TrimStart('\')
}

$manifest = @{ created_by = 'tools/migrate-launchers.ps1'; time = (Get-Date -Format 's'); files = @(); created = @() }
$failures = 0

function Save-Manifest {
    $json = ConvertTo-Json -InputObject $manifest -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $BackupDir 'manifest.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
}

$restore = @'
<#
  Puts back every file that tools/migrate-launchers.ps1 changed in this run (byte for byte).
  -RemoveCreated also deletes the files and folders the run created (icon copies, the
  SoundVolumeView copy, the copied cable_listen.json).
  Run: powershell -NoProfile -ExecutionPolicy Bypass -File restore.ps1 [-RemoveCreated]
#>
param([switch]$RemoveCreated)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$m = Get-Content -LiteralPath (Join-Path $here 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$fail = 0
foreach ($f in @($m.files)) {
    try {
        Copy-Item -LiteralPath (Join-Path $here $f.backup) -Destination $f.original -Force
        Write-Output "restored  $($f.original)"
    } catch { $fail++; Write-Output "FAILED    $($f.original): $($_.Exception.Message)" }
}
foreach ($c in @($m.created)) {
    if (-not $c) { continue }
    if ($RemoveCreated) {
        if (Test-Path -LiteralPath $c) { Remove-Item -LiteralPath $c -Recurse -Force; Write-Output "removed   $c" }
    } else { Write-Output "kept      $c (created by the migration; -RemoveCreated deletes it)" }
}
if ($fail) { exit 1 }
'@
[System.IO.File]::WriteAllText((Join-Path $BackupDir 'restore.ps1'), $restore, (New-Object System.Text.UTF8Encoding($true)))

$out2 = New-Object System.Collections.Generic.List[string]
$out2.Add('')
$out2.Add("Backup: $(Show $BackupDir)")

foreach ($r in $changes) {
    try {
        if ($r.Kind -in 'helper', 'settings') {
            if ($r.Kind -eq 'helper') {
                New-Item -ItemType Directory -Path $r.Dest -Force | Out-Null
                $manifest.created += $r.Dest; Save-Manifest
                foreach ($f in Get-ChildItem -LiteralPath $r.Source -Recurse -Force) {
                    $rel = $f.FullName.Substring($r.Source.Length).TrimStart('\')
                    $to = Join-Path $r.Dest $rel
                    if ($f.PSIsContainer) { New-Item -ItemType Directory -Path $to -Force | Out-Null } else { Copy-Item -LiteralPath $f.FullName -Destination $to }
                }
            } else {
                New-Item -ItemType Directory -Path (Split-Path -Parent $r.Dest) -Force | Out-Null
                Copy-Item -LiteralPath $r.Source -Destination $r.Dest
                $manifest.created += $r.Dest; Save-Manifest
            }
            $out2.Add("copied    $(Show $r.Source) -> $(Show $r.Dest)")
            continue
        }

        # Back up first, then record it, then change the file.
        $rel = Backup-Rel $r.Path
        $bak = Join-Path $BackupDir $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $bak) -Force | Out-Null
        Copy-Item -LiteralPath $r.Path -Destination $bak
        $manifest.files += @{ original = $r.Path; backup = $rel }
        Save-Manifest

        if ($r.Copy -and $r.Copy.Needed -and -not (Test-Path -LiteralPath $r.Copy.Dest)) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $r.Copy.Dest) -Force | Out-Null
            Copy-Item -LiteralPath $r.Copy.Source -Destination $r.Copy.Dest
            $manifest.created += $r.Copy.Dest; Save-Manifest
            $out2.Add("copied    $(Show $r.Copy.Source) -> $(Show $r.Copy.Dest)")
        }

        if ($r.Kind -eq 'bat') {
            [System.IO.File]::WriteAllText($r.Path, (Bat-Text $r.Module), $BatEncoding)
        } else {
            $l = $Shell.CreateShortcut($r.Path)
            $l.TargetPath = $r.New.Target
            $l.Arguments = $r.New.Args
            $l.WorkingDirectory = $r.New.Work
            $l.IconLocation = $r.New.Icon
            $l.Save()
        }
        $out2.Add("changed   $(Show $r.Path)")
    } catch {
        $failures++
        $out2.Add("FAILED    $(Show $r.Path)$(Show $r.Dest): $($_.Exception.Message)")
    }
}
Save-Manifest
$out2.Add('')
$out2.Add("Restore:  powershell -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $BackupDir 'restore.ps1')`"")
if ($failures) { $out2.Add("$failures change(s) FAILED. The backup holds every file touched so far.") }
$out2 | ForEach-Object { Write-Output $_ }
if ($failures) { exit 1 }
exit 0
