<#
.SYNOPSIS
  Reaps stale/orphaned/duplicate processes. Dry-run by default; -Force actually kills.

.DESCRIPTION
  Kill criteria (a process must clear ALL guards, then match ANY rule):

  GUARDS (never killed)
    - name in Blacklist (OS core), or SessionId 0 (services) unless -Aggressive
    - self, and every ancestor of self (never saw off the branch we sit on)
    - age < MinAgeMinutes
    - has a visible main window, unless name in WindowExempt
    - PID / name listed in Protect

  RULES
    - Orphan   : parent PID is gone, OR parent exists but was created AFTER the
                 child (PID reuse => the real parent is dead)
    - Duplicate: >= DupThreshold processes share an identical CommandLine =>
                 keep the KeepNewest most recent, reap the rest
    - OrphanAgent: name in ReapWhenOrphaned (agent hosts such as claude.exe) whose
                 parent died. These sit in Blacklist AND ProtectDescendantsOf so a
                 LIVE one is untouchable, which also made a crashed-editor leftover
                 immortal - it and its whole MCP subtree. This rule is the one
                 exception, and it only ever fires on a dead parent.
    - Descendant: any live child of a reaped process is reaped with it

  Whitelist mode (default) only ever considers names in Whitelist.
  -Aggressive flips to "everything except Blacklist".

.EXAMPLE
  .\reap.ps1                 # dry-run, prints the table
  .\reap.ps1 -Force          # actually kill
  .\reap.ps1 -Force -Json    # machine output, for hooks
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Json,
    [switch]$Quiet,
    [switch]$Aggressive,
    [switch]$Audit,
    [switch]$NoDefer,
    [int]$MinAgeMinutes = -1,
    [int]$DupThreshold  = -1,
    [int]$KeepNewest    = -1,
    [int]$SampleSeconds = 3,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

# A background janitor must never compete with foreground work. BelowNormal
# rather than Idle: Idle can starve completely while a game pins every core.
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch { }

# $PSScriptRoot is not reliably bound inside param() defaults under -File,
# which silently left the whitelist empty => a no-op run. Resolve it here.
if (-not $ConfigPath) {
    $root = $PSScriptRoot
    if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $ConfigPath = Join-Path $root 'reap.config.json'
}

# ---------------------------------------------------------------- config ----
$cfg = @{
    MinAgeMinutes   = 10
    DupThreshold    = 3
    KeepNewest      = 1
    MaxKillsPerRun  = 500
    Aggressive      = $false
    Whitelist       = @()
    Blacklist       = @()
    WindowExempt    = @('conhost', 'cmd')
    DuplicateExempt = @('conhost')
    TransientNames  = @()
    TransientMaxAgeMinutes = 60
    CpuIdleDeltaSec = 0.05
    ListenerMaxAgeHours = 12
    Protect         = @()
    ProtectPorts    = @()
    ProtectCommandLineMatch = @()
    ProtectDescendantsOf = @()
    ReapWhenOrphaned     = @()
    OrphanGraceMinutes   = 15
    IdleAgents           = @()
    IdleAgentHours       = 8
    IdleAgentCpuBudgetSec = 300
    IdleAgentMaxAgeHours = 24
    LogPath         = "$env:LOCALAPPDATA\proc-reaper\reap.log"
    LogMaxBytes     = 5242880
}
if (Test-Path $ConfigPath) {
    $fromFile = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($k in $fromFile.PSObject.Properties.Name) { $cfg[$k] = $fromFile.$k }
} else {
    Write-Error "Config not found: $ConfigPath"
    exit 2
}
if ($MinAgeMinutes -ge 0) { $cfg.MinAgeMinutes = $MinAgeMinutes }
if ($DupThreshold  -ge 0) { $cfg.DupThreshold  = $DupThreshold  }
if ($KeepNewest    -ge 0) { $cfg.KeepNewest    = $KeepNewest    }
if ($Aggressive)          { $cfg.Aggressive    = $true          }

