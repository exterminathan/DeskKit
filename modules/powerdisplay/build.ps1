# Build this module into bin\ as one self-contained win-x64 exe (docs/module-contract.md 2.1).
# The version comes from module.json, so the exe and module.json always agree.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$module = Get-Content (Join-Path $here 'module.json') -Raw | ConvertFrom-Json
$project = Get-ChildItem (Join-Path $here 'src') -Recurse -Filter *.csproj | Select-Object -First 1
if (-not $project) { throw 'No .csproj found under src\.' }
dotnet publish $project.FullName -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:Version=$($module.version) -o (Join-Path $here 'bin')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
Write-Output "Built $($module.name) $($module.version) into bin\"
