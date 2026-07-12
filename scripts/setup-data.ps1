param(
    [string]$ReleaseUrl = 'https://github.com/RatScanner/RatScannerData/releases/latest/download/Data.zip'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$destination = Join-Path $repositoryRoot 'RatScanner\Data'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('RatScanner-data-' + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $workRoot 'Data.zip'
$extractPath = Join-Path $workRoot 'extract'

try {
    New-Item -ItemType Directory -Force -Path $extractPath | Out-Null
    Write-Host "Downloading RatScanner data..."
    Invoke-WebRequest -Uri $ReleaseUrl -OutFile $archivePath
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force

    $requiredFiles = @(
        'maps.json',
        'unknown.png',
        'traineddata\eng.traineddata'
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $extractPath $relativePath))) {
            throw "Data archive is missing required file: $relativePath"
        }
    }

    $iconCount = @(Get-ChildItem -LiteralPath (Join-Path $extractPath 'icons') -Filter '*.png' -File).Count
    if ($iconCount -eq 0) {
        throw 'Data archive does not contain item icons.'
    }

    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Get-ChildItem -LiteralPath $extractPath -Force | Copy-Item -Destination $destination -Recurse -Force
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
