$script:RatScannerDataRepository = 'tarkovtracker-org/RatScannerData'
$script:RatScannerDataReleaseTag = 'data-f1f047dc5d38ee43'
$script:RatScannerDataArchiveSha256 = 'bce49e8bc7dde57ad46fb95010627831d4483db2273d554e3add6c49388a3b38'
$script:RatScannerDataManifestSchema = 1
$script:RatScannerDataMinimumIconCount = 4000

function Get-RatScannerDataReleaseContract {
    $baseUrl = "https://github.com/$script:RatScannerDataRepository/releases/download/$script:RatScannerDataReleaseTag"
    return [pscustomobject]@{
        Repository       = $script:RatScannerDataRepository
        ReleaseTag       = $script:RatScannerDataReleaseTag
        ArchiveSha256    = $script:RatScannerDataArchiveSha256
        ManifestSchema   = $script:RatScannerDataManifestSchema
        MinimumIconCount = $script:RatScannerDataMinimumIconCount
        ArchiveUrl       = "$baseUrl/Data.zip"
        ChecksumUrl      = "$baseUrl/Data.zip.sha256"
        ManifestUrl      = "$baseUrl/manifest.json"
    }
}

function Get-RatScannerDataFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-RatScannerDataContentSha256 {
    param([Parameter(Mandatory = $true)]$Entries)

    $entriesByPath = @{}
    [string[]]$paths = @($Entries | ForEach-Object {
        $path = [string]$_.path
        if ($entriesByPath.ContainsKey($path)) {
            throw "RatScanner data manifest contains a duplicate file path: $path"
        }
        $entriesByPath[$path] = $_
        $path
    })
    [System.Array]::Sort($paths, [System.StringComparer]::Ordinal)

    $digest = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = New-Object System.IO.MemoryStream
        try {
            foreach ($path in $paths) {
                $entry = $entriesByPath[$path]
                $line = [System.Text.Encoding]::UTF8.GetBytes("$path`0$(([string]$entry.sha256).ToLowerInvariant())`n")
                $stream.Write($line, 0, $line.Length)
            }
            $stream.Position = 0
            return ([System.BitConverter]::ToString($digest.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $digest.Dispose()
    }
}

function ConvertFrom-RatScannerDataChecksum {
    param([Parameter(Mandatory = $true)][string]$Text)

    $line = @([regex]::Split($Text.Trim(), '\r?\n') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($line.Count -ne 1 -or $line[0] -notmatch '^(?<hash>[0-9A-Fa-f]{64})(?:\s+\*?Data\.zip)?\s*$') {
        throw 'Data.zip.sha256 must contain one SHA-256 digest with an optional Data.zip filename.'
    }
    return $Matches['hash'].ToLowerInvariant()
}

function Assert-RatScannerDataArchiveChecksum {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$ChecksumText,
        [Parameter(Mandatory = $true)][string]$PinnedSha256
    )

    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        throw "RatScanner data archive does not exist: $ArchivePath"
    }

    $publishedSha256 = ConvertFrom-RatScannerDataChecksum -Text $ChecksumText
    if ($PinnedSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'Pinned RatScanner data SHA-256 is not a 64-character hexadecimal digest.'
    }
    $pinned = $PinnedSha256.ToLowerInvariant()
    if ($publishedSha256 -ne $pinned) {
        throw "Published Data.zip checksum does not match the pinned release checksum. Published: $publishedSha256; pinned: $pinned"
    }

    $actual = Get-RatScannerDataFileSha256 -Path $ArchivePath
    if ($actual -ne $pinned) {
        throw "Downloaded Data.zip checksum mismatch. Actual: $actual; expected: $pinned"
    }
    return $actual
}

function Resolve-RatScannerDataRoot {
    param([Parameter(Mandatory = $true)][string]$ExtractPath)

    foreach ($candidate in @($ExtractPath, (Join-Path $ExtractPath 'Data'))) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'manifest.json') -PathType Leaf) {
            return $candidate
        }
    }
    throw 'Data archive is missing manifest.json at its root or under Data/.'
}

function Read-RatScannerDataManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][int]$ExpectedSchema,
        [Parameter(Mandatory = $true)][int]$MinimumIconCount
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "RatScanner data manifest does not exist: $ManifestPath"
    }

    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "RatScanner data manifest is not valid JSON: $($_.Exception.Message)"
    }

    if ($null -eq $manifest.schemaVersion -or [int]$manifest.schemaVersion -ne $ExpectedSchema) {
        throw "Unsupported RatScanner data manifest schema. Found: $($manifest.schemaVersion); expected: $ExpectedSchema"
    }
    if ($null -eq $manifest.iconCount -or [int]$manifest.iconCount -lt $MinimumIconCount) {
        throw "RatScanner data manifest iconCount is missing or below $MinimumIconCount. Found: $($manifest.iconCount)"
    }
    if ($null -eq $manifest.catalogItemCount -or [int]$manifest.catalogItemCount -lt [int]$manifest.iconCount) {
        throw 'RatScanner data manifest catalogItemCount is missing or smaller than iconCount.'
    }
    if ($null -eq $manifest.skippedItemCount -or [int]$manifest.skippedItemCount -lt 0) {
        throw 'RatScanner data manifest skippedItemCount is missing or negative.'
    }
    if ([int]$manifest.catalogItemCount -ne ([int]$manifest.iconCount + [int]$manifest.skippedItemCount)) {
        throw 'RatScanner data manifest catalogItemCount must equal iconCount plus skippedItemCount.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.contentSha256) -or [string]$manifest.contentSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'RatScanner data manifest contentSha256 is missing or invalid.'
    }
    if ($null -eq $manifest.fileCount -or [int]$manifest.fileCount -le 0) {
        throw 'RatScanner data manifest fileCount is missing or invalid.'
    }
    if ($null -eq $manifest.files -or @($manifest.files).Count -ne [int]$manifest.fileCount) {
        throw 'RatScanner data manifest files do not match fileCount.'
    }
    return $manifest
}

