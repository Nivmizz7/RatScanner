<#
.SYNOPSIS
  Objective documentation-integrity checks for the agent control plane and context docs.

.DESCRIPTION
  Verifies facts that can be tested reliably without interpreting free-form prose:
  - Required AGENTS.md, context, tooling, project, and workflow files exist
  - Root AGENTS.md and the context index route every context document
  - Local Markdown links resolve
  - App structurally ProjectReferences standalone RatEye source (no NuGet RatEye)
  - RatEye submodule path and URL remain explicit
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

function Get-GitSubmoduleSections {
    param([string]$Text)

    $sections = New-Object System.Collections.Generic.List[hashtable]
    $current = $null
    foreach ($line in [regex]::Split($Text, '\r?\n')) {
        if ($line -match '^\s*\[submodule\s+"(?<name>[^"]+)"\]\s*$') {
            $current = @{
                Name = $Matches['name']
                Path = ''
                Url  = ''
            }
            $sections.Add($current) | Out-Null
            continue
        }
        if ($line -match '^\s*\[.+\]\s*$') {
            $current = $null
            continue
        }
        if ($null -eq $current -or $line -notmatch '^\s*(?<key>path|url)\s*=\s*(?<value>.*?)\s*$') {
            continue
        }

        $current[$Matches['key'].Substring(0, 1).ToUpperInvariant() + $Matches['key'].Substring(1)] = $Matches['value']
    }
    return $sections
}

<#
.SYNOPSIS
  True when a command AST sits in a statically unreachable branch.

.DESCRIPTION
  FindAll walks unreachable code too, so an assertion parked in `if ($false) { ... }` or in the
  `else` of `if ($true) { ... }` would otherwise satisfy a presence guard while never running.
#>
function Test-AstIsTopLevelStatement {
    param([Parameter(Mandatory = $true)][System.Management.Automation.Language.Ast]$Node)

    for ($parent = $Node.Parent; $null -ne $parent; $parent = $parent.Parent) {
        if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
            $parent -is [System.Management.Automation.Language.ScriptBlockExpressionAst] -or
            $parent -is [System.Management.Automation.Language.IfStatementAst] -or
            $parent -is [System.Management.Automation.Language.LoopStatementAst] -or
            $parent -is [System.Management.Automation.Language.SwitchStatementAst] -or
            $parent -is [System.Management.Automation.Language.TryStatementAst] -or
            $parent -is [System.Management.Automation.Language.TrapStatementAst]) {
            return $false
        }
    }
    return $true
}

function Test-AstIsUnreachable {
    param([Parameter(Mandatory = $true)][System.Management.Automation.Language.Ast]$Node)

    for ($parent = $Node.Parent; $null -ne $parent; $parent = $parent.Parent) {
        if ($parent -is [System.Management.Automation.Language.WhileStatementAst] -or
            $parent -is [System.Management.Automation.Language.ForStatementAst]) {
            $condition = $parent.Condition
            if ($null -ne $condition -and
                $condition.Extent.Text.Trim() -match '(?i)^\(*\s*\$false\s*\)*$') {
                return $true
            }
            continue
        }
        if ($parent -isnot [System.Management.Automation.Language.IfStatementAst]) {
            continue
        }
        $precedingClauseIsStaticallyTrue = $false
        foreach ($clause in $parent.Clauses) {
            $conditionText = $clause.Item1.Extent.Text.Trim()
            $nodeIsInClause =
                $clause.Item2.Extent.StartOffset -le $Node.Extent.StartOffset -and
                $clause.Item2.Extent.EndOffset -ge $Node.Extent.EndOffset
            if ($nodeIsInClause -and
                ($precedingClauseIsStaticallyTrue -or
                    $conditionText -match '(?i)^\(*\s*\$false\s*\)*$')) {
                return $true
            }
            if ($conditionText -match '(?i)^\(*\s*\$true\s*\)*$') {
                $precedingClauseIsStaticallyTrue = $true
            }
        }
        # An else body is dead when any preceding clause is statically true.
        if ($null -ne $parent.ElseClause -and
            $precedingClauseIsStaticallyTrue -and
            $parent.ElseClause.Extent.StartOffset -le $Node.Extent.StartOffset -and
            $parent.ElseClause.Extent.EndOffset -ge $Node.Extent.EndOffset) {
            return $true
        }
    }
    return $false
}

