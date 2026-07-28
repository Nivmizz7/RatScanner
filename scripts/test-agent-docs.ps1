<#
.SYNOPSIS
  Adversarial regression tests for check-agent-docs.ps1.

.DESCRIPTION
  Builds an isolated repository fixture under a path containing spaces, invokes
  the integrity check from a different working directory, and verifies that
  representative structural failures are actionable and non-zero. The fixture
  is always deleted; the working tree is never mutated.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
}
else {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('RatScanner agent docs ' + [Guid]::NewGuid().ToString('N'))
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$fixtureRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
if (-not $fixtureRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw ("Refusing to create or clean a fixture outside the temp directory: " + $fixtureRoot)
}
$fixtureScript = Join-Path $fixtureRoot 'scripts\check-agent-docs.ps1'
$testCount = 0

function Copy-FixturePath {
    param([string]$RelativePath)

    $source = Join-Path $RepoRoot $RelativePath
    $destination = Join-Path $fixtureRoot $RelativePath
    $parent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
}

function Restore-FixtureFile {
    param([string]$RelativePath)

    $destination = Join-Path $fixtureRoot $RelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $RepoRoot $RelativePath) -Destination $destination -Force
}

function Invoke-IntegrityCheck {
    param(
        [bool]$ShouldPass,
        [string]$ExpectedText,
        [string]$Scenario
    )

    Push-Location ([System.IO.Path]::GetTempPath())
    try {
        $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $fixtureScript -RepoRoot $fixtureRoot 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($ShouldPass -and $exitCode -ne 0) {
        throw ("Scenario '" + $Scenario + "' should pass but exited " + $exitCode + ":`n" + $output)
    }
    if (-not $ShouldPass -and $exitCode -eq 0) {
        throw ("Scenario '" + $Scenario + "' should fail but exited 0:`n" + $output)
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedText) -and $output -notlike ('*' + $ExpectedText + '*')) {
        throw ("Scenario '" + $Scenario + "' did not report expected text '" + $ExpectedText + "':`n" + $output)
    }

    $script:testCount++
    Write-Host ("PASS: " + $Scenario) -ForegroundColor Green
}

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    foreach ($relative in @(
        '.github',
        'docs',
        'media',
        'scripts',
        'AGENTS.md',
        'CONTRIBUTING.md',
        'FAQ.md',
        'LICENSE',
        '.gitmodules',
        'README.md',
        'RatScanner.sln',
        'dev.bat',
        'publish.bat',
        'dotnet-tools.json',
        'Directory.Build.targets',
        '.csharpierrc.json',
        '.markdownlint-cli2.jsonc',
        '.markdownlint.json',
        'package.json',
        'package-lock.json',
        'src\App\AGENTS.md',
        'src\App\RatScanner.csproj',
        'src\ScanEngine\AGENTS.md',
        'src\ScanEngine\RatEye\RatEye.csproj',
        'tests\AGENTS.md',
        'tests\RatScanner.Tests\RatScanner.Tests.csproj'
    )) {
        Copy-FixturePath -RelativePath $relative
    }

    # Generated projects must not affect structural checks.
    $generatedProject = Join-Path $fixtureRoot 'src\App\obj\Generated.csproj'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $generatedProject) | Out-Null
    [System.IO.File]::WriteAllText(
        $generatedProject,
        '<Project><ItemGroup><PackageReference Include="RatEye" Version="*" /></ItemGroup></Project>'
    )

    Invoke-IntegrityCheck -ShouldPass $true -ExpectedText 'All documentation integrity checks passed.' -Scenario 'baseline from another cwd and a path containing spaces'

    $contextPath = Join-Path $fixtureRoot 'docs\agent-context\architecture.md'
    Move-Item -LiteralPath $contextPath -Destination ($contextPath + '.disabled')
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'Required path - missing: docs\agent-context\architecture.md' -Scenario 'missing required context document'
    }
    finally {
        Move-Item -LiteralPath ($contextPath + '.disabled') -Destination $contextPath
    }

    $scopedAgentsPath = Join-Path $fixtureRoot 'src\App\AGENTS.md'
    Move-Item -LiteralPath $scopedAgentsPath -Destination ($scopedAgentsPath + '.disabled')
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'Required path - missing: src\App\AGENTS.md' -Scenario 'missing scoped AGENTS.md'
    }
    finally {
        Move-Item -LiteralPath ($scopedAgentsPath + '.disabled') -Destination $scopedAgentsPath
    }

    $readmePath = Join-Path $fixtureRoot 'README.md'
    [System.IO.File]::AppendAllText($readmePath, "`r`n[Broken fixture link](docs/agent-context/does-not-exist.md)`r`n")
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'Broken local Markdown link in README.md' -Scenario 'invalid local Markdown link'
    }
    finally {
        Restore-FixtureFile -RelativePath 'README.md'
    }

    $appProjectPath = Join-Path $fixtureRoot 'src\App\RatScanner.csproj'
    [xml]$appProject = Get-Content -LiteralPath $appProjectPath -Raw
    $itemGroup = $appProject.CreateElement('ItemGroup')
    $package = $appProject.CreateElement('PackageReference')
    $package.SetAttribute('Include', 'RatEye')
    $package.SetAttribute('Version', '4.0.1')
    [void]$itemGroup.AppendChild($package)
    [void]$appProject.DocumentElement.AppendChild($itemGroup)
    $appProject.Save($appProjectPath)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'NuGet RatEye PackageReference found in src\App\RatScanner.csproj' -Scenario 'RatEye PackageReference reintroduction with structural XML formatting'
    }
    finally {
        Restore-FixtureFile -RelativePath 'src\App\RatScanner.csproj'
    }

    $gitmodulesPath = Join-Path $fixtureRoot '.gitmodules'
    [System.IO.File]::WriteAllText(
        $gitmodulesPath,
        "[submodule `"RatEye`"]`r`n`tpath = src/ScanEngine`r`n`turl = https://example.invalid/RatEye.git`r`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText '.gitmodules must use https://github.com/tarkovtracker-org/RatEye.git' -Scenario 'RatEye submodule points at an unexpected repository'
    }
    finally {
        Restore-FixtureFile -RelativePath '.gitmodules'
    }

    [System.IO.File]::WriteAllText(
        $gitmodulesPath,
        @"
[submodule "RatEye"]
	path = src/ScanEngine
[core]
	url = https://github.com/tarkovtracker-org/RatEye.git
"@
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText '.gitmodules must use https://github.com/tarkovtracker-org/RatEye.git' -Scenario 'non-submodule section cannot supply the RatEye URL'
    }
    finally {
        Restore-FixtureFile -RelativePath '.gitmodules'
    }

    [System.IO.File]::WriteAllText(
        $gitmodulesPath,
        @"
[submodule "RatEye"]
	path = src/ScanEngine
	url = https://example.invalid/RatEye.git
[submodule "Decoy"]
	path = src/Decoy
	url = https://github.com/tarkovtracker-org/RatEye.git
"@
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText '.gitmodules must use https://github.com/tarkovtracker-org/RatEye.git' -Scenario 'RatEye path and URL must belong to the same submodule stanza'
    }
    finally {
        Restore-FixtureFile -RelativePath '.gitmodules'
    }

    [xml]$appProject = Get-Content -LiteralPath $appProjectPath -Raw
    foreach ($reference in @($appProject.SelectNodes("//*[local-name()='ProjectReference']"))) {
        [void]$reference.ParentNode.RemoveChild($reference)
    }
    $appProject.Save($appProjectPath)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'App must ProjectReference src\ScanEngine\RatEye\RatEye.csproj' -Scenario 'missing RatEye submodule ProjectReference'
    }
    finally {
        Restore-FixtureFile -RelativePath 'src\App\RatScanner.csproj'
    }

    [xml]$appProject = Get-Content -LiteralPath $appProjectPath -Raw
    $package = $appProject.SelectSingleNode("//*[local-name()='PackageReference']")
    $package.RemoveAttribute('Version')
    $versionElement = $appProject.CreateElement('Version')
    $versionElement.InnerText = '9.*'
    [void]$package.AppendChild($versionElement)
    $appProject.Save($appProjectPath)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'Floating or open-ended package version' -Scenario 'floating package version in child XML element'
    }
    finally {
        Restore-FixtureFile -RelativePath 'src\App\RatScanner.csproj'
    }

    [System.IO.File]::WriteAllText($appProjectPath, '<Project><ItemGroup>')
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'Invalid MSBuild XML in src\App\RatScanner.csproj' -Scenario 'malformed project XML has an actionable error'
    }
    finally {
        Restore-FixtureFile -RelativePath 'src\App\RatScanner.csproj'
    }

    $workflowPath = Join-Path $fixtureRoot '.github\workflows\build.yml'
    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    [System.IO.File]::WriteAllText(
        $workflowPath,
        [regex]::Replace($workflow, '(?m)^\s*submodules:\s*recursive\r?\n', '')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI checkout must initialize RatEye with submodules: recursive' -Scenario 'CI omits RatEye submodule initialization'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $nestedSubmodules = [regex]::Replace(
        $workflow,
        '(?m)^\s*submodules:\s*recursive\r?\n',
        ''
    )
    $nestedSubmodules = [regex]::Replace(
        $nestedSubmodules,
        '(?m)(^\s*persist-credentials:\s*false\s*$)',
        "`$1`r`n          sparse-checkout: |`r`n            submodules: recursive",
        1
    )
    [System.IO.File]::WriteAllText($workflowPath, $nestedSubmodules)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI checkout must initialize RatEye with submodules: recursive' -Scenario 'nested YAML text cannot impersonate a direct checkout input'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $nonCheckoutSubmodules = [regex]::Replace(
        $workflow,
        '(?m)^\s*submodules:\s*recursive\r?\n',
        ''
    )
    $nonCheckoutSubmodules = [regex]::Replace(
        $nonCheckoutSubmodules,
        '(?m)(^\s*dotnet-version:\s*10\.0\.x\s*$)',
        "`$1`r`n          submodules: recursive",
        1
    )
    [System.IO.File]::WriteAllText($workflowPath, $nonCheckoutSubmodules)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI checkout must initialize RatEye with submodules: recursive' -Scenario 'recursive submodules on a non-checkout action do not satisfy checkout'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $conditionalStepSubmodules = [regex]::Replace(
        $workflow,
        '(?m)^\s*submodules:\s*recursive\r?\n',
        ''
    )
    $conditionalStepSubmodules = [regex]::Replace(
        $conditionalStepSubmodules,
        '(?m)(^\s*persist-credentials:\s*false\s*$)',
        "`$1`r`n      - if: `${{ always() }}`r`n        uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1`r`n        with:`r`n          submodules: recursive",
        1
    )
    [System.IO.File]::WriteAllText($workflowPath, $conditionalStepSubmodules)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI checkout must initialize RatEye with submodules: recursive' -Scenario 'conditional non-checkout step cannot supply recursive submodules for checkout'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $wrongPushWorkflow = [regex]::Replace(
        $workflow,
        '(?m)(^  push:\r?\n    branches:\r?\n      - )master',
        '${1}develop'
    )
    [System.IO.File]::WriteAllText($workflowPath, $wrongPushWorkflow)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI push branches must include master; found: develop' -Scenario 'pushes to the integration branch are not validated'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $wrongPullRequestWorkflow = [regex]::Replace(
        $workflow,
        '(?m)(^  pull_request:\r?\n    branches:\r?\n      - )master',
        '${1}develop'
    )
    [System.IO.File]::WriteAllText($workflowPath, $wrongPullRequestWorkflow)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI pull_request branches must include master; found: develop' -Scenario 'workflow targets the wrong integration branch'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $contributingPath = Join-Path $fixtureRoot 'CONTRIBUTING.md'
    $contributing = Get-Content -LiteralPath $contributingPath -Raw
    [System.IO.File]::WriteAllText(
        $contributingPath,
        $contributing.Replace('`master` is the primary integration branch', '`develop` is the primary integration branch')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText "CONTRIBUTING.md contradicts the branch policy by naming 'develop'" -Scenario 'prose names a conflicting primary branch'
    }
    finally {
        Restore-FixtureFile -RelativePath 'CONTRIBUTING.md'
    }

    Write-Host ""
    Write-Host ("All " + $testCount + ' agent-doc integrity scenarios passed.') -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
