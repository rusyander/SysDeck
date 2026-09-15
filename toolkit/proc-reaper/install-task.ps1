<#
.SYNOPSIS
  Registers (or removes) the ProcReaper scheduled task: reap.ps1 -Force every N minutes.

.NOTES
  Uses schtasks.exe rather than Register-ScheduledTask: the latter serialises an
  indefinite repetition as P99999999DT23H59M59S, which Task Scheduler rejects
  outright. `/sc MINUTE /mo N` is indefinite by design.

  No /ru means the task runs as the current user while logged on - which is
  required anyway, since the reaper must see session-1 processes.

.EXAMPLE
  .\install-task.ps1                 # install, 4-hour interval
  .\install-task.ps1 -Minutes 60
  .\install-task.ps1 -StartTime 03:50
  .\install-task.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # 240, not 60: a run kills 5-20 process trees at once and churns the registry
    # for ~15 s. Hourly that lands on the desktop as a periodic stutter, and the
    # garbage it collects is not time-critical.
    [int]$Minutes = 240,
    [string]$StartTime = '03:50',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$TaskName = 'ProcReaper'

if ($Uninstall) {
    & schtasks.exe /delete /tn $TaskName /f 2>&1 | Out-Null
    Write-Host "Removed scheduled task '$TaskName'."
    exit 0
}

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$reap = Join-Path $root 'reap.ps1'
if (-not (Test-Path $reap)) { throw "reap.ps1 not found next to this script ($root)" }

# Launched through run-hidden.vbs, not powershell.exe directly: -WindowStyle
# Hidden still flashes a console frame, which is enough to pull focus out of a
# fullscreen game. The VBS shim creates no window at all.
$vbs = Join-Path $root 'run-hidden.vbs'
if (-not (Test-Path $vbs)) { throw "run-hidden.vbs not found next to this script ($root)" }
$run = "wscript.exe \`"$vbs\`" \`"$reap\`" -Force -Quiet"

& schtasks.exe /create /tn $TaskName /tr $run /sc MINUTE /mo $Minutes /st $StartTime /f | Out-Null
if ($LASTEXITCODE -ne 0) { throw "schtasks failed with exit code $LASTEXITCODE" }

Write-Host "Installed '$TaskName': reap.ps1 -Force every $Minutes min, no window, BelowNormal priority."
Write-Host "Log:    $env:LOCALAPPDATA\proc-reaper\reap.log"
Write-Host "Config: $root\reap.config.json"
