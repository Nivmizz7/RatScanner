$script:RatScannerDataRepository = 'tarkovtracker-org/RatScannerData'
$script:RatScannerDataReleaseTag = 'data-f1f047dc5d38ee43'
$script:RatScannerDataArchiveSha256 = 'bce49e8bc7dde57ad46fb95010627831d4483db2273d554e3add6c49388a3b38'
$script:RatScannerDataManifestSchema = 1
$script:RatScannerDataMinimumIconCount = 4000

function Get-RatScannerDataReleaseContract {
    # RatScannerData names releases after the first 16 hex characters of the payload contentSha256,
    # so the tag alone is enough to prove a built package carries the pinned payload. Capture the
    # group explicitly rather than relying on $Matches from a negated test.
    $tagMatch = [regex]::Match($script:RatScannerDataReleaseTag, '^data-(?<prefix>[0-9a-f]{16})$')
    if (-not $tagMatch.Success) {
        throw 'Pinned RatScanner data release tag must use the content-addressed form data-<16 lowercase hex characters>.'
    }
    $contentSha256Prefix = $tagMatch.Groups['prefix'].Value
    $baseUrl = "https://github.com/$script:RatScannerDataRepository/releases/download/$script:RatScannerDataReleaseTag"
    return [pscustomobject]@{
        Repository          = $script:RatScannerDataRepository
        ReleaseTag          = $script:RatScannerDataReleaseTag
        ArchiveSha256       = $script:RatScannerDataArchiveSha256
        ContentSha256Prefix = $contentSha256Prefix
        ManifestSchema      = $script:RatScannerDataManifestSchema
        MinimumIconCount    = $script:RatScannerDataMinimumIconCount
        ArchiveUrl          = "$baseUrl/Data.zip"
        ChecksumUrl         = "$baseUrl/Data.zip.sha256"
        ManifestUrl         = "$baseUrl/manifest.json"
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

function Assert-RatScannerDataManifestObject {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][int]$ExpectedSchema,
        [Parameter(Mandatory = $true)][int]$MinimumIconCount
    )

    if ($null -eq $Manifest.schemaVersion -or [int]$Manifest.schemaVersion -ne $ExpectedSchema) {
        throw "Unsupported RatScanner data manifest schema. Found: $($Manifest.schemaVersion); expected: $ExpectedSchema"
    }
    if ($null -eq $Manifest.iconCount -or [int]$Manifest.iconCount -lt $MinimumIconCount) {
        throw "RatScanner data manifest iconCount is missing or below $MinimumIconCount. Found: $($Manifest.iconCount)"
    }
    if ($null -eq $Manifest.catalogItemCount -or [int]$Manifest.catalogItemCount -lt [int]$Manifest.iconCount) {
        throw 'RatScanner data manifest catalogItemCount is missing or smaller than iconCount.'
    }
    if ($null -eq $Manifest.skippedItemCount -or [int]$Manifest.skippedItemCount -lt 0) {
        throw 'RatScanner data manifest skippedItemCount is missing or negative.'
    }
    if ([int]$Manifest.catalogItemCount -ne ([int]$Manifest.iconCount + [int]$Manifest.skippedItemCount)) {
        throw 'RatScanner data manifest catalogItemCount must equal iconCount plus skippedItemCount.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$Manifest.contentSha256) -or [string]$Manifest.contentSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'RatScanner data manifest contentSha256 is missing or invalid.'
    }
    if ($null -eq $Manifest.fileCount -or [int]$Manifest.fileCount -le 0) {
        throw 'RatScanner data manifest fileCount is missing or invalid.'
    }
    if ($null -eq $Manifest.files -or @($Manifest.files).Count -ne [int]$Manifest.fileCount) {
        throw 'RatScanner data manifest files do not match fileCount.'
    }
    return $Manifest
}

