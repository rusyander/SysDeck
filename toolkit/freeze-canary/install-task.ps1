<#
Registers FreezeCanary: starts at logon, runs until logoff, restarts itself if
it ever dies. Hidden via run-hidden.vbs (a bare powershell.exe task flashes a
console and can steal focus from a fullscreen game).

Priority 7 is deliberately NOT used here: the canary must run at above-normal so
that "I woke up late" means the system stalled rather than that the task was
outranked. The script raises its own priority at startup; the task must not cap
it below that.

Safe to re-run: replaces the existing task.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$vbs    = Join-Path $here 'run-hidden.vbs'
$script = Join-Path $here 'canary.ps1'

$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('"{0}" "{1}"' -f $vbs, $script)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"

# ExecutionTimeLimit 0 = never kill it; this one is meant to run all day.
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartInterval (New-TimeSpan -Minutes 1) -RestartCount 3

Register-ScheduledTask -TaskName 'FreezeCanary' -Action $action -Trigger $trigger `
    -Settings $settings -Description 'Records system-wide stalls (>=1s) with DPC/interrupt time, CPU burners and foreground app. Log: %LOCALAPPDATA%\freeze-canary\stalls.log' -Force | Out-Null

Write-Host 'FreezeCanary registered (at logon, auto-restart).'