function Assert-RatScannerDataFiles {
    param(
        [Parameter(Mandatory = $true)][string]$DataRoot,
        [Parameter(Mandatory = $true)]$Manifest
    )

    foreach ($entry in @($Manifest.files | Sort-Object -Property path)) {
        $relativePath = [string]$entry.path
        $expectedHash = ([string]$entry.sha256).ToLowerInvariant()
        $expectedSize = [long]$entry.size
        if ([string]::IsNullOrWhiteSpace($relativePath) -or $relativePath -match '(^|[\\/])\.\.([\\/]|$)' -or [System.IO.Path]::IsPathRooted($relativePath)) {
            throw "RatScanner data manifest contains an unsafe file path: $relativePath"
        }
        if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or $expectedSize -lt 0) {
            throw "RatScanner data manifest has invalid metadata for: $relativePath"
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $DataRoot ($relativePath.Replace('/', '\'))))
        $rootPrefix = [System.IO.Path]::GetFullPath($DataRoot).TrimEnd('\') + '\'
        if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "RatScanner data manifest path escapes the data root: $relativePath"
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Data archive is missing manifest file: $relativePath"
        }
        $actualSize = (Get-Item -LiteralPath $fullPath).Length
        if ($actualSize -ne $expectedSize) {
            throw "Data archive file size mismatch for $relativePath. Actual: $actualSize; manifest: $expectedSize"
        }
        $actualHash = Get-RatScannerDataFileSha256 -Path $fullPath
        if ($actualHash -ne $expectedHash) {
            throw "Data archive file checksum mismatch for $relativePath. Actual: $actualHash; manifest: $expectedHash"
        }
    }

    $actualContentHash = Get-RatScannerDataContentSha256 -Entries $Manifest.files

    if ($actualContentHash -ne ([string]$Manifest.contentSha256).ToLowerInvariant()) {
        throw "RatScanner data contentSha256 mismatch. Actual: $actualContentHash; manifest: $($Manifest.contentSha256)"
    }
}

function Assert-RatScannerDataPayload {
    param(
        [Parameter(Mandatory = $true)][string]$DataRoot,
        [Parameter(Mandatory = $true)][string]$PublishedManifestPath,
        [Parameter(Mandatory = $true)][int]$ExpectedSchema,
        [Parameter(Mandatory = $true)][int]$MinimumIconCount
    )

    $embeddedManifestPath = Join-Path $DataRoot 'manifest.json'
    $embeddedHash = Get-RatScannerDataFileSha256 -Path $embeddedManifestPath
    $publishedHash = Get-RatScannerDataFileSha256 -Path $PublishedManifestPath
    if ($embeddedHash -ne $publishedHash) {
        throw 'Published manifest.json does not match the manifest embedded in Data.zip.'
    }

    $manifest = Read-RatScannerDataManifest `
        -ManifestPath $embeddedManifestPath `
        -ExpectedSchema $ExpectedSchema `
        -MinimumIconCount $MinimumIconCount
    Assert-RatScannerDataFiles -DataRoot $DataRoot -Manifest $manifest

    foreach ($relativePath in @('maps.json', 'unknown.png', 'traineddata\eng.traineddata')) {
        if (-not (Test-Path -LiteralPath (Join-Path $DataRoot $relativePath) -PathType Leaf)) {
            throw "Data archive is missing required file: $relativePath"
        }
    }

    $iconsPath = Join-Path $DataRoot 'icons'
    if (-not (Test-Path -LiteralPath $iconsPath -PathType Container)) {
        throw 'Data archive is missing the icons directory.'
    }
    $actualIconCount = @(Get-ChildItem -LiteralPath $iconsPath -Filter '*.png' -File).Count
    if ($actualIconCount -ne [int]$manifest.iconCount) {
        throw "Data archive icon count does not match manifest.json. Actual: $actualIconCount; manifest: $($manifest.iconCount)"
    }

    return [pscustomobject]@{
        DataRoot       = $DataRoot
        Manifest       = $manifest
        IconCount      = $actualIconCount
        ContentSha256  = [string]$manifest.contentSha256
    }
}

function Test-RatScannerDataInstallation {
    param(
        [Parameter(Mandatory = $true)][string]$DataRoot,
        [Parameter(Mandatory = $true)][int]$ExpectedSchema,
        [Parameter(Mandatory = $true)][int]$MinimumIconCount
    )

    try {
        $manifest = Read-RatScannerDataManifest `
            -ManifestPath (Join-Path $DataRoot 'manifest.json') `
            -ExpectedSchema $ExpectedSchema `
            -MinimumIconCount $MinimumIconCount
        foreach ($relativePath in @('maps.json', 'unknown.png', 'traineddata\eng.traineddata')) {
            if (-not (Test-Path -LiteralPath (Join-Path $DataRoot $relativePath) -PathType Leaf)) {
                return $false
            }
        }
        $actualIconCount = @(Get-ChildItem -LiteralPath (Join-Path $DataRoot 'icons') -Filter '*.png' -File -ErrorAction Stop).Count
        return $actualIconCount -eq [int]$manifest.iconCount
    }
    catch {
        return $false
    }
}
