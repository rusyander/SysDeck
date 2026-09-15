<#
.SYNOPSIS
  Registers (or removes) the VSCodeWatcherReaper scheduled task:
  watcher-reap.ps1 -Force -Quiet every N minutes, with no window at all.

.NOTES
  Uses schtasks.exe rather than Register-ScheduledTask: the latter serialises an
  indefinite repetition as P99999999DT23H59M59S, which Task Scheduler rejects
  outright. `/sc MINUTE /mo N` is indefinite by design.

  No /ru means the task runs as the current user while logged on - required
  anyway, since the reaper must see the session's Code.exe processes.

  Start time 00:40 keeps the run away from ProcReaper (03:50) and other heavy
  (05:20), so two background passes never land on the same minute.

.EXAMPLE
  .\install-task.ps1                 # install, 2-hour interval
  .\install-task.ps1 -Minutes 240
  .\install-task.ps1 -StartTime 01:10
  .\install-task.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # 120, not 5: the check itself is cheap, but every run spawns powershell.exe
    # plus conhost, and that process churn is exactly what proc-reaper exists to
    # clean up. Trade-off: a watcher that starts spinning right after a pass
    # burns one core until the next one.
    [int]$Minutes = 120,
    [string]$StartTime = '00:40',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$TaskName = 'VSCodeWatcherReaper'

if ($Uninstall) {
    & schtasks.exe /delete /tn $TaskName /f 2>&1 | Out-Null
    Write-Host "Removed scheduled task '$TaskName'."
    exit 0
}

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$reap = Join-Path $root 'watcher-reap.ps1'
if (-not (Test-Path $reap)) { throw "watcher-reap.ps1 not found next to this script ($root)" }

# Launched through run-hidden.vbs, not powershell.exe directly: -WindowStyle
# Hidden still flashes a console frame, which is enough to pull focus out of a
# fullscreen game. The VBS shim creates no window at all.
$vbs = Join-Path $root 'run-hidden.vbs'
if (-not (Test-Path $vbs)) { throw "run-hidden.vbs not found next to this script ($root)" }
$run = "wscript.exe `"$vbs`" `"$reap`" -Force -Quiet"

& schtasks.exe /create /tn $TaskName /tr $run /sc MINUTE /mo $Minutes /st $StartTime /f | Out-Null
if ($LASTEXITCODE -ne 0) { throw "schtasks failed with exit code $LASTEXITCODE" }

Write-Host "Installed '$TaskName': watcher-reap.ps1 -Force every $Minutes min, no window, BelowNormal priority."
Write-Host "Log: $env:LOCALAPPDATA\vscode-watcher-reaper\watcher-reap.log"