# ------------------------------------------------- fullscreen defer ---------
# A full pass costs ~1 s of Normal-priority WMI CPU (measured 2026-08-31:
# 721 ms wall per Win32_Process walk, WmiPrvSE +0.7 s) plus kill cascades -
# a visible hitch inside a running game. Windows defers its own maintenance
# while something owns the screen; this janitor does the same. Only unattended
# -Force runs defer: a human typing reap.ps1 by hand wants it now (-NoDefer).
# A maximized window stops above the taskbar, so only true fullscreen matches.
if ($Force -and -not $NoDefer) {
    try {
        Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)] public struct PRRECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential)] public struct PRMONINFO { public int cbSize; public PRRECT rcMonitor; public PRRECT rcWork; public uint dwFlags; }
public static class PRFullscreen {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out PRRECT rect);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref PRMONINFO mi);
}
'@
        $fgWin = [PRFullscreen]::GetForegroundWindow()
        if ($fgWin -ne [IntPtr]::Zero) {
            $fgOwner = [uint32]0
            [void][PRFullscreen]::GetWindowThreadProcessId($fgWin, [ref]$fgOwner)
            $rect = New-Object PRRECT
            $mi   = New-Object PRMONINFO
            $mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][PRMONINFO])
            $mon = [PRFullscreen]::MonitorFromWindow($fgWin, 2)   # MONITOR_DEFAULTTONEAREST
            if ([PRFullscreen]::GetWindowRect($fgWin, [ref]$rect) -and [PRFullscreen]::GetMonitorInfo($mon, [ref]$mi)) {
                $fgName = (Get-Process -Id $fgOwner -ErrorAction SilentlyContinue).ProcessName
                # The bare desktop (explorer's Progman) also covers the monitor.
                if ($fgName -and $fgName -ne 'explorer' -and
                    $rect.L -le $mi.rcMonitor.L -and $rect.T -le $mi.rcMonitor.T -and
                    $rect.R -ge $mi.rcMonitor.R -and $rect.B -ge $mi.rcMonitor.B) {
                    $deferDir = Split-Path $cfg.LogPath -Parent
                    if (-not (Test-Path $deferDir)) { New-Item -ItemType Directory -Path $deferDir -Force | Out-Null }
                    Add-Content -Path $cfg.LogPath -Encoding UTF8 -Value (
                        '{0}  DEFER fullscreen foreground: {1} (pid {2})' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $fgName, $fgOwner)
                    exit 0
                }
            }
        }
    } catch { }   # the guard must never block the reap itself
}

$white   = @{}; foreach ($n in $cfg.Whitelist)    { $white[$n.ToLower()]   = $true }
$black   = @{}; foreach ($n in $cfg.Blacklist)    { $black[$n.ToLower()]   = $true }
$winExpt = @{}; foreach ($n in $cfg.WindowExempt) { $winExpt[$n.ToLower()] = $true }

$protectNames = @{}; $protectPids = @{}
foreach ($p in $cfg.Protect) {
    if ($p -match '^\d+$') { $protectPids[[int]$p] = $true }
    else { $protectNames[([string]$p).ToLower()] = $true }
}

# An empty whitelist in non-aggressive mode can never match anything. Fail loud
# rather than reporting a clean system that is in fact drowning in processes.
if (-not $cfg.Aggressive -and $white.Count -eq 0) {
    Write-Error "Whitelist is empty and -Aggressive is off - nothing could ever be reaped. Check $ConfigPath."
    exit 2
}

# ------------------------------------------------------------- snapshot ----
$now  = Get-Date
$live = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
$byPid = @{}
foreach ($p in $live) { $byPid[[int]$p.ProcessId] = $p }

# Windowed PIDs — a visible window means a human is probably looking at it.
$windowed = @{}
foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
    if ($p.MainWindowHandle -ne 0) { $windowed[$p.Id] = $true }
}

# Holding a socket is evidence of a real job in progress; used by the transient rule.
$sockets = @{}
foreach ($c in (Get-NetTCPConnection -ErrorAction SilentlyContinue)) { $sockets[[int]$c.OwningProcess] = $true }

# LISTEN sockets tracked separately: a listening server (vite, storybook, a stub
# API) is serving clients even when its parent shell is gone. Claude Code runs
# every command through a transient shell, so an agent's dev server ALWAYS looks
# orphaned minutes after starting - reaping it kills work in progress (happened
# 2026-08-28: a 12-minute-old vite tree died as [descendant]).
$listeners = @{}
foreach ($c in (Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)) {
    $listeners[[int]$c.OwningProcess] = $true
}

