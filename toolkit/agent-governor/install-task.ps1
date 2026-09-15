<#
Registers the AgentGovernor scheduled task: a sweep at logon and every 5 minutes,
hidden (run-hidden.vbs - a direct powershell.exe task flashes a console and can
steal focus from a fullscreen game). Safe to re-run: replaces the existing task.
#>
[CmdletBinding()]
param(
    [int]$Minutes = 10
)
$ErrorActionPreference = 'Stop'

$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$vbs    = Join-Path $here 'run-hidden.vbs'
$script = Join-Path $here 'governor.ps1'

$action  = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('"{0}" "{1}"' -f $vbs, $script)
$rep     = New-TimeSpan -Minutes $Minutes
$t1      = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval $rep
$t2      = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
# Priority 7 = the task's process tree starts at below-normal, so even the
# sweep's own startup burst cannot steal time from a foreground game.
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
    -Priority 7

Register-ScheduledTask -TaskName 'AgentGovernor' -Action $action -Trigger $t1, $t2 `
    -Settings $settings -Description 'Pins Claude agent process trees to BelowNormal priority + upper-half CPU affinity so they never lag foreground work.' -Force | Out-Null

Write-Host "AgentGovernor registered: every $Minutes min + at logon."
