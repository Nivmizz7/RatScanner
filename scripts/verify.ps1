<#
.SYNOPSIS
  Runs RatScanner's canonical fast, full, or WebView UI verification pipeline.

.PARAMETER Mode
  Fast: static checks, Debug build, and unit tests.
  Full: hermetic script checks, Debug/Release builds, unit tests, and WebView UI smoke tests.
  Ui: setup/build and run only the real WebView2 smoke suite.

.PARAMETER Repeat
  Repeats the UI smoke suite to expose lifecycle or timing instability.
#>
param(
    [ValidateSet('Fast', 'Full', 'Ui')]
    [string]$Mode = 'Fast',
    [ValidateRange(1, 20)]
    [int]$Repeat = 1,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repositoryRoot

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Label,
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    Write-Host "`n=== $Label ===" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet is not on PATH. Install the SDK pinned by global.json.'
}

$solution = Join-Path $repositoryRoot 'RatScanner.sln'
$unitProject = Join-Path $repositoryRoot 'tests\RatScanner.Tests\RatScanner.Tests.csproj'
$uiProject = Join-Path $repositoryRoot 'tests\RatScanner.UiTests\RatScanner.UiTests.csproj'

if ($Mode -in @('Fast', 'Full')) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw 'Node.js is required for the repository Markdown checks.'
    }

    Invoke-Checked 'Restore local .NET tools' { dotnet tool restore }
    if (-not $SkipRestore) {
        Invoke-Checked 'Restore solution' { dotnet restore $solution }
    }
    Invoke-Checked 'C# formatting' { dotnet csharpier check . }
    Invoke-Checked 'Markdown lint' {
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lint-markdown.ps1
    }
    Invoke-Checked 'Agent documentation integrity' {
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-agent-docs.ps1
    }
    Invoke-Checked 'Debug build and analyzer gate' { dotnet build $solution -c Debug --no-restore }
    Invoke-Checked 'Unit tests' { dotnet test $unitProject -c Debug --no-build --no-restore }
}

if ($Mode -eq 'Full') {
    Invoke-Checked 'Ensure runtime data' {
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\setup-data.ps1
    }
    Invoke-Checked 'Agent documentation checker tests' {
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-agent-docs.ps1
    }
    Invoke-Checked 'RatScannerData validator tests' {
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-data-validation.ps1
    }
    Invoke-Checked 'Release build and analyzer gate' { dotnet build $solution -c Release --no-restore }
    Invoke-Checked 'Release unit tests' { dotnet test $unitProject -c Release --no-build --no-restore }
}

if ($Mode -in @('Full', 'Ui')) {
    if ($Mode -eq 'Ui') {
        Invoke-Checked 'Ensure runtime data' {
            powershell -NoProfile -ExecutionPolicy Bypass -File scripts\setup-data.ps1
        }
        if (-not $SkipRestore) {
            Invoke-Checked 'Restore UI test project' { dotnet restore $uiProject }
        }
        Invoke-Checked 'Build UI test project' { dotnet build $uiProject -c Release --no-restore }
    }

    for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
        Invoke-Checked "WebView UI smoke ($iteration/$Repeat)" {
            dotnet test $uiProject -c Release --no-build --no-restore
        }
    }
}

Write-Host "`n$Mode verification passed." -ForegroundColor Green
