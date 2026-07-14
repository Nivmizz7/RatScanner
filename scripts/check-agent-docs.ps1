<#
.SYNOPSIS
  Objective documentation-integrity checks for the agent control plane and context docs.

.DESCRIPTION
  Verifies facts that can be tested reliably without interpreting free-form prose:
  - Required AGENTS.md, context, tooling, project, and workflow files exist
  - Root AGENTS.md and the context index route every context document
  - Local Markdown links resolve
  - App structurally ProjectReferences the in-tree ScanEngine (no NuGet RatEye)
  - ScanEngine remains non-packable
  - MSBuild XML is valid and package versions are not floating or open-ended
  - Branch-policy documents identify master as the integration branch
  - CI pull requests and branch pushes target master

  Generated and downloaded directories are excluded. Exit 0 on success and 1
  with actionable failures otherwise. Suitable for Windows PowerShell 5.1 and CI.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Stop'

try {
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
        $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDir '..')).ProviderPath
    }
    else {
        $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath
    }
}
catch {
    Write-Host ("FAIL: Repository root could not be resolved: " + $RepoRoot) -ForegroundColor Red
    exit 1
}

$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure {
    param([string]$Message)

    $script:failures.Add($Message) | Out-Null
    Write-Host ("FAIL: " + $Message) -ForegroundColor Red
}

