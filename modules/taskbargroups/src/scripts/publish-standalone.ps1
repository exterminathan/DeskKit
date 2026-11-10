[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

# Dev helper. The DeskKit build is modules/taskbargroups/build.ps1 (self-contained, zip in dist\).
# Default output: the module's own bin\ folder. This script installs nothing anywhere else.
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Project  = [System.IO.Path]::Combine($RepoRoot, "TaskbarGroups", "TaskbarGroups.csproj")

if (-not $OutputDir) {
    $OutputDir = [System.IO.Path]::Combine($RepoRoot, "..", "bin")
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

$Mode = if ($SelfContained) { "self-contained (bundled .NET runtime)" } else { "framework-dependent (requires .NET 8 Desktop Runtime)" }

Write-Host "Publishing TaskbarGroups.exe"
Write-Host "  Project: $Project"
Write-Host "  Config:  $Configuration"
Write-Host "  Runtime: $Runtime"
Write-Host "  Mode:    $Mode"
Write-Host "  Output:  $OutputDir"
Write-Host ""

$publishArgs = @(
    $Project,
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--self-contained", $SelfContained.IsPresent.ToString().ToLower(),
    "--output", $OutputDir,
    "/p:PublishSingleFile=true",
    "/p:DebugType=None",
    "/p:DebugSymbols=false",
    "-nologo"
)

if ($SelfContained) {
    $publishArgs += @(
        "/p:IncludeNativeLibrariesForSelfExtract=true",
        "/p:IncludeAllContentForSelfExtract=true"
    )
}

& dotnet publish @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

$ExePath = [System.IO.Path]::Combine($OutputDir, "TaskbarGroups.exe")
if (Test-Path $ExePath) {
    $SizeMB = [Math]::Round((Get-Item $ExePath).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Done. Build at: $ExePath ($SizeMB MB)"
    if (-not $SelfContained) {
        Write-Host "Requires: .NET 8 Desktop Runtime - https://dotnet.microsoft.com/download/dotnet/8.0"
    }
}
else {
    Write-Warning "Publish reported success but $ExePath was not found."
    exit 1
}
