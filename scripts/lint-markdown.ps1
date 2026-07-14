<#
.SYNOPSIS
  Lint or auto-fix Markdown files with markdownlint-cli2.

.DESCRIPTION
  Mirrors the CSharpier workflow for Markdown:
  - Default: check only (non-zero exit on violations)
  - -Fix: apply auto-fixes (tables, trailing spaces, final newline, etc.)

  Requires Node.js/npm. On first run (or when node_modules is missing), runs
  `npm ci` if package-lock.json exists, otherwise `npm install`.

  Suitable for local use, agent post-edit fixes, optional git pre-commit hooks,
  and CI (check mode).

.PARAMETER Fix
  Apply markdownlint --fix instead of report-only.

.PARAMETER RepoRoot
  Repository root. Defaults to parent of this scripts directory.
#>
[CmdletBinding()]
param(
    [switch]$Fix,
    [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

Set-Location -LiteralPath $RepoRoot

function Test-CommandExists {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

if (-not (Test-CommandExists 'npm')) {
    Write-Host 'FAIL: npm/Node.js is required for markdown lint.' -ForegroundColor Red
    Write-Host 'Install Node.js LTS from https://nodejs.org/ and re-run.' -ForegroundColor Yellow
    exit 1
}

$nodeModulesCli = Join-Path $RepoRoot 'node_modules\markdownlint-cli2\markdownlint-cli2-bin.mjs'
$packageLock = Join-Path $RepoRoot 'package-lock.json'
$packageJson = Join-Path $RepoRoot 'package.json'

if (-not (Test-Path -LiteralPath $packageJson)) {
    Write-Host 'FAIL: package.json missing (markdownlint tooling).' -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $nodeModulesCli)) {
    Write-Host 'Installing markdownlint tooling (npm)...' -ForegroundColor Cyan
    if (Test-Path -LiteralPath $packageLock) {
        npm ci --no-fund --no-audit
    }
    else {
        npm install --no-fund --no-audit
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'FAIL: npm install failed.' -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

$mode = if ($Fix) { 'fix' } else { 'check' }
Write-Host ("=== Markdown lint ($mode) ===") -ForegroundColor Cyan

$npmScript = if ($Fix) { 'lint:md:fix' } else { 'lint:md' }
npm run $npmScript
$exit = $LASTEXITCODE

if ($exit -eq 0) {
    if ($Fix) {
        Write-Host 'Markdown auto-fix completed with no remaining violations.' -ForegroundColor Green
    }
    else {
        Write-Host 'Markdown lint passed.' -ForegroundColor Green
    }
}
else {
    Write-Host 'Markdown lint reported issues.' -ForegroundColor Red
    if (-not $Fix) {
        Write-Host 'Auto-fix with:  scripts\lint-markdown.ps1 -Fix' -ForegroundColor Yellow
        Write-Host 'Or:               npm run lint:md:fix' -ForegroundColor Yellow
    }
}

exit $exit
