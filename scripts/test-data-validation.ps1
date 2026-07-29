<#
.SYNOPSIS
  Hermetic tests for the RatScannerData release contract and payload validators.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
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

    Write-Host "`nAll $testCount RatScannerData validation scenarios passed." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