function Assert-RatScannerDataContentPin {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$ContentSha256Prefix
    )

    $contentSha256 = ([string]$Manifest.contentSha256).ToLowerInvariant()
    if (-not $contentSha256.StartsWith($ContentSha256Prefix.ToLowerInvariant(), [System.StringComparison]::Ordinal)) {
        throw "RatScanner data contentSha256 does not match the pinned release. Found: $contentSha256; expected prefix: $ContentSha256Prefix"
    }
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

    return Assert-RatScannerDataManifestObject `
        -Manifest $manifest `
        -ExpectedSchema $ExpectedSchema `
        -MinimumIconCount $MinimumIconCount
}

function Assert-RatScannerDataFiles {
    param(
        [Parameter(Mandatory = $true)][string]$DataRoot,
        [Parameter(Mandatory = $true)]$Manifest
    )

    $expectedFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    [void]$expectedFiles.Add('manifest.json')
    foreach ($entry in @($Manifest.files | Sort-Object -Property path)) {
        $relativePath = ([string]$entry.path).Replace('\', '/')
        $expectedHash = ([string]$entry.sha256).ToLowerInvariant()
        $expectedSize = [long]$entry.size
        if ([string]::IsNullOrWhiteSpace($relativePath) -or $relativePath -match '(^|/)\.\.(/|$)' -or [System.IO.Path]::IsPathRooted($relativePath)) {
            throw "RatScanner data manifest contains an unsafe file path: $relativePath"
        }
        if (-not $expectedFiles.Add($relativePath)) {
            throw "RatScanner data manifest contains a duplicate file path: $relativePath"
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
        $item = Get-Item -LiteralPath $fullPath
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Data archive manifest file is a reparse point: $relativePath"
        }
        $actualSize = $item.Length
        if ($actualSize -ne $expectedSize) {
            throw "Data archive file size mismatch for $relativePath. Actual: $actualSize; manifest: $expectedSize"
        }
        $actualHash = Get-RatScannerDataFileSha256 -Path $fullPath
        if ($actualHash -ne $expectedHash) {
            throw "Data archive file checksum mismatch for $relativePath. Actual: $actualHash; manifest: $expectedHash"
        }
    }

    $rootPrefix = [System.IO.Path]::GetFullPath($DataRoot).TrimEnd('\') + '\'
    foreach ($item in Get-ChildItem -LiteralPath $DataRoot -File -Recurse) {
        $relativePath = $item.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        if (-not $expectedFiles.Contains($relativePath)) {
            throw "Data archive contains a file not listed in manifest.json: $relativePath"
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
        [Parameter(Mandatory = $true)][int]$MinimumIconCount,
        [string]$ContentSha256Prefix = ''
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
    if (-not [string]::IsNullOrWhiteSpace($ContentSha256Prefix)) {
        Assert-RatScannerDataContentPin -Manifest $manifest -ContentSha256Prefix $ContentSha256Prefix
    }
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
        [Parameter(Mandatory = $true)][int]$MinimumIconCount,
        [string]$ContentSha256Prefix = '',
        [switch]$Deep
    )

    try {
        $manifest = Read-RatScannerDataManifest `
            -ManifestPath (Join-Path $DataRoot 'manifest.json') `
            -ExpectedSchema $ExpectedSchema `
            -MinimumIconCount $MinimumIconCount
        # Without this, an installation left over from a previously pinned release satisfies the
        # skip-when-installed path and development silently continues on stale data.
        if (-not [string]::IsNullOrWhiteSpace($ContentSha256Prefix)) {
            Assert-RatScannerDataContentPin -Manifest $manifest -ContentSha256Prefix $ContentSha256Prefix
        }
        foreach ($relativePath in @('maps.json', 'unknown.png', 'traineddata\eng.traineddata')) {
            if (-not (Test-Path -LiteralPath (Join-Path $DataRoot $relativePath) -PathType Leaf)) {
                return $false
            }
        }
        $actualIconCount = @(Get-ChildItem -LiteralPath (Join-Path $DataRoot 'icons') -Filter '*.png' -File -ErrorAction Stop).Count
        if ($actualIconCount -ne [int]$manifest.iconCount) {
            return $false
        }
        if ($Deep) {
            # A matching manifest still does not prove the files on disk are intact.
            Assert-RatScannerDataFiles -DataRoot $DataRoot -Manifest $manifest
        }
        return $true
    }
    catch {
        return $false
    }
}