# Children index, for descendant expansion.
$children = @{}
foreach ($p in $live) {
    $pp = [int]$p.ParentProcessId
    if (-not $children.ContainsKey($pp)) { $children[$pp] = New-Object System.Collections.ArrayList }
    [void]$children[$pp].Add([int]$p.ProcessId)
}

function Get-Name([object]$p) {
    return ([string]$p.Name) -replace '\.exe$', ''
}

function Get-Age([object]$p) {
    if (-not $p.CreationDate) { return $null }
    return ($now - $p.CreationDate).TotalMinutes
}

# True when the recorded parent is gone, or is a PID-reuse impostor.
function Test-Orphan([object]$p) {
    $pp = [int]$p.ParentProcessId
    if ($pp -eq 0) { return $false }
    if (-not $byPid.ContainsKey($pp)) { return $true }
    $parent = $byPid[$pp]
    if ($parent.CreationDate -and $p.CreationDate -and $parent.CreationDate -gt $p.CreationDate) {
        return $true
    }
    return $false
}

# Self + ancestors: reaping any of these would kill the reaper mid-run. Kept as
# its own set because $immune later absorbs whole guarded subtrees, and the
# orphan-agent rule must be able to override those while still honouring this.
$selfChain = @{}
$cur = $PID
$hops = 0
while ($cur -and $byPid.ContainsKey($cur) -and $hops -lt 64) {
    $selfChain[$cur] = $true
    $cur = [int]$byPid[$cur].ParentProcessId
    $hops++
}
$immune = @{}
foreach ($k in $selfChain.Keys) { $immune[$k] = $true }

# Agent hosts (claude.exe) whose parent editor/shell is dead. They are blacklisted
# and guard subtrees, so nothing else could ever touch them: when VS Code crashes
# instead of closing cleanly, the leftover agent plus its MCP servers, node
# children and conhost survive until reboot. Computed before the guard BFS so a
# dead root cannot immunise its own orphaned subtree.
$orphanAgents = @{}
$agentNames = @{}
foreach ($n in $cfg.ReapWhenOrphaned) { $agentNames[([string]$n).ToLower()] = $true }
if ($agentNames.Count -gt 0) {
    foreach ($p in $live) {
        $procId = [int]$p.ProcessId
        if (-not $agentNames.ContainsKey((Get-Name $p).ToLower())) { continue }
        if ($selfChain.ContainsKey($procId)) { continue }
        if ($protectPids.ContainsKey($procId)) { continue }
        if ($protectNames.ContainsKey((Get-Name $p).ToLower())) { continue }
        if (-not (Test-Orphan $p)) { continue }
        $age = Get-Age $p
        # A longer grace than MinAgeMinutes: an agent launched detached on purpose
        # has no live parent either, and should get time to finish its work.
        if ($null -eq $age -or $age -lt $cfg.OrphanGraceMinutes) { continue }
        $orphanAgents[$procId] = $true
    }
}

