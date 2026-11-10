<#
.SYNOPSIS
  Builds the DeskKit suite installer (contract sections 10.4 and 11).

.DESCRIPTION
  1. Validates suite.json and every module.json (tools/validate-modules.py).
  2. Builds the shell and each in-repo compiled module (build.ps1) when they exist.
  3. Stages every module payload in dist\stage\.
  4. Downloads each pinned asset, checks its SHA256, caches it in dist\cache\.
     A module whose url is null is skipped with a warning (an error with -Release).
  5. Writes dist\generated.iss and runs Inno Setup on packaging\DeskKit.iss.
  6. Writes dist\SHA256SUMS.txt.

  Output: dist\DeskKit-Setup-<version>.exe

.PARAMETER Release
  Pass --release to the validator and fail on any skipped module or missing shell.
.PARAMETER SkipValidate
  Skip step 1 (the test script uses it only when Python is missing).
.PARAMETER SkipBuild
  Skip the shell and module build.ps1 calls; use what is already built.
.PARAMETER Root
  Repo root. Default: the parent of this script's folder.
.PARAMETER DistDir
  Output folder. Default: <Root>\dist.
.PARAMETER Iscc
  Path to ISCC.exe. Default: search the usual places.
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$SkipValidate,
    [switch]$SkipBuild,
    [string]$Root,
    [string]$DistDir,
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = (Resolve-Path $Root).Path
if (-not $DistDir) { $DistDir = Join-Path $Root 'dist' }
New-Item -ItemType Directory -Force $DistDir | Out-Null
$DistDir = (Resolve-Path $DistDir).Path
$Stage = Join-Path $DistDir 'stage'
$Cache = Join-Path $DistDir 'cache'

$script:Warnings = New-Object System.Collections.Generic.List[string]
function Warn([string]$m) { Write-Warning $m; $script:Warnings.Add($m) }
function Step([string]$m) { Write-Host "== $m" -ForegroundColor Cyan }
function Fail([string]$m) { throw "build-suite: $m" }

function Read-Json([string]$path) {
    Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Run-Powershell([string]$script, [string]$workdir) {
    Push-Location $workdir
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $script
        if ($LASTEXITCODE -ne 0) { Fail "$script exited with code $LASTEXITCODE" }
    } finally { Pop-Location }
}

function Copy-Payload([string]$src, [string]$dst, [string[]]$names) {
    # Copies the named files and folders from $src to $dst, skipping __pycache__ and *.pyc.
    New-Item -ItemType Directory -Force $dst | Out-Null
    foreach ($n in $names) {
        $p = Join-Path $src $n
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $target = Join-Path $dst $n
        if ((Get-Item -LiteralPath $p).PSIsContainer) {
            New-Item -ItemType Directory -Force $target | Out-Null
            & robocopy $p $target /E /NFL /NDL /NJH /NJS /NP /XD __pycache__ /XF *.pyc | Out-Null
            if ($LASTEXITCODE -ge 8) { Fail "robocopy failed for $p (code $LASTEXITCODE)" }
            $global:LASTEXITCODE = 0
        } else {
            Copy-Item -LiteralPath $p -Destination $target -Force
        }
    }
}

# ---------------------------------------------------------------- 1. validate
$suitePath = Join-Path $Root 'suite.json'
if (-not (Test-Path $suitePath)) { Fail "suite.json not found in $Root" }

if (-not $SkipValidate) {
    Step 'Validate suite.json and module.json files'
    $py = Get-Command python -ErrorAction SilentlyContinue
    if (-not $py) { Fail 'python not found on PATH; install Python or pass -SkipValidate' }
    $vargs = @((Join-Path $Root 'tools\validate-modules.py'), '--root', $Root)
    if ($Release) { $vargs += '--release' }
    & python -I @vargs
    if ($LASTEXITCODE -ne 0) { Fail 'validate-modules.py failed' }
} else {
    Warn 'Validation skipped (-SkipValidate).'
}

$suite = Read-Json $suitePath
$version = [string]$suite.version
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { Fail "suite.json version '$version' is not SemVer" }

