<#
.SYNOPSIS
  Verifies a packaged RatScanner release archive against the pinned RatScannerData contract.

.DESCRIPTION
  Runs after packaging, on the exact zip that gets promoted. Installation validation only covers
  the staged publish tree; this checks the archive itself so a packaging step cannot silently
  drop, truncate, or duplicate payload files.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-package.ps1
#>
[CmdletBinding()]
param(
    [string]$PackagePath = ''
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$package = if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    Join-Path $repositoryRoot 'RatScanner.zip'
}
else {
    [System.IO.Path]::GetFullPath($PackagePath)
}

. (Join-Path $PSScriptRoot 'RatScannerData.ps1')
$contract = Get-RatScannerDataReleaseContract

Write-Host "Verifying release package $package against $($contract.ReleaseTag)..."
$result = Assert-RatScannerDataPackage `
    -PackagePath $package `
    -ExpectedSchema $contract.ManifestSchema `
    -MinimumIconCount $contract.MinimumIconCount `
    -ContentSha256Prefix $contract.ContentSha256Prefix

Write-Host "Package entries:       $($result.EntryCount)"
Write-Host "Verified data files:   $($result.FileCount)"
Write-Host "Packaged icons:        $($result.IconCount)"
Write-Host "RatScannerData content: $($result.ContentSha256)"
Write-Host 'Release package verified.'