function Get-RepoRelativePath {
    param([string]$FullName)

    if ($FullName.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullName.Substring($RepoRoot.Length).TrimStart([char[]]@('\', '/'))
    }
    return $FullName
}

function Assert-PathExists {
    param(
        [string]$RelativePath,
        [string]$Reason
    )

    $full = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        Add-Failure ($Reason + " - missing: " + $RelativePath)
        return $false
    }
    return $true
}

function Test-ShouldSkipPath {
    param([string]$FullName)

    $relative = (Get-RepoRelativePath -FullName $FullName).Replace('/', '\')
    if ($relative -match '(?i)(^|\\)(bin|obj|publish|node_modules|\.vs|Data\\bench)(\\|$)') {
        return $true
    }
    if ([System.IO.Path]::GetFileName($FullName) -match '(?i)_wpftmp\.csproj$') {
        return $true
    }
    return $false
}

function Read-MsBuildXml {
    param([string]$Path)

    try {
        $document = New-Object System.Xml.XmlDocument
        $document.PreserveWhitespace = $true
        $document.Load($Path)
        return $document
    }
    catch {
        $relative = Get-RepoRelativePath -FullName $Path
        Add-Failure ("Invalid MSBuild XML in " + $relative + ": " + $_.Exception.Message)
        return $null
    }
}

function Get-MsBuildProperties {
    param([System.Xml.XmlDocument]$Document)

    $properties = @{}
    foreach ($group in $Document.SelectNodes("//*[local-name()='PropertyGroup']")) {
        foreach ($child in $group.ChildNodes) {
            if ($child.NodeType -eq [System.Xml.XmlNodeType]::Element) {
                $properties[$child.LocalName] = $child.InnerText.Trim()
            }
        }
    }
    return $properties
}

function Resolve-MsBuildProperties {
    param(
        [string]$Value,
        [hashtable]$Properties
    )

    $resolved = $Value
    for ($iteration = 0; $iteration -lt 10; $iteration++) {
        $matches = [regex]::Matches($resolved, '\$\((?<name>[^)]+)\)')
        if ($matches.Count -eq 0) {
            break
        }

        $changed = $false
        foreach ($match in $matches) {
            $name = $match.Groups['name'].Value
            if ($Properties.ContainsKey($name)) {
                $resolved = $resolved.Replace($match.Value, [string]$Properties[$name])
                $changed = $true
            }
        }
        if (-not $changed) {
            break
        }
    }
    return $resolved.Trim()
}

function Test-IsFloatingPackageVersion {
    param([string]$Version)

    if ([string]::IsNullOrWhiteSpace($Version)) {
        return $false
    }

    $value = $Version.Trim()
    if ($value -match '\*' -or $value -match '^(?i:latest)$') {
        return $true
    }
    if ($value -match '^[\[\(]\s*,' -or $value -match ',\s*[\]\)]$') {
        return $true
    }
    return $false
}

function Get-ItemVersion {
    param([System.Xml.XmlElement]$Item)

    foreach ($attributeName in @('Version', 'VersionOverride')) {
        if ($Item.HasAttribute($attributeName)) {
            return $Item.GetAttribute($attributeName)
        }
    }
    foreach ($elementName in @('Version', 'VersionOverride')) {
        $element = $Item.SelectSingleNode("./*[local-name()='" + $elementName + "']")
        if ($null -ne $element) {
            return $element.InnerText
        }
    }
    return ''
}

function Test-LocalMarkdownLinks {
    $markdownFiles = Get-ChildItem -LiteralPath $RepoRoot -Filter '*.md' -Recurse -File |
        Where-Object { -not (Test-ShouldSkipPath -FullName $_.FullName) }

    $inlinePattern = '!?\[[^\]]*\]\(\s*(?<target><[^>]+>|[^)\s]+)'
    $referencePattern = '(?m)^[ \t]{0,3}\[[^\]]+\]:[ \t]*(?<target><[^>]+>|\S+)'

    foreach ($file in $markdownFiles) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        $targets = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($pattern in @($inlinePattern, $referencePattern)) {
            foreach ($match in [regex]::Matches($text, $pattern)) {
                $target = $match.Groups['target'].Value.Trim()
                if ($target.StartsWith('<') -and $target.EndsWith('>')) {
                    $target = $target.Substring(1, $target.Length - 2)
                }
                [void]$targets.Add($target)
            }
        }

        foreach ($target in $targets) {
            if ([string]::IsNullOrWhiteSpace($target) -or $target.StartsWith('#') -or $target.StartsWith('//')) {
                continue
            }
            if ($target -match '^[A-Za-z][A-Za-z0-9+.-]*:' -and $target -notmatch '^[A-Za-z]:[\\/]') {
                continue
            }

            $pathPart = $target.Split('#')[0].Split('?')[0]
            if ([string]::IsNullOrWhiteSpace($pathPart)) {
                continue
            }
            try {
                $pathPart = [System.Uri]::UnescapeDataString($pathPart).Replace('/', '\')
                $candidate = if ([System.IO.Path]::IsPathRooted($pathPart)) {
                    $pathPart
                }
                else {
                    Join-Path $file.DirectoryName $pathPart
                }
                if (-not (Test-Path -LiteralPath $candidate)) {
                    $relative = Get-RepoRelativePath -FullName $file.FullName
                    Add-Failure ("Broken local Markdown link in " + $relative + ": '" + $target + "'")
                }
            }
            catch {
                $relative = Get-RepoRelativePath -FullName $file.FullName
                Add-Failure ("Invalid local Markdown link in " + $relative + ": '" + $target + "'")
            }
        }
    }
}

function Get-WorkflowEventBranches {
    param(
        [string]$WorkflowPath,
        [string]$EventName
    )

    $lines = Get-Content -LiteralPath $WorkflowPath
    $pullRequestIndex = -1
    $pullRequestIndent = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $eventPattern = '^(?<indent>[ ]*)' + [regex]::Escape($EventName) + '\s*:\s*(?:#.*)?$'
        if ($lines[$index] -match $eventPattern) {
            $pullRequestIndex = $index
            $pullRequestIndent = $Matches['indent'].Length
            break
        }
    }
    if ($pullRequestIndex -lt 0) {
        return @()
    }

    for ($index = $pullRequestIndex + 1; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line -match '^\s*(?:#.*)?$') {
            continue
        }
        $indent = ([regex]::Match($line, '^[ ]*')).Value.Length
        if ($indent -le $pullRequestIndent) {
            break
        }
        if ($line -match '^(?<indent>[ ]*)branches\s*:\s*\[(?<values>[^\]]*)\]\s*(?:#.*)?$') {
            return @($Matches['values'].Split(',') | ForEach-Object { $_.Trim().Trim('"', "'") } | Where-Object { $_ })
        }
        if ($line -notmatch '^(?<indent>[ ]*)branches\s*:\s*(?:#.*)?$') {
            continue
        }

        $branchesIndent = $Matches['indent'].Length
        $branches = New-Object System.Collections.Generic.List[string]
        for ($branchIndex = $index + 1; $branchIndex -lt $lines.Count; $branchIndex++) {
            $branchLine = $lines[$branchIndex]
            if ($branchLine -match '^\s*(?:#.*)?$') {
                continue
            }
            $branchIndent = ([regex]::Match($branchLine, '^[ ]*')).Value.Length
            if ($branchIndent -le $branchesIndent) {
                break
            }
            if ($branchLine -match '^\s*-\s*(?<value>[^#]+?)\s*(?:#.*)?$') {
                $branches.Add($Matches['value'].Trim().Trim('"', "'")) | Out-Null
            }
        }
        return $branches.ToArray()
    }
    return @()
}