function Test-ScriptDotSourcesFile {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    $parseErrors = $null
    $scriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $ScriptPath, [ref]$null, [ref]$parseErrors)
    if (@($parseErrors).Count -gt 0) {
        return [pscustomobject]@{ Parsed = $false; Matches = $false; Ast = $null }
    }

    $contractVariables = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($assignment in @($scriptAst.FindAll({
        param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst]
    }, $true))) {
        if ($assignment.Left -isnot [System.Management.Automation.Language.VariableExpressionAst] -or
            -not (Test-AstIsTopLevelStatement -Node $assignment)) {
            continue
        }
        $referencesFile = @($assignment.Right.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            [System.IO.Path]::GetFileName($node.Value) -eq $FileName
        }, $true)).Count -gt 0
        if ($referencesFile) {
            [void]$contractVariables.Add($assignment.Left.VariablePath.UserPath)
        }
    }

    $commands = @($scriptAst.FindAll({
        param($node) $node -is [System.Management.Automation.Language.CommandAst]
    }, $true))
    foreach ($command in $commands) {
        if ($command.InvocationOperator -ne [System.Management.Automation.Language.TokenKind]::Dot -or
            -not (Test-AstIsTopLevelStatement -Node $command)) {
            continue
        }
        $referencesFile = @($command.FindAll({
            param($node)
            ($node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                [System.IO.Path]::GetFileName($node.Value) -eq $FileName) -or
            ($node -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $contractVariables.Contains($node.VariablePath.UserPath))
        }, $true)).Count -gt 0
        if ($referencesFile) {
            return [pscustomobject]@{ Parsed = $true; Matches = $true; Ast = $scriptAst }
        }
    }

    return [pscustomobject]@{ Parsed = $true; Matches = $false; Ast = $scriptAst }
}

function Test-DataContractStringAssignment {
    param(
        [Parameter(Mandatory = $true)][string]$ContractPath,
        [Parameter(Mandatory = $true)][string]$VariableName,
        [Parameter(Mandatory = $true)][scriptblock]$ValuePredicate
    )

    $parseErrors = $null
    $contractAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $ContractPath, [ref]$null, [ref]$parseErrors)
    if (@($parseErrors).Count -gt 0) {
        return [pscustomobject]@{ Parsed = $false; Matches = $false }
    }

    $assignments = @($contractAst.FindAll({
        param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst]
    }, $true))
    foreach ($assignment in $assignments) {
        if ($assignment.Left -isnot [System.Management.Automation.Language.VariableExpressionAst]) {
            continue
        }
        if ($assignment.Left.VariablePath.UserPath -ne $VariableName) {
            continue
        }
        if (Test-AstIsUnreachable -Node $assignment) {
            continue
        }
        $right = $assignment.Right
        if ($right -is [System.Management.Automation.Language.CommandExpressionAst]) {
            $right = $right.Expression
        }
        if ($right -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            (& $ValuePredicate $right.Value)) {
            return [pscustomobject]@{ Parsed = $true; Matches = $true }
        }
    }
    return [pscustomobject]@{ Parsed = $true; Matches = $false }
}