# Agent hosts alive under a LIVE window but abandoned: each session leaves its
# own claude.exe behind, and it only dies with the window. Two triggers, both
# needing IdleAgentHours of hourly CPU samples first:
#   idle  - own CPU grew <= IdleAgentCpuBudgetSec across the window. Measured on
#           a dev machine: abandoned agents drift 16-35 s/hour (max ~280 s/8h),
#           used ones land above; the budget of 300 sits on that boundary. Own
#           CPU, not subtree: MCP pollers burn 40-370 s/hour in trees nobody
#           touches, and write-I/O turned out inverted (stale agents log-spam
#           50-230 KB/min while an active one writes almost nothing).
#   stale - older than IdleAgentMaxAgeHours AND no established non-loopback TCP
#           connection. Catches the anomalous busy-looper the CPU budget spares;
#           the connection guard keeps a hung-but-connected API stream alive
#           rather than killing it mid-flight.
# The whole MCP/LSP subtree dies with the agent - including a dev server started
# from that session. Deliberate: an 8h-untouched stand is garbage here, restart
# is one command. Computed before the guard BFS for the same reason as
# orphanAgents: a doomed root must not immunise its own tree.
$idleAgents = @{}
$idleNames = @{}
foreach ($n in $cfg.IdleAgents) { $idleNames[([string]$n).ToLower()] = $true }
if ($idleNames.Count -gt 0) {
    $stateDir = Split-Path $cfg.LogPath -Parent
    if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }
    $activityPath = Join-Path $stateDir 'agent-activity.json'
    $hist = @{}
    if (Test-Path $activityPath) {
        try {
            $loaded = Get-Content $activityPath -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($prop in $loaded.PSObject.Properties) { $hist[$prop.Name] = @($prop.Value) }
        } catch { $hist = @{} }
    }
    $fmt = 'yyyy-MM-dd HH:mm:ss'
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $cutoff = $now.AddHours(-[double]$cfg.IdleAgentHours)
    # Established connections to a non-loopback peer. Loopback (Figma SSE, local
    # MCP) says nothing about the session being alive; an API stream does.
    $extConn = @{}
    foreach ($c in (Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue)) {
        if ($c.RemoteAddress -ne '127.0.0.1' -and $c.RemoteAddress -ne '::1') {
            $extConn[[int]$c.OwningProcess] = $true
        }
    }
    $newHist = @{}
    foreach ($p in $live) {
        $procId = [int]$p.ProcessId
        if (-not $idleNames.ContainsKey((Get-Name $p).ToLower())) { continue }
        if (-not $p.CreationDate) { continue }
        $gp = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if (-not $gp) { continue }
        $cpuNow = [math]::Round($gp.TotalProcessorTime.TotalSeconds, 1)
        # PID alone is reusable; PID + creation stamp is not. Dead agents simply
        # drop out of $newHist, so the state file never grows stale entries.
        $key = "$procId|$($p.CreationDate.ToString('yyyyMMddHHmmss', $inv))"
        $samples = @()
        if ($hist.ContainsKey($key)) { $samples = @($hist[$key]) }
        $samples += [pscustomobject]@{ t = $now.ToString($fmt, $inv); cpu = $cpuNow }
        # Keep the newest sample older than the window (the baseline) plus all
        # samples inside it: ~one entry per hourly run per agent, nothing more.
        $baseline = $null
        $inWindow = @()
        foreach ($s in $samples) {
            $ts = [datetime]::ParseExact([string]$s.t, $fmt, $inv)
            if ($ts -le $cutoff) {
                if ($null -eq $baseline -or $ts -gt [datetime]::ParseExact([string]$baseline.t, $fmt, $inv)) { $baseline = $s }
            } else {
                $inWindow += $s
            }
        }
        $keep = @()
        if ($baseline) { $keep += $baseline }
        $keep += $inWindow
        $newHist[$key] = $keep

        if ($null -eq $baseline) { continue }                                # not enough history yet
        $idleByCpu  = ($cpuNow - [double]$baseline.cpu) -le [double]$cfg.IdleAgentCpuBudgetSec
        $agentAge   = Get-Age $p
        $staleByAge = ($null -ne $agentAge) -and
                      ($agentAge -ge [double]$cfg.IdleAgentMaxAgeHours * 60) -and
                      (-not $extConn.ContainsKey($procId))
        if (-not ($idleByCpu -or $staleByAge)) { continue }
        if ($selfChain.ContainsKey($procId))  { continue }
        if ($protectPids.ContainsKey($procId)) { continue }
        if ($protectNames.ContainsKey((Get-Name $p).ToLower())) { continue }
        $idleAgents[$procId] = $true
    }
    $jsonOut = if ($newHist.Count -gt 0) { $newHist | ConvertTo-Json -Depth 4 } else { '{}' }
    Set-Content -Path $activityPath -Value $jsonOut -Encoding UTF8
}

