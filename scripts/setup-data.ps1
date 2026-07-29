param(
    [string]$DestinationPath = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$destination = if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    Join-Path $repositoryRoot 'src\App\Data'
}
else {
    [System.IO.Path]::GetFullPath($DestinationPath)
}
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('RatScanner-data-' + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $workRoot 'Data.zip'
$checksumPath = Join-Path $workRoot 'Data.zip.sha256'
$publishedManifestPath = Join-Path $workRoot 'manifest.json'
$extractPath = Join-Path $workRoot 'extract'
$destinationParent = Split-Path -Parent $destination
$destinationLeaf = Split-Path -Leaf $destination
$installId = [Guid]::NewGuid().ToString('N')
$stagingPath = Join-Path $destinationParent ('.' + $destinationLeaf + '.install-' + $installId)
$backupPath = Join-Path $destinationParent ('.' + $destinationLeaf + '.backup-' + $installId)
$expandScript = Join-Path $PSScriptRoot 'Expand-Zip.ps1'
$dataScript = Join-Path $PSScriptRoot 'RatScannerData.ps1'

. $dataScript
$contract = Get-RatScannerDataReleaseContract

function Invoke-DataDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutFile
    )

    try {
        Invoke-WebRequest -Uri $Uri -OutFile $OutFile
    }
    catch {
        Write-Host "Invoke-WebRequest failed for $Uri; trying curl..."
        & curl.exe -fL --retry 3 --retry-all-errors $Uri --output $OutFile
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutFile -PathType Leaf)) {
            throw "Failed to download RatScanner data asset from $Uri"
        }
    }
}

if (-not $Force -and (Test-RatScannerDataInstallation `
    -DataRoot $destination `
    -ExpectedSchema $contract.ManifestSchema `
    -MinimumIconCount $contract.MinimumIconCount)) {
    $manifest = Get-Content -LiteralPath (Join-Path $destination 'manifest.json') -Raw | ConvertFrom-Json
    Write-Host "Data already installed ($($manifest.iconCount) icons, content $($manifest.contentSha256)) at $destination"
    Write-Host 'Pass -Force to re-download.'
    exit 0
}

try {
    New-Item -ItemType Directory -Force -Path $extractPath | Out-Null
    Write-Host "Downloading pinned RatScanner data release $($contract.ReleaseTag)..."
    Invoke-DataDownload -Uri $contract.ArchiveUrl -OutFile $archivePath
    Invoke-DataDownload -Uri $contract.ChecksumUrl -OutFile $checksumPath
    Invoke-DataDownload -Uri $contract.ManifestUrl -OutFile $publishedManifestPath

    $checksumText = Get-Content -LiteralPath $checksumPath -Raw
    [void](Assert-RatScannerDataArchiveChecksum `
        -ArchivePath $archivePath `
        -ChecksumText $checksumText `
        -PinnedSha256 $contract.ArchiveSha256)

    Write-Host 'Extracting data archive...'
    & $expandScript -ArchivePath $archivePath -DestinationPath $extractPath
    $sourceRoot = Resolve-RatScannerDataRoot -ExtractPath $extractPath
    $validation = Assert-RatScannerDataPayload `
        -DataRoot $sourceRoot `
        -PublishedManifestPath $publishedManifestPath `
        -ExpectedSchema $contract.ManifestSchema `
        -MinimumIconCount $contract.MinimumIconCount `
        -ContentSha256Prefix $contract.ContentSha256Prefix

    New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
    New-Item -ItemType Directory -Force -Path $stagingPath | Out-Null
    Get-ChildItem -LiteralPath $validation.DataRoot -Force | Copy-Item -Destination $stagingPath -Recurse -Force

    $hadDestination = Test-Path -LiteralPath $destination
    try {
        if ($hadDestination) {
            Move-Item -LiteralPath $destination -Destination $backupPath
        }
        Move-Item -LiteralPath $stagingPath -Destination $destination
        if ($hadDestination) {
            Remove-Item -LiteralPath $backupPath -Recurse -Force
        }
    }
    catch {
        if (-not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backupPath)) {
            Move-Item -LiteralPath $backupPath -Destination $destination
        }
        throw
    }
    Write-Host "Installed $($validation.IconCount) icons and OCR data to $destination"
    Write-Host "RatScannerData content: $($validation.ContentSha256)"
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedWorkRoot = [IO.Path]::GetFullPath($workRoot)
    if ($resolvedWorkRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $resolvedWorkRoot -Leaf).StartsWith('RatScanner-data-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($temporaryInstallPath in @($stagingPath, $backupPath)) {
        if ((Test-Path -LiteralPath $temporaryInstallPath) -and
            [System.IO.Path]::GetFullPath($temporaryInstallPath).StartsWith([System.IO.Path]::GetFullPath($destinationParent), [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $temporaryInstallPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
