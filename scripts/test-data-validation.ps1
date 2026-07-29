<#
.SYNOPSIS
  Hermetic tests for the RatScannerData release contract and payload validators.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'RatScannerData.ps1')

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('RatScanner data tests ' + [Guid]::NewGuid().ToString('N'))
$testCount = 0

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$ExpectedText,
        [Parameter(Mandatory = $true)][string]$Scenario
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike ('*' + $ExpectedText + '*')) {
            throw "Scenario '$Scenario' threw an unexpected error: $($_.Exception.Message)"
        }
        $script:testCount++
        Write-Host "PASS: $Scenario" -ForegroundColor Green
        return
    }
    throw "Scenario '$Scenario' should have failed."
}

function Assert-Passes {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Scenario
    )

    & $Action
    $script:testCount++
    Write-Host "PASS: $Scenario" -ForegroundColor Green
}

function New-DataFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$SchemaVersion = 1,
        [int]$IconCount = 2,
        [int]$SkippedItemCount = 1,
        [switch]$Nested
    )

    $root = if ($Nested) { Join-Path $Path 'Data' } else { $Path }
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'icons') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'traineddata') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $root 'maps.json'), '[]')
    [System.IO.File]::WriteAllBytes((Join-Path $root 'unknown.png'), [byte[]](1, 2, 3))
    [System.IO.File]::WriteAllBytes((Join-Path $root 'traineddata\eng.traineddata'), [byte[]](4, 5, 6))
    for ($index = 0; $index -lt $IconCount; $index++) {
        [System.IO.File]::WriteAllBytes((Join-Path $root "icons\item-$index.png"), [byte[]](137, 80, 78, 71))
    }
    $entries = @()
    foreach ($relativePath in @('maps.json', 'unknown.png', 'traineddata/eng.traineddata')) {
        $fullPath = Join-Path $root ($relativePath.Replace('/', '\'))
        $entries += [ordered]@{
            path   = $relativePath
            sha256 = Get-RatScannerDataFileSha256 -Path $fullPath
            size   = (Get-Item -LiteralPath $fullPath).Length
        }
    }
    for ($index = 0; $index -lt $IconCount; $index++) {
        $relativePath = "icons/item-$index.png"
        $fullPath = Join-Path $root ($relativePath.Replace('/', '\'))
        $entries += [ordered]@{
            path   = $relativePath
            sha256 = Get-RatScannerDataFileSha256 -Path $fullPath
            size   = (Get-Item -LiteralPath $fullPath).Length
        }
    }
    $contentHash = Get-RatScannerDataContentSha256 -Entries $entries

    $manifest = [ordered]@{
        schemaVersion    = $SchemaVersion
        contentSha256    = $contentHash
        catalogItemCount = $IconCount + $SkippedItemCount
        iconCount        = $IconCount
        skippedItemCount = $SkippedItemCount
        fileCount        = $entries.Count
        files            = $entries
    }
    $json = $manifest | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $root 'manifest.json'), $json)
    return $root
}

function New-PackageFixture {
    param(
        [Parameter(Mandatory = $true)][string]$StagingPath,
        [Parameter(Mandatory = $true)][string]$Name,
        [scriptblock]$Mutate
    )

    $workPath = Join-Path (Split-Path -Parent $StagingPath) ('package-' + $Name)
    if (Test-Path -LiteralPath $workPath) {
        Remove-Item -LiteralPath $workPath -Recurse -Force
    }
    # Copy the staging directory itself so the Data/ subtree is preserved.
    Copy-Item -LiteralPath $StagingPath -Destination $workPath -Recurse -Force
    if ($Mutate) {
        & $Mutate $workPath
    }

    $packagePath = Join-Path (Split-Path -Parent $StagingPath) ($Name + '.zip')
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($workPath, $packagePath)
    return $packagePath
}

function Add-ZipEntry {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$EntryName,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    $archive = [System.IO.Compression.ZipFile]::Open($PackagePath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $stream = $archive.CreateEntry($EntryName).Open()
        try {
            $stream.Write($Bytes, 0, $Bytes.Length)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null

    $archive = Join-Path $fixtureRoot 'Data.zip'
    [System.IO.File]::WriteAllText($archive, 'fixture archive')
    $archiveHash = Get-RatScannerDataFileSha256 -Path $archive
    Assert-Passes -Scenario 'valid sha256sum text and archive hash' -Action {
        $actual = Assert-RatScannerDataArchiveChecksum `
            -ArchivePath $archive `
            -ChecksumText "$archiveHash  Data.zip`n" `
            -PinnedSha256 $archiveHash
        if ($actual -ne $archiveHash) { throw 'Archive hash result differed.' }
    }
    Assert-Throws -Scenario 'malformed checksum text' -ExpectedText 'must contain one SHA-256 digest' -Action {
        ConvertFrom-RatScannerDataChecksum -Text 'not-a-hash Data.zip'
    }
    Assert-Throws -Scenario 'published checksum differs from pin' -ExpectedText 'does not match the pinned release checksum' -Action {
        Assert-RatScannerDataArchiveChecksum `
            -ArchivePath $archive `
            -ChecksumText (('b' * 64) + '  Data.zip') `
            -PinnedSha256 $archiveHash
    }

    $validFixture = Join-Path $fixtureRoot 'valid'
    $validRoot = New-DataFixture -Path $validFixture
    $publishedManifest = Join-Path $fixtureRoot 'manifest.json'
    Copy-Item -LiteralPath (Join-Path $validRoot 'manifest.json') -Destination $publishedManifest
    Assert-Passes -Scenario 'valid root payload' -Action {
        $resolved = Resolve-RatScannerDataRoot -ExtractPath $validFixture
        $result = Assert-RatScannerDataPayload `
            -DataRoot $resolved `
            -PublishedManifestPath $publishedManifest `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
        if ($result.IconCount -ne 2) { throw 'Unexpected icon count.' }
    }

    $nestedFixture = Join-Path $fixtureRoot 'nested'
    $nestedRoot = New-DataFixture -Path $nestedFixture -Nested
    Copy-Item -LiteralPath (Join-Path $nestedRoot 'manifest.json') -Destination $publishedManifest -Force
    Assert-Passes -Scenario 'valid legacy nested Data payload' -Action {
        $resolved = Resolve-RatScannerDataRoot -ExtractPath $nestedFixture
        if ($resolved -ne $nestedRoot) { throw 'Nested root was not resolved.' }
        [void](Assert-RatScannerDataPayload `
            -DataRoot $resolved `
            -PublishedManifestPath $publishedManifest `
            -ExpectedSchema 1 `
            -MinimumIconCount 1)
    }

    $schemaFixture = Join-Path $fixtureRoot 'schema'
    $schemaRoot = New-DataFixture -Path $schemaFixture -SchemaVersion 2
    Copy-Item -LiteralPath (Join-Path $schemaRoot 'manifest.json') -Destination $publishedManifest -Force
    Assert-Throws -Scenario 'unsupported manifest schema' -ExpectedText 'Unsupported RatScanner data manifest schema' -Action {
        Assert-RatScannerDataPayload `
            -DataRoot $schemaRoot `
            -PublishedManifestPath $publishedManifest `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    $mismatchFixture = Join-Path $fixtureRoot 'manifest-mismatch'
    $mismatchRoot = New-DataFixture -Path $mismatchFixture
    [System.IO.File]::WriteAllText($publishedManifest, '{}')
    Assert-Throws -Scenario 'standalone manifest differs from embedded manifest' -ExpectedText 'does not match the manifest embedded' -Action {
        Assert-RatScannerDataPayload `
            -DataRoot $mismatchRoot `
            -PublishedManifestPath $publishedManifest `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    $countFixture = Join-Path $fixtureRoot 'count-mismatch'
    $countRoot = New-DataFixture -Path $countFixture -IconCount 2
    Remove-Item -LiteralPath (Join-Path $countRoot 'icons\item-1.png')
    Copy-Item -LiteralPath (Join-Path $countRoot 'manifest.json') -Destination $publishedManifest -Force
    Assert-Throws -Scenario 'missing manifest-listed icon is rejected' -ExpectedText 'missing manifest file: icons/item-1.png' -Action {
        Assert-RatScannerDataPayload `
            -DataRoot $countRoot `
            -PublishedManifestPath $publishedManifest `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    $corruptFixture = Join-Path $fixtureRoot 'corrupt-file'
    $corruptRoot = New-DataFixture -Path $corruptFixture
    [System.IO.File]::WriteAllText((Join-Path $corruptRoot 'maps.json'), '[{"changed":true}]')
    Copy-Item -LiteralPath (Join-Path $corruptRoot 'manifest.json') -Destination $publishedManifest -Force
    Assert-Throws -Scenario 'payload file differs from manifest checksum' -ExpectedText 'file size mismatch for maps.json' -Action {
        Assert-RatScannerDataPayload `
            -DataRoot $corruptRoot `
            -PublishedManifestPath $publishedManifest `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    $missingFixture = Join-Path $fixtureRoot 'missing-required'
    $missingRoot = New-DataFixture -Path $missingFixture
    Remove-Item -LiteralPath (Join-Path $missingRoot 'maps.json')
    Copy-Item -LiteralPath (Join-Path $missingRoot 'manifest.json') -Destination $publishedManifest -Force
    Assert-Throws -Scenario 'required payload file is missing' -ExpectedText 'missing manifest file: maps.json' -Action {
        Assert-RatScannerDataPayload `
            -DataRoot $missingRoot `
            -PublishedManifestPath $publishedManifest `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    $packageStaging = Join-Path $fixtureRoot 'package-stage'
    $packageDataRoot = New-DataFixture -Path (Join-Path $packageStaging 'Data')
    [System.IO.File]::WriteAllBytes((Join-Path $packageStaging 'RatScanner.exe'), [byte[]](77, 90))
    [System.IO.File]::WriteAllText((Join-Path $packageStaging 'LICENSE'), 'fixture license')
    $packageManifest = Get-Content -LiteralPath (Join-Path $packageDataRoot 'manifest.json') -Raw | ConvertFrom-Json
    $packagePrefix = ([string]$packageManifest.contentSha256).Substring(0, 16)

    Assert-Passes -Scenario 'valid release package' -Action {
        $result = Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'valid') `
            -ExpectedSchema 1 `
            -MinimumIconCount 1 `
            -ContentSha256Prefix $packagePrefix
        if ($result.IconCount -ne 2) { throw "Unexpected packaged icon count: $($result.IconCount)" }
        if ($result.FileCount -ne 5) { throw "Unexpected verified file count: $($result.FileCount)" }
    }

    Assert-Throws -Scenario 'packaged payload does not match the pinned release' -ExpectedText 'does not match the pinned release' -Action {
        Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'wrong-pin') `
            -ExpectedSchema 1 `
            -MinimumIconCount 1 `
            -ContentSha256Prefix ('a' * 16)
    }

    Assert-Throws -Scenario 'package is missing the application executable' -ExpectedText 'missing required entry: RatScanner.exe' -Action {
        Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'no-exe' -Mutate {
                param($Path)
                Remove-Item -LiteralPath (Join-Path $Path 'RatScanner.exe') -Force
            }) `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    Assert-Throws -Scenario 'packaged payload file was corrupted after validation' -ExpectedText 'checksum mismatch for maps.json' -Action {
        Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'corrupt' -Mutate {
                param($Path)
                # Same length, different bytes: only a content hash can catch this.
                [System.IO.File]::WriteAllText((Join-Path $Path 'Data\maps.json'), '{}')
            }) `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    Assert-Throws -Scenario 'package is missing a manifest-listed payload file' -ExpectedText 'missing manifest file: icons/item-1.png' -Action {
        Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'missing-icon' -Mutate {
                param($Path)
                Remove-Item -LiteralPath (Join-Path $Path 'Data\icons\item-1.png') -Force
            }) `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    Assert-Throws -Scenario 'package carries an unlisted extra icon' -ExpectedText 'icon count does not match manifest.json' -Action {
        Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'extra-icon' -Mutate {
                param($Path)
                [System.IO.File]::WriteAllBytes((Join-Path $Path 'Data\icons\stray.png'), [byte[]](137, 80, 78, 71))
            }) `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    Assert-Throws -Scenario 'package retains the temporary data archive' -ExpectedText 'temporary data archive' -Action {
        Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'temp-archive' -Mutate {
                param($Path)
                [System.IO.File]::WriteAllText((Join-Path $Path 'Data\Data.zip'), 'leftover')
            }) `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    Assert-Throws -Scenario 'package keeps an unflattened nested Data directory' -ExpectedText 'unflattened nested data directory' -Action {
        Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'nested' -Mutate {
                param($Path)
                $nested = Join-Path $Path 'Data\Data'
                New-Item -ItemType Directory -Force -Path $nested | Out-Null
                [System.IO.File]::WriteAllText((Join-Path $nested 'manifest.json'), '{}')
            }) `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    Assert-Throws -Scenario 'package keeps a rollback staging leftover' -ExpectedText 'data installation leftover' -Action {
        Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'leftover' -Mutate {
                param($Path)
                $leftover = Join-Path $Path '.Data.backup-abc123'
                New-Item -ItemType Directory -Force -Path $leftover | Out-Null
                [System.IO.File]::WriteAllText((Join-Path $leftover 'manifest.json'), '{}')
            }) `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
    }

    Assert-Passes -Scenario 'installed payload from a stale pin is not treated as ready' -Action {
        $installed = New-DataFixture -Path (Join-Path $fixtureRoot 'installed')
        $installedManifest = Get-Content -LiteralPath (Join-Path $installed 'manifest.json') -Raw | ConvertFrom-Json
        $matchingPrefix = ([string]$installedManifest.contentSha256).Substring(0, 16)
        if (-not (Test-RatScannerDataInstallation -DataRoot $installed -ExpectedSchema 1 -MinimumIconCount 1 -ContentSha256Prefix $matchingPrefix)) {
            throw 'A matching pin should report the installation as ready.'
        }
        if (Test-RatScannerDataInstallation -DataRoot $installed -ExpectedSchema 1 -MinimumIconCount 1 -ContentSha256Prefix ('a' * 16)) {
            throw 'A stale pin must not report the installation as ready.'
        }
    }

    Assert-Passes -Scenario 'release tag must be content addressed' -Action {
        $contract = Get-RatScannerDataReleaseContract
        if ($contract.ContentSha256Prefix.Length -ne 16) {
            throw "Unexpected content prefix: $($contract.ContentSha256Prefix)"
        }
        if (-not $contract.ReleaseTag.EndsWith($contract.ContentSha256Prefix, [System.StringComparison]::Ordinal)) {
            throw 'Content prefix must be derived from the release tag.'
        }
    }

    Assert-Passes -Scenario 'nested icon subdirectories do not inflate the packaged icon count' -Action {
        $result = Assert-RatScannerDataPackage `
            -PackagePath (New-PackageFixture -StagingPath $packageStaging -Name 'nested-icons' -Mutate {
                param($Path)
                # Not manifest-listed and not a direct child: must not count toward the icon total.
                $sub = Join-Path $Path 'Data\icons\thumbs'
                New-Item -ItemType Directory -Force -Path $sub | Out-Null
                [System.IO.File]::WriteAllBytes((Join-Path $sub 'nested.png'), [byte[]](137, 80, 78, 71))
            }) `
            -ExpectedSchema 1 `
            -MinimumIconCount 1
        if ($result.IconCount -ne 2) { throw "Nested icons changed the count: $($result.IconCount)" }
    }

    $collidePackage = Join-Path $fixtureRoot 'case-collide.zip'
    Copy-Item -LiteralPath (New-PackageFixture -StagingPath $packageStaging -Name 'collide-source') -Destination $collidePackage -Force
    # NTFS cannot hold both cases, so the colliding entry is injected into the archive directly.
    Add-ZipEntry -PackagePath $collidePackage -EntryName 'Data/icons/ITEM-0.png' -Bytes ([byte[]](137, 80, 78, 71))
    Assert-Throws -Scenario 'case-colliding icon entries are rejected' -ExpectedText 'differ only by case' -Action {
        Assert-RatScannerDataPackage -PackagePath $collidePackage -ExpectedSchema 1 -MinimumIconCount 1
    }

    # A collision outside icons/ changes no count, so only explicit detection catches it.
    $collideMapsPackage = Join-Path $fixtureRoot 'case-collide-maps.zip'
    Copy-Item -LiteralPath (New-PackageFixture -StagingPath $packageStaging -Name 'collide-maps-source') -Destination $collideMapsPackage -Force
    Add-ZipEntry -PackagePath $collideMapsPackage -EntryName 'Data/MAPS.json' -Bytes ([System.Text.Encoding]::UTF8.GetBytes('[{"spoofed":true}]'))
    Assert-Throws -Scenario 'case-colliding non-icon entries are rejected' -ExpectedText 'differ only by case' -Action {
        Assert-RatScannerDataPackage -PackagePath $collideMapsPackage -ExpectedSchema 1 -MinimumIconCount 1
    }

    # Windows resolves './' on extraction, so this would overwrite the hashed Data/maps.json.
    $aliasPackage = Join-Path $fixtureRoot 'path-alias.zip'
    Copy-Item -LiteralPath (New-PackageFixture -StagingPath $packageStaging -Name 'alias-source') -Destination $aliasPackage -Force
    Add-ZipEntry -PackagePath $aliasPackage -EntryName 'Data/./maps.json' -Bytes ([System.Text.Encoding]::UTF8.GetBytes('[{"spoofed":true}]'))
    Assert-Throws -Scenario 'relative-segment path aliases are rejected' -ExpectedText 'non-canonical entry name' -Action {
        Assert-RatScannerDataPackage -PackagePath $aliasPackage -ExpectedSchema 1 -MinimumIconCount 1
    }

    # Windows strips trailing dots and spaces, aliasing this onto Data/maps.json.
    $trailingPackage = Join-Path $fixtureRoot 'trailing-alias.zip'
    Copy-Item -LiteralPath (New-PackageFixture -StagingPath $packageStaging -Name 'trailing-source') -Destination $trailingPackage -Force
    Add-ZipEntry -PackagePath $trailingPackage -EntryName 'Data/maps.json.' -Bytes ([System.Text.Encoding]::UTF8.GetBytes('[{"spoofed":true}]'))
    Assert-Throws -Scenario 'trailing-dot path aliases are rejected' -ExpectedText 'non-canonical entry name' -Action {
        Assert-RatScannerDataPackage -PackagePath $trailingPackage -ExpectedSchema 1 -MinimumIconCount 1
    }

    # Reserved device names cannot be extracted on Windows even with an extension.
    $reservedPackage = Join-Path $fixtureRoot 'reserved-name.zip'
    Copy-Item -LiteralPath (New-PackageFixture -StagingPath $packageStaging -Name 'reserved-source') -Destination $reservedPackage -Force
    Add-ZipEntry -PackagePath $reservedPackage -EntryName 'Data/NUL.txt' -Bytes ([System.Text.Encoding]::UTF8.GetBytes('x'))
    Assert-Throws -Scenario 'reserved Windows device names are rejected' -ExpectedText 'reserved Windows device name' -Action {
        Assert-RatScannerDataPackage -PackagePath $reservedPackage -ExpectedSchema 1 -MinimumIconCount 1
    }

    $duplicatePackage = Join-Path $fixtureRoot 'duplicate.zip'
    Copy-Item -LiteralPath (New-PackageFixture -StagingPath $packageStaging -Name 'dupe-source') -Destination $duplicatePackage -Force
    # The zip format permits repeated names; the verifier must not pick one and move on.
    Add-ZipEntry -PackagePath $duplicatePackage -EntryName 'Data/maps.json' -Bytes ([System.Text.Encoding]::UTF8.GetBytes('[]'))
    Assert-Throws -Scenario 'duplicate archive entries are rejected' -ExpectedText 'duplicate entry: Data/maps.json' -Action {
        Assert-RatScannerDataPackage -PackagePath $duplicatePackage -ExpectedSchema 1 -MinimumIconCount 1
    }

    Write-Host "`nAll $testCount RatScannerData validation scenarios passed." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
