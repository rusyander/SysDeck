<#
Disables Multi-Plane Overlay (MPO) in DWM.

Why: on a mixed-GPU multi-monitor setup (2x 165 Hz on the dGPU + 1x 60 Hz on the
iGPU) MPO causes cursor/window freezes when crossing screens. Documented fix:
HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = 5. Windows and driver
updates can silently remove the value and bring the stalls back - hence this
script, so the fix is one command to re-apply and survives a machine move.

Needs elevation (HKLM). Takes effect after sign-out or reboot; often immediately.
-Revert deletes the value (returns MPO to default).
#>
[CmdletBinding()]
param(
    [switch]$Revert
)
$ErrorActionPreference = 'Stop'

$key = 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run elevated: HKLM write requires administrator.'
    exit 1
}

if ($Revert) {
    Remove-ItemProperty -Path $key -Name 'OverlayTestMode' -ErrorAction SilentlyContinue
    Write-Host 'OverlayTestMode removed - MPO back to default.'
} else {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name 'OverlayTestMode' -Type DWord -Value 5
    Write-Host 'OverlayTestMode = 5 - MPO disabled. Sign out or reboot to be sure it sticks.'
}
