<#
.SYNOPSIS
  Local development loop for RatScanner (auto rebuild + restart on file changes).

.DESCRIPTION
  Stupid-proof day-to-day workflow. Prefer this over publish.bat while coding.

  WPF does not hot-reload most C# / XAML the way web apps do. Best practice is
  rebuild + restart on save. By default a debounced watcher waits for a quiet
  period (no edits for N seconds) before rebuilding, so rapid agent-driven
  bursts don't cause endless close/reopen cycles. Use -NoDebounce to fall back
  to the original dotnet watch behavior (rebuild within ~1-2s of each save).

.PARAMETER Once
  Build and run once (no file watcher / no auto-restart).

.PARAMETER Release
  Use Release configuration instead of Debug.

.PARAMETER ForceSetup
  Re-download icons/OCR data even if already present.

.PARAMETER SkipRestore
  Skip `dotnet restore` (faster when packages are already restored).

.PARAMETER Debounce
  Quiet period in seconds with no file changes before a rebuild fires.
  Default: 15. Only used in debounced watch mode (the default).

.PARAMETER NoDebounce
  Use the original `dotnet watch` behavior instead of the debounced loop.
  Rebuilds fire within ~1-2s of each save regardless of ongoing edits.

.EXAMPLE
  .\scripts\dev.ps1
  .\scripts\dev.ps1 -Once
  .\scripts\dev.ps1 -Debounce 8
  .\scripts\dev.ps1 -NoDebounce
  .\dev.bat
#>
param(
    [switch]$Once,
    [switch]$Release,
    [switch]$ForceSetup,
    [switch]$SkipRestore,
    [int]$Debounce = 15,
    [switch]$NoDebounce
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repositoryRoot

$configuration = if ($Release) { 'Release' } else { 'Debug' }
$project = Join-Path $repositoryRoot 'src\App\RatScanner.csproj'
$solution = Join-Path $repositoryRoot 'RatScanner.sln'
$setupScript = Join-Path $PSScriptRoot 'setup-data.ps1'

function Assert-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' is not on PATH. Install the .NET SDK: https://dotnet.microsoft.com/download"
    }
}

Write-Host ""
Write-Host "=== RatScanner local dev ===" -ForegroundColor Cyan
Write-Host "Repo:   $repositoryRoot"
Write-Host "Config: $configuration"
if ($Once) {
    Write-Host "Mode:   one-shot run"
} elseif ($NoDebounce) {
    Write-Host "Mode:   watch (dotnet watch, no debounce)"
} else {
    Write-Host "Mode:   watch (debounced, ${Debounce}s quiet period)"
}
Write-Host ""

Assert-Command -Name 'dotnet'

