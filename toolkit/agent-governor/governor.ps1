<#
.SYNOPSIS
  Keeps Claude agent process trees from competing with foreground work.

.DESCRIPTION
  Finds every root named in RootNames (claude.exe) and walks its whole
  descendant tree (MCP servers, node, vitest, esbuild, shells). Each process
  gets:
    - PriorityClass = BelowNormal  (never raises: Idle stays Idle)
    - ProcessorAffinity = upper half of logical CPUs, only on machines with
      >= MinLogicalForAffinity logical processors (on a 7950X that pins agents
      to CCD1, leaving CCD0 whole for games / foreground)

  Both are INHERITED by children on Windows (BelowNormal is one of the two
  priority classes CreateProcess propagates; affinity always propagates), so
  one pass per new session suffices; the 5-minute task only catches strays.

  Idempotent: reads before writing, logs only actual changes. Access-denied
  processes are skipped silently (elevated strays are not ours to manage).

  Deliberately does NOT touch: VS Code itself, tsserver, terminals, or anything
  outside a claude.exe tree - throttling the editor the human is typing in
  would trade one lag for another.

.EXAMPLE
  .\governor.ps1            # one sweep
  .\governor.ps1 -Report    # show current state of agent trees, change nothing
#>
[CmdletBinding()]
param(
    [switch]$Report
)

$ErrorActionPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

# The janitor itself must not compete with anything.
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch { }

$cfg = @{
    RootNames             = @('claude')
    MinLogicalForAffinity = 24      # below this, affinity would starve the agents; priority still applies
    LogPath               = "$env:LOCALAPPDATA\agent-governor\governor.log"
    LogMaxBytes           = 1048576
}
$cfgPath = Join-Path $PSScriptRoot 'governor.config.json'
if (Test-Path $cfgPath) {
    $fromFile = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($k in $fromFile.PSObject.Properties.Name) { $cfg[$k] = $fromFile.$k }
}

# Upper half of logical CPUs as an affinity mask; $null = leave affinity alone.
$mask = $null
$n = [Environment]::ProcessorCount
if ($n -ge $cfg.MinLogicalForAffinity -and $n -le 62) {
    $mask = [long]((([long]1 -shl $n) - 1) -bxor (([long]1 -shl [int]($n / 2)) - 1))
}

$roots = @{}
foreach ($r in $cfg.RootNames) { $roots[([string]$r).ToLower()] = $true }

# Steady-state short-circuit. The expensive part of a sweep is the full
# Win32_Process walk (600+ processes); doing it every run showed up as a
# ~1 s hitch in a running game (2026-08-31 17:41). Children INHERIT priority
# and affinity from a governed root, so if the set of root PIDs is unchanged
# since the last sweep and every root is still governed, nothing ungoverned
# can exist below them - skip the walk entirely.
$statePath = Join-Path (Split-Path $cfg.LogPath -Parent) 'last-roots.txt'
$rootProcs = @(Get-Process -Name @($cfg.RootNames) -ErrorAction SilentlyContinue)
$sig = @($rootProcs | ForEach-Object {
    try { '{0}:{1}' -f $_.Id, $_.StartTime.ToString('HHmmss') } catch { [string]$_.Id }
} | Sort-Object) -join ','
if (-not $Report) {
    if ($rootProcs.Count -eq 0) { Write-Host 'no agent roots running'; exit 0 }
    $prev = ''
    if (Test-Path $statePath) { $prev = (Get-Content $statePath -Raw -ErrorAction SilentlyContinue).Trim() }
    $allGoverned = $rootProcs.Count -gt 0
    foreach ($rp in $rootProcs) {
        if ($rp.PriorityClass -in 'Normal', 'AboveNormal', 'High', 'RealTime') { $allGoverned = $false; break }
        if ($mask -and [long]$rp.ProcessorAffinity -ne $mask) { $allGoverned = $false; break }
    }
    if ($sig -eq $prev -and $allGoverned) {
        Write-Host 'steady state - no sweep needed'
        exit 0
    }
}

$live = Get-CimInstance Win32_Process
$children = @{}
foreach ($p in $live) {
    $pp = [int]$p.ParentProcessId
    if (-not $children.ContainsKey($pp)) { $children[$pp] = New-Object System.Collections.ArrayList }
    [void]$children[$pp].Add([int]$p.ProcessId)
}

# BFS from every root: the tree IS the definition of "agent work".
$targets = New-Object System.Collections.ArrayList
$queue = New-Object System.Collections.Queue
foreach ($p in $live) {
    $pn = (([string]$p.Name) -replace '\.exe$', '').ToLower()
    if ($roots.ContainsKey($pn)) { $queue.Enqueue([int]$p.ProcessId); [void]$targets.Add([int]$p.ProcessId) }
}
$seen = @{}
foreach ($t in $targets) { $seen[$t] = $true }
while ($queue.Count -gt 0) {
    $cur = $queue.Dequeue()
    if (-not $children.ContainsKey($cur)) { continue }
    foreach ($c in $children[$cur]) {
        if ($seen.ContainsKey($c)) { continue }
        $seen[$c] = $true
        [void]$targets.Add($c)
        $queue.Enqueue($c)
    }
}

if ($Report) {
    $byPid = @{}
    foreach ($p in $live) { $byPid[[int]$p.ProcessId] = $p }
    Write-Host ("agent trees: {0} processes, affinity mask {1}" -f $targets.Count, $(if ($mask) { '0x{0:X}' -f $mask } else { '<off>' }))
    foreach ($t in $targets) {
        $gp = Get-Process -Id $t -ErrorAction SilentlyContinue
        if ($gp) { Write-Host ("  {0,-24} {1,7}  {2,-12} 0x{3:X}" -f $byPid[$t].Name, $t, $gp.PriorityClass, [long]$gp.ProcessorAffinity) }
    }
    return
}

$changed = @()
foreach ($t in $targets) {
    $gp = Get-Process -Id $t -ErrorAction SilentlyContinue
    if (-not $gp) { continue }
    $before = @()
    try {
        # Lower only. Idle/BelowNormal already yield; raising Idle would be a regression.
        if ($gp.PriorityClass -in 'Normal', 'AboveNormal', 'High', 'RealTime') {
            $old = $gp.PriorityClass
            $gp.PriorityClass = 'BelowNormal'
            $before += "prio $old->BelowNormal"
        }
    } catch { }
    try {
        if ($mask -and [long]$gp.ProcessorAffinity -ne $mask) {
            $old = [long]$gp.ProcessorAffinity
            $gp.ProcessorAffinity = [IntPtr]$mask
            $before += ('affinity 0x{0:X}->0x{1:X}' -f $old, $mask)
        }
    } catch { }
    if ($before.Count) { $changed += ('{0} {1}: {2}' -f $gp.ProcessName, $t, ($before -join ', ')) }
}

Set-Content -Path $statePath -Value $sig -Encoding UTF8 -ErrorAction SilentlyContinue

if ($changed.Count) {
    $logDir = Split-Path $cfg.LogPath -Parent
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    if ((Test-Path $cfg.LogPath) -and (Get-Item $cfg.LogPath).Length -gt $cfg.LogMaxBytes) {
        Move-Item $cfg.LogPath "$($cfg.LogPath).1" -Force
    }
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $lines = foreach ($c in $changed) { "$stamp  $c" }
    Add-Content -Path $cfg.LogPath -Value $lines -Encoding UTF8
}
Write-Host ("swept {0} agent processes, changed {1}" -f $targets.Count, $changed.Count)
