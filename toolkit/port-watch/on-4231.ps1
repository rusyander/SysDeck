<#
Fires on System event Tcpip/4231 (ephemeral TCP port allocation failed).
The event never names the culprit; this snapshot, taken seconds after,
does: per-process connection counts by state, top talkers first.
Log: %LOCALAPPDATA%\port-watch\4231.log
#>
$ErrorActionPreference = 'SilentlyContinue'
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch { }

$logDir = Join-Path $env:LOCALAPPDATA 'port-watch'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$log = Join-Path $logDir '4231.log'

$c = Get-NetTCPConnection
$lines = @('=== ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  total=' + $c.Count + ' ===')
$lines += ($c | Group-Object State | Sort-Object Count -Descending |
    ForEach-Object { '  {0,-12} {1}' -f $_.Name, $_.Count })
$lines += '-- top owners (established+bound+timewait) --'
$lines += ($c | Group-Object OwningProcess | Sort-Object Count -Descending | Select-Object -First 20 |
    ForEach-Object {
        $p = Get-Process -Id $_.Name -ErrorAction SilentlyContinue
        $st = ($_.Group | Group-Object State | ForEach-Object { '{0}:{1}' -f $_.Name, $_.Count }) -join ' '
        '  {0,5}  pid {1,-7} {2,-20} {3}' -f $_.Count, $_.Name, $p.ProcessName, $st
    })
Add-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8

# Keep bounded.
$all = @(Get-Content $log)
if ($all.Count -gt 3000) { $all[-3000..-1] | Set-Content $log -Encoding UTF8 }
