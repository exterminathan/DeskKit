# Publish the DeskKit shell: self-contained, single-file, win-x64, into dist\shell\ (contract 10.4).
# The version comes from suite.json shell.version, so the exe and suite.json always agree.
#   powershell -NoProfile -ExecutionPolicy Bypass -File shell/build.ps1 [-OutDir <folder>]
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
if (-not $OutDir) { $OutDir = Join-Path $root 'dist\shell' }
$suite = Get-Content (Join-Path $root 'suite.json') -Raw | ConvertFrom-Json
$version = $suite.shell.version
$project = Join-Path $here 'DeskKit\DeskKit.csproj'

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
[System.Threading.Thread]::CurrentThread.Priority = 'BelowNormal'
$publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
  '-p:PublishSingleFile=true', "-p:Version=$version", '-p:DebugType=None', '-p:DebugSymbols=false',
  '-o', $OutDir, '-nologo', '-v', 'quiet')
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
$exe = Join-Path $OutDir 'DeskKit.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe." }
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Output "Built DeskKit $version into $OutDir (DeskKit.exe, $mb MB)"