# A live editor / agent / terminal owns its whole subtree: a dev server still
# hanging off a running VS Code window is in use, not garbage. Only trees whose
# root already died can be orphans, so this costs no coverage.
$guardRoots = @{}
foreach ($n in $cfg.ProtectDescendantsOf) { $guardRoots[([string]$n).ToLower()] = $true }
if ($guardRoots.Count -gt 0) {
    $gq = New-Object System.Collections.Queue
    foreach ($p in $live) {
        $procId = [int]$p.ProcessId
        if ($orphanAgents.ContainsKey($procId)) { continue }
        if ($idleAgents.ContainsKey($procId))   { continue }
        $pn = (([string]$p.Name) -replace '\.exe$', '').ToLower()
        if ($guardRoots.ContainsKey($pn)) { $gq.Enqueue($procId) }
    }
    while ($gq.Count -gt 0) {
        $r = $gq.Dequeue()
        if (-not $children.ContainsKey($r)) { continue }
        foreach ($c in $children[$r]) {
            if ($immune.ContainsKey($c)) { continue }
            # A doomed agent nested under a live editor: do not descend into it,
            # or the guard would immunise the very subtree the kill must drag.
            if ($idleAgents.ContainsKey($c) -or $orphanAgents.ContainsKey($c)) { continue }
            $immune[$c] = $true
            $gq.Enqueue($c)
        }
    }
}

# Immunising a listener alone is not enough: `pnpm`, `cmd` and `node --watch`
# above it hold no socket, get reaped as orphans, and the stand comes apart
# limb by limb - the surviving halves read to a human as "it switched itself
# off" (a local service kept losing either its API supervisor or its vite half
# between sessions). Agent hosts are NOT climbed into: a stand started
# from an agent session must not make that session immortal, and it does not
# need to - the descendant cascade already skips immune children, so the tree
# outlives its host's death anyway.
function Protect-Ancestors([int]$seed) {
    $cur = $seed
    $hops = 0
    while ($hops -lt 64 -and $byPid.ContainsKey($cur)) {
        $node = $byPid[$cur]
        $pp = [int]$node.ParentProcessId
        if ($pp -eq 0 -or -not $byPid.ContainsKey($pp)) { break }
        $parent = $byPid[$pp]
        # A parent younger than its child is a PID-reuse impostor, not an ancestor.
        if ($parent.CreationDate -and $node.CreationDate -and $parent.CreationDate -gt $node.CreationDate) { break }
        $pn = (Get-Name $parent).ToLower()
        if ($agentNames.ContainsKey($pn) -or $idleNames.ContainsKey($pn)) { break }
        $immune[$pp] = $true
        $cur = $pp
        $hops++
    }
}

# Same, plus everything the process spawned. Used only where the whole tree is
# known to belong to one service (ProtectPorts / ProtectCommandLineMatch).
function Protect-Tree([int]$seed) {
    if (-not $byPid.ContainsKey($seed)) { return }
    $seen = @{}
    $q = New-Object System.Collections.Queue
    $q.Enqueue($seed)
    while ($q.Count -gt 0) {
        $n = [int]$q.Dequeue()
        if ($seen.ContainsKey($n)) { continue }
        $seen[$n] = $true
        $immune[$n] = $true
        if ($children.ContainsKey($n)) { foreach ($c in $children[$n]) { $q.Enqueue([int]$c) } }
    }
    Protect-Ancestors $seed
}

# Young listeners are immune from every rule (orphan, duplicate, transient,
# descendant-drag). Past ListenerMaxAgeHours they are garbage like everything
# else - the 193-leaked-vite scenario this tool was built for still gets
# cleaned, just a day later. Agent hosts (claude.exe) are deliberately NOT
# covered: their lifecycle is the orphan/idle-agent rules' job.
$maxListenMin = [double]$cfg.ListenerMaxAgeHours * 60
if ($maxListenMin -gt 0) {
    foreach ($p in $live) {
        $procId = [int]$p.ProcessId
        if (-not $listeners.ContainsKey($procId)) { continue }
        if ($immune.ContainsKey($procId)) { continue }
        $pn = (Get-Name $p).ToLower()
        if ($agentNames.ContainsKey($pn) -or $idleNames.ContainsKey($pn)) { continue }
        $age = Get-Age $p
        if ($null -ne $age -and $age -ge $maxListenMin) { continue }
        $immune[$procId] = $true
        Protect-Ancestors $procId
    }
}

# Ports of a service the user keeps up on purpose. Unlike ListenerMaxAgeHours
# this never expires: a local panel running for days is the point of it, not
# leaked garbage.
$protectPorts = @{}
foreach ($n in $cfg.ProtectPorts) { $protectPorts[[int]$n] = $true }
if ($protectPorts.Count -gt 0) {
    foreach ($c in (Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)) {
        if (-not $protectPorts.ContainsKey([int]$c.LocalPort)) { continue }
        Protect-Tree ([int]$c.OwningProcess)
    }
}

