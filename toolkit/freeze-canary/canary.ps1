<#
.SYNOPSIS
  Records system-wide stalls the moment they happen, with the evidence needed
  to name the culprit.

.DESCRIPTION
  Every lag report arrived after the lag had ended, leaving only
  event-log fragments to reconstruct from. This is the missing instrument: a
  thread that wakes on a fixed tick and measures how late it actually woke.

  A thread at ABOVE-NORMAL priority that cannot get scheduled for a full second
  is not losing a CPU race - the whole system stalled. That is exactly the
  symptom being hunted, so the detector matches it by construction.

  On a stall it diffs current state against a baseline taken seconds earlier and
  writes one report:
    * how long the stall lasted
    * DPC and interrupt time across it - a driver storm shows up HERE and
      nowhere in per-process CPU, which is why "everything lagged but nothing
      was busy" kept coming up empty
    * kernel vs user time, disk queue and idle %, free RAM
    * the processes that actually burned CPU across the window
    * which process owned the foreground window

  Cost: one sleeping PowerShell process. The tick does no work; the baseline
  snapshot (~100 ms) is taken once per BaselineSec, so steady-state cost is well
  under 1% of a single core out of 32.

  Stop it: Unregister-ScheduledTask FreezeCanary -Confirm:$false, then kill the
  powershell process running this file.

.EXAMPLE
  .\canary.ps1                  # run here, print stalls as they happen
  .\canary.ps1 -MinStallMs 500  # more sensitive
  .\canary.ps1 -Report          # summarise the log so far and exit
#>
[CmdletBinding()]
param(
    # 1 s is far above scheduler noise (tick jitter is ~15 ms) and just below
    # what a human reliably notices as "it froze".
    [int]$MinStallMs = 1000,
    [int]$TickMs = 200,
    [int]$BaselineSec = 10,
    [int]$HeartbeatMin = 60,
    [string]$LogPath = "$env:LOCALAPPDATA\freeze-canary\stalls.log",
    [int]$LogMaxBytes = 4194304,
    [switch]$Report
)

$ErrorActionPreference = 'SilentlyContinue'

$logDir = Split-Path $LogPath -Parent
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

if ($Report) {
    if (-not (Test-Path $LogPath)) { Write-Host 'no log yet'; exit 0 }
    $lines = @(Get-Content $LogPath)
    $stalls = @($lines | Where-Object { $_ -match '\sSTALL\s' })
    Write-Host ("stalls recorded: {0}" -f $stalls.Count)
    $lines | Where-Object { $_ -match '\sSTALL\s|burners:' } | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
    exit 0
}

