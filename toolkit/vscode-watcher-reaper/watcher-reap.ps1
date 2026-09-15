<#
.SYNOPSIS
  Kills VS Code file-watcher processes stuck in the @parcel/watcher spin loop.
  VS Code respawns a clean watcher on its own.

.DESCRIPTION
  Symptom (microsoft/vscode#303702): one `file-watcher` utility process per
  window pins a full core forever while the editor is idle. Measured on this
  machine 16.08.2026, VS Code 1.133.0, 4 windows: ~12% of a 32-thread CPU.

  Detection is by signature, not by name - the process name and command line of
  file-watcher, extension-host, pty-host and shared-process are identical
  (`--type=utility --utility-sub-type=node.mojom.NodeService`). A stuck watcher
  is the only one that matches all four of:

    * >= MinCorePct of ONE core sustained across the whole sample window
    * >= MinOtherOpsPerSec non-read/write I/O ops per second
    * <= MaxBytesPerSec of actual traffic - a spin loop moves no bytes
    * mostly kernel time (>= MinKernelPct), which is what the busy syscall is

  Measured contrast: stuck 226 000 ops/s at 100% of a core with
  0 bytes moved; healthy 31 ops/s at 0,3%. Four orders of magnitude apart, so
  the thresholds have enormous headroom and cannot clip a busy extension host
  (that one moves bytes and runs in user mode).

  files.watcherExclude does NOT prevent this: a 158-directory workspace span at
  exactly the same rate as a 23 832-directory one. The spin is volume-independent.

.EXAMPLE
  .\watcher-reap.ps1                 # dry-run, print candidates only
  .\watcher-reap.ps1 -Force          # kill
  .\watcher-reap.ps1 -Force -Quiet   # for the scheduled task: log file only
#>
[CmdletBinding()]
param(
    # Length of the measurement window. Longer = no chance of catching a
    # legitimate burst; a real spin loop never ends, so nothing is lost by waiting.
    [int]$SampleSec = 8,
    # CPU as a percentage of ONE core.
    [int]$MinCorePct = 80,
    # Non-read/write I/O operations per second.
    [int]$MinOtherOpsPerSec = 50000,
    # Ceiling on real traffic: above this the process is doing work, not spinning.
    [int]$MaxBytesPerSec = 262144,
    # Share of CPU time spent in kernel mode.
    [int]$MinKernelPct = 60,
    # Without this nothing is killed.
    [switch]$Force,
    [switch]$Quiet,
    [string]$LogPath = "$env:LOCALAPPDATA\vscode-watcher-reaper\watcher-reap.log"
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
# Background job: never compete with foreground work.
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch { }

function Write-Log {
    param([string]$Message)
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    if (-not $Quiet) { Write-Host $line }
    try {
        $dir = Split-Path $LogPath -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -Path $LogPath -Value $line -Encoding UTF8
    } catch { }
}

function Get-Snapshot {
    $snap = @{}
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='Code.exe'" -ErrorAction SilentlyContinue) {
        $cl = $p.CommandLine
        if (-not $cl) { continue }
        # Node utility processes only: file-watcher, extension host, pty-host,
        # shared-process. Renderers and the GPU process can never match anyway,
        # but there is no reason to sample them.
        if ($cl -notlike '*--utility-sub-type=node.mojom.NodeService*') { continue }
        $snap[[int]$p.ProcessId] = [pscustomobject]@{
            Cpu100ns = [double]$p.UserModeTime + [double]$p.KernelModeTime
            Kern100ns = [double]$p.KernelModeTime
            Other    = [double]$p.OtherOperationCount
            Bytes    = [double]$p.ReadTransferCount + [double]$p.WriteTransferCount
            Start    = $p.CreationDate
        }
    }
    return $snap
}

$a = Get-Snapshot
if ($a.Count -eq 0) { return }
Start-Sleep -Seconds $SampleSec
$b = Get-Snapshot

$hits = 0
foreach ($procId in $b.Keys) {
    if (-not $a.ContainsKey($procId)) { continue }
    # Same process, not a recycled PID.
    if ($a[$procId].Start -ne $b[$procId].Start) { continue }

    $cpuDelta  = $b[$procId].Cpu100ns - $a[$procId].Cpu100ns
    if ($cpuDelta -le 0) { continue }
    $kernDelta = $b[$procId].Kern100ns - $a[$procId].Kern100ns

    $corePct   = ($cpuDelta / 1e7) / $SampleSec * 100
    $kernPct   = $kernDelta / $cpuDelta * 100
    $opsSec    = ($b[$procId].Other - $a[$procId].Other) / $SampleSec
    $bytesSec  = ($b[$procId].Bytes - $a[$procId].Bytes) / $SampleSec

    $stuck = ($corePct -ge $MinCorePct) -and
             ($opsSec -ge $MinOtherOpsPerSec) -and
             ($bytesSec -le $MaxBytesPerSec) -and
             ($kernPct -ge $MinKernelPct)
    if (-not $stuck) { continue }

    $hits++
    $desc = 'PID {0}: cpu={1}% kernel={2}% ops/s={3} bytes/s={4}' -f `
            $procId, [math]::Round($corePct, 1), [math]::Round($kernPct, 0), [int]$opsSec, [int]$bytesSec

    if (-not $Force) {
        Write-Log "[dry-run] spinning file-watcher -> $desc"
        continue
    }
    try {
        Stop-Process -Id $procId -Force
        Write-Log "killed spinning file-watcher -> $desc"
    } catch {
        Write-Log "failed to kill PID ${procId}: $($_.Exception.Message)"
    }
}

# One heartbeat line per run even when clean. At a 2-hour interval that is ~12
# lines a day, and without it the log cannot distinguish "nothing to kill" from
# "the scheduled task silently stopped firing".
if ($hits -eq 0) {
    Write-Log "clean: $($b.Count) node utility processes sampled, none spinning"
}