function Test-PrimaryBranchClaim {
    param([string]$RelativePath)

    $path = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $text = (Get-Content -LiteralPath $path -Raw) -replace '[`*_]', ''
    $patterns = @(
        '(?im)(?:primary|default)\s+integration\s+(?:branch|target)\s*(?:is|:)?\s*(?<branch>master|main|develop|dev)\b',
        '(?im)\b(?<branch>master|main|develop|dev)\s+is\s+the\s+(?:primary|default)\s+integration\s+(?:branch|target)\b',
        '(?im)^\s*\|?\s*(?<branch>master|main|develop|dev)\s*\|[^\r\n]*(?:primary|default)\s+integration\s+(?:branch|target)\b'
    )

    $claims = New-Object System.Collections.Generic.List[string]
    foreach ($pattern in $patterns) {
        foreach ($match in [regex]::Matches($text, $pattern)) {
            $claims.Add($match.Groups['branch'].Value) | Out-Null
        }
    }
    if ($claims.Count -eq 0) {
        Add-Failure ($RelativePath + ' must explicitly identify master as the primary/default integration branch')
        return
    }
    foreach ($branch in $claims) {
        if ($branch -ne 'master') {
            Add-Failure ($RelativePath + " contradicts the branch policy by naming '" + $branch + "' as the integration branch")
        }
    }
}

Write-Host "=== Agent docs integrity check ===" -ForegroundColor Cyan
Write-Host ("Repo: " + $RepoRoot)
Write-Host ""

$requiredFiles = @(
    'AGENTS.md',
    'CONTRIBUTING.md',
    'README.md',
    'LICENSE',
    'RatScanner.sln',
    'dev.bat',
    'publish.bat',
    'dotnet-tools.json',
    '.csharpierrc.json',
    'scripts\dev.ps1',
    'scripts\setup-data.ps1',
    'scripts\Expand-Zip.ps1',
    'scripts\check-agent-docs.ps1',
    'scripts\test-agent-docs.ps1',
    'scripts\lint-markdown.ps1',
    'package.json',
    'package-lock.json',
    '.markdownlint-cli2.jsonc',
    '.markdownlint.json',
    'src\App\RatScanner.csproj',
    'src\ScanEngine\RatEye.csproj',
    'src\ScanEngine\VENDOR.md',
    'tests\RatScanner.Tests\RatScanner.Tests.csproj',
    'src\App\AGENTS.md',
    'src\ScanEngine\AGENTS.md',
    'tests\AGENTS.md',
    'docs\agent-context\README.md',
    'docs\agent-context\project-overview.md',
    'docs\agent-context\architecture.md',
    'docs\agent-context\repository-map.md',
    'docs\agent-context\local-development.md',
    'docs\agent-context\build-and-validation.md',
    'docs\agent-context\app-ui.md',
    'docs\agent-context\scan-engine.md',
    'docs\agent-context\data-integrations.md',
    'docs\agent-context\configuration-and-cache.md',
    'docs\agent-context\localization.md',
    'docs\agent-context\dependency-management.md',
    'docs\agent-context\release-and-versioning.md',
    'docs\agent-context\contribution-workflow.md',
    '.github\workflows\build.yml'
)