# Regex over the full command line, for supervisors that hold no port of their
# own: without this a watchdog is an orphan like any other and dies exactly when
# it is needed - while the thing it guards is down.
foreach ($rx in @($cfg.ProtectCommandLineMatch)) {
    if (-not $rx) { continue }
    foreach ($p in $live) {
        $cl = [string]$p.CommandLine
        if ($cl -and $cl -match $rx) { Protect-Tree ([int]$p.ProcessId) }
    }
}

# ---------------------------------------------------------------- guards ----
function Test-Reapable([object]$p) {
    $procId = [int]$p.ProcessId
    $name   = (Get-Name $p).ToLower()

    if ($immune.ContainsKey($procId))     { return $false }   # self / ancestors
    if ($protectPids.ContainsKey($procId)){ return $false }
    if ($protectNames.ContainsKey($name)) { return $false }
    if ($black.ContainsKey($name))        { return $false }   # OS core
    if (-not $cfg.Aggressive -and -not $white.ContainsKey($name)) { return $false }
    if (-not $cfg.Aggressive -and $p.SessionId -eq 0)            { return $false }

    $age = Get-Age $p
    if ($null -eq $age -or $age -lt $cfg.MinAgeMinutes) { return $false }

    if ($windowed.ContainsKey($procId) -and -not $winExpt.ContainsKey($name)) { return $false }

    return $true
}

# ----------------------------------------------------------------- rules ----
$doomed = @{}   # pid -> reason

# Seeded first so the descendant pass drags the whole dead agent tree with it.
foreach ($k in $orphanAgents.Keys) { $doomed[$k] = 'orphan-agent' }
Write-Verbose "orphan-agents=$($orphanAgents.Count)"

foreach ($k in $idleAgents.Keys) {
    if (-not $doomed.ContainsKey($k)) { $doomed[$k] = 'idle-agent' }
}
Write-Verbose "idle-agents=$($idleAgents.Count)"

$reapable = @($live | Where-Object { Test-Reapable $_ })
Write-Verbose "live=$($live.Count) immune=$($immune.Count) reapable=$($reapable.Count)"

foreach ($p in $reapable) {
    if (Test-Orphan $p) { $doomed[[int]$p.ProcessId] = 'orphan' }
}
Write-Verbose "orphans=$($doomed.Count)"

# conhost's command line is the generic `conhost.exe 0x4` for every instance, so
# grouping by it would treat unrelated live consoles as copies of each other.
$dupExempt = @{}
foreach ($n in $cfg.DuplicateExempt) { $dupExempt[([string]$n).ToLower()] = $true }

$candidates = $live |
    Where-Object { $_.CommandLine } |
    Where-Object { -not $dupExempt.ContainsKey((Get-Name $_).ToLower()) } |
    Where-Object { Test-Reapable $_ }
foreach ($grp in ($candidates | Group-Object CommandLine)) {
    if ($grp.Count -lt $cfg.DupThreshold) { continue }
    $sorted = $grp.Group | Sort-Object CreationDate -Descending
    for ($i = $cfg.KeepNewest; $i -lt $sorted.Count; $i++) {
        $procId = [int]$sorted[$i].ProcessId
        if (-not $doomed.ContainsKey($procId)) { $doomed[$procId] = 'duplicate' }
    }
}

# Processes that are short-lived BY DESIGN but overstayed: a COM surrogate or a
# `git fetch` still around an hour later is hung, not working. Deliberately NOT
# a general "kill idle processes" rule - a language server sitting at 0% CPU is
# doing its job, waiting, and reaping it would break the editor.
$transient = @{}
foreach ($n in $cfg.TransientNames) { $transient[([string]$n).ToLower()] = $true }

$tCand = New-Object System.Collections.ArrayList
foreach ($p in $live) {
    $procId = [int]$p.ProcessId
    if ($doomed.ContainsKey($procId)) { continue }
    if (-not $transient.ContainsKey((Get-Name $p).ToLower())) { continue }
    if (-not (Test-Reapable $p)) { continue }
    $age = Get-Age $p
    if ($null -eq $age -or $age -lt $cfg.TransientMaxAgeMinutes) { continue }
    if ($children.ContainsKey($procId)) { continue }   # still parenting something
    if ($sockets.ContainsKey($procId)) { continue }    # still holding a connection
    # WindowExempt loosens the window guard for orphaned cmd/conhost tails; the
    # transient rule must not inherit that, or an idle console a human left open
    # would be reaped out from under them.
    if ($windowed.ContainsKey($procId)) { continue }
    [void]$tCand.Add($procId)
}

