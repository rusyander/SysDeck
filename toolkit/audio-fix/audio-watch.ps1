<#
  audio-watch.ps1 - records the state of the audio device over time so the next silent
  failure can be explained instead of guessed at.

  Why this exists: the failure leaves no trace. On 2026-08-20 the sound died with zero
  system events in the preceding two hours, on a clean devnode, with the endpoint
  reporting ACTIVE and bits flowing (peak 0.34). Every repair so far has therefore been
  a blind reset. This samples the few signals that would actually distinguish the
  candidate causes, so the next occurrence names one:

    - device power state (D0/D3) of the bus node and the MEDIA child. The claim that the
      failure is undetectable was wrong: DEVPKEY_Device_PowerData exposes the current
      D-state, and a codec sitting in D3 while its endpoint says ACTIVE is exactly the
      signature that would prove idle power-down.
    - devnode flags, to catch a PnP transition (NEED_RESTART, PROBLEM).
    - audiodg pid + start time, to catch an audio-engine restart.
    - Armoury Crate / ROG process start times - resident ASUS software that manages this
      codec and is the standing suspect for a silent reconfiguration.
    - the newest Kernel-Power / Kernel-PnP event, to correlate with sleep or bus activity.

  It changes nothing. Read-only by design: it never repairs, never touches volume, mute
  or the default device. Repair stays in audio-fix.ps1.
#>
[CmdletBinding()]
param(
  [switch] $Loop,          # run forever, sampling every -Interval seconds
  [int]    $Interval = 30,
  [switch] $Install,       # register the boot task and start watching now
  [switch] $Uninstall,
  [switch] $Report         # print what changed between samples
)

$ErrorActionPreference = 'Continue'
$root     = Split-Path -Parent $MyInvocation.MyCommand.Path
$stateDir = 'C:\ProgramData\audio-fix'
$csv      = Join-Path $stateDir 'audio-watch.csv'
$csvCols  = 'ts','busPwr','mediaPwr','busFlags','mediaFlags','epStatus','audiodgPid','audiodgAge','asusProcs','asusSig','lastEvt'
$csvHead  = $csvCols -join ','
$taskName = 'AudioFix-Watch'

if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }

# The audio device is resolved once, from the current default endpoint downwards, so
# nothing here is hardcoded to this machine.
function Resolve-Nodes {
  $ep = Get-PnpDevice -Class AudioEndpoint -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.Status -eq 'OK' -and $_.FriendlyName -match 'Digital Output' } |
        Select-Object -First 1
  if (-not $ep) {
    $ep = Get-PnpDevice -Class AudioEndpoint -PresentOnly -ErrorAction SilentlyContinue |
          Where-Object { $_.Status -eq 'OK' } | Select-Object -First 1
  }
  if (-not $ep) { return $null }
  $media = (Get-PnpDeviceProperty -InstanceId $ep.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
  $bus   = $null
  if ($media) { $bus = (Get-PnpDeviceProperty -InstanceId $media -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data }
  [pscustomobject]@{ Endpoint = $ep.InstanceId; EndpointName = $ep.FriendlyName; Media = $media; Bus = $bus }
}

function Get-PowerState($id) {
  # CM_POWER_DATA: PD_MostRecentPowerState is a DWORD at offset 4. 1 = D0, 4 = D3.
  if (-not $id) { return '-' }
  $d = (Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_PowerData' -ErrorAction SilentlyContinue).Data
  if (-not $d -or $d.Length -lt 8) { return '?' }
  switch ([BitConverter]::ToInt32($d, 4)) {
    1 { 'D0' } 2 { 'D1' } 3 { 'D2' } 4 { 'D3' } default { 'unspec' }
  }
}

function Get-Flags($id) {
  if (-not $id) { return '-' }
  $s = (Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_DevNodeStatus' -ErrorAction SilentlyContinue).Data
  if ($null -eq $s) { return '?' }
  $f = @()
  if ($s -band 0x8)    { $f += 'STARTED' }
  if ($s -band 0x100)  { $f += 'NEEDRESTART' }
  if ($s -band 0x400)  { $f += 'PROBLEM' }
  if ($f.Count -eq 0)  { $f += ('0x{0:X}' -f $s) }
  $f -join '+'
}

function Get-Sample($n) {
  $adg = Get-Process audiodg -ErrorAction SilentlyContinue | Select-Object -First 1
  $asus = Get-Process -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -match 'ArmouryCrate|ROGLive|ArmourySwAgent|Sonic|Nahimic' } |
          Sort-Object Name
  # A changing hash here means some ASUS component restarted between samples.
  $asusSig = ($asus | ForEach-Object { '{0}:{1}' -f $_.Name, $_.Id }) -join ';'

  $ev = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName=@('Microsoft-Windows-Kernel-Power','Microsoft-Windows-Kernel-PnP')} `
          -MaxEvents 1 -ErrorAction SilentlyContinue

  [pscustomobject]@{
    ts         = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    busPwr     = Get-PowerState $n.Bus
    mediaPwr   = Get-PowerState $n.Media
    busFlags   = Get-Flags $n.Bus
    mediaFlags = Get-Flags $n.Media
    epStatus   = (Get-PnpDevice -InstanceId $n.Endpoint -ErrorAction SilentlyContinue).Status
    audiodgPid = $(if ($adg) { $adg.Id } else { 0 })
    audiodgAge = $(if ($adg) { [int]((Get-Date) - $adg.StartTime).TotalMinutes } else { -1 })
    asusProcs  = $asus.Count
    asusSig    = $asusSig
    lastEvt    = $(if ($ev) { '{0} {1:HH:mm:ss}' -f $ev.Id, $ev.TimeCreated } else { '-' })
  }
}

function Write-Sample($s) {
  if (-not (Test-Path $csv)) {
    $csvHead | Set-Content -Path $csv -Encoding UTF8
  }
  ('{0},{1},{2},{3},{4},{5},{6},{7},{8},"{9}","{10}"' -f `
    $s.ts, $s.busPwr, $s.mediaPwr, $s.busFlags, $s.mediaFlags, $s.epStatus,
    $s.audiodgPid, $s.audiodgAge, $s.asusProcs, $s.asusSig, $s.lastEvt) |
    Add-Content -Path $csv -Encoding UTF8

  # Keep it bounded: one line per interval is small, but this runs for weeks.
  $f = Get-Item $csv -ErrorAction SilentlyContinue
  if ($f -and $f.Length -gt 3MB) {
    # The header must survive rotation, or -Report can no longer read its own log.
    $keep = @($csvHead) + (Get-Content $csv -Tail 20000 | Where-Object { $_ -ne $csvHead })
    Set-Content -Path $csv -Value $keep -Encoding UTF8
  }
}