foreach ($relative in $requiredFiles) {
    [void](Assert-PathExists -RelativePath $relative -Reason 'Required path')
}

$agentsPath = Join-Path $RepoRoot 'AGENTS.md'
$contextIndexPath = Join-Path $RepoRoot 'docs\agent-context\README.md'
$contextDir = Join-Path $RepoRoot 'docs\agent-context'
if ((Test-Path -LiteralPath $agentsPath) -and (Test-Path -LiteralPath $contextDir)) {
    $agentsText = Get-Content -LiteralPath $agentsPath -Raw
    $contextIndexText = if (Test-Path -LiteralPath $contextIndexPath) {
        Get-Content -LiteralPath $contextIndexPath -Raw
    }
    else {
        ''
    }
    $contextFiles = Get-ChildItem -LiteralPath $contextDir -Filter '*.md' -File
    foreach ($file in $contextFiles) {
        $rootNeedle = 'docs/agent-context/' + $file.Name
        if ($agentsText -notlike ('*' + $rootNeedle + '*')) {
            Add-Failure ('AGENTS.md routing does not mention context file: ' + $file.Name)
        }
        if ($file.Name -ne 'README.md' -and $contextIndexText -notlike ('*(' + $file.Name + ')*')) {
            Add-Failure ('Context index does not link context file: ' + $file.Name)
        }
    }

    foreach ($nested in @('src/App/AGENTS.md', 'src/ScanEngine/AGENTS.md', 'tests/AGENTS.md')) {
        if ($agentsText -notlike ('*' + $nested + '*')) {
            Add-Failure ('AGENTS.md nested instructions table missing: ' + $nested)
        }
    }
}

Test-LocalMarkdownLinks

$projectFiles = Get-ChildItem -LiteralPath $RepoRoot -Filter '*.csproj' -Recurse -File |
    Where-Object { -not (Test-ShouldSkipPath -FullName $_.FullName) }
$projectDocuments = @{}
foreach ($projectFile in $projectFiles) {
    $document = Read-MsBuildXml -Path $projectFile.FullName
    if ($null -eq $document) {
        continue
    }
    $projectDocuments[$projectFile.FullName] = $document
    $relative = Get-RepoRelativePath -FullName $projectFile.FullName
    $properties = Get-MsBuildProperties -Document $document

    foreach ($package in $document.SelectNodes("//*[local-name()='PackageReference']")) {
        $packageId = if ($package.HasAttribute('Include')) {
            $package.GetAttribute('Include').Trim()
        }
        else {
            $package.GetAttribute('Update').Trim()
        }
        if ($packageId -eq 'RatEye') {
            Add-Failure ('NuGet RatEye PackageReference found in ' + $relative + ' - use the in-tree ProjectReference')
        }
        $rawVersion = Get-ItemVersion -Item $package
        $resolvedVersion = Resolve-MsBuildProperties -Value $rawVersion -Properties $properties
        if ($resolvedVersion -match '\$\(') {
            Add-Failure ("Package version for '" + $packageId + "' in " + $relative + ' could not be resolved structurally: ' + $rawVersion)
        }
        elseif (Test-IsFloatingPackageVersion -Version $resolvedVersion) {
            Add-Failure ("Floating or open-ended package version for '" + $packageId + "' in " + $relative + ': ' + $resolvedVersion)
        }
    }
}

$centralPackageFiles = Get-ChildItem -LiteralPath $RepoRoot -Filter 'Directory.Packages.props' -Recurse -File |
    Where-Object { -not (Test-ShouldSkipPath -FullName $_.FullName) }