function Get-RatScannerDataEntrySha256 {
    param([Parameter(Mandatory = $true)]$Entry)

    $stream = $Entry.Open()
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

<#
.SYNOPSIS
  Verifies the packaged release archive that is actually promoted.

.DESCRIPTION
  Installation validation only proves the staged publish tree was correct. Packaging happens
  afterwards, so the promoted artifact is verified here directly from the zip: the manifest it
  carries, every manifest-listed payload byte, the pinned content hash, the icon count, the
  required application entries, and the absence of packaging leftovers.
#>
function Assert-RatScannerDataPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][int]$ExpectedSchema,
        [Parameter(Mandatory = $true)][int]$MinimumIconCount,
        [string]$ContentSha256Prefix = '',
        [string]$DataPrefix = 'Data/',
        [string[]]$RequiredEntries = @('RatScanner.exe', 'LICENSE')
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "Release package does not exist: $PackagePath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead([System.IO.Path]::GetFullPath($PackagePath))
    try {
        # PowerShell hashtables are case-insensitive, but zip entry names are case-sensitive and
        # manifest paths are compared ordinally elsewhere. A case-insensitive map would silently
        # collapse Data/icons/AbC.png and Data/icons/abc.png, leaving one payload file unhashed.
        $files = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
        # Tracked separately: this target is Windows x64 only, where two entries differing only by
        # case overwrite each other on extraction, so the shipped file may not be the verified one.
        $caseFoldedNames = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            # Directory entries carry an empty Name; only real files are verifiable.
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }
            # 7-Zip writes spec-compliant forward slashes; Compress-Archive on Windows PowerShell
            # writes backslashes. Normalize so either packer produces the same lookup keys.
            $normalizedName = $entry.FullName.Replace('\', '/')
            # Windows resolves './' segments and strips trailing dots and spaces, so a
            # non-canonical name can extract over a file that was hashed under its canonical name.
            # A well-formed package never contains such names, so reject them outright.
            foreach ($segment in $normalizedName.Split('/')) {
                if ([string]::IsNullOrEmpty($segment) -or $segment -eq '.' -or $segment -eq '..' -or
                    $segment -ne $segment.TrimEnd('.', ' ') -or $segment.Contains(':') -or
                    $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
                    throw ("Release package contains a non-canonical entry name that can alias " +
                        "another file on Windows: $normalizedName")
                }
                # Reserved device names cannot be materialized even with an extension, so such an
                # entry would fail extraction on a user machine after passing verification here.
                $deviceStem = $segment.Split('.')[0]
                $hasSuperscriptDeviceSuffix = $deviceStem.Length -eq 4 -and
                    $deviceStem.Substring(0, 3) -match '(?i)^(?:COM|LPT)$' -and
                    ([int][char]$deviceStem[3]) -in @(0x00B9, 0x00B2, 0x00B3)
                if ($deviceStem -match '(?i)^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$' -or
                    $hasSuperscriptDeviceSuffix) {
                    throw ("Release package contains a reserved Windows device name that cannot be " +
                        "extracted: $normalizedName")
                }
            }
            if ($files.ContainsKey($normalizedName)) {
                throw "Release package contains a duplicate entry: $normalizedName"
            }
            if ($caseFoldedNames.ContainsKey($normalizedName)) {
                throw ("Release package contains entries that differ only by case and collide when " +
                    "extracted on Windows: $($caseFoldedNames[$normalizedName]) and $normalizedName")
            }
            $caseFoldedNames[$normalizedName] = $normalizedName
            $files[$normalizedName] = $entry
        }

        foreach ($required in $RequiredEntries) {
            if (-not $files.ContainsKey($required)) {
                throw "Release package is missing required entry: $required"
            }
        }

        foreach ($entryName in $files.Keys) {
            if ($entryName -eq 'Data.zip' -or $entryName -eq ($DataPrefix + 'Data.zip')) {
                throw "Release package contains a temporary data archive: $entryName"
            }
            if ($entryName -like ($DataPrefix + 'Data/*')) {
                throw "Release package contains an unflattened nested data directory: $entryName"
            }
            if ($entryName -like '*/.Data.install-*' -or $entryName -like '*/.Data.backup-*' -or
                $entryName -like '.Data.install-*' -or $entryName -like '.Data.backup-*') {
                throw "Release package contains a data installation leftover: $entryName"
            }
        }

        $manifestEntryName = $DataPrefix + 'manifest.json'
        if (-not $files.ContainsKey($manifestEntryName)) {
            throw "Release package is missing required entry: $manifestEntryName"
        }
        $manifestStream = $files[$manifestEntryName].Open()
        try {
            $reader = New-Object System.IO.StreamReader($manifestStream)
            try {
                $manifestText = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $manifestStream.Dispose()
        }

        try {
            $manifest = $manifestText | ConvertFrom-Json
        }
        catch {
            throw "Release package manifest is not valid JSON: $($_.Exception.Message)"
        }

        [void](Assert-RatScannerDataManifestObject `
            -Manifest $manifest `
            -ExpectedSchema $ExpectedSchema `
            -MinimumIconCount $MinimumIconCount)
        if (-not [string]::IsNullOrWhiteSpace($ContentSha256Prefix)) {
            Assert-RatScannerDataContentPin -Manifest $manifest -ContentSha256Prefix $ContentSha256Prefix
        }

        $manifestFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        [void]$manifestFiles.Add($manifestEntryName)
        foreach ($manifestEntry in @($manifest.files)) {
            $relativePath = ([string]$manifestEntry.path).Replace('\', '/')
            $entryName = $DataPrefix + $relativePath
            if (-not $manifestFiles.Add($entryName)) {
                throw "Release package manifest contains a duplicate file path: $relativePath"
            }
            if (-not $files.ContainsKey($entryName)) {
                throw "Release package is missing manifest file: $relativePath"
            }
            $packagedEntry = $files[$entryName]
            $expectedSize = [long]$manifestEntry.size
            if ($packagedEntry.Length -ne $expectedSize) {
                throw "Release package file size mismatch for $relativePath. Actual: $($packagedEntry.Length); manifest: $expectedSize"
            }
            $expectedHash = ([string]$manifestEntry.sha256).ToLowerInvariant()
            $actualHash = Get-RatScannerDataEntrySha256 -Entry $packagedEntry
            if ($actualHash -ne $expectedHash) {
                throw "Release package file checksum mismatch for $relativePath. Actual: $actualHash; manifest: $expectedHash"
            }
        }

        foreach ($entryName in $files.Keys) {
            if ($entryName.StartsWith($DataPrefix, [System.StringComparison]::Ordinal) -and
                -not $manifestFiles.Contains($entryName)) {
                throw "Release package contains a data file not listed in manifest.json: $entryName"
            }
        }

        $actualContentHash = Get-RatScannerDataContentSha256 -Entries $manifest.files
        if ($actualContentHash -ne ([string]$manifest.contentSha256).ToLowerInvariant()) {
            throw "Release package contentSha256 mismatch. Actual: $actualContentHash; manifest: $($manifest.contentSha256)"
        }

        foreach ($relativePath in @('maps.json', 'unknown.png', 'traineddata/eng.traineddata')) {
            if (-not $files.ContainsKey($DataPrefix + $relativePath)) {
                throw "Release package is missing required data file: $relativePath"
            }
        }

        $iconPrefix = $DataPrefix + 'icons/'
        # Count only direct children so this matches the non-recursive install-side count.
        $packagedIconCount = @($files.Keys | Where-Object {
            $_ -like ($iconPrefix + '*.png') -and -not ($_.Substring($iconPrefix.Length).Contains('/'))
        }).Count
        if ($packagedIconCount -ne [int]$manifest.iconCount) {
            throw "Release package icon count does not match manifest.json. Actual: $packagedIconCount; manifest: $($manifest.iconCount)"
        }

        return [pscustomobject]@{
            PackagePath   = [System.IO.Path]::GetFullPath($PackagePath)
            EntryCount    = $files.Count
            IconCount     = $packagedIconCount
            FileCount     = @($manifest.files).Count
            ContentSha256 = [string]$manifest.contentSha256
        }
    }
    finally {
        $archive.Dispose()
    }
}
