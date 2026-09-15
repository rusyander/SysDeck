<#
.SYNOPSIS
  Installs the AudioFix tasks, the desktop shortcut, and migrates the older
  UsbAudioPowerGuard install into this one.

.NOTES
  Register-ScheduledTask rather than schtasks.exe: one task needs an event trigger
  (Kernel-Power 107) with a start delay, which schtasks cannot express.

  Both tasks run as SYSTEM - cycling a PnP device and writing HKLM needs it. The
  default-playback-device part of audio-fix.ps1 is per-user and therefore only ever
  runs from the shortcut, never from a task.

.EXAMPLE
  .\install-task.ps1
  .\install-task.ps1 -Uninstall
#>
[CmdletBinding()]
param(
  [switch]$Uninstall,
  [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$script = Join-Path $root 'audio-fix.ps1'
$vbs    = Join-Path $root 'run-hidden.vbs'
$cfgF   = Join-Path $root 'audio-fix.config.json'
$state  = 'C:\ProgramData\audio-fix'

function Test-Elevated {
  ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Elevated)) {
  $argv = @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File',"`"$($MyInvocation.MyCommand.Path)`"")
  if ($Uninstall)  { $argv += '-Uninstall' }
  if ($NoShortcut) { $argv += '-NoShortcut' }
  Start-Process powershell.exe -Verb RunAs -ArgumentList $argv
  exit 0
}

$shortcutName = 'Fix sound'
if (Test-Path $cfgF) {
  $c = Get-Content $cfgF -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($c.shortcutName) { $shortcutName = $c.shortcutName }
}
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "$shortcutName.lnk"

# ------------------------------------------------------------------ uninstall

if ($Uninstall) {
  foreach ($t in 'AudioFix-Boot','AudioFix-Wake') {
    Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "removed task $t"
  }
  Remove-Item $lnk -Force -ErrorAction SilentlyContinue
  Write-Host "removed shortcut, kept $state for the log"
  exit 0
}

foreach ($p in $script, $vbs) { if (-not (Test-Path $p)) { throw "not found next to this script: $p" } }

# ------------------------------------------------------------------ state dir

New-Item -ItemType Directory -Path $state -Force | Out-Null
# Users get modify, not just read: a shortcut run writes the same log the SYSTEM tasks do.
icacls $state /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)M' | Out-Null
Write-Host "state dir: $state (users may write the log)"

# ------------------------------------------------------------------ migration

$legacyDir = 'C:\ProgramData\UsbAudioPowerGuard'
foreach ($t in 'UsbAudioPowerGuard','UsbAudioPowerGuard-Resume') {
  if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $t -Confirm:$false
    Write-Host "migrated away from legacy task $t"
  }
}
if (Test-Path $legacyDir) {
  $old = Join-Path $legacyDir 'guard.log'
  if (Test-Path $old) { Copy-Item $old (Join-Path $state 'legacy-guard.log') -Force }
  Remove-Item $legacyDir -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host "legacy folder folded in (history kept as legacy-guard.log)"
}
# Matched by what the shortcut points at, not by its name: the old name is Russian and
# this file stays ASCII so it parses without a BOM.
$wsh = New-Object -ComObject WScript.Shell
Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter '*.lnk' -ErrorAction SilentlyContinue |
  ForEach-Object {
    $sc = $wsh.CreateShortcut($_.FullName)
    if ($sc.Arguments -like '*UsbAudioPowerGuard*' -or $sc.TargetPath -like '*UsbAudioPowerGuard*') {
      Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
      Write-Host "removed superseded shortcut $($_.Name)"
    }
  }

# ------------------------------------------------------------------ tasks

$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
               -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew

function Register-AudioTask($name, $trigger, $switchName, $description) {
  # Through run-hidden.vbs: powershell -WindowStyle Hidden still flashes a console frame.
  $action = New-ScheduledTaskAction -Execute 'wscript.exe' `
              -Argument "`"$vbs`" `"$script`" $switchName"
  Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Description $description -Force | Out-Null
  Write-Host "registered $name"
}

$boot = New-ScheduledTaskTrigger -AtStartup
$boot.Delay = 'PT30S'      # let USB enumeration finish first
Register-AudioTask 'AudioFix-Boot' $boot '-OnBoot' `
  'Re-applies USB power settings for the playback device. A driver reinstall (Windows Update, Armoury Crate) recreates Device Parameters with stock defaults, which silently re-arms USB selective suspend.'

$cls = Get-CimClass -Namespace ROOT\Microsoft\Windows\TaskScheduler -ClassName MSFT_TaskEventTrigger
$wake = New-CimInstance -CimClass $cls -ClientOnly
# 107 or 566: a Modern Standby cycle can log no 107 at all, only a pair
# of 566 session transitions, and those are the wakes the digital output dies on. The
# wake gate in audio-fix.ps1 decides whether an event is worth a reset; the trigger only
# has to deliver it.
$wake.Subscription = "<QueryList><Query Id='0' Path='System'><Select Path='System'>*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=107 or EventID=566)]]</Select></Query></QueryList>"
$wake.Enabled = $true
$wake.Delay   = 'PT15S'    # let the USB tree finish resuming before touching it
Register-AudioTask 'AudioFix-Wake' $wake '-OnWake' `
  'Re-initialises the playback device after resume from sleep. Resume restores the bus link but not the digital transmitter in the codec, leaving the endpoint ACTIVE while the output is silent - undetectable, so the re-init is unconditional.'

# ------------------------------------------------------------------ shortcut

if (-not $NoShortcut) {
  $ws = New-Object -ComObject WScript.Shell
  $sc = $ws.CreateShortcut($lnk)
  $sc.TargetPath   = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
  $sc.Arguments    = "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$script`""
  $sc.IconLocation = "$env:SystemRoot\System32\mmres.dll,0"
  $sc.WorkingDirectory = $root
  $sc.Save()
  # Byte 21, flag 0x20 = "run as administrator". Set here so the script does not have to
  # re-launch itself, which would close the window before the report can be read.
  $b = [IO.File]::ReadAllBytes($lnk); $b[0x15] = $b[0x15] -bor 0x20; [IO.File]::WriteAllBytes($lnk, $b)
  Write-Host "shortcut: $lnk"
}

Write-Host ''
Write-Host 'Installed. Verify with:  audio-fix.ps1 -Audit'
Write-Host "Log: $state\audio-fix.log"