function Test-DataContractRepositoryAssignment {
    param(
        [Parameter(Mandatory = $true)][string]$ContractPath,
        [Parameter(Mandatory = $true)][string]$ExpectedRepository
    )

    return Test-DataContractStringAssignment `
        -ContractPath $ContractPath `
        -VariableName 'script:RatScannerDataRepository' `
        -ValuePredicate { param($value) $value -eq $ExpectedRepository }
}

function Get-WorkflowBlockScalarMap {
    param([AllowEmptyString()][string[]]$Lines)

    $blockScalarLines = [bool[]]::new($Lines.Count)
    for ($header = 0; $header -lt $Lines.Count; $header++) {
        if ($blockScalarLines[$header]) {
            continue
        }
        if (
            $Lines[$header] -notmatch
            '^(?<indent>[ ]*)(?:-\s+)?[^#].*:\s*[|>](?:[1-9][+-]?|[+-][1-9]?)?\s*(?:#.*)?$'
        ) {
            continue
        }

        $headerIndent = $Matches['indent'].Length
        for ($content = $header + 1; $content -lt $Lines.Count; $content++) {
            if ($Lines[$content] -match '^\s*$') {
                $blockScalarLines[$content] = $true
                continue
            }
            $contentIndent = ([regex]::Match($Lines[$content], '^[ ]*')).Value.Length
            if ($contentIndent -le $headerIndent) {
                break
            }
            $blockScalarLines[$content] = $true
        }
    }
    return $blockScalarLines
}

<#
.SYNOPSIS
  Returns the ordered steps of every job in a workflow as line ranges.

.DESCRIPTION
  String offsets cannot distinguish a real step from prose in a comment or block scalar, so
  ordering and presence checks need step boundaries. Each result carries the step's own lines with
  comments stripped, so commented-out text cannot satisfy a guard.
#>
function Get-WorkflowStepRanges {
    param([Parameter(Mandatory = $true)][string]$WorkflowText)

    $lines = [regex]::Split($WorkflowText, '\r?\n')
    $blockScalarLines = Get-WorkflowBlockScalarMap -Lines $lines
    $stepStarterPattern = '^(?<indent>\s*)-\s+[^#\s][^:]*\s*:'
    $steps = New-Object System.Collections.Generic.List[object]

    for ($stepsIndex = 0; $stepsIndex -lt $lines.Count; $stepsIndex++) {
        if (
            $blockScalarLines[$stepsIndex] -or
            $lines[$stepsIndex] -notmatch '^(?<indent>[ ]*)steps\s*:\s*(?:#.*)?$'
        ) {
            continue
        }

        $stepsIndent = $Matches['indent'].Length
        $stepsEnd = $lines.Count
        for ($candidate = $stepsIndex + 1; $candidate -lt $lines.Count; $candidate++) {
            if ($blockScalarLines[$candidate] -or $lines[$candidate] -match '^\s*(?:#.*)?$') {
                continue
            }
            if (([regex]::Match($lines[$candidate], '^[ ]*')).Value.Length -le $stepsIndent) {
                $stepsEnd = $candidate
                break
            }
        }

        $stepIndent = -1
        for ($index = $stepsIndex + 1; $index -lt $stepsEnd; $index++) {
            if ($blockScalarLines[$index] -or $lines[$index] -notmatch $stepStarterPattern) {
                continue
            }
            $candidateStepIndent = $Matches['indent'].Length
            if ($stepIndent -lt 0) {
                $stepIndent = $candidateStepIndent
            }
            if ($candidateStepIndent -ne $stepIndent) {
                continue
            }

            $stepEnd = $stepsEnd
            for ($candidate = $index + 1; $candidate -lt $stepsEnd; $candidate++) {
                if (
                    -not $blockScalarLines[$candidate] -and
                    $lines[$candidate] -match $stepStarterPattern -and
                    $Matches['indent'].Length -eq $stepIndent
                ) {
                    $stepEnd = $candidate
                    break
                }
            }

            $effectiveLines = New-Object System.Collections.Generic.List[string]
            for ($line = $index; $line -lt $stepEnd; $line++) {
                # Drop comment-only lines and trailing comments so commented text cannot satisfy a
                # guard. Shell comments inside a run block are stripped for the same reason.
                $text = [regex]::Replace($lines[$line], '(^|\s)#.*$', '')
                if (-not [string]::IsNullOrWhiteSpace($text)) {
                    $effectiveLines.Add($text)
                }
            }

            $steps.Add([pscustomobject]@{
                StartIndex     = $index
                EndIndex       = $stepEnd
                EffectiveText  = ($effectiveLines -join "`n")
            })
            $index = $stepEnd - 1
        }
        $stepsIndex = $stepsEnd - 1
    }
    return $steps.ToArray()
}