# ---------------------------------------------------------------- clean stage
Step "Stage payload in $Stage"
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force $Stage | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Stage 'modules') | Out-Null

# ---------------------------------------------------------------- 2. shell
$shellDir = Join-Path $Root $suite.shell.path
$hasShell = $false
$shellBuild = Join-Path $shellDir 'build.ps1'
if (-not $SkipBuild -and (Test-Path $shellBuild)) {
    Step 'Build the shell'
    Run-Powershell $shellBuild $shellDir
}
$shellExe = $null
$distShell = Join-Path $DistDir 'shell\DeskKit.exe'
if (Test-Path $distShell) { $shellExe = $distShell }
foreach ($c in @('bin\DeskKit.exe', 'out\DeskKit.exe', 'publish\DeskKit.exe', 'bin\publish\DeskKit.exe')) {
    if ($shellExe) { break }
    $p = Join-Path $shellDir $c
    if (Test-Path $p) { $shellExe = $p }
}
if (-not $shellExe -and (Test-Path $shellDir)) {   # last resort: newest DeskKit.exe anywhere under shell\
    $found = Get-ChildItem -Path $shellDir -Recurse -Filter DeskKit.exe -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\obj\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($found) { $shellExe = $found.FullName }
}
if ($shellExe) {
    Copy-Item $shellExe (Join-Path $Stage 'DeskKit.exe') -Force
    $hasShell = $true
    Write-Host "Shell: $shellExe"
} else {
    if ($Release) { Fail 'DeskKit.exe not found in shell/ (a release needs the shell)' }
    Warn 'DeskKit.exe not found in shell/. The installer is built without the shell.'
}

