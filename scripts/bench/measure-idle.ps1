param(
    [Parameter(Mandatory = $true)]
    [string]$Label,

    [string]$Executable,

    [int]$WarmupSeconds = 8,

    [ValidateRange(1, [int]::MaxValue)]
    [int]$SampleCount = 10,

    [ValidateRange(1, [int]::MaxValue)]
    [int]$SampleIntervalMilliseconds = 1000
)

$ErrorActionPreference = 'Stop'

function Get-ProcessTreeIds {
    param([int]$RootProcessId)

    $rows = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($RootProcessId)

    do {
        $added = $false
        foreach ($row in $rows) {
            if ($ids.Contains([int]$row.ParentProcessId) -and $ids.Add([int]$row.ProcessId)) {
                $added = $true
            }
        }
    } while ($added)

    return @($ids)
}

function Get-ProcessTreeSnapshot {
    param([int]$RootProcessId)

    $ids = Get-ProcessTreeIds -RootProcessId $RootProcessId
    $processes = foreach ($id in $ids) {
        Get-Process -Id $id -ErrorAction SilentlyContinue
    }

    $cpuMilliseconds = 0.0
    $privateBytes = [long]0
    $workingSetBytes = [long]0
    $processCount = 0

    foreach ($process in $processes) {
        if ($null -eq $process) { continue }
        $processCount++
        # A process in the tree can exit between enumeration and property access.
        try { $cpuMilliseconds += [double]$process.TotalProcessorTime.TotalMilliseconds } catch { Write-Verbose "Process $($process.Id) exited before CPU time could be read." }
        try { $privateBytes += [long]$process.PrivateMemorySize64 } catch { Write-Verbose "Process $($process.Id) exited before private memory could be read." }
        try { $workingSetBytes += [long]$process.WorkingSet64 } catch { Write-Verbose "Process $($process.Id) exited before working set could be read." }
    }

    return [pscustomobject]@{
        ProcessCount = $processCount
        CpuMilliseconds = $cpuMilliseconds
        PrivateBytes = $privateBytes
        WorkingSetBytes = $workingSetBytes
    }
}

function Get-Percentile {
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    $sorted = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($Percentile * $sorted.Count) - 1)
    return [double]$sorted[$index]
}

if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $PSScriptRoot '..\..\src\App\bin\Release\net10.0-windows10.0.22621.0\RatScanner.exe'
}

$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$root = Start-Process -FilePath $resolvedExecutable -WorkingDirectory (Split-Path $resolvedExecutable) -PassThru
# Capture a stable identity before sampling so cleanup never targets a recycled PID.
$rootStartTime = $null
try { $rootStartTime = $root.StartTime } catch { $rootStartTime = $null }

try {
    Start-Sleep -Seconds $WarmupSeconds
    if ($root.HasExited) {
        throw "RatScanner exited during benchmark warmup with code $($root.ExitCode)."
    }

    $samples = [System.Collections.Generic.List[object]]::new()
    $previous = Get-ProcessTreeSnapshot -RootProcessId $root.Id
    $previousAt = [DateTimeOffset]::UtcNow

    for ($i = 0; $i -lt $SampleCount; $i++) {
        Start-Sleep -Milliseconds $SampleIntervalMilliseconds
        if ($root.HasExited) {
            throw "RatScanner exited during benchmark sampling with code $($root.ExitCode)."
        }
        $now = [DateTimeOffset]::UtcNow
        $current = Get-ProcessTreeSnapshot -RootProcessId $root.Id
        $elapsedMilliseconds = ($now - $previousAt).TotalMilliseconds
        $cpuMillisecondsPerSecond = if ($elapsedMilliseconds -gt 0) {
            (($current.CpuMilliseconds - $previous.CpuMilliseconds) / $elapsedMilliseconds) * 1000
        } else {
            0
        }

        $samples.Add([pscustomobject]@{
            Index = $i + 1
            ElapsedMilliseconds = [Math]::Round($elapsedMilliseconds, 3)
            CpuMillisecondsPerSecond = [Math]::Round($cpuMillisecondsPerSecond, 3)
            PrivateBytes = $current.PrivateBytes
            WorkingSetBytes = $current.WorkingSetBytes
            ProcessCount = $current.ProcessCount
        })

        $previous = $current
        $previousAt = $now
    }

    $cpuValues = [double[]]($samples | ForEach-Object CpuMillisecondsPerSecond)
    $privateValues = [double[]]($samples | ForEach-Object PrivateBytes)
    $workingSetValues = [double[]]($samples | ForEach-Object WorkingSetBytes)
    $timestamp = [DateTimeOffset]::UtcNow
    $outputDirectory = Join-Path $PSScriptRoot '..\..\data\bench'
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $safeLabel = $Label -replace '[^a-zA-Z0-9._-]', '-'
    $outputPath = Join-Path $outputDirectory "$($timestamp.ToString('yyyyMMdd-HHmmss'))-$safeLabel.json"

    $result = [ordered]@{
        TimestampUtc = $timestamp.ToString('O')
        Label = $Label
        GitSha = (git -C (Join-Path $PSScriptRoot '..\..') rev-parse HEAD).Trim()
        DotNetVersion = (dotnet --version).Trim()
        Machine = $env:COMPUTERNAME
        Executable = $resolvedExecutable
        WarmupSeconds = $WarmupSeconds
        SampleCount = $SampleCount
        SampleIntervalMilliseconds = $SampleIntervalMilliseconds
        Metrics = [ordered]@{
            CpuMillisecondsPerSecond = [ordered]@{
                Median = Get-Percentile -Values $cpuValues -Percentile 0.5
                P95 = Get-Percentile -Values $cpuValues -Percentile 0.95
            }
            PrivateBytes = [ordered]@{
                Median = Get-Percentile -Values $privateValues -Percentile 0.5
                P95 = Get-Percentile -Values $privateValues -Percentile 0.95
            }
            WorkingSetBytes = [ordered]@{
                Median = Get-Percentile -Values $workingSetValues -Percentile 0.5
                P95 = Get-Percentile -Values $workingSetValues -Percentile 0.95
            }
        }
        Samples = $samples
    }

    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding utf8
    $result | ConvertTo-Json -Depth 8
    Write-Host "Raw benchmark: $outputPath"
}
finally {
    $rootStillOurs = $false
    if ($null -ne $root) {
        try {
            $live = Get-Process -Id $root.Id -ErrorAction SilentlyContinue
            if ($null -ne $live -and ($null -eq $rootStartTime -or $live.StartTime -eq $rootStartTime)) {
                $rootStillOurs = $true
            }
        } catch {
            $rootStillOurs = $false
        }
    }

    if ($rootStillOurs) {
        $treeIds = Get-ProcessTreeIds -RootProcessId $root.Id
        if (-not $root.HasExited) {
            [void]$root.CloseMainWindow()
            if (-not $root.WaitForExit(5000)) {
                Stop-Process -Id $root.Id -Force -ErrorAction SilentlyContinue
            }
        }

        # WebView2 child processes can outlive their WPF parent. Only stop processes
        # captured from this benchmark's tree so unrelated WebView sessions are untouched.
        foreach ($processId in $treeIds) {
            if ($processId -ne $root.Id) {
                Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