foreach ($centralFile in $centralPackageFiles) {
    $document = Read-MsBuildXml -Path $centralFile.FullName
    if ($null -eq $document) {
        continue
    }
    $relative = Get-RepoRelativePath -FullName $centralFile.FullName
    $properties = Get-MsBuildProperties -Document $document
    foreach ($package in $document.SelectNodes("//*[local-name()='PackageVersion']")) {
        $packageId = $package.GetAttribute('Include').Trim()
        $rawVersion = Get-ItemVersion -Item $package
        $resolvedVersion = Resolve-MsBuildProperties -Value $rawVersion -Properties $properties
        if ($resolvedVersion -match '\$\(' -or (Test-IsFloatingPackageVersion -Version $resolvedVersion)) {
            Add-Failure ("Floating, open-ended, or unresolved central package version for '" + $packageId + "' in " + $relative + ': ' + $rawVersion)
        }
    }
}

$appCsprojPath = Join-Path $RepoRoot 'src\App\RatScanner.csproj'
$scanCsprojPath = Join-Path $RepoRoot 'src\ScanEngine\RatEye.csproj'
if ($projectDocuments.ContainsKey($appCsprojPath)) {
    $appDocument = $projectDocuments[$appCsprojPath]
    $expectedScanPath = [System.IO.Path]::GetFullPath($scanCsprojPath)
    $hasScanProjectReference = $false
    foreach ($reference in $appDocument.SelectNodes("//*[local-name()='ProjectReference']")) {
        $include = $reference.GetAttribute('Include')
        if ([string]::IsNullOrWhiteSpace($include)) {
            continue
        }
        try {
            $referencedPath = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $appCsprojPath) $include))
            if ($referencedPath.Equals($expectedScanPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $hasScanProjectReference = $true
            }
        }
        catch {
            Add-Failure ("Invalid ProjectReference path in src\App\RatScanner.csproj: " + $include)
        }
    }
    if (-not $hasScanProjectReference) {
        Add-Failure 'App must ProjectReference src\ScanEngine\RatEye.csproj'
    }
    if ($appDocument.SelectNodes("//*[local-name()='Version']").Count -eq 0) {
        Add-Failure 'App csproj missing Version element'
    }
}

if ($projectDocuments.ContainsKey($scanCsprojPath)) {
    $scanDocument = $projectDocuments[$scanCsprojPath]
    $isPackable = $scanDocument.SelectSingleNode("//*[local-name()='IsPackable']")
    $generatePackage = $scanDocument.SelectSingleNode("//*[local-name()='GeneratePackageOnBuild']")
    if ($null -eq $isPackable -or $isPackable.InnerText.Trim() -ne 'false') {
        Add-Failure 'ScanEngine must set IsPackable=false'
    }
    if ($null -eq $generatePackage -or $generatePackage.InnerText.Trim() -ne 'false') {
        Add-Failure 'ScanEngine must set GeneratePackageOnBuild=false'
    }
}

foreach ($branchDocument in @('AGENTS.md', 'CONTRIBUTING.md', 'README.md', 'docs\agent-context\contribution-workflow.md')) {
    Test-PrimaryBranchClaim -RelativePath $branchDocument
}

$ciPath = Join-Path $RepoRoot '.github\workflows\build.yml'
if (Test-Path -LiteralPath $ciPath) {
    $pullRequestBranches = @(Get-WorkflowEventBranches -WorkflowPath $ciPath -EventName 'pull_request')
    if ($pullRequestBranches.Count -eq 0) {
        Add-Failure 'CI workflow must declare an explicit pull_request branches list containing master'
    }
    elseif ($pullRequestBranches -notcontains 'master') {
        Add-Failure ('CI pull_request branches must include master; found: ' + ($pullRequestBranches -join ', '))
    }

    $pushBranches = @(Get-WorkflowEventBranches -WorkflowPath $ciPath -EventName 'push')
    if ($pushBranches.Count -eq 0) {
        Add-Failure 'CI workflow must declare an explicit push branches list containing master'
    }
    elseif ($pushBranches -notcontains 'master') {
        Add-Failure ('CI push branches must include master; found: ' + ($pushBranches -join ', '))
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "All documentation integrity checks passed." -ForegroundColor Green
    exit 0
}

Write-Host (($failures.Count.ToString()) + " failure(s).") -ForegroundColor Red
exit 1
