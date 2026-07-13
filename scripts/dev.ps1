<#
.SYNOPSIS
  Local development loop for RatScanner (auto rebuild + restart on file changes).

.DESCRIPTION
  Stupid-proof day-to-day workflow. Prefer this over publish.bat while coding.

  WPF does not hot-reload most C# / XAML the way web apps do. Best practice is
  `dotnet watch run`: save a file -> build -> kill previous process -> relaunch.

.PARAMETER Once
  Build and run once (no file watcher / no auto-restart).

.PARAMETER Release
  Use Release configuration instead of Debug.

.PARAMETER ForceSetup
  Re-download icons/OCR data even if already present.

.PARAMETER SkipRestore
  Skip `dotnet restore` (faster when packages are already restored).

.EXAMPLE
  .\scripts\dev.ps1
  .\scripts\dev.ps1 -Once
  .\dev.bat
#>
param(
    [switch]$Once,
    [switch]$Release,
    [switch]$ForceSetup,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repositoryRoot

$configuration = if ($Release) { 'Release' } else { 'Debug' }
$project = Join-Path $repositoryRoot 'RatScanner\RatScanner.csproj'
$solution = Join-Path $repositoryRoot 'RatScanner.sln'
$setupScript = Join-Path $PSScriptRoot 'setup-data.ps1'
$dataDir = Join-Path $repositoryRoot 'RatScanner\Data'

function Assert-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' is not on PATH. Install the .NET SDK: https://dotnet.microsoft.com/download"
    }
}

function Test-DataReady {
    param([string]$DataDir)
    $required = @(
        'maps.json',
        'unknown.png',
        'traineddata\eng.traineddata'
    )
    foreach ($relativePath in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $DataDir $relativePath))) {
            return $false
        }
    }
    $iconsDir = Join-Path $DataDir 'icons'
    if (-not (Test-Path -LiteralPath $iconsDir)) {
        return $false
    }
    return (@(Get-ChildItem -LiteralPath $iconsDir -Filter '*.png' -File -ErrorAction SilentlyContinue).Count -gt 0)
}

Write-Host ""
Write-Host "=== RatScanner local dev ===" -ForegroundColor Cyan
Write-Host "Repo:   $repositoryRoot"
Write-Host "Config: $configuration"
Write-Host "Mode:   $(if ($Once) { 'one-shot run' } else { 'watch (auto rebuild + restart on save)' })"
Write-Host ""

Assert-Command -Name 'dotnet'

# 1) Runtime data (icons + OCR) — copied into bin output by the csproj
if ($ForceSetup -or -not (Test-DataReady -DataDir $dataDir)) {
    Write-Host "Ensuring item icons and OCR data are installed..."
    $setupArgs = @()
    if ($ForceSetup) { $setupArgs += '-Force' }
    & $setupScript @setupArgs
    if ($LASTEXITCODE -ne 0) {
        throw "setup-data.ps1 failed."
    }
}
else {
    Write-Host "Data OK: $dataDir"
}

# 2) Restore packages once unless skipped
if (-not $SkipRestore) {
    Write-Host "Restoring NuGet packages..."
    & dotnet restore $solution
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed."
    }
}

Write-Host ""
Write-Host "Notes:" -ForegroundColor DarkGray
Write-Host "  - Save a .cs/.xaml/.razor file -> watch rebuilds and restarts the app." -ForegroundColor DarkGray
Write-Host "  - Close the RatScanner window or Ctrl+C here to stop." -ForegroundColor DarkGray
Write-Host "  - Use publish.bat only for release-style packaging (slow)." -ForegroundColor DarkGray
Write-Host "  - True in-process hot reload is limited for WPF; restart-on-save is the reliable loop." -ForegroundColor DarkGray
Write-Host ""

if ($Once) {
    Write-Host "Building and launching once..."
    & dotnet run --project $project -c $configuration --no-restore
    exit $LASTEXITCODE
}

# 3) Watch loop: rebuild + full restart on changes.
# WPF / WebView2 does not get reliable in-process hot reload; force restart-on-save.
# --non-interactive: no keyboard prompts when rebuild fails or the app is busy.
$env:DOTNET_WATCH_SUPPRESS_EMOJIS = '1'
Write-Host "Starting dotnet watch (Ctrl+C to stop)..."
Write-Host ""

& dotnet watch --project $project --non-interactive --no-hot-reload run -c $configuration --no-restore
exit $LASTEXITCODE
