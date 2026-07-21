# Shared zip extraction helper for environments where Expand-Archive / Archive module is broken.
# Prefer Expand-Archive, then .NET ZipFile, then python zipfile.
# Uses returns (not exit) so callers can invoke this with & without killing their session.
param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ArchivePath)) {
    throw "Archive not found: $ArchivePath"
}

New-Item -ItemType Directory -Force -Path $DestinationPath | Out-Null

function Test-ExtractSucceeded {
    param([string]$Path)
    return (Test-Path -LiteralPath $Path) -and (@(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue).Count -gt 0)
}

# 1) Expand-Archive (may fail if Microsoft.PowerShell.Archive cannot load)
try {
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $DestinationPath -Force -ErrorAction Stop
    if (Test-ExtractSucceeded -Path $DestinationPath) {
        Write-Host "Extracted with Expand-Archive."
        return
    }
}
catch {
    Write-Host "Expand-Archive unavailable ($($_.Exception.Message)). Trying .NET ZipFile..."
}

# 2) System.IO.Compression.ZipFile
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $DestinationPath)
    if (Test-ExtractSucceeded -Path $DestinationPath) {
        Write-Host "Extracted with System.IO.Compression.ZipFile."
        return
    }
}
catch {
    Write-Host "ZipFile extract failed ($($_.Exception.Message)). Trying python..."
}

# 3) python zipfile
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    $python = Get-Command py -ErrorAction SilentlyContinue
}
if ($python) {
    $code = @'
import sys, zipfile, os
archive, dest = sys.argv[1], sys.argv[2]
os.makedirs(dest, exist_ok=True)
with zipfile.ZipFile(archive) as z:
    z.extractall(dest)
print("ok")
'@
    & $python.Source -c $code $ArchivePath $DestinationPath
    if ($LASTEXITCODE -eq 0 -and (Test-ExtractSucceeded -Path $DestinationPath)) {
        Write-Host "Extracted with python zipfile."
        return
    }
}

throw "Failed to extract '$ArchivePath' to '$DestinationPath'. Install PowerShell Archive module, .NET ZipFile support, or Python."
