[CmdletBinding()]
param(
    [string]$EditorConfigPath,
    [string]$BuildPropsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($EditorConfigPath)) {
    $EditorConfigPath = Join-Path $repositoryRoot '.editorconfig'
}
if ([string]::IsNullOrWhiteSpace($BuildPropsPath)) {
    $BuildPropsPath = Join-Path $repositoryRoot 'Directory.Build.props'
}

function Get-UniqueRules {
    param(
        [string[]]$Rules,
        [string]$SourceName
    )

    $normalized = @($Rules | ForEach-Object { $_.ToUpperInvariant() })
    $duplicates = @($normalized | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($duplicates.Count -gt 0) {
        throw "$SourceName contains duplicate analyzer rules: $($duplicates -join ', ')"
    }
    return @($normalized | Sort-Object)
}

$editorRules = @()
foreach ($line in Get-Content -LiteralPath $EditorConfigPath) {
    if ($line -match '^\s*dotnet_diagnostic\.(?<rule>(?:CA|IDE)\d{4})\.severity\s*=\s*warning\s*(?:[#;].*)?$') {
        $editorRules += $Matches.rule
    }
}
$editorRules = Get-UniqueRules -Rules $editorRules -SourceName '.editorconfig warning block'

[xml]$buildProps = Get-Content -LiteralPath $BuildPropsPath -Raw
$buildRules = @()
foreach ($node in $buildProps.SelectNodes('/Project/PropertyGroup/WarningsAsErrors')) {
    foreach ($entry in $node.InnerText -split ';') {
        $candidate = $entry.Trim()
        if ($candidate -match '^(?:CA|IDE)\d{4}$') {
            $buildRules += $candidate
        }
    }
}
$buildRules = Get-UniqueRules -Rules $buildRules -SourceName 'Directory.Build.props WarningsAsErrors'

if ($editorRules.Count -eq 0) {
    throw 'No curated CA/IDE warning rules were found in .editorconfig.'
}
if ($buildRules.Count -eq 0) {
    throw 'No curated CA/IDE rules were found in Directory.Build.props WarningsAsErrors.'
}

$missingFromBuild = @($editorRules | Where-Object { $_ -notin $buildRules })
$missingFromEditor = @($buildRules | Where-Object { $_ -notin $editorRules })
if ($missingFromBuild.Count -gt 0 -or $missingFromEditor.Count -gt 0) {
    $details = @()
    if ($missingFromBuild.Count -gt 0) {
        $details += "missing from Directory.Build.props: $($missingFromBuild -join ', ')"
    }
    if ($missingFromEditor.Count -gt 0) {
        $details += "missing from .editorconfig warning block: $($missingFromEditor -join ', ')"
    }
    throw "Analyzer gate definitions differ ($($details -join '; '))."
}

Write-Host "Analyzer gate is consistent: $($editorRules.Count) curated rules."
