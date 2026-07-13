param(
    [string]$ReleaseUrl = 'https://github.com/RatScanner/RatScannerData/releases/latest/download/Data.zip',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$destination = Join-Path $repositoryRoot 'RatScanner\Data'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('RatScanner-data-' + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $workRoot 'Data.zip'
$extractPath = Join-Path $workRoot 'extract'
$expandScript = Join-Path $PSScriptRoot 'Expand-Zip.ps1'

function Test-DataReady {
    param([string]$DataDir)
    $required = @(
        'maps.json',
        'unknown.png',
        'traineddata\eng.traineddata'
    )
    foreach ($relativePath in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $DataDir $relativePath))) {
            return $false
        }
    }
    $iconsDir = Join-Path $DataDir 'icons'
    if (-not (Test-Path -LiteralPath $iconsDir)) {
        return $false
    }
    $iconCount = @(Get-ChildItem -LiteralPath $iconsDir -Filter '*.png' -File -ErrorAction SilentlyContinue).Count
    return $iconCount -gt 0
}

if (-not $Force -and (Test-DataReady -DataDir $destination)) {
    $iconCount = @(Get-ChildItem -LiteralPath (Join-Path $destination 'icons') -Filter '*.png' -File).Count
    Write-Host "Data already installed ($iconCount icons) at $destination"
    Write-Host "Pass -Force to re-download."
    exit 0
}

try {
    New-Item -ItemType Directory -Force -Path $extractPath | Out-Null
    Write-Host "Downloading RatScanner data..."
    try {
        Invoke-WebRequest -Uri $ReleaseUrl -OutFile $archivePath
    }
    catch {
        # Fallback when IWR fails (proxy / TLS quirks)
        Write-Host "Invoke-WebRequest failed; trying curl..."
        & curl.exe -L $ReleaseUrl --output $archivePath
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $archivePath)) {
            throw "Failed to download data from $ReleaseUrl"
        }
    }

    Write-Host "Extracting data archive..."
    & $expandScript -ArchivePath $archivePath -DestinationPath $extractPath

    # Archive contents may be at extract root or under a Data/ prefix
    $sourceRoot = $extractPath
    if (-not (Test-Path -LiteralPath (Join-Path $extractPath 'maps.json'))) {
        $nested = Join-Path $extractPath 'Data'
        if (Test-Path -LiteralPath (Join-Path $nested 'maps.json')) {
            $sourceRoot = $nested
        }
    }

    $requiredFiles = @(
        'maps.json',
        'unknown.png',
        'traineddata\eng.traineddata'
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $relativePath))) {
            throw "Data archive is missing required file: $relativePath"
        }
    }

    $iconCount = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'icons') -Filter '*.png' -File).Count
    if ($iconCount -eq 0) {
        throw 'Data archive does not contain item icons.'
    }

    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Get-ChildItem -LiteralPath $sourceRoot -Force | Copy-Item -Destination $destination -Recurse -Force
    Write-Host "Installed $iconCount icons and OCR data to $destination"
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedWorkRoot = [IO.Path]::GetFullPath($workRoot)
    if ($resolvedWorkRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $resolvedWorkRoot -Leaf).StartsWith('RatScanner-data-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