function Test-CheckoutUsesRecursiveSubmodules {
    param([string]$WorkflowText)

    $lines = [regex]::Split($WorkflowText, '\r?\n')
    $blockScalarLines = Get-WorkflowBlockScalarMap -Lines $lines

    $stepStarterPattern = '^(?<indent>\s*)-\s+[^#\s][^:]*\s*:'
    for ($stepsIndex = 0; $stepsIndex -lt $lines.Count; $stepsIndex++) {
        if (
            $blockScalarLines[$stepsIndex] -or
            $lines[$stepsIndex] -notmatch '^(?<indent>[ ]*)steps\s*:\s*(?:#.*)?$'
        ) {
            continue
        }

        $stepsIndent = $Matches['indent'].Length
        $stepsEnd = $lines.Count
        for ($candidate = $stepsIndex + 1; $candidate -lt $lines.Count; $candidate++) {
            if ($blockScalarLines[$candidate]) {
                continue
            }
            if ($lines[$candidate] -match '^\s*(?:#.*)?$') {
                continue
            }
            $candidateIndent = ([regex]::Match($lines[$candidate], '^[ ]*')).Value.Length
            if ($candidateIndent -le $stepsIndent) {
                $stepsEnd = $candidate
                break
            }
        }

        $stepIndent = -1
        for ($index = $stepsIndex + 1; $index -lt $stepsEnd; $index++) {
            if ($blockScalarLines[$index] -or $lines[$index] -notmatch $stepStarterPattern) {
                continue
            }

            $candidateStepIndent = $Matches['indent'].Length
            if ($stepIndent -lt 0) {
                $stepIndent = $candidateStepIndent
            }
            if ($candidateStepIndent -ne $stepIndent) {
                continue
            }

            $stepEnd = $stepsEnd
            for ($candidate = $index + 1; $candidate -lt $stepsEnd; $candidate++) {
                if (
                    -not $blockScalarLines[$candidate] -and
                    $lines[$candidate] -match $stepStarterPattern -and
                    $Matches['indent'].Length -eq $stepIndent
                ) {
                    $stepEnd = $candidate
                    break
                }
            }

            $propertyIndent = $stepIndent + 2
            $usesCheckout = $lines[$index] -match '^[ ]*-\s+uses\s*:\s*actions/checkout@'
            if (-not $usesCheckout) {
                for ($candidate = $index + 1; $candidate -lt $stepEnd; $candidate++) {
                    if ($blockScalarLines[$candidate]) {
                        continue
                    }
                    if ($lines[$candidate] -notmatch '^(?<indent>[ ]*)uses\s*:\s*actions/checkout@') {
                        continue
                    }
                    if ($Matches['indent'].Length -eq $propertyIndent) {
                        $usesCheckout = $true
                        break
                    }
                }
            }
            if (-not $usesCheckout) {
                $index = $stepEnd - 1
                continue
            }

            $checkoutCondition = $null
            if ($lines[$index] -match '^[ ]*-\s+if\s*:\s*(?<condition>.*?)\s*$') {
                $checkoutCondition = $Matches['condition']
            }
            for ($candidate = $index + 1; $candidate -lt $stepEnd; $candidate++) {
                if ($blockScalarLines[$candidate]) {
                    continue
                }
                if (
                    $lines[$candidate] -match '^(?<indent>[ ]*)if\s*:\s*(?<condition>.*?)\s*$' -and
                    $Matches['indent'].Length -eq $propertyIndent
                ) {
                    $checkoutCondition = $Matches['condition']
                    break
                }
            }
            if (
                $null -ne $checkoutCondition -and
                $checkoutCondition -notmatch
                '(?i)^(?:true|\$\{\{\s*true\s*\}\})(?:\s+#.*)?\s*$'
            ) {
                $index = $stepEnd - 1
                continue
            }

            for ($candidate = $index + 1; $candidate -lt $stepEnd; $candidate++) {
                if ($blockScalarLines[$candidate]) {
                    continue
                }
                if (
                    $lines[$candidate] -match '^(?<indent>[ ]*)with\s*:\s*$' -and
                    $Matches['indent'].Length -eq $propertyIndent
                ) {
                    $withIndent = $Matches['indent'].Length
                    $directEntryIndent = -1
                    for ($entry = $candidate + 1; $entry -lt $stepEnd; $entry++) {
                        if ($blockScalarLines[$entry]) {
                            continue
                        }
                        if ($lines[$entry] -match '^\s*(#.*)?$') {
                            continue
                        }
                        $entryIndent = ([regex]::Match($lines[$entry], '^[ ]*')).Value.Length
                        if ($entryIndent -le $withIndent) {
                            break
                        }
                        if ($directEntryIndent -lt 0) {
                            $directEntryIndent = $entryIndent
                        }
                        if (
                            $entryIndent -eq $directEntryIndent -and
                            $lines[$entry] -match
                            '^\s*submodules\s*:\s*recursive(?:\s+#.*)?\s*$'
                        ) {
                            return $true
                        }
                    }
                }
            }
            $index = $stepEnd - 1
        }
        $stepsIndex = $stepsEnd - 1
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
    '.gitmodules',
    'RatScanner.sln',
    'dev.bat',
    'publish.bat',
    'dotnet-tools.json',
    'Directory.Build.targets',
    '.csharpierrc.json',
    'scripts\dev.ps1',
    'scripts\setup-data.ps1',
    'scripts\RatScannerData.ps1',
    'scripts\test-data-validation.ps1',
    'scripts\verify-package.ps1',
    'scripts\Expand-Zip.ps1',
    'scripts\check-agent-docs.ps1',
    'scripts\test-agent-docs.ps1',
    'scripts\lint-markdown.ps1',
    'package.json',
    'package-lock.json',
    '.markdownlint-cli2.jsonc',
    '.markdownlint.json',
    'src\App\RatScanner.csproj',
    'src\ScanEngine\RatEye\RatEye.csproj',
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

$gitmodulesPath = Join-Path $RepoRoot '.gitmodules'
if (Test-Path -LiteralPath $gitmodulesPath) {
    $gitmodulesText = Get-Content -LiteralPath $gitmodulesPath -Raw
    $ratEyeSubmodules = @(
        Get-GitSubmoduleSections -Text $gitmodulesText |
            Where-Object { $_.Path.Replace('\', '/') -eq 'src/ScanEngine' }
    )
    if ($ratEyeSubmodules.Count -ne 1) {
        Add-Failure '.gitmodules must map RatEye to src/ScanEngine'
    }
    elseif ($ratEyeSubmodules[0].Url -ne 'https://github.com/tarkovtracker-org/RatEye.git') {
        Add-Failure '.gitmodules must use https://github.com/tarkovtracker-org/RatEye.git'
    }
}

$dataContractPath = Join-Path $RepoRoot 'scripts\RatScannerData.ps1'
$setupDataPath = Join-Path $RepoRoot 'scripts\setup-data.ps1'
$publishPath = Join-Path $RepoRoot 'publish.bat'
$ciPath = Join-Path $RepoRoot '.github\workflows\build.yml'
if ((Test-Path -LiteralPath $dataContractPath) -and (Test-Path -LiteralPath $setupDataPath)) {
    $dataContractText = Get-Content -LiteralPath $dataContractPath -Raw
    $setupDataText = Get-Content -LiteralPath $setupDataPath -Raw
    # Validate the assignment through the AST: neither a comment nor a string literal elsewhere in
    # the file may authorize a different data source.
    $repositoryAssignment = Test-DataContractRepositoryAssignment `
        -ContractPath $dataContractPath `
        -ExpectedRepository 'tarkovtracker-org/RatScannerData'
    if (-not $repositoryAssignment.Parsed) {
        Add-Failure 'scripts\RatScannerData.ps1 must parse without errors'
    }
    elseif (-not $repositoryAssignment.Matches) {
        Add-Failure 'RatScannerData contract must use tarkovtracker-org/RatScannerData'
    }
    $releaseTagAssignment = Test-DataContractStringAssignment `
        -ContractPath $dataContractPath `
        -VariableName 'script:RatScannerDataReleaseTag' `
        -ValuePredicate { param($value) $value -match '^data-[0-9a-f]{16}$' }
    if ($releaseTagAssignment.Parsed -and -not $releaseTagAssignment.Matches) {
        Add-Failure 'RatScannerData contract must pin a content-addressed data release tag'
    }
    $setupContractSource = Test-ScriptDotSourcesFile `
        -ScriptPath $setupDataPath `
        -FileName 'RatScannerData.ps1'
    if (-not $setupContractSource.Parsed) {
        Add-Failure 'scripts\setup-data.ps1 must parse without errors'
    }
    elseif (-not $setupContractSource.Matches) {
        Add-Failure 'scripts\setup-data.ps1 must use scripts\RatScannerData.ps1'
    }
}

foreach ($activePath in @($setupDataPath, $publishPath, $ciPath)) {
    if (-not (Test-Path -LiteralPath $activePath)) {
        continue
    }
    $activeText = Get-Content -LiteralPath $activePath -Raw
    # Match the path segment, not an owner-qualified URL: reintroducing the unpinned latest release
    # is a regression regardless of which org it points at.
    if ($activeText -like '*RatScannerData/releases/latest*') {
        Add-Failure ((Get-RepoRelativePath -FullName $activePath) + ' must not download the old unpinned RatScannerData latest release')
    }
}
if (Test-Path -LiteralPath $publishPath) {
    $publishText = Get-Content -LiteralPath $publishPath -Raw
    if ($publishText -notlike '*scripts\setup-data.ps1*') {
        Add-Failure 'publish.bat must delegate RatScannerData installation to scripts\setup-data.ps1'
    }
    if ($publishText -notlike '*scripts\verify-package.ps1*') {
        Add-Failure 'publish.bat must verify the packaged archive with scripts\verify-package.ps1'
    }
}

$verifyPackagePath = Join-Path $RepoRoot 'scripts\verify-package.ps1'
if (Test-Path -LiteralPath $verifyPackagePath) {
    $verifyContractSource = Test-ScriptDotSourcesFile `
        -ScriptPath $verifyPackagePath `
        -FileName 'RatScannerData.ps1'
    if (-not $verifyContractSource.Parsed) {
        Add-Failure 'scripts\verify-package.ps1 must parse without errors'
    }
    else {
        $verifyAst = $verifyContractSource.Ast
        $commandAsts = @($verifyAst.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
        $invokesAssertion = @($commandAsts | Where-Object {
            $_.InvocationOperator -ne [System.Management.Automation.Language.TokenKind]::Dot -and
            $_.GetCommandName() -eq 'Assert-RatScannerDataPackage' -and
            (Test-AstIsTopLevelStatement -Node $_) -and
            -not (Test-AstIsUnreachable -Node $_)
        }).Count -gt 0
        $dotSourcesContract = $verifyContractSource.Matches
        if (-not $invokesAssertion -or -not $dotSourcesContract) {
            Add-Failure 'scripts\verify-package.ps1 must verify packages through the shared RatScannerData contract'
        }
    }
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
            Add-Failure ('NuGet RatEye PackageReference found in ' + $relative + ' - use the submodule ProjectReference')
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
$scanCsprojPath = Join-Path $RepoRoot 'src\ScanEngine\RatEye\RatEye.csproj'
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
        Add-Failure 'App must ProjectReference src\ScanEngine\RatEye\RatEye.csproj'
    }
    if ($appDocument.SelectNodes("//*[local-name()='Version']").Count -eq 0) {
        Add-Failure 'App csproj missing Version element'
    }
}

foreach ($branchDocument in @('AGENTS.md', 'CONTRIBUTING.md', 'README.md', 'docs\agent-context\contribution-workflow.md')) {
    Test-PrimaryBranchClaim -RelativePath $branchDocument
}

$ciPath = Join-Path $RepoRoot '.github\workflows\build.yml'
if (Test-Path -LiteralPath $ciPath) {
    $ciText = Get-Content -LiteralPath $ciPath -Raw
    if (-not (Test-CheckoutUsesRecursiveSubmodules -WorkflowText $ciText)) {
        Add-Failure 'CI checkout must initialize RatEye with submodules: recursive'
    }

    if ($ciText -notlike '*scripts/setup-data.ps1*') {
        Add-Failure 'CI Include Data must delegate to scripts/setup-data.ps1'
    }

    # Compare real steps, not raw offsets: a mention in a comment or block scalar must not let a
    # future workflow upload an unverified zip.
    $ciSteps = @(Get-WorkflowStepRanges -WorkflowText $ciText)
    $verifyStepIndex = -1
    $uploadStepIndex = -1
    for ($stepIndex = 0; $stepIndex -lt $ciSteps.Count; $stepIndex++) {
        $stepText = $ciSteps[$stepIndex].EffectiveText
        if ($verifyStepIndex -lt 0 -and
            $stepText -match '(?im)^\s*(?:&\s*)?(?:powershell(?:\.exe)?\s+.*?-File\s+)?scripts[\\/]verify-package\.ps1(?:\s|$)') {
            $verifyStepIndex = $stepIndex
        }
        if ($uploadStepIndex -lt 0 -and $stepText -match 'uses\s*:\s*\S*upload-artifact') {
            $uploadStepIndex = $stepIndex
        }
    }
    if ($verifyStepIndex -lt 0 -or ($uploadStepIndex -ge 0 -and $verifyStepIndex -gt $uploadStepIndex)) {
        Add-Failure 'CI must verify the packaged artifact with scripts/verify-package.ps1 before upload'
    }

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
