# Test the Cable Listen module. Exit 0 = all checks passed.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File test.ps1 [-SvvPath <SoundVolumeView.exe>] [-SkipWindow]
# -SvvPath: use this SoundVolumeView for the window test (else it is found by the module's own rules).
# The window test starts a copy from the unpacked zip with a throw-away data dir. It never touches a
# running Cable Listen (it stops if the mutex already exists) and never turns listening on.
param([string]$SvvPath, [switch]$SkipWindow)
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
$py = (Get-Command py.exe -ErrorAction SilentlyContinue)
function PyRun([string[]]$a) { if ($py) { & py.exe -3 -W error @a } else { & python.exe -W error @a } }

try {
  # 1. syntax
  $entry = Join-Path $ModuleDir $mod.entry
  $pyc = Join-Path $tmp 'syntax.pyc'
  $chk = Join-Path $tmp 'syntax.py'
  Set-Content $chk -Encoding ASCII -Value 'import py_compile, sys', 'py_compile.compile(sys.argv[1], cfile=sys.argv[2], doraise=True)'
  PyRun @($chk, $entry, $pyc) 2>&1 | Out-Null
  Check 'python syntax' (($LASTEXITCODE -eq 0) -and (Test-Path $pyc))

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
  $bad = Get-ChildItem $ModuleDir -Recurse -File -Include *.pyw, *.py, *.ps1, *.cmd, module.json |
    Where-Object { $_.Name -ne 'test.ps1' -and $_.FullName -notmatch '__pycache__' } |
    Select-String -Pattern '(?i)\b[A-Z]:[\\/]', '(?i)\\Users\\', '(?i)C:.Tools'
  Check 'no hard-coded path' (-not $bad) (($bad | ForEach-Object { "$($_.Filename):$($_.LineNumber)" }) -join ', ')

  # 5. entry runs from the unpacked zip
  # 5a. missing helper -> exit 3 and an ERROR line (only when SoundVolumeView is not findable here)
  $env:DESKKIT_SHELL = '1'
  $onPath = Get-Command SoundVolumeView.exe -ErrorAction SilentlyContinue
  $helperDir = Join-Path $env:LOCALAPPDATA 'DeskKit\helpers\soundvolumeview\SoundVolumeView.exe'
  if (-not $onPath -and -not (Test-Path $helperDir)) {
    $env:APPDATA = Join-Path $tmp 'roaming-a'; New-Item -ItemType Directory -Force $env:APPDATA | Out-Null
    $eFile = Join-Path $tmp 'err.txt'
    if ($py) { $p = Start-Process py.exe -ArgumentList '-3', ('"' + (Join-Path $root $mod.entry) + '"') -NoNewWindow -Wait -PassThru -RedirectStandardError $eFile }
    else { $p = Start-Process python.exe -ArgumentList ('"' + (Join-Path $root $mod.entry) + '"') -NoNewWindow -Wait -PassThru -RedirectStandardError $eFile }
    Check 'missing helper exits 3 with an ERROR line' ($p.ExitCode -eq 3 -and ((Get-Content $eFile -Raw) -match 'ERROR: SoundVolumeView')) "exit $($p.ExitCode)"
  } else { Write-Host 'SKIP  missing-helper test (SoundVolumeView is installed on this PC)' }

  # 5b. window opens, stop event closes it, the Listen setting is unchanged
  if ($SkipWindow) { Write-Host 'SKIP  window test (-SkipWindow)' }
  else {
    $svv = $SvvPath
    if (-not $svv -and $onPath) { $svv = $onPath.Source }
    if (-not $svv -and (Test-Path $helperDir)) { $svv = $helperDir }
    if (-not $svv) { Write-Host 'SKIP  window test (no SoundVolumeView found; pass -SvvPath)' }
    else {
      $mutexName = $mod.instance.mutex
      $existing = $null
      if ([Threading.Mutex]::TryOpenExisting($mutexName, [ref]$existing)) { $existing.Dispose(); throw 'Cable Listen is already running; not touching it. Close it and run the test again.' }
      function ListenState {
        $base = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'
        $r = @()
        foreach ($k in Get-ChildItem $base -ErrorAction SilentlyContinue) {
          $v = (Get-ItemProperty -Path "$($k.PSPath)\Properties" -ErrorAction SilentlyContinue).'{24dbb0fc-9311-4b3d-9cf0-18ff155639d4},1'
          if ($v) { $r += ($k.PSChildName + '=' + (($v | Select-Object -Skip 8 -First 2) -join ',')) }
        }
        ($r | Sort-Object) -join ';'
      }
      $before = ListenState
      $env:APPDATA = Join-Path $tmp 'roaming-b'
      New-Item -ItemType Directory -Force (Join-Path $env:APPDATA 'DeskKit\cablelisten') | Out-Null
      $cfg = Join-Path $env:APPDATA 'DeskKit\cablelisten\settings.json'
      [IO.File]::WriteAllText($cfg, (@{ source = ''; svv_path = $svv } | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
      $entryPath = Join-Path $root $mod.entry
      if ($py) { $pw = (& py.exe -3 -c 'import sys;print(sys.executable)') -replace 'python\.exe$', 'pythonw.exe' } else { $pw = (Get-Command pythonw.exe).Source }
      $proc = Start-Process $pw -ArgumentList ('"' + $entryPath + '"') -PassThru
      $up = $false
      for ($i = 0; $i -lt 60 -and -not $proc.HasExited; $i++) {
        $m = $null
        if ([Threading.Mutex]::TryOpenExisting($mutexName, [ref]$m)) { $m.Dispose(); $up = $true; break }
        Start-Sleep -Milliseconds 250
      }
      Check 'window process holds the mutex' $up
      Start-Sleep -Seconds 2
      $proc.Refresh()
      $title = $proc.MainWindowTitle
      Check 'window is open' ((-not $proc.HasExited) -and $title -eq 'Cable Listen') "title='$title' exited=$($proc.HasExited)"
      # second copy hands over and exits 0
      $p2 = Start-Process $pw -ArgumentList ('"' + $entryPath + '"') -PassThru -Wait
      Check 'second copy exits 0' ($p2.ExitCode -eq 0) "exit $($p2.ExitCode)"
      # stop event = same as closing the window
      $ev = [Threading.EventWaitHandle]::OpenExisting($mod.instance.stop_event)
      [void]$ev.Set(); $ev.Dispose()
      $closed = $proc.WaitForExit(5000)
      Check 'stop event closes it within 5 s with exit 0' ($closed -and $proc.ExitCode -eq 0) "exited=$closed"
      if (-not $closed) { try { $proc.Kill() } catch {} }
      $after = ListenState
      Check 'Listen to this device unchanged after close' ($before -eq $after) "before=[$before] after=[$after]"
      $saved = (Get-Content $cfg -Raw | ConvertFrom-Json)
      Check 'settings.json kept svv_path and saved a source' ($saved.svv_path -eq $svv -and $saved.source -ne $null)
    }
  }
} finally {
  Remove-Item Env:DESKKIT_SHELL -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
if ($fails.Count) { Write-Host "`n$($fails.Count) check(s) failed: $($fails -join '; ')"; exit 1 }
Write-Host "`nAll checks passed."
exit 0