# ---------------------------------------------------------------- 3 and 4. modules
$staged = @()   # module objects staged, in suite.json order
foreach ($entry in $suite.modules) {
    $id = [string]$entry.id
    if ($id.StartsWith('_')) { continue }
    if ([string]$entry.status -eq 'planned') { Write-Host "Skipped planned module $id (status planned in suite.json)"; continue }
    $mdir = Join-Path $Root ([string]$entry.path -replace '/', '\')
    $mjsonPath = Join-Path $mdir 'module.json'
    if (-not (Test-Path $mjsonPath)) { Fail "module $id : module.json not found at $mdir" }
    $mod = Read-Json $mjsonPath
    $dst = Join-Path $Stage "modules\$id"

    if ($entry.source -eq 'in-repo') {
        $buildPs1 = Join-Path $mdir 'build.ps1'
        $compiled = $mod.runtime.type -in @('dotnet', 'native')
        if ($compiled -and -not $SkipBuild) {
            if (Test-Path $buildPs1) {
                Step "Build $id"
                Run-Powershell $buildPs1 $mdir
            } else {
                Warn "Module $id is compiled but has no build.ps1; it is skipped."
                continue
            }
        }
        $names = @('module.json', 'README.md', 'CHANGELOG.md', 'launch.cmd', 'icon.ico')
        if ($compiled) { $names += 'bin' } else { $names += 'src' }
        if ($compiled) {
            $entryPath = Join-Path $mdir ([string]$mod.entry -replace '/', '\')
            if (-not (Test-Path $entryPath)) { Warn "Module $id : entry $($mod.entry) not built; it is skipped."; continue }
        }
        Copy-Payload $mdir $dst $names
        if (-not (Test-Path (Join-Path $dst ([string]$mod.entry -replace '/', '\')))) {
            Remove-Item -Recurse -Force $dst
            Warn "Module $id : entry $($mod.entry) missing from the payload; it is skipped."
            continue
        }
        Write-Host "Staged in-repo module $id $($mod.version)"
    }
    else {
        if (-not $entry.url) {
            $msg = "Module $id is pinned with url null (no release yet); it is skipped."
            if ($Release) { Fail $msg }
            Warn $msg
            continue
        }
        if (-not $entry.sha256) { Fail "Module $id has a url but no sha256" }
        Step "Fetch pinned module $id $($entry.version)"
        $cdir = Join-Path $Cache "$id-$($entry.version)"
        New-Item -ItemType Directory -Force $cdir | Out-Null
        $fname = [IO.Path]::GetFileName(([Uri][string]$entry.url).AbsolutePath)
        $asset = Join-Path $cdir $fname
        $want = ([string]$entry.sha256).ToLowerInvariant()
        $have = $null
        if (Test-Path $asset) { $have = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant() }
        if ($have -ne $want) {
            if (Test-Path $asset) { Remove-Item -Force $asset }
            Write-Host "Download $($entry.url)"
            $tmp = "$asset.part"
            try {
                Invoke-WebRequest -Uri ([string]$entry.url) -OutFile $tmp -UseBasicParsing
            } catch {
                if (Test-Path $tmp) { Remove-Item -Force $tmp }
                Fail "download of $id failed: $($_.Exception.Message)"
            }
            $got = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($got -ne $want) {
                Remove-Item -Force $tmp
                Fail "SHA256 mismatch for $id : expected $want, got $got. The file was deleted."
            }
            Move-Item $tmp $asset
        } else {
            Write-Host "Cache hit (SHA256 ok): $asset"
        }
        New-Item -ItemType Directory -Force $dst | Out-Null
        $entryRel = [string]$mod.entry -replace '/', '\'
        if ($entry.asset_type -eq 'exe') {
            $t = Join-Path $dst $entryRel
            New-Item -ItemType Directory -Force (Split-Path -Parent $t) | Out-Null
            Copy-Item $asset $t -Force
        } else {
            $bin = Join-Path $dst 'bin'
            New-Item -ItemType Directory -Force $bin | Out-Null
            Expand-Archive -LiteralPath $asset -DestinationPath $bin -Force
            if (-not (Test-Path (Join-Path $dst $entryRel))) { Fail "module $id : entry $($mod.entry) not found after unzip" }
        }
        Copy-Payload $mdir $dst @('module.json', 'README.md', 'icon.ico')
        Write-Host "Staged pinned module $id $($mod.version)"
    }
    Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $dst 'LICENSE') -Force
    $staged += , $mod
}

# ---------------------------------------------------------------- 5. suite files
Copy-Item $suitePath (Join-Path $Stage 'suite.json') -Force
Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $Stage 'LICENSE') -Force

if ($staged.Count -eq 0) { Warn 'No module was staged. The installer holds only the shell.' }

# ---------------------------------------------------------------- generate.iss
function Q([string]$s) { "'" + ($s -replace "'", "''") + "'" }   # Pascal string literal
function IQ([string]$s) { '"' + ($s -replace '"', '""') + '"' }  # Inno parameter value

$g = New-Object System.Text.StringBuilder
[void]$g.AppendLine('; Generated by packaging/build-suite.ps1. Do not edit.')
if ($hasShell) { [void]$g.AppendLine('#define HasShell') }
if ($staged | Where-Object { $_.id -eq 'regioncontrol' }) { [void]$g.AppendLine('#define HasRC') }
if ($staged.Count -gt 0) {
    [void]$g.AppendLine('[Types]')
    [void]$g.AppendLine('Name: "custom"; Description: "Choose modules"; Flags: iscustom')
    [void]$g.AppendLine('[Components]')
    foreach ($m in $staged) {
        [void]$g.AppendLine("Name: $(IQ $m.id); Description: $(IQ ($m.name + ': ' + $m.description)); Types: custom")
    }
    [void]$g.AppendLine('[Files]')
    foreach ($m in $staged) {
        # Skip a module when the shell already updated it to a newer version (docs/module-contract.md 16).
        $pv = ''
        try { $pv = [string](Get-Content (Join-Path $Stage ('modules\' + $m.id + '\module.json')) -Raw | ConvertFrom-Json).version } catch {}
        [void]$g.AppendLine("Source: $(IQ ('{#StageDir}\modules\' + $m.id + '\*')); DestDir: $(IQ ('{app}\modules\' + $m.id)); Components: $($m.id); Check: PayloadNotOlder($(Q $m.id), $(Q $pv)); Flags: ignoreversion recursesubdirs createallsubdirs")
    }
}
[void]$g.AppendLine('[Code]')
[void]$g.AppendLine("const ModuleCount = $($staged.Count);")
[void]$g.AppendLine('function ModuleId(I: Integer): String;')
[void]$g.AppendLine('begin')
[void]$g.AppendLine("  Result := '';")
[void]$g.AppendLine('  case I of')
for ($i = 0; $i -lt $staged.Count; $i++) { [void]$g.AppendLine("    ${i}: Result := $(Q $staged[$i].id);") }
[void]$g.AppendLine('  end;')
[void]$g.AppendLine('end;')

# helpers table: one row per (module, helper)
$rows = @()
foreach ($m in $staged) {
    foreach ($h in $m.helpers) { $rows += , @($m.id, $h) }
}
[void]$g.AppendLine("const HelperRowCount = $($rows.Count);")
[void]$g.AppendLine('function HelperField(I: Integer; const F: String): String;')
[void]$g.AppendLine('begin')
[void]$g.AppendLine("  Result := '';")
[void]$g.AppendLine('  case I of')
for ($i = 0; $i -lt $rows.Count; $i++) {
    $mid = $rows[$i][0]; $h = $rows[$i][1]
    $paths = ''
    if ($h.detect.paths) { $paths = ($h.detect.paths -join '|') }
    $fields = [ordered]@{
        module = $mid; id = $h.id; name = $h.name; licence = $h.licence; page = $h.download.page
        url = $h.download.url; sha256 = $h.download.sha256; archive = $h.download.archive
        paths = $paths; onpath = [string]$h.detect.on_path
    }
    [void]$g.AppendLine("    ${i}: begin")
    foreach ($k in $fields.Keys) {
        [void]$g.AppendLine("      if F = $(Q $k) then Result := $(Q ([string]$fields[$k]));")
    }
    [void]$g.AppendLine('    end;')
}
[void]$g.AppendLine('  end;')
[void]$g.AppendLine('end;')
$genPath = Join-Path $DistDir 'generated.iss'
[IO.File]::WriteAllText($genPath, $g.ToString(), (New-Object Text.UTF8Encoding($true)))

# ---------------------------------------------------------------- 6. ISCC
Step 'Compile the installer'
if (-not $Iscc) {
    $cands = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $Iscc = $cands | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $Iscc) {
        $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($cmd) { $Iscc = $cmd.Source }
    }
}
if (-not $Iscc -or -not (Test-Path $Iscc)) { Fail 'ISCC.exe not found. Install Inno Setup 6 or pass -Iscc.' }

$iss = Join-Path $Root 'packaging\DeskKit.iss'
& $Iscc "/DAppVersion=$version" "/DStageDir=$Stage" "/DGenDir=$DistDir" "/DOutDir=$DistDir" $iss
if ($LASTEXITCODE -ne 0) { Fail "ISCC exited with code $LASTEXITCODE" }

$setup = Join-Path $DistDir "DeskKit-Setup-$version.exe"
if (-not (Test-Path $setup)) { Fail "expected $setup was not produced" }

# ---------------------------------------------------------------- SHA256SUMS
Step 'Write SHA256SUMS.txt'
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$sumsPath = Join-Path $DistDir 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumsPath, "$hash  $(Split-Path -Leaf $setup)`n", (New-Object Text.UTF8Encoding($false)))

$size = (Get-Item $setup).Length
Write-Host ''
Write-Host "Installer : $setup"
Write-Host ("Size      : {0:N0} bytes ({1:N1} MB)" -f $size, ($size / 1MB))
Write-Host "SHA256    : $hash"
Write-Host "Modules   : $(($staged | ForEach-Object { $_.id }) -join ', ')"
if ($script:Warnings.Count -gt 0) {
    Write-Host ''
    Write-Host "$($script:Warnings.Count) warning(s):" -ForegroundColor Yellow
    $script:Warnings | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}
