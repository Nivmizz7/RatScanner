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
    $nestedSubmodules = ([regex]'(?m)(^\s*persist-credentials:\s*false\s*$)').Replace(
        $nestedSubmodules,
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
    $nonCheckoutSubmodules = ([regex]'(?m)(^\s*dotnet-version:\s*10\.0\.x\s*$)').Replace(
        $nonCheckoutSubmodules,
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
    $matrixListSubmodules = [regex]::Replace(
        $workflow,
        '(?m)^\s*submodules:\s*recursive\r?\n',
        ''
    )
    $matrixListSubmodules = ([regex]'(?m)(^    runs-on:\s*windows-latest\s*$)').Replace(
        $matrixListSubmodules,
        "`$1`r`n    strategy:`r`n      matrix:`r`n        include:`r`n          - os: windows-latest",
        1
    )
    $matrixListSubmodules = ([regex]'(?m)(^\s*dotnet-version:\s*10\.0\.x\s*$)').Replace(
        $matrixListSubmodules,
        "`$1`r`n          submodules: recursive",
        1
    )
    [System.IO.File]::WriteAllText($workflowPath, $matrixListSubmodules)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI checkout must initialize RatEye with submodules: recursive' -Scenario 'matrix list entries cannot merge checkout with a later action'
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
    $conditionalStepSubmodules = ([regex]'(?m)(^\s*persist-credentials:\s*false\s*$)').Replace(
        $conditionalStepSubmodules,
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
    $conditionalCheckout = ([regex]'(?m)(^      - name:\s*Checkout\s*$)').Replace(
        $workflow,
        "`$1`r`n        if: false",
        1
    )
    [System.IO.File]::WriteAllText($workflowPath, $conditionalCheckout)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI checkout must initialize RatEye with submodules: recursive' -Scenario 'a statically false checkout does not guarantee submodule initialization'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $blockScalarCheckout = [regex]::Replace(
        $workflow,
        '(?m)^\s*submodules:\s*recursive\r?\n',
        ''
    )
    $blockScalarCheckout = ([regex]'(?m)^jobs:\s*$').Replace(
        $blockScalarCheckout,
        "env:`r`n  EXAMPLE_WORKFLOW: |`r`n    steps:`r`n      - uses: actions/checkout@example`r`n        with:`r`n          submodules: recursive`r`njobs:",
        1
    )
    [System.IO.File]::WriteAllText($workflowPath, $blockScalarCheckout)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI checkout must initialize RatEye with submodules: recursive' -Scenario 'workflow text inside a block scalar cannot impersonate checkout'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $commentedSubmodules = ([regex]'(?m)(^\s*submodules:\s*recursive)\s*$').Replace(
        $workflow,
        '$1 # initialize RatEye',
        1
    )
    [System.IO.File]::WriteAllText($workflowPath, $commentedSubmodules)
    try {
        Invoke-IntegrityCheck -ShouldPass $true -ExpectedText 'All documentation integrity checks passed.' -Scenario 'recursive submodules allow an inline YAML comment'
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

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    [System.IO.File]::WriteAllText(
        $workflowPath,
        $workflow.Replace('scripts/setup-data.ps1', 'scripts/Expand-Zip.ps1')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI Include Data must delegate to scripts/setup-data.ps1' -Scenario 'CI bypasses the shared RatScannerData installer'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $publishPath = Join-Path $fixtureRoot 'publish.bat'
    $publish = Get-Content -LiteralPath $publishPath -Raw
    [System.IO.File]::WriteAllText(
        $publishPath,
        $publish.Replace('scripts\setup-data.ps1', 'scripts\Expand-Zip.ps1')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'publish.bat must delegate RatScannerData installation' -Scenario 'local publish bypasses the shared RatScannerData installer'
    }
    finally {
        Restore-FixtureFile -RelativePath 'publish.bat'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    [System.IO.File]::WriteAllText(
        $workflowPath,
        $workflow.Replace('scripts/verify-package.ps1', 'scripts/Expand-Zip.ps1')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI must verify the packaged artifact' -Scenario 'CI drops post-zip package verification'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $publish = Get-Content -LiteralPath $publishPath -Raw
    [System.IO.File]::WriteAllText(
        $publishPath,
        $publish.Replace('scripts\verify-package.ps1', 'scripts\Expand-Zip.ps1')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'publish.bat must verify the packaged archive' -Scenario 'local publish drops post-zip package verification'
    }
    finally {
        Restore-FixtureFile -RelativePath 'publish.bat'
    }

    $verifyPackageFixture = Join-Path $fixtureRoot 'scripts\verify-package.ps1'
    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Write-Output')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'package verifier stops using the shared data contract'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    [System.IO.File]::WriteAllText(
        $workflowPath,
        $workflow.Replace(
            'scripts/setup-data.ps1',
            'scripts/setup-data.ps1 # https://github.com/tarkovtracker-org/RatScannerData/releases/latest/download/Data.zip'
        )
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must not download the old unpinned RatScannerData latest release' -Scenario 'CI reintroduces the unpinned latest release under the current org'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    [System.IO.File]::WriteAllText(
        $workflowPath,
        $workflow.Replace(
            '-File scripts/verify-package.ps1',
            '-File scripts/Expand-Zip.ps1 # scripts/verify-package.ps1'
        )
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'CI must verify the packaged artifact' -Scenario 'commented-out CI verification does not satisfy the guard'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $namedButNotInvoked = $workflow.Replace(
        '      - name: Verify release package',
        '      - name: Prepare scripts/verify-package.ps1')
    $namedButNotInvoked = $namedButNotInvoked.Replace(
        '          powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-package.ps1 -PackagePath (Join-Path $PWD ''RatScanner.zip'')',
        '          powershell -NoProfile -ExecutionPolicy Bypass -Command "Write-Host skipped"')
    if ($namedButNotInvoked -eq $workflow) {
        throw 'Fixture mutation did not apply: the package verification step was not found.'
    }
    [System.IO.File]::WriteAllText($workflowPath, $namedButNotInvoked)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'before upload' -Scenario 'a step name mentioning the verifier does not count as invoking it'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $zipStepLine = '      - name: Zip Content'
    $prematureUpload = @(
        '      - name: Premature upload',
        '        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1',
        '        with:',
        '          name: RatScanner.zip',
        '          path: ./RatScanner.zip',
        $zipStepLine
    ) -join "`r`n"
    $mutatedWorkflow = $workflow.Replace($zipStepLine, $prematureUpload)
    if ($mutatedWorkflow -eq $workflow) {
        throw "Fixture mutation did not apply: '$zipStepLine' was not found in build.yml."
    }
    [System.IO.File]::WriteAllText($workflowPath, $mutatedWorkflow)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'before upload' -Scenario 'an upload step before verification is rejected'
    }
    finally {
        Restore-FixtureFile -RelativePath '.github\workflows\build.yml'
    }

    $verifyPackageFixture = Join-Path $fixtureRoot 'scripts\verify-package.ps1'
    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        "# Assert-RatScannerDataPackage appears only in this comment.`n" +
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'commented package assertion does not satisfy the guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`n`$unused = 'Assert-RatScannerDataPackage'`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'a string literal does not satisfy the package assertion guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    $withoutDotSource = $verifyPackage.Replace(
        '. (Join-Path $PSScriptRoot ''RatScannerData.ps1'')',
        '$null = Join-Path $PSScriptRoot ''RatScannerData.ps1''')
    if ($withoutDotSource -eq $verifyPackage) {
        throw 'Fixture mutation did not apply: the contract dot-source was not found in verify-package.ps1.'
    }
    [System.IO.File]::WriteAllText($verifyPackageFixture, $withoutDotSource)
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'package verifier stops dot-sourcing the shared contract'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nif (`$false) { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'an unreachable assertion does not satisfy the package guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nif ((`$false)) { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'nested parentheses around a false condition do not make an unreachable assertion count'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nwhile (`$false) { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'an assertion in a literal-false while loop does not satisfy the package guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nswitch (`$false) { `$true { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 } }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'an assertion inside a switch does not satisfy the top-level package guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nfor (`$index = 0; `$false; `$index++) { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'an assertion in a literal-false for loop does not satisfy the package guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nif (`$true) { `$null = 1 } else { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'an assertion in a dead else branch does not satisfy the package guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nif (`$true) { `$null = 1 } elseif (`$false) { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'an assertion in an elseif after a literal-true clause does not satisfy the package guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nif ((`$true)) { `$null = 1 } elseif (`$false) { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'nested parentheses around a true condition leave later elseif clauses unreachable'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $verifyPackage = Get-Content -LiteralPath $verifyPackageFixture -Raw
    [System.IO.File]::WriteAllText(
        $verifyPackageFixture,
        $verifyPackage.Replace('Assert-RatScannerDataPackage', 'Assert-RatScannerDataPayload') +
        "`nif (`$false) { `$null = 1 } elseif (`$true) { `$null = 2 } else { Assert-RatScannerDataPackage -PackagePath 'x' -ExpectedSchema 1 -MinimumIconCount 1 }`n"
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must verify packages through the shared RatScannerData contract' -Scenario 'an assertion in an else after a literal-true elseif does not satisfy the package guard'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\verify-package.ps1'
    }

    $dataContractPath = Join-Path $fixtureRoot 'scripts\RatScannerData.ps1'
    $dataContract = Get-Content -LiteralPath $dataContractPath -Raw
    $releaseTagLiteralOnly = $dataContract.Replace(
        '$script:RatScannerDataReleaseTag = ''data-f1f047dc5d38ee43''',
        '$script:RatScannerDataReleaseTag = ''latest''')
    if ($releaseTagLiteralOnly -eq $dataContract) {
        throw 'Fixture mutation did not apply: the release tag assignment was not found.'
    }
    [System.IO.File]::WriteAllText(
        $dataContractPath,
        $releaseTagLiteralOnly + "`n`$unused = 'RatScannerDataReleaseTag = ''data-f1f047dc5d38ee43'''`n")
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must pin a content-addressed data release tag' -Scenario 'a string literal cannot authorize the data release tag'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\RatScannerData.ps1'
    }

    $dataContract = Get-Content -LiteralPath $dataContractPath -Raw
    $releaseTagCommentOnly = $dataContract.Replace(
        '$script:RatScannerDataReleaseTag = ''data-f1f047dc5d38ee43''',
        '$script:RatScannerDataReleaseTag = ''latest''')
    [System.IO.File]::WriteAllText(
        $dataContractPath,
        $releaseTagCommentOnly + "`n# RatScannerDataReleaseTag = 'data-f1f047dc5d38ee43'`n")
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'must pin a content-addressed data release tag' -Scenario 'a comment cannot authorize the data release tag'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\RatScannerData.ps1'
    }

    $dataContract = Get-Content -LiteralPath $dataContractPath -Raw
    $literalOnly = $dataContract.Replace(
        '$script:RatScannerDataRepository = ''tarkovtracker-org/RatScannerData''',
        '$script:RatScannerDataRepository = ''RatScanner/RatScannerData''')
    if ($literalOnly -eq $dataContract) {
        throw 'Fixture mutation did not apply: the repository assignment was not found.'
    }
    [System.IO.File]::WriteAllText($dataContractPath, $literalOnly + "`n`$unused = 'tarkovtracker-org/RatScannerData'`n")
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'RatScannerData contract must use tarkovtracker-org/RatScannerData' -Scenario 'a string literal cannot authorize the contract repository'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\RatScannerData.ps1'
    }

    $dataContract = Get-Content -LiteralPath $dataContractPath -Raw
    [System.IO.File]::WriteAllText(
        $dataContractPath,
        $dataContract.Replace(
            '$script:RatScannerDataRepository = ''tarkovtracker-org/RatScannerData''',
            '$script:RatScannerDataRepository = ''RatScanner/RatScannerData'' # tarkovtracker-org/RatScannerData'
        )
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'RatScannerData contract must use tarkovtracker-org/RatScannerData' -Scenario 'a comment cannot authorize a changed contract repository'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\RatScannerData.ps1'
    }

    $dataContractPath = Join-Path $fixtureRoot 'scripts\RatScannerData.ps1'
    $dataContract = Get-Content -LiteralPath $dataContractPath -Raw
    [System.IO.File]::WriteAllText(
        $dataContractPath,
        $dataContract.Replace('tarkovtracker-org/RatScannerData', 'RatScanner/RatScannerData')
    )
    try {
        Invoke-IntegrityCheck -ShouldPass $false -ExpectedText 'RatScannerData contract must use tarkovtracker-org/RatScannerData' -Scenario 'data contract points back to the old upstream repository'
    }
    finally {
        Restore-FixtureFile -RelativePath 'scripts\RatScannerData.ps1'
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

exit 0
