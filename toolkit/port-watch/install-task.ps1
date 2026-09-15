<#
Registers PortWatch4231: fires ON the System event Tcpip/4231 (ephemeral TCP
port allocation failed) and snapshots per-process port usage while the storm
is still in flight. Event-triggered - zero cost until the problem recurs.
Safe to re-run.
#>
$ErrorActionPreference = 'Stop'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$vbs    = Join-Path $here 'run-hidden.vbs'
$script = Join-Path $here 'on-4231.ps1'

# Paths contain no spaces, so no nested quoting is needed (PS 5.1 mangles it).
$tr    = "wscript.exe $vbs $script"
$xpath = "*[System[Provider[@Name='Tcpip'] and (EventID=4231)]]"

& schtasks /Create /TN 'PortWatch4231' /SC ONEVENT /EC System /MO $xpath /TR $tr /F