# The whole measurement rests on this: at AboveNormal, running late means the
# system stalled, not that something outranked us.
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'AboveNormal' } catch { }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FCWin {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
'@

$nproc = [Environment]::ProcessorCount

function Write-Line([string]$text) {
    if ((Test-Path $LogPath) -and (Get-Item $LogPath).Length -gt $LogMaxBytes) {
        Move-Item $LogPath "$LogPath.1" -Force
    }
    $line = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
    Write-Host $line
}

# One cheap CIM read. These counters are cumulative 100 ns ticks summed over all
# cores, so delta / (elapsed * cores) is a true percentage.
function Get-CpuCounters {
    $c = Get-CimInstance Win32_PerfRawData_PerfOS_Processor -Filter "Name='_Total'"
    if (-not $c) { return $null }
    return [pscustomobject]@{
        Ts        = [double]$c.Timestamp_Sys100NS
        Idle      = [double]$c.PercentIdleTime
        Dpc       = [double]$c.PercentDPCTime
        Interrupt = [double]$c.PercentInterruptTime
        Priv      = [double]$c.PercentPrivilegedTime
        User      = [double]$c.PercentUserTime
    }
}

function Get-ProcCpu {
    $h = @{}
    foreach ($p in (Get-Process)) {
        if ($null -ne $p.CPU) { $h[$p.Id] = @($p.ProcessName, [double]$p.CPU) }
    }
    return $h
}

function New-Baseline {
    return [pscustomobject]@{
        At    = Get-Date
        Cpu   = Get-CpuCounters
        Procs = Get-ProcCpu
    }
}

Write-Line ("START canary: threshold={0}ms tick={1}ms baseline={2}s cores={3} pid={4}" -f $MinStallMs, $TickMs, $BaselineSec, $nproc, $PID)

$base = New-Baseline
$sw = [Diagnostics.Stopwatch]::StartNew()
$lastTick = $sw.ElapsedMilliseconds
$lastBaseline = $sw.ElapsedMilliseconds
$lastHeartbeat = Get-Date
$stallCount = 0

while ($true) {
    Start-Sleep -Milliseconds $TickMs
    $nowMs = $sw.ElapsedMilliseconds
    $late = ($nowMs - $lastTick) - $TickMs
    $lastTick = $nowMs

    if ($late -ge $MinStallMs) {
        $stallCount++
        $cpuNow = Get-CpuCounters
        $elapsed = 1.0
        $dpcPct = -1.0; $intPct = -1.0; $idlePct = -1.0; $privPct = -1.0; $userPct = -1.0
        if ($cpuNow -and $base.Cpu) {
            $dt = $cpuNow.Ts - $base.Cpu.Ts
            if ($dt -gt 0) {
                $elapsed = $dt / 1e7
                $den = $dt * $nproc
                $dpcPct  = [math]::Round(($cpuNow.Dpc - $base.Cpu.Dpc) / $den * 100, 2)
                $intPct  = [math]::Round(($cpuNow.Interrupt - $base.Cpu.Interrupt) / $den * 100, 2)
                $idlePct = [math]::Round(($cpuNow.Idle - $base.Cpu.Idle) / $den * 100, 1)
                $privPct = [math]::Round(($cpuNow.Priv - $base.Cpu.Priv) / $den * 100, 1)
                $userPct = [math]::Round(($cpuNow.User - $base.Cpu.User) / $den * 100, 1)
            }
        }

        $fgPid = [uint32]0
        [void][FCWin]::GetWindowThreadProcessId([FCWin]::GetForegroundWindow(), [ref]$fgPid)
        $fgName = (Get-Process -Id $fgPid).ProcessName
        if (-not $fgName) { $fgName = '?' }

        $os = Get-CimInstance Win32_OperatingSystem
        $freeGb = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
        $disk = Get-CimInstance Win32_PerfFormattedData_PerfDisk_LogicalDisk -Filter "Name='C:'"

        $head = 'STALL {0} ms  (window {1:N1}s)  fg={2}  dpc={3}% int={4}% kernel={5}% user={6}% idle={7}%  diskQ={8} diskIdle={9}%  freeRAM={10}GB'
        Write-Line ($head -f $late, $elapsed, $fgName, $dpcPct, $intPct, $privPct, $userPct, $idlePct, $disk.CurrentDiskQueueLength, $disk.PercentIdleTime, $freeGb)

        $procsNow = Get-ProcCpu
        $burners = foreach ($k in $procsNow.Keys) {
            $cur = $procsNow[$k]
            if (-not $base.Procs.ContainsKey($k)) {
                # Started inside the window: everything it burned counts.
                [pscustomobject]@{ Name = $cur[0]; Id = $k; Sec = [double]$cur[1]; New = $true }
            } else {
                $d = [double]$cur[1] - [double]$base.Procs[$k][1]
                [pscustomobject]@{ Name = $cur[0]; Id = $k; Sec = $d; New = $false }
            }
        }
        $top = @($burners | Where-Object { $_.Sec -gt 0.2 } | Sort-Object Sec -Descending | Select-Object -First 6)
        if ($top.Count) {
            $txt = ($top | ForEach-Object {
                $tag = ''
                if ($_.New) { $tag = '*new' }
                '{0}({1}) {2}%{3}' -f $_.Name, $_.Id, [math]::Round($_.Sec / $elapsed * 100, 0), $tag
            }) -join '  '
            Write-Line ('      burners: ' + $txt)
        } else {
            Write-Line '      burners: none - no process consumed CPU across the stall'
        }

        $base = New-Baseline
        $lastBaseline = $sw.ElapsedMilliseconds
        continue
    }

    if (($nowMs - $lastBaseline) -ge ($BaselineSec * 1000)) {
        $base = New-Baseline
        $lastBaseline = $nowMs
    }

    if (((Get-Date) - $lastHeartbeat).TotalMinutes -ge $HeartbeatMin) {
        Write-Line ('alive: {0} stalls in the last {1} min' -f $stallCount, $HeartbeatMin)
        $stallCount = 0
        $lastHeartbeat = Get-Date
    }
}