if ($tCand.Count -gt 0) {
    # Sample CPU across a real interval - cumulative CPU cannot tell "never did
    # anything" from "did its work and went quiet".
    $cpuBefore = @{}
    foreach ($q in (Get-Process -ErrorAction SilentlyContinue)) {
        if ($null -ne $q.CPU) { $cpuBefore[$q.Id] = $q.CPU }
    }
    Start-Sleep -Seconds $SampleSeconds
    foreach ($q in (Get-Process -ErrorAction SilentlyContinue)) {
        if (-not $tCand.Contains($q.Id)) { continue }
        if ($null -eq $q.CPU -or -not $cpuBefore.ContainsKey($q.Id)) { continue }
        if (($q.CPU - $cpuBefore[$q.Id]) -le $cfg.CpuIdleDeltaSec) {
            $doomed[$q.Id] = 'stale-transient'
        }
    }
}

# Descendants of doomed processes go too (npm -> cmd -> conhost tails).
$queue = New-Object System.Collections.Queue
foreach ($k in @($doomed.Keys)) { $queue.Enqueue($k) }
while ($queue.Count -gt 0) {
    $parentPid = $queue.Dequeue()
    if (-not $children.ContainsKey($parentPid)) { continue }
    foreach ($childPid in $children[$parentPid]) {
        if ($doomed.ContainsKey($childPid) -or $immune.ContainsKey($childPid)) { continue }
        $cp = $byPid[$childPid]
        if ($protectPids.ContainsKey($childPid)) { continue }
        if ($protectNames.ContainsKey((Get-Name $cp).ToLower())) { continue }
        if ($black.ContainsKey((Get-Name $cp).ToLower())) { continue }
        $doomed[$childPid] = 'descendant'
        $queue.Enqueue($childPid)
    }
}

# ------------------------------------------------------------------ plan ----
$rss = @{}
foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) { $rss[$p.Id] = $p.WorkingSet64 }

function Get-Depth([int]$procId) {
    $d = 0; $c = $procId
    while ($byPid.ContainsKey($c) -and $d -lt 64) {
        $c = [int]$byPid[$c].ParentProcessId
        if (-not $doomed.ContainsKey($c)) { break }
        $d++
    }
    return $d
}

$plan = foreach ($procId in $doomed.Keys) {
    $p = $byPid[$procId]
    $cmd = [string]$p.CommandLine
    if ($cmd.Length -gt 120) { $cmd = $cmd.Substring(0, 120) + '...' }
    [pscustomobject]@{
        PID      = $procId
        Name     = Get-Name $p
        Reason   = $doomed[$procId]
        AgeMin   = [math]::Round((Get-Age $p), 0)
        RamMB    = [math]::Round(($rss[$procId] / 1MB), 1)
        Depth    = Get-Depth $procId
        Command  = $cmd
    }
}
$plan = @($plan | Sort-Object Depth -Descending)

if ($plan.Count -gt $cfg.MaxKillsPerRun) {
    if (-not $Quiet) { Write-Warning "Plan has $($plan.Count) targets, capped at MaxKillsPerRun=$($cfg.MaxKillsPerRun)." }
    $plan = @($plan | Select-Object -First $cfg.MaxKillsPerRun)
}

$freedMB = [math]::Round((($plan | Measure-Object RamMB -Sum).Sum), 0)

