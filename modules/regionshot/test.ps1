# RegionShot checks. Exit 0 = all pass, 1 = a check failed.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File modules/regionshot/test.ps1 [-SkipLaunch]
# The launch test builds a venv in a temp folder (needs internet, about 1-3 minutes).
# It never touches a running RegionShot: if the real mutex exists, the launch test is skipped.
param([switch]$SkipLaunch, [int]$ApiPort = 47899, [int]$LaunchTimeoutSec = 300)
$ErrorActionPreference = 'Stop'
$module = $PSScriptRoot
$repo = Split-Path (Split-Path $module -Parent) -Parent
$failed = 0
function Pass($m) { Write-Output "PASS  $m" }
function Fail($m) { Write-Output "FAIL  $m"; $script:failed++ }
function Get-Descendants($rootPid) {
    # venv python.exe/pythonw.exe is a redirector that starts the real interpreter, so look deeper.
    $all = Get-CimInstance Win32_Process
    $queue = @([int]$rootPid); $found = @()
    while ($queue.Count) {
        $cur = $queue[0]; $queue = @($queue | Select-Object -Skip 1)
        foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $cur })) { $found += $c; $queue += $c.ProcessId }
    }
    return $found
}
function Skip($m) { Write-Output "SKIP  $m" }

# Find a real Python 3.11+ (py launcher first, then PATH, never the Store stub).
$py = $null
try { $p = (& py -3 -c "import sys; print(sys.executable)") 2>$null; if ($p -and (Test-Path $p)) { $py = $p } } catch {}
if (-not $py) {
    $c = Get-Command python.exe -ErrorAction SilentlyContinue | Where-Object { $_.Source -notmatch 'WindowsApps' } | Select-Object -First 1
    if ($c) { $py = $c.Source }
}
if (-not $py) { Fail 'Python 3.11 or newer not found'; exit 1 }

# 1. Syntax.
& $py -W error -m py_compile (Join-Path $module 'src\region_shot.py')
if ($LASTEXITCODE -eq 0) { Pass 'python syntax compiles' } else { Fail 'python syntax compile' }
Remove-Item (Join-Path $module 'src\__pycache__') -Recurse -Force -ErrorAction SilentlyContinue

# 2. Validator.
Push-Location $repo
$out = & $py tools/validate-modules.py 2>&1 | Out-String
$vcode = $LASTEXITCODE
Pop-Location
if ($vcode -eq 0) { Pass 'validate-modules.py passes' } else { Fail "validate-modules.py exit $vcode`n$out" }