# ---------------------------------------------------------------- install / uninstall

if ($Uninstall) {
  Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*audio-watch.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Write-Host "removed $taskName and stopped any running watcher"
  return
}

if ($Install) {
  $vbs = Join-Path $root 'run-hidden.vbs'
  $ps  = Join-Path $root 'audio-watch.ps1'
  $act = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ("`"$vbs`" `"$ps`" -Loop")
  $trg = New-ScheduledTaskTrigger -AtStartup
  $pri = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  # No execution time limit: this is a long-running loop, not a one-shot repair.
  $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
           -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName $taskName -Action $act -Trigger $trg -Principal $pri -Settings $set -Force | Out-Null
  Start-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
  Write-Host "installed $taskName (samples every ${Interval}s into $csv)"
  return
}

# ---------------------------------------------------------------- report

if ($Report) {
  if (-not (Test-Path $csv)) { Write-Host "no samples yet: $csv"; return }
  # A log rotated by an older build can start on a data row; read it headerless then.
  $first = Get-Content $csv -TotalCount 1
  $rows  = if ($first -eq $csvHead) { Import-Csv $csv } else { Import-Csv $csv -Header $csvCols }
  if (-not $rows) { Write-Host "no readable samples in $csv"; return }
  Write-Host ("samples: {0}   from {1}   to {2}" -f $rows.Count, $rows[0].ts, $rows[-1].ts)
  Write-Host ''
  Write-Host 'transitions (only lines where something changed):'
  $prev = $null
  foreach ($r in $rows) {
    if ($prev) {
      $diff = @()
      foreach ($k in 'busPwr','mediaPwr','busFlags','mediaFlags','epStatus','audiodgPid','asusSig') {
        if ($r.$k -ne $prev.$k) { $diff += ('{0}: {1} -> {2}' -f $k, $prev.$k, $r.$k) }
      }
      if ($diff.Count) { Write-Host ("  {0}  {1}" -f $r.ts, ($diff -join ' | ')) }
    }
    $prev = $r
  }
  return
}

# ---------------------------------------------------------------- sample

$nodes = Resolve-Nodes
if (-not $nodes) { Write-Host 'no audio endpoint resolved'; exit 1 }

if ($Loop) {
  while ($true) {
    # Re-resolve every pass: a port reset republishes the endpoint under a new instance.
    $n = Resolve-Nodes
    if ($n) { Write-Sample (Get-Sample $n) }
    Start-Sleep -Seconds $Interval
  }
} else {
  $s = Get-Sample $nodes
  Write-Sample $s
  $s | Format-List
}