# ----------------------------------------------------------------- audit ----
# Report-only: what is growing, versus the previous audit. Answers "is anything
# leaking" without the risk of guessing whether a live process is still wanted.
if ($Audit) {
    $stateDir  = Split-Path $cfg.LogPath -Parent
    if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }
    $statePath = Join-Path $stateDir 'audit.json'

    $groups = Get-Process -ErrorAction SilentlyContinue | Group-Object ProcessName | ForEach-Object {
        [pscustomobject]@{
            Name  = $_.Name
            Count = $_.Count
            RamMB = [math]::Round((($_.Group | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 0)
        }
    }

    $prev = @{}
    if (Test-Path $statePath) {
        $old = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($g in $old.groups) { $prev[$g.Name] = $g.Count }
        Write-Host "Previous audit: $($old.stamp) - $($old.total) processes"
    }

    Write-Host "Now: $($live.Count) processes, reapable right now: $($plan.Count)"
    Write-Host ""
    $groups | Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object {
        $d = ''
        if ($prev.ContainsKey($_.Name)) {
            $diff = $_.Count - $prev[$_.Name]
            if ($diff -gt 0) { $d = "  (+$diff)" } elseif ($diff -lt 0) { $d = "  ($diff)" }
        } else { $d = '  (new)' }
        '{0,-28} {1,4}  {2,6} MB{3}' -f $_.Name, $_.Count, $_.RamMB, $d | Write-Host
    }
    Write-Host ""
    Write-Host "Reapable breakdown:"
    if ($plan.Count -eq 0) {
        Write-Host "  nothing - no orphans, no duplicates, no stale transients"
    } else {
        $plan | Group-Object Name, Reason | Sort-Object Count -Descending |
            ForEach-Object { '  {0,4}  {1}' -f $_.Count, $_.Name | Write-Host }
    }

    [pscustomobject]@{
        stamp  = $now.ToString('yyyy-MM-dd HH:mm:ss')
        total  = $live.Count
        groups = @($groups)
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $statePath -Encoding UTF8
    exit 0
}

# ------------------------------------------------------------------ act -----
$killed   = 0
$failed   = 0
$cascaded = 0
if ($Force) {
    foreach ($t in $plan) {
        try { Stop-Process -Id $t.PID -Force -ErrorAction Stop; $killed++ }
        catch {
            # Reaping a leaf makes its `cmd /c` parent exit on its own, which in
            # turn releases conhost. Most misses here are cascade wins, not errors.
            if (Get-Process -Id $t.PID -ErrorAction SilentlyContinue) { $failed++ } else { $cascaded++ }
        }
    }

    $logDir = Split-Path $cfg.LogPath -Parent
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    if ((Test-Path $cfg.LogPath) -and (Get-Item $cfg.LogPath).Length -gt $cfg.LogMaxBytes) {
        Move-Item $cfg.LogPath "$($cfg.LogPath).1" -Force
    }
    $stamp = $now.ToString('yyyy-MM-dd HH:mm:ss')
    $lines = @("$stamp  RUN killed=$killed cascaded=$cascaded failed=$failed freedMB=$freedMB total=$($live.Count)")
    foreach ($t in $plan) { $lines += "$stamp  KILL $($t.PID) $($t.Name) [$($t.Reason)] age=$($t.AgeMin)m $($t.Command)" }
    Add-Content -Path $cfg.LogPath -Value $lines -Encoding UTF8
}

# --------------------------------------------------------------- output -----
if ($Json) {
    [pscustomobject]@{
        mode       = if ($Force) { 'kill' } else { 'dry-run' }
        candidates = $plan.Count
        killed     = $killed
        cascaded   = $cascaded
        failed     = $failed
        freedMB    = $freedMB
        totalProcs = $live.Count
    } | ConvertTo-Json -Compress
    exit 0
}

if (-not $Quiet) {
    if ($plan.Count -eq 0) {
        Write-Host "Nothing to reap. Live processes: $($live.Count)."
        exit 0
    }
    $plan | Group-Object Name, Reason | Sort-Object Count -Descending |
        Format-Table @{n = 'Count'; e = { $_.Count } }, @{n = 'Name, Reason'; e = { $_.Name } } -AutoSize | Out-String | Write-Host
    $plan | Select-Object -First 40 | Format-Table PID, Name, Reason, AgeMin, RamMB, Command -AutoSize | Out-String -Width 240 | Write-Host
    if ($plan.Count -gt 40) { Write-Host "... and $($plan.Count - 40) more" }
    Write-Host ""
    if ($Force) {
        Write-Host "KILLED $killed / $($plan.Count) (failed: $failed), freed ~$freedMB MB. Log: $($cfg.LogPath)"
    } else {
        Write-Host "DRY-RUN: $($plan.Count) targets, ~$freedMB MB would be freed. Re-run with -Force to kill."
    }
}