# 1) Runtime data (icons + OCR) — copied into bin output by the csproj
# setup-data.ps1 owns the readiness decision: it validates the installed payload against the pinned
# contract and exits early when nothing needs to change. A second predicate here would drift.
Write-Host "Ensuring item icons and OCR data are installed..."
$setupArgs = @()
if ($ForceSetup) { $setupArgs += '-Force' }
& $setupScript @setupArgs
if ($LASTEXITCODE -ne 0) {
    throw "setup-data.ps1 failed."
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
Write-Host "  - Save a .cs/.xaml/.razor file -> after the quiet period the app rebuilds and restarts." -ForegroundColor DarkGray
Write-Host "  - Close the RatScanner window or Ctrl+C here to stop." -ForegroundColor DarkGray
Write-Host "  - Use publish.bat only for release-style packaging (slow)." -ForegroundColor DarkGray
Write-Host "  - True in-process hot reload is limited for WPF; restart-on-save is the reliable loop." -ForegroundColor DarkGray
if (-not $Once -and -not $NoDebounce) {
    Write-Host "  - Debounced: rebuild waits ${Debounce}s with no edits. Use -NoDebounce for instant restart." -ForegroundColor DarkGray
}
Write-Host ""

if ($Once) {
    Write-Host "Building and launching once..."
    & dotnet run --project $project -c $configuration --no-restore
    exit $LASTEXITCODE
}

if ($NoDebounce) {
    # Original dotnet watch behavior: rebuild within ~1-2s of each save.
    # --non-interactive: no keyboard prompts when rebuild fails or the app is busy.
    $env:DOTNET_WATCH_SUPPRESS_EMOJIS = '1'
    Write-Host "Starting dotnet watch (Ctrl+C to stop)..."
    Write-Host ""
    & dotnet watch --project $project --non-interactive --no-hot-reload run -c $configuration --no-restore
    exit $LASTEXITCODE
}

# 3) Debounced watch loop: wait for a quiet period (no edits for N seconds)
# before rebuilding. Prevents endless close/reopen during rapid agent-driven
# bursts. Changes that arrive while a build is running are coalesced - after
# the app launches, if new edits occurred, the quiet timer restarts and
# triggers one more rebuild once edits stop.
$srcDir = Join-Path $repositoryRoot 'src'

# Synchronized hashtable so FileSystemWatcher event handlers (separate runspace)
# can update shared state safely.
$syncHash = [hashtable]::Synchronized(@{
    LastChange = [DateTime]::MinValue
})

$onFileChanged = {
    $state = $Event.MessageData
    $e = $Event.SourceEventArgs
    $path = $e.FullPath
    if (-not $path) { return }
    $ext = [System.IO.Path]::GetExtension($path)
    if (@('.cs', '.razor', '.razor.css', '.css', '.xaml', '.csproj', '.json', '.props', '.targets', '.html', '.js') -notcontains $ext) { return }
    if ($path -match '\\(bin|obj)\\') { return }
    $state.LastChange = [DateTime]::Now
}

$watcher = New-Object System.IO.FileSystemWatcher $srcDir
$watcher.IncludeSubdirectories = $true
$watcher.EnableRaisingEvents = $true

$subscriptions = @()
$subscriptions += Register-ObjectEvent -InputObject $watcher -EventName Changed -Action $onFileChanged -MessageData $syncHash
$subscriptions += Register-ObjectEvent -InputObject $watcher -EventName Created -Action $onFileChanged -MessageData $syncHash
$subscriptions += Register-ObjectEvent -InputObject $watcher -EventName Deleted -Action $onFileChanged -MessageData $syncHash
$subscriptions += Register-ObjectEvent -InputObject $watcher -EventName Renamed -Action $onFileChanged -MessageData $syncHash

$appProcess = $null
$lastBuildStart = [DateTime]::MinValue
$building = $false
$notifiedWaiting = $false

function Stop-AppProcess {
    param([object]$Process)
    if ($null -ne $Process -and -not $Process.HasExited) {
        Write-Host "  Stopping app (PID $($Process.Id))..." -ForegroundColor DarkGray
        & taskkill /PID $Process.Id /T /F 2>$null | Out-Null
        $Process.WaitForExit(5000) | Out-Null
    }
}

function Start-App {
    param([string]$ProjectPath, [string]$Config)
    $runArgs = @('run', '--project', $ProjectPath, '-c', $Config, '--no-build', '--no-restore')
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList $runArgs -PassThru -NoNewWindow
    Write-Host "  App launched (PID $($proc.Id))." -ForegroundColor Green
    return $proc
}

try {
    Write-Host "Starting debounced watch - ${Debounce}s quiet period (Ctrl+C to stop)..."
    Write-Host ""

    # Initial build + launch
    Write-Host "Initial build..." -ForegroundColor Cyan
    & dotnet build $project -c $configuration --no-restore
    if ($LASTEXITCODE -eq 0) {
        $appProcess = Start-App -ProjectPath $project -Config $configuration
    } else {
        Write-Host "  Initial build failed. Waiting for changes..." -ForegroundColor Red
    }
    Write-Host ""

    # Polling loop: check for quiet period every 500ms
    while ($true) {
        Start-Sleep -Milliseconds 500

        if ($building) { continue }

        $lastChange = $syncHash.LastChange
        if ($lastChange -eq [DateTime]::MinValue) { continue }
        if ($lastChange -le $lastBuildStart) {
            $notifiedWaiting = $false
            continue
        }

        $elapsed = ([DateTime]::Now - $lastChange).TotalSeconds

        if (-not $notifiedWaiting) {
            Write-Host "Change detected - waiting ${Debounce}s with no further edits before rebuilding..." -ForegroundColor Yellow
            $notifiedWaiting = $true
        }

        if ($elapsed -lt $Debounce) { continue }

        # Quiet period reached — rebuild
        $notifiedWaiting = $false
        $building = $true
        $lastBuildStart = [DateTime]::Now

        Write-Host ""
        Write-Host "Quiet period reached - rebuilding..." -ForegroundColor Cyan

        Stop-AppProcess -Process $appProcess
        $appProcess = $null

        & dotnet build $project -c $configuration --no-restore
        $buildOk = $LASTEXITCODE -eq 0

        if ($buildOk) {
            $appProcess = Start-App -ProjectPath $project -Config $configuration
        } else {
            Write-Host "  Build failed - waiting for next change..." -ForegroundColor Red
        }

        Write-Host ""
        $building = $false
    }
}
finally {
    foreach ($sub in $subscriptions) {
        Unregister-Event -SourceIdentifier $sub.Name -ErrorAction SilentlyContinue
    }
    if ($null -ne $watcher) {
        $watcher.EnableRaisingEvents = $false
        $watcher.Dispose()
    }
    Stop-AppProcess -Process $appProcess
}

exit 0