# 3. No hard-coded path in shipped code or module.json.
$hits = @()
$targets = @((Join-Path $module 'src\region_shot.py'), (Join-Path $module 'launch.cmd'), (Join-Path $module 'module.json'))
foreach ($t in $targets) {
    $n = 0
    foreach ($line in Get-Content $t) {
        $n++
        if ($line -match '(?<![A-Za-z])[A-Za-z]:[\\/]' -or $line -match '\\Users\\' -or $line -match 'Nathan') {
            $hits += "$(Split-Path $t -Leaf):${n}: $($line.Trim())"
        }
    }
}
if ($hits.Count -eq 0) { Pass 'no hard-coded drive, Users or user-name path' } else { Fail "hard-coded path:`n$($hits -join "`n")" }

# 4. Build and unpack.
$buildOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $module 'build.ps1') 2>&1
if ($LASTEXITCODE -ne 0) { Fail "build.ps1 failed:`n$buildOut"; exit 1 }
$zip = ($buildOut | Select-Object -Last 1).ToString().Trim()
$version = (Get-Content (Join-Path $module 'module.json') -Raw | ConvertFrom-Json).version
if ((Test-Path $zip) -and ((Split-Path $zip -Leaf) -eq "regionshot-$version.zip")) { Pass "zip built: $zip" } else { Fail "zip missing: $zip"; exit 1 }

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("regionshot-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
Expand-Archive $zip -DestinationPath (Join-Path $tmp 'unpacked')
$mod = Join-Path $tmp 'unpacked\regionshot'
$need = 'module.json', 'README.md', 'CHANGELOG.md', 'launch.cmd', 'icon.ico', 'LICENSE', 'src\region_shot.py', 'src\requirements.txt'
$missing = $need | Where-Object { -not (Test-Path (Join-Path $mod $_)) }
$extra = Get-ChildItem $mod -Recurse -File | Where-Object { $_.FullName -match '__pycache__|build\.ps1|test\.ps1' }
if (-not $missing -and -not $extra) { Pass 'zip holds the payload and nothing else' } else { Fail "zip content: missing [$($missing -join ', ')] extra [$($extra.Name -join ', ')]" }
$cmd = [System.IO.File]::ReadAllBytes((Join-Path $mod 'launch.cmd'))
$lf = 0; for ($i = 0; $i -lt $cmd.Length; $i++) { if ($cmd[$i] -eq 10 -and ($i -eq 0 -or $cmd[$i - 1] -ne 13)) { $lf++ } }
if ($lf -eq 0) { Pass 'launch.cmd has CRLF endings' } else { Fail "launch.cmd has $lf bare LF" }

# 5. Real launch from the unpacked zip.
if ($SkipLaunch) { Skip 'launch test (-SkipLaunch)' }
else {
    $mutexName = 'Local\DeskKit.regionshot'
    $existing = $null
    if ([System.Threading.Mutex]::TryOpenExisting($mutexName, [ref]$existing)) {
        $existing.Dispose()
        Skip 'launch test: a RegionShot copy is already running (mutex exists); not touching it'
    } else {
        $data = Join-Path $tmp 'appdata'; $local = Join-Path $tmp 'local'
        New-Item -ItemType Directory -Force (Join-Path $data 'DeskKit\regionshot'), $local | Out-Null
        # Port override so the test never collides with a running RegionShot on 47800.
        '{ "api_port": ' + $ApiPort + ' }' | Set-Content (Join-Path $data 'DeskKit\regionshot\settings.json') -Encoding utf8
        $env:APPDATA = $data; $env:LOCALAPPDATA = $local
        $env:DESKKIT_NO_MIGRATE = '1'; $env:DESKKIT_NO_SETUP_WINDOW = '1'
        $env:Path = ($env:Path -split ';' | Where-Object { $_ -ne '' }) -join ';'
        $proc = $null
        try {
            $proc = Start-Process $py -ArgumentList @('"' + (Join-Path $mod 'src\region_shot.py') + '"') -PassThru -WindowStyle Hidden
            $api = $null; $deadline = (Get-Date).AddSeconds($LaunchTimeoutSec)
            while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
                try { $api = Invoke-RestMethod "http://127.0.0.1:$ApiPort/status" -TimeoutSec 3; break } catch { Start-Sleep -Seconds 2 }
            }
            if ($proc.HasExited -and -not $api) {
                $lg = Join-Path $data 'DeskKit\regionshot\logs\regionshot.log'
                $tail = if (Test-Path $lg) { (Get-Content $lg -Tail 8) -join "`n" } else { '(no log)' }
                Fail "module exited early with code $($proc.ExitCode) after $([int]((Get-Date) - $deadline.AddSeconds(-$LaunchTimeoutSec)).TotalSeconds) s`n$tail"
            }
            elseif (-not $api) { Fail "HTTP API did not answer within $LaunchTimeoutSec s" }
            else {
                Pass "HTTP API answers: $($api | ConvertTo-Json -Compress)"
                $venvOk = Test-Path (Join-Path $local 'DeskKit\regionshot\venv\Scripts\python.exe')
                if ($venvOk) { Pass 'venv built in LOCALAPPDATA\DeskKit\regionshot\venv' } else { Fail 'venv not built' }
                $kids = Get-Descendants $proc.Id
                $win = $kids | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } | Where-Object { $_.MainWindowTitle -eq 'Region Shot' }
                if ($win) { Pass "window 'Region Shot' is open (pid $($win.Id))" } else { Fail 'no window titled Region Shot under the started process' }
                $sj = Join-Path $data 'DeskKit\regionshot\settings.json'
                $keys = (Get-Content $sj -Raw | ConvertFrom-Json).PSObject.Properties.Name
                if ($keys -contains 'ffmpeg_path' -and $keys -contains 'dir' -and $keys -contains 'api_port') { Pass 'settings.json written with all keys' } else { Fail "settings.json keys: $keys" }
                if (Test-Path (Join-Path $data 'DeskKit\regionshot\logs\regionshot.log')) { Pass 'log written to data dir' } else { Fail 'no log in data dir' }
                # Second copy must hand over and exit 0.
                $second = Start-Process $py -ArgumentList @('"' + (Join-Path $mod 'src\region_shot.py') + '"') -PassThru -WindowStyle Hidden
                if ($second.WaitForExit(30000) -and $second.ExitCode -eq 0) { Pass 'second copy exits 0 (single instance)' } else { Fail 'second copy did not exit 0 within 30 s' }
                # Stop through the contract stop event (our own instance only).
                $ev = [System.Threading.EventWaitHandle]::OpenExisting('Local\DeskKit.regionshot.stop')
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                [void]$ev.Set(); $ev.Dispose()
                if ($proc.WaitForExit(15000)) {
                    $ms = $sw.ElapsedMilliseconds
                    if ($proc.ExitCode -eq 0) { Pass 'stop event: clean exit 0' } else { Fail "stop event: exit code $($proc.ExitCode)" }
                    if ($ms -lt 2000) { Pass "stop event: exit in $ms ms (under 2000)" } else { Fail "stop event: exit took $ms ms (limit 2000)" }
                } else { Fail 'stop event: no exit within 15 s' }
            }
        } finally {
            # Close only what this test started.
            if ($proc -and -not $proc.HasExited) {
                Get-Descendants $proc.Id | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
            Remove-Item Env:DESKKIT_NO_MIGRATE, Env:DESKKIT_NO_SETUP_WINDOW -ErrorAction SilentlyContinue
        }
    }
}

# Clean up the temp folder (own folder only).
Start-Sleep -Seconds 1
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
if ($failed -eq 0) { Write-Output 'ALL PASS'; exit 0 } else { Write-Output "$failed check(s) FAILED"; exit 1 }
