# Test the Taskbar Groups module. Exit 0 = all checks passed.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File test.ps1 [-Zip <taskbargroups-x.y.z.zip>]
# -Zip: test this zip instead of building one (into a temp folder) first.
# Every run uses a temp folder: the zip is unpacked there, and APPDATA, LOCALAPPDATA and
# TASKBARGROUPS_LEGACY_DIR point into it, so the real %APPDATA% and the live install are never
# read or written. The test closes only the processes it started itself.
param([string]$Zip)
$ErrorActionPreference = 'Continue'
$ModuleDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ModuleDir '..\..')).Path
$mod = Get-Content (Join-Path $ModuleDir 'module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$fails = New-Object Collections.Generic.List[string]
function Check($name, [bool]$ok, $detail = '') {
  if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name  $detail"; $fails.Add($name) }
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class TbgWin {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  // Visible top-level windows of one process, as "handle|title".
  public static string[] Windows(int pid) {
    var r = new List<string>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == (uint)pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        r.Add(h.ToInt64() + "|" + sb.ToString());
      }
      return true;
    }, IntPtr.Zero);
    return r.ToArray();
  }
  public static void CloseAll(int pid) {
    foreach (var w in Windows(pid)) PostMessage(new IntPtr(long.Parse(w.Split('|')[0])), 0x0010, IntPtr.Zero, IntPtr.Zero);
  }
}
'@

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("dk-" + $mod.id + "-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$started = New-Object Collections.Generic.List[System.Diagnostics.Process]
$realAppData = $env:APPDATA
$realDataDir = Join-Path $realAppData 'DeskKit\taskbargroups'
$realDataExisted = Test-Path $realDataDir
$saved = @{}
foreach ($v in 'APPDATA', 'LOCALAPPDATA', 'DESKKIT_SHELL', 'DESKKIT_DATA_DIR', 'TASKBARGROUPS_LEGACY_DIR') { $saved[$v] = [Environment]::GetEnvironmentVariable($v) }

function Start-Tbg([string[]]$a, [string]$errFile) {
  $p = @{ FilePath = $script:exe; PassThru = $true; WorkingDirectory = $script:binDir }
  if ($a.Count) { $p.ArgumentList = ($a | ForEach-Object { '"' + $_ + '"' }) }
  if ($errFile) { $p.RedirectStandardError = $errFile; $p.RedirectStandardOutput = "$errFile.out"; $p.NoNewWindow = $true }
  $proc = Start-Process @p
  $null = $proc.Handle   # cache the handle, or ExitCode stays empty after a redirected start
  $script:started.Add($proc)
  $proc
}
function Wait-Window($proc, [string]$title, [int]$seconds = 30) {
  for ($i = 0; $i -lt $seconds * 4; $i++) {
    if ($proc.HasExited) { return $null }
    $w = [TbgWin]::Windows($proc.Id) | Where-Object { $_.Split('|', 2)[1] -eq $title }
    if ($w) { return $w }
    Start-Sleep -Milliseconds 250
  }
  $null
}
function Close-Tbg($proc) {
  [TbgWin]::CloseAll($proc.Id)
  if ($proc.WaitForExit(10000)) { return $proc.ExitCode }
  $null
}
function New-UserEnv([string]$name) {
  $env:APPDATA = Join-Path $tmp "$name\roaming"; $env:LOCALAPPDATA = Join-Path $tmp "$name\local"
  New-Item -ItemType Directory -Force $env:APPDATA, $env:LOCALAPPDATA | Out-Null
  Join-Path $env:APPDATA 'DeskKit\taskbargroups'
}
function Hash-Tree([string]$dir) {
  (Get-ChildItem $dir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $_.FullName.Substring($dir.Length) + '=' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join ';'
}

try {
  # 1. validator
  $out = & python.exe (Join-Path $RepoRoot 'tools\validate-modules.py') --root $RepoRoot 2>&1
  Check 'validate-modules.py' ($LASTEXITCODE -eq 0) ($out -join ' ')

  # 2. no hard-coded drive, user or install paths in the code, no install step in the build
  $bad = Get-ChildItem (Join-Path $ModuleDir 'src') -Recurse -File -Include *.cs, *.csproj, *.ps1 |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern '(?i)\b[A-Z]:[\\/]', '(?i)\\Users\\', '(?i)InstallPath', '(?i)taskkill'
  Check 'no hard-coded path or install step in src' (-not $bad) (($bad | ForEach-Object { "$($_.Filename):$($_.LineNumber)" }) -join ', ')

  # 3. zip: build it (or use -Zip), unpack, check the payload
  if (-not $Zip) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ModuleDir 'build.ps1') -OutDir (Join-Path $tmp 'dist') | Out-Null
    $Zip = Join-Path $tmp "dist\$($mod.id)-$($mod.version).zip"
  }
  Check 'zip exists' (Test-Path $Zip) $Zip
  $unpack = Join-Path $tmp 'unpacked'
  Expand-Archive -Path $Zip -DestinationPath $unpack -Force
  $root = Join-Path $unpack $mod.id
  $need = 'module.json', 'README.md', 'CHANGELOG.md', 'LICENSE', $mod.entry
  $missing = $need | Where-Object { -not (Test-Path (Join-Path $root $_)) }
  Check 'zip holds the payload' (-not $missing) ($missing -join ', ')
  $extra = 'src', 'build.ps1', 'test.ps1' | Where-Object { Test-Path (Join-Path $root $_) }
  Check 'zip leaves out src, build.ps1, test.ps1' (-not $extra) ($extra -join ', ')
  $script:exe = Join-Path $root $mod.entry
  $script:binDir = Split-Path $script:exe -Parent
  $binBefore = (Get-ChildItem $script:binDir -Recurse -File | ForEach-Object Name) -join ','

  # 4. a fake old install: config\Sample (absolute icon and shortcut paths), icons\, settings.json
  $old = Join-Path $tmp 'fake-old-install'
  New-Item -ItemType Directory -Force (Join-Path $old 'config\Sample\icons'), (Join-Path $old 'icons') | Out-Null
  $icoSrc = Join-Path $ModuleDir 'src\TaskbarGroups\icons\link.ico'
  Copy-Item $icoSrc (Join-Path $old 'icons\link.ico')
  Copy-Item $icoSrc (Join-Path $old 'config\Sample\GroupIcon.ico')
  Copy-Item $icoSrc (Join-Path $old 'config\Sample\icons\Notepad.ico')
  $oldIcon = Join-Path $old 'icons\link.ico'
  $notepad = Join-Path $env:SystemRoot 'System32\notepad.exe'
  $group = [ordered]@{ name = 'Sample'; iconPath = $oldIcon; backgroundColor = '#1E1E1E'; opacity = 0.9; iconSize = 32
    shortcuts = @([ordered]@{ name = 'Notepad'; targetPath = $notepad; workingDirectory = ''; arguments = ''; iconCachePath = 'icons/Notepad.ico' }) }
  $utf8 = New-Object Text.UTF8Encoding $false
  [IO.File]::WriteAllText((Join-Path $old 'config\Sample\group.json'), ($group | ConvertTo-Json -Depth 5), $utf8)
  # a second group whose icon lies outside the old tree: it must stay as stored
  $outsideIcon = Join-Path $tmp 'outside-icon.ico'
  Copy-Item $icoSrc $outsideIcon
  New-Item -ItemType Directory -Force (Join-Path $old 'config\Outside') | Out-Null
  $group2 = [ordered]@{ name = 'Outside'; iconPath = $outsideIcon; backgroundColor = '#1E1E1E'; opacity = 0.9; iconSize = 32; shortcuts = @() }
  [IO.File]::WriteAllText((Join-Path $old 'config\Outside\group.json'), ($group2 | ConvertTo-Json -Depth 5), $utf8)
  [IO.File]::WriteAllText((Join-Path $old 'settings.json'), '{ "shortcutsPath": null }', $utf8)
  $oldHash = Hash-Tree $old

  $env:DESKKIT_SHELL = '1'      # errors go to stderr only, no message box
  Remove-Item Env:DESKKIT_DATA_DIR -ErrorAction SilentlyContinue
  $env:TASKBARGROUPS_LEGACY_DIR = $old
  $data = New-UserEnv 'user-a'

  # 5. config window opens (first run copies the old config)
  $c1 = Start-Tbg @()
  $w = Wait-Window $c1 'Taskbar Groups'
  Check 'config window opens' ($null -ne $w) "exited=$($c1.HasExited)"
  Check 'migration copied config\Sample\group.json' (Test-Path (Join-Path $data 'config\Sample\group.json'))
  Check 'migration copied icons\link.ico and GroupIcon.ico' ((Test-Path (Join-Path $data 'icons\link.ico')) -and (Test-Path (Join-Path $data 'config\Sample\GroupIcon.ico')))
  $copied = Get-Content (Join-Path $data 'config\Sample\group.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
  Check 'iconPath inside the old folder is rewritten to the copy in icons' ($copied.iconPath -eq 'icons/link.ico') "iconPath=$($copied.iconPath)"
  Check 'the referenced icon file exists in the data icons folder' ((Get-FileHash (Join-Path $data 'icons\link.ico')).Hash -eq (Get-FileHash $oldIcon).Hash)
  Check 'shortcut target kept as stored' ($copied.shortcuts[0].targetPath -eq $notepad)
  $copied2 = Get-Content (Join-Path $data 'config\Outside\group.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
  Check 'iconPath outside the old folder is left as stored' ($copied2.iconPath -eq $outsideIcon) "iconPath=$($copied2.iconPath)"
  $marker = Get-Content (Join-Path $data 'migrated-from.txt') -Raw -ErrorAction SilentlyContinue
  Check 'migrated-from.txt names the old folder' ($marker -and $marker.Contains($old))
  $st = Get-Content (Join-Path $data 'settings.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
  Check 'settings.json has every key' ($st -and ($st.PSObject.Properties.Name -contains 'shortcutsPath') -and ($st.PSObject.Properties.Name -contains 'stayOpenUntilClickOff'))
  Check 'old folder unchanged (copied, not moved)' ((Hash-Tree $old) -eq $oldHash)

  # 6. a second config window at the same time (popup module: single false, two copies are safe)
  $c2 = Start-Tbg @()
  $w2 = Wait-Window $c2 'Taskbar Groups'
  Check 'second config window opens next to the first' (($null -ne $w2) -and -not $c1.HasExited)

  # 7. popup for the test group, and a second popup at the same time
  $p1 = Start-Tbg @('Sample')
  $pw1 = Wait-Window $p1 'Sample'
  Check 'popup for group Sample opens' ($null -ne $pw1) "exited=$($p1.HasExited) code=$(if ($p1.HasExited) { $p1.ExitCode })"
  $p2 = Start-Tbg @('Sample')
  $pw2 = Wait-Window $p2 'Sample'
  Check 'second popup opens while the first is open' (($null -ne $pw2) -and -not $p1.HasExited)

  $codes = foreach ($p in $p1, $p2, $c2, $c1) { Close-Tbg $p }
  Check 'all four windows close with exit 0' (-not ($codes | Where-Object { $_ -ne 0 })) ("codes=" + ($codes -join ','))

  # 8. errors: missing group -> exit 1 + ERROR line; two args -> exit 2
  $e1 = Join-Path $tmp 'err-missing.txt'
  $m = Start-Tbg @('NoSuchGroup') $e1
  $ok = $m.WaitForExit(30000)
  Check 'missing group exits 1 with an ERROR line' ($ok -and $m.ExitCode -eq 1 -and ((Get-Content $e1 -Raw) -match 'ERROR: Group ''NoSuchGroup'' not found')) "exit=$($m.ExitCode) err=$(Get-Content $e1 -Raw)"
  $e2 = Join-Path $tmp 'err-usage.txt'
  $u = Start-Tbg @('a', 'b') $e2
  $ok = $u.WaitForExit(30000)
  Check 'two arguments exit 2 with an ERROR line' ($ok -and $u.ExitCode -eq 2 -and ((Get-Content $e2 -Raw) -match 'ERROR: Usage')) "exit=$($u.ExitCode)"

  # 9. first run copies from the exe folder when the old app kept config there
  $data = New-UserEnv 'user-b'
  $env:TASKBARGROUPS_LEGACY_DIR = Join-Path $tmp 'no-such-folder'
  $exeCopyDir = Join-Path $tmp 'old-exe-folder'
  New-Item -ItemType Directory -Force $exeCopyDir | Out-Null
  Copy-Item $script:exe $exeCopyDir
  Copy-Item (Join-Path $old 'config') $exeCopyDir -Recurse
  $saveExe = $script:exe; $saveBin = $script:binDir
  $script:exe = Join-Path $exeCopyDir 'TaskbarGroups.exe'; $script:binDir = $exeCopyDir
  $p3 = Start-Tbg @('Sample')
  $pw3 = Wait-Window $p3 'Sample'
  Check 'exe-folder config copied and its popup opens' (($null -ne $pw3) -and (Test-Path (Join-Path $data 'config\Sample\group.json')))
  $code3 = Close-Tbg $p3
  Check 'that popup closes with exit 0' ($code3 -eq 0) "code=$code3"
  $script:exe = $saveExe; $script:binDir = $saveBin

  # 10. nothing written next to the exe, nothing in the real %APPDATA%
  $binAfter = (Get-ChildItem $script:binDir -Recurse -File | ForEach-Object Name) -join ','
  Check 'nothing written next to the exe' ($binAfter -eq $binBefore) "after=$binAfter"
  Check 'real %APPDATA%\DeskKit\taskbargroups untouched' ($realDataExisted -or -not (Test-Path $realDataDir))
} finally {
  foreach ($p in $started) {
    if (-not $p.HasExited) { try { $p.Kill(); $p.WaitForExit(5000) | Out-Null } catch {} }
  }
  foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
if ($fails.Count) { Write-Host "`n$($fails.Count) check(s) failed: $($fails -join '; ')"; exit 1 }
Write-Host "`nAll checks passed."
exit 0
