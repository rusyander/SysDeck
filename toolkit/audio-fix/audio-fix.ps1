<#
  audio-fix.ps1 - universal repair for a silent Windows playback device.

  The failure class it targets: the audio endpoint reports ACTIVE, the volume meter
  moves, bits leave the driver - and nothing comes out. Digital outputs (S/PDIF, HDMI)
  have no jack sense and no feedback path, so Windows cannot tell a live transmitter
  from a dead one. Nothing is detectable, therefore nothing can be "checked first":
  the only cure is to re-initialise the audio function, which this script does in an
  escalating ladder, cheapest step first.

  Nothing here is device-specific. The target is resolved from the endpoint down:
  endpoint (SWD\MMDEVAPI) -> MEDIA node (the function driver) -> bus node (USB/PCI).

  Two levers, and the difference between them is the whole point:
    - cycling the MEDIA node reloads the function driver. Cheap, and enough when the
      driver is what got confused.
    - query-removing the bus node and re-enumerating resets the hardware. The bus issues
      a real port reset, so the codec comes up from scratch. This is the one that cures a
      digital output that died on a bad wake, because no amount of driver reloading ever
      power-cycles the chip. It also clears DN_NEED_RESTART, which is otherwise stuck
      until a reboot and which blocks every other step while it is set.

  It never touches volume, mute or the mix format - those are the user's settings, and a
  repair that "fixes" sound by turning it up is not a repair.
#>
[CmdletBinding()]
param(
  [string] $Endpoint,      # name fragment of the playback device; default from config
  [switch] $OnWake,        # what the wake task runs: re-init only, never re-picks the default device
  [switch] $OnBoot,        # boot task: re-apply USB power settings, no device cycling
  [switch] $Deep,          # escalate to a driver reinstall (remove-device + rescan)
  [switch] $Audit,         # report only, change nothing
  [switch] $NoDefault      # do not restore the preferred default endpoint
)

$ErrorActionPreference = 'Continue'
$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$stateDir  = 'C:\ProgramData\audio-fix'
$logFile   = Join-Path $stateDir 'audio-fix.log'
$cfgFile   = Join-Path $root 'audio-fix.config.json'

$cfg = @{
  preferredEndpoint     = ''
  restartServices       = $true
  portReset             = $true    # query-remove + re-enumerate: the step that resets the chip
  allowDriverReinstall  = $false   # off by default - the port reset supersedes it; -Deep still forces it
  logMaxKB              = 200
  shortcutName          = 'Fix sound'
  wakeDelaySec          = 90     # let a standby exit settle before the port reset
  wakeCooldownMin       = 30     # one repair per standby cycle, not one per event
}
if (Test-Path $cfgFile) {
  (Get-Content $cfgFile -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties |
    ForEach-Object { $cfg[$_.Name] = $_.Value }
}

# ---------------------------------------------------------------- logging

if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }
if (Test-Path $logFile) {
  $f = Get-Item $logFile
  if ($f.Length -gt ($cfg.logMaxKB * 1KB)) { Get-Content $logFile -Tail 400 | Set-Content $logFile -Force }
}
$script:mode = if ($OnWake) { 'wake' } elseif ($OnBoot) { 'boot' } elseif ($Audit) { 'audit' } else { 'manual' }
function Log($m) {
  $line = "{0}  [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $script:mode, $m
  Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
  Write-Host $line
}

# ---------------------------------------------------------------- elevation

function Test-Elevated {
  ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
# Scheduled runs are already elevated; an interactive run re-launches itself through UAC
# so the desktop shortcut needs no special setup.
if (-not $Audit -and -not (Test-Elevated)) {
  if ([Environment]::UserInteractive) {
    $argv = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$($MyInvocation.MyCommand.Path)`"")
    $PSBoundParameters.GetEnumerator() | ForEach-Object {
      $argv += "-$($_.Key)"
      if ($_.Value -isnot [switch]) { $argv += "`"$($_.Value)`"" }
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argv
    exit 0
  }
  Log 'ERROR: not elevated and not interactive - cannot continue'
  exit 1
}

# ---------------------------------------------------------------- audio COM

if (-not ('AudioCfg' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// PnP configuration manager. Query-remove + re-enumerate is the only lever available to
// user mode that resets the hardware itself rather than just reloading its driver, and
// unlike Disable-PnpDevice it is not refused on a node carrying DN_NEED_RESTART.
public static class CfgMgr {
  [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)]
  public static extern int CM_Locate_DevNodeW(out uint dn, string id, uint flags);
  [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)]
  public static extern int CM_Query_And_Remove_SubTreeW(uint dn, out int vetoType, StringBuilder vetoName, uint len, uint flags);
  [DllImport("cfgmgr32.dll")]
  public static extern int CM_Reenumerate_DevNode(uint dn, uint flags);
  [DllImport("cfgmgr32.dll")]
  public static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint dn, uint flags);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] public class MMDeviceEnumeratorComObject { }
[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] public class CPolicyConfigClient { }

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator {
  int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
  int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
  int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice ppDevice);
  int RegisterEndpointNotificationCallback(IntPtr client);
  int UnregisterEndpointNotificationCallback(IntPtr client);
}
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection { int GetCount(out int n); int Item(int i, out IMMDevice d); }
[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice {
  int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  int OpenPropertyStore(int access, out IntPtr store);
  int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  int GetState(out int state);
}
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioMeterInformation { int GetPeakValue(out float peak); }

// Undocumented since Vista, still the only way to set the default endpoint without the
// Sound control panel. Only SetDefaultEndpoint is used; the members above it exist to
// line up the vtable slots.
[Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfig {
  int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr f);
  int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, out IntPtr f);
  int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
  int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
  int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, out IntPtr a, out IntPtr b);
  int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
  int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr a);
  int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
  int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int store, IntPtr key, out IntPtr pv);
  int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int store, IntPtr key, IntPtr pv);
  int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
  int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}

// All COM lives in C#: casting an RCW to a ComImport interface performs a real
// QueryInterface here, which PowerShell's own cast operator does not.
public static class AudioCfg {
  static IMMDeviceEnumerator En() { return (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject()); }

  public static string DefaultId(int role) {
    IMMDevice d;
    if (En().GetDefaultAudioEndpoint(0, role, out d) != 0 || d == null) return null;
    string id; d.GetId(out id); return id;
  }
  public static string[] Render() {
    var res = new List<string>();
    IMMDeviceCollection c;
    if (En().EnumAudioEndpoints(0, 1, out c) != 0) return res.ToArray();   // 0 = render, 1 = ACTIVE
    int n; c.GetCount(out n);
    for (int i = 0; i < n; i++) { IMMDevice d; c.Item(i, out d); string id; d.GetId(out id); res.Add(id); }
    return res.ToArray();
  }
  public static int SetDefault(string id, int role) {
    return ((IPolicyConfig)(new CPolicyConfigClient())).SetDefaultEndpoint(id, role);
  }
  public static float Peak(string id) {
    IMMDevice d;
    if (En().GetDevice(id, out d) != 0 || d == null) return -1f;
    Guid iid = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    object o;
    if (d.Activate(ref iid, 1, IntPtr.Zero, out o) != 0) return -1f;
    float p; ((IAudioMeterInformation)o).GetPeakValue(out p); return p;
  }
}
'@
}

$ROLES = @('eConsole','eMultimedia','eComm')

function Get-EndpointName($id) {
  if (-not $id) { return '<none>' }
  $g = ([regex]'\{[0-9a-fA-F-]{36}\}$').Match($id).Value
  if (-not $g) { return $id }
  $p = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\$g\Properties" -ErrorAction SilentlyContinue
  $n = $p.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'
  if (-not $n) { $n = $p.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6' }
  if ($n) { $n } else { $g }
}

# ---------------------------------------------------------------- target resolution

# Endpoint -> MEDIA node -> bus node. Nothing is hardcoded: the same walk works for USB
# audio, an HDAUDIO codec on PCI, or a virtual device.
function Resolve-Target([string]$pattern) {
  $eps = @(Get-PnpDevice -Class AudioEndpoint -PresentOnly -ErrorAction SilentlyContinue |
           Where-Object { $_.Status -eq 'OK' })
  $ep = $null
  if ($pattern) { $ep = $eps | Where-Object { $_.FriendlyName -match [regex]::Escape($pattern) } | Select-Object -First 1 }
  if (-not $ep) {
    # Fall back to whatever Windows currently plays through, so the script is useful
    # even with an empty config on a machine it has never seen.
    $curId = [AudioCfg]::DefaultId(0)
    if ($curId) {
      $ep = $eps | Where-Object { $_.InstanceId -like "*$(($curId -split '\.')[-1])*" } | Select-Object -First 1
    }
  }
  if (-not $ep) { return $null }

  $mediaId   = (Get-PnpDeviceProperty -InstanceId $ep.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
  $busId     = $null
  $mediaName = $null
  if ($mediaId) {
    $busId     = (Get-PnpDeviceProperty -InstanceId $mediaId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
    $mediaName = (Get-PnpDevice -InstanceId $mediaId -ErrorAction SilentlyContinue).FriendlyName
  }

  [pscustomobject]@{
    EndpointName = $ep.FriendlyName
    EndpointPnp  = $ep.InstanceId
    EndpointId   = $ep.InstanceId -replace '^SWD\\MMDEVAPI\\',''   # the IMMDevice id
    MediaId      = $mediaId
    MediaName    = $mediaName
    BusId        = $busId
  }
}

# ---------------------------------------------------------------- repair steps

function Get-DevNodeFlags($id) {
  # Returns the raw DN_* status plus the flags worth naming, or $null if the node is gone.
  if (-not $id) { return $null }
  $dn = 0
  if ([CfgMgr]::CM_Locate_DevNodeW([ref]$dn, $id, 0) -ne 0) { return $null }
  $s = 0; $p = 0
  if ([CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $dn, 0) -ne 0) { return $null }
  $f = @()
  if ($s -band 0x8)    { $f += 'STARTED' }
  if ($s -band 0x100)  { $f += 'NEED_RESTART' }
  if ($s -band 0x400)  { $f += 'PROBLEM' }
  if ($s -band 0x2000) { $f += 'DISABLEABLE' }
  [pscustomobject]@{
    Status      = $s
    Problem     = $p
    Flags       = $f
    NeedRestart = [bool]($s -band 0x100)
    Started     = [bool]($s -band 0x8)
    Text        = ('0x{0:X8} [{1}]' -f $s, ($f -join ' '))
  }
}

function Get-DState($id) {
  # CM_POWER_DATA: PD_MostRecentPowerState is a DWORD at offset 4. 1 = D0, 4 = D3.
  # $null when unreadable - callers must treat that as "unknown", not "healthy".
  if (-not $id) { return $null }
  $d = (Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_PowerData' -ErrorAction SilentlyContinue).Data
  if ($d -and $d.Length -ge 8) { return [BitConverter]::ToUInt32($d, 4) }
  return $null
}

function Invoke-PortReset($target) {
  # The step that actually cures this failure, and the only one that resets the codec
  # rather than its driver. Cycling the MEDIA node reloads the function driver, but the
  # chip is never power-cycled, so a digital output block that failed to come up after a
  # bad wake stays dead through any number of driver reloads. Query-removing the bus node
  # is what "Safely Remove Hardware" does: the device leaves the port, and the following
  # re-enumeration makes the bus issue a real port reset, so the codec initialises from
  # scratch. No driver package is touched - nothing is reinstalled.
  #
  # It also clears DN_NEED_RESTART, which is otherwise stuck until a reboot and which by
  # itself blocks every other repair step (Disable-PnpDevice -> "general failure",
  # pnputil /restart-device -> exit 50). Verified 2026-08-14 on a machine 11 days up:
  # 0x0180210A [STARTED NEED_RESTART] -> removed -> 0x0180200A [STARTED], sound restored.
  $id = $target.BusId
  if (-not $id) { $id = $target.MediaId }
  if (-not $id) { Log 'no bus node behind the endpoint - port reset skipped'; return $false }

  # The parent is captured now because after the removal the node no longer has one, and
  # re-enumerating just the parent bus is far less disruptive than rescanning the machine.
  $parentId = (Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
  $before = Get-DevNodeFlags $id
  Log ("port reset on {0}, before: {1}" -f $id, $(if ($before) { $before.Text } else { '<unreadable>' }))

  $dn = 0
  if ([CfgMgr]::CM_Locate_DevNodeW([ref]$dn, $id, 0) -ne 0) { Log 'could not locate the devnode'; return $false }

  $removed = $false
  for ($try = 1; $try -le 3; $try++) {
    $vt = 0
    $vn = New-Object Text.StringBuilder 260
    $rc = [CfgMgr]::CM_Query_And_Remove_SubTreeW($dn, [ref]$vt, $vn, 260, 0)
    if ($rc -eq 0) { $removed = $true; Log "query-remove ok on attempt $try"; break }
    # A veto names its holder, which is worth logging: it is almost always audiodg.
    Log ("query-remove refused: rc={0} vetoType={1} vetoedBy='{2}'" -f $rc, $vt, $vn.ToString())
    Start-Sleep -Seconds 2
  }

  # Re-enumerate whether or not the removal took: if it did, this brings the device back,
  # and leaving it off is never an acceptable outcome.
  Start-Sleep -Seconds 3
  $pdn = 0
  $scope = $parentId
  if (-not $parentId -or [CfgMgr]::CM_Locate_DevNodeW([ref]$pdn, $parentId, 0) -ne 0) {
    [CfgMgr]::CM_Locate_DevNodeW([ref]$pdn, $null, 0) | Out-Null   # fall back to the root devnode
    $scope = '<root>'
  }
  $rc = [CfgMgr]::CM_Reenumerate_DevNode($pdn, 0x1)                # CM_REENUMERATE_SYNCHRONOUS
  Log ("re-enumerated {0} -> rc={1}" -f $scope, $rc)
  & pnputil /scan-devices | Out-Null

  # The device needs a moment to enumerate, start and republish its endpoints.
  $after = $null
  for ($i = 1; $i -le 6; $i++) {
    Start-Sleep -Seconds 3
    $after = Get-DevNodeFlags $id
    if ($after -and $after.Started) { break }
  }
  Log ("port reset {0}, after: {1}" -f $(if ($removed) { 'done' } else { 'REFUSED' }),
       $(if ($after) { $after.Text } else { '<device did not come back>' }))

  # Success means the hardware is back and the flag that blocked everything is gone -
  # not merely that the calls returned zero.
  return [bool]($removed -and $after -and $after.Started -and -not $after.NeedRestart)
}

function Invoke-CycleDevice($target) {
  if (-not $target.MediaId) { Log 'no MEDIA node behind the endpoint - nothing to cycle'; return $false }
  $id = $target.MediaId

  # The MEDIA child is the right handle. Its bus parent often carries DN_NEED_RESTART
  # (0x100) once a PnP operation on it is half-finished - which is exactly what a bad
  # wake leaves behind - and in that state pnputil /restart-device returns 50 and
  # Disable-PnpDevice fails with "general failure". The child stays disableable.
  $disabled = $false
  try {
    Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop
    $disabled = $true
    Log "disabled $id"
    Start-Sleep -Seconds 5
  } catch {
    Log ("disable failed: " + $_.Exception.Message)
  }
  finally {
    # Never leave the audio device off, whatever happened above.
    if ($disabled) {
      for ($i = 1; $i -le 4; $i++) {
        Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 4
        if ((Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue).Status -eq 'OK') {
          Log "enabled on attempt $i"; break
        }
        Log "enable attempt $i did not take, rescanning"
        & pnputil /scan-devices | Out-Null
      }
    }
  }

  if ($disabled -and (Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue).Status -eq 'OK') { return $true }

  # Fallbacks, weakest last: a targeted restart, then the bus node.
  foreach ($fb in @($id, $target.BusId)) {
    if (-not $fb) { continue }
    $out = & pnputil /restart-device "$fb" 2>&1
    if ($LASTEXITCODE -eq 0) { Log "restarted $fb via pnputil"; Start-Sleep -Seconds 4; return $true }
    Log ("pnputil /restart-device {0} -> exit {1}: {2}" -f $fb, $LASTEXITCODE, (($out -join ' ') -replace '\s+',' '))
  }
  return $false
}

function Get-PnpVeto {
  # Kernel-PnP 225 names the process that vetoed a device removal. This is the reason a
  # cycle fails while the device otherwise looks perfectly healthy, so it is worth
  # naming in the log rather than reporting a bare "general failure".
  $e = Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-PnP';Id=225} `
         -MaxEvents 1 -ErrorAction SilentlyContinue
  if ($e -and $e.TimeCreated -gt (Get-Date).AddMinutes(-5)) { return (($e.Message -replace '\s+',' ')).Trim() }
  return $null
}

function Stop-AudioStack {
  # audiodg.exe keeps a stream open on the endpoint and vetoes the PnP query-remove,
  # which is exactly what makes Disable-PnpDevice fail with "general failure" and leaves
  # DN_NEED_RESTART stuck on the node. Nothing can cycle the device while it holds on,
  # so the stack goes down first and the cycle happens with no holder present.
  foreach ($svc in 'Audiosrv','AudioEndpointBuilder') { Stop-Service $svc -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 3
  Get-Process audiodg -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    Log "killed lingering audiodg (pid $($_.Id))"
  }
  Start-Sleep -Seconds 2
  Log 'audio stack stopped'
}

function Start-AudioStack {
  foreach ($svc in 'AudioEndpointBuilder','Audiosrv') { Start-Service $svc -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 5
  $states = (Get-Service Audiosrv, AudioEndpointBuilder -ErrorAction SilentlyContinue |
             ForEach-Object { "$($_.Name)=$($_.Status)" }) -join ' '
  Log "audio stack started: $states"
}

function Invoke-DeepReinstall($target) {
  if (-not $target.MediaId) { return $false }
  # The driver package stays in the store, so the rescan reinstalls it immediately -
  # no download, no reboot. This is the strongest step short of a restart.
  $out = & pnputil /remove-device "$($target.MediaId)" 2>&1
  Log ("remove-device -> exit {0}: {1}" -f $LASTEXITCODE, (($out -join ' ') -replace '\s+',' '))
  Start-Sleep -Seconds 3
  & pnputil /scan-devices | Out-Null
  Start-Sleep -Seconds 8
  $back = Get-PnpDevice -InstanceId $target.MediaId -ErrorAction SilentlyContinue
  Log ("after rescan: {0}" -f $(if ($back) { "$($back.FriendlyName) = $($back.Status)" } else { 'device did not come back' }))
  return [bool]$back
}

function Set-PreferredDefault($target) {
  if (-not $target.EndpointId) { return }
  $changed = @()
  foreach ($role in 0,1,2) {
    if ([AudioCfg]::DefaultId($role) -ne $target.EndpointId) {
      if ([AudioCfg]::SetDefault($target.EndpointId, $role) -eq 0) { $changed += $ROLES[$role] }
    }
  }
  if ($changed.Count) { Log ("default endpoint -> '{0}' for {1}" -f $target.EndpointName, ($changed -join ', ')) }
}

function Set-UsbPowerSettings($target) {
  # Only meaningful for a USB-attached codec. On modern ASUS boards the rear audio hangs
  # off an internal USB line, so it obeys USB power management and can be suspended while
  # a PCI codec never would be - the reason this whole class of failure exists here.
  if ($target.BusId -notlike 'USB\*') { Log 'audio device is not USB-attached, power step skipped'; return 0 }
  $hwid = ($target.BusId -split '\\')[1]
  $changed = 0

  foreach ($mode in 'ac','dc') {
    & powercfg "/set${mode}valueindex" SCHEME_CURRENT `
        2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 | Out-Null
  }
  & powercfg /setactive SCHEME_CURRENT | Out-Null

  $svc = 'HKLM:\SYSTEM\CurrentControlSet\Services\USB'
  if (-not (Test-Path $svc)) { New-Item -Path $svc -Force | Out-Null }
  if ((Get-ItemProperty $svc -ErrorAction SilentlyContinue).DisableSelectiveSuspend -ne 1) {
    Set-ItemProperty -Path $svc -Name 'DisableSelectiveSuspend' -Value 1 -Type DWord -Force
    Log 'set Services\USB\DisableSelectiveSuspend = 1'; $changed++
  }

  # Found by hardware id, not by instance path, so moving the device to another port
  # does not silently disarm this.
  $nodes = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB' -ErrorAction SilentlyContinue |
           Where-Object { $_.PSChildName -like "$hwid*" } | ForEach-Object { Get-ChildItem $_.PSPath }
  foreach ($n in $nodes) {
    $dp = Join-Path $n.PSPath 'Device Parameters'
    if (-not (Test-Path $dp)) { New-Item -Path $dp -Force | Out-Null }
    foreach ($name in 'SelectiveSuspendEnabled','EnhancedPowerManagementEnabled','AllowIdleIrpInD3','DeviceIdleEnabled') {
      if ((Get-ItemProperty $dp -ErrorAction SilentlyContinue).$name -ne 0) {
        Set-ItemProperty -Path $dp -Name $name -Value 0 -Type DWord -Force
        $changed++
      }
    }
  }

  # "Allow the computer to turn off this device" on the codec and on the hubs carrying
  # it - a parent hub powering down takes the codec with it.
  Get-CimInstance -Namespace root\WMI -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.InstanceName -match "ROOT_HUB|$hwid" -and $_.Enable) {
      try { $_.Enable = $false; Set-CimInstance -InputObject $_ -ErrorAction Stop; $script:pwrCleared++ } catch { }
    }
  }
  Log ("usb power settings applied for {0}, {1} registry value(s) changed" -f $hwid, $changed)
  return $changed
}

# ---------------------------------------------------------------- report

function Write-Report($target) {
  Write-Host ''
  Write-Host '=== TARGET ==='
  if ($target) {
    Write-Host ("  endpoint : {0}" -f $target.EndpointName)
    Write-Host ("  media    : {0}  [{1}]" -f $target.MediaName, $target.MediaId)
    Write-Host ("  bus      : {0}" -f $target.BusId)
    # NEED_RESTART here is the whole diagnosis in one line: while it is set, nothing but
    # a port reset (or a reboot) can touch the device.
    $bf = Get-DevNodeFlags $target.BusId
    $mf = Get-DevNodeFlags $target.MediaId
    if ($bf) { Write-Host ("  bus node : {0}" -f $bf.Text) }
    if ($mf) { Write-Host ("  media    : {0}" -f $mf.Text) }
  } else { Write-Host '  <no active playback endpoint resolved>' }

  Write-Host ''
  Write-Host '=== DEFAULT PLAYBACK DEVICE ==='
  foreach ($role in 0,1,2) {
    Write-Host ("  {0,-12}: {1}" -f $ROLES[$role], (Get-EndpointName ([AudioCfg]::DefaultId($role))))
  }

  Write-Host ''
  Write-Host '=== ACTIVE PLAYBACK ENDPOINTS ==='
  [AudioCfg]::Render() | ForEach-Object { Write-Host ("  " + (Get-EndpointName $_)) }

  if ($target -and $target.BusId -like 'USB\*') {
    Write-Host ''
    Write-Host '=== USB POWER STATE ==='
    $hwid = ($target.BusId -split '\\')[1]
    Write-Host ("  Services\USB\DisableSelectiveSuspend = {0}" -f (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\USB' -ErrorAction SilentlyContinue).DisableSelectiveSuspend)
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB' -ErrorAction SilentlyContinue |
      Where-Object { $_.PSChildName -like "$hwid*" } | ForEach-Object { Get-ChildItem $_.PSPath } | ForEach-Object {
        $p = Get-ItemProperty (Join-Path $_.PSPath 'Device Parameters') -ErrorAction SilentlyContinue
        Write-Host ("  {0}: SelSusp={1} EnhPM={2} D3Idle={3} DevIdle={4}" -f `
          $_.PSChildName, $p.SelectiveSuspendEnabled, $p.EnhancedPowerManagementEnabled, $p.AllowIdleIrpInD3, $p.DeviceIdleEnabled)
      }
  }

  Write-Host ''
  Write-Host '=== TASKS ==='
  # A SYSTEM task is invisible to an unelevated Get-ScheduledTask, which would read as
  # "not installed". Say so instead of printing nothing.
  $tasks = @(Get-ScheduledTask -TaskName 'AudioFix*' -ErrorAction SilentlyContinue)
  if ($tasks.Count) {
    $tasks | ForEach-Object { Write-Host ("  {0,-16} {1}" -f $_.TaskName, $_.State) }
  } elseif (-not (Test-Elevated)) {
    Write-Host '  <not readable without elevation - run as administrator to see task state>'
  } else {
    Write-Host '  <none installed - run install-task.ps1>'
  }

  Write-Host ''
  Write-Host '=== LOG TAIL ==='
  Get-Content $logFile -Tail 12 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("  " + $_) }
}

# ---------------------------------------------------------------- main

if (-not $Endpoint) { $Endpoint = [string]$cfg.preferredEndpoint }
$target = Resolve-Target $Endpoint

if ($Audit) { Write-Report $target; return }

if (-not $target) { Log 'ERROR: no active playback endpoint found'; exit 1 }
Log ("target: '{0}' via {1} on {2}" -f $target.EndpointName, $target.MediaName, $target.BusId)

if ($OnBoot) {
  # Driver reinstalls (Windows Update, Armoury Crate) recreate Device Parameters with
  # stock defaults, which silently re-arms USB suspend. Re-applied on every boot.
  Set-UsbPowerSettings $target | Out-Null
  Log 'boot pass done'
  return
}

if ($OnWake) {
  # Wake gate (2026-08-31, widened 2026-09-09). Reset only when something real
  # happened: an OS resume event, a codec off D0, or a devnode already in
  # trouble. Unknown D-state counts as trouble, so a failed read can never
  # suppress a repair.
  #
  # 566 counts as real. The original gate accepted only Kernel-Power 107/507, on the
  # assumption that a bare 566 SxTransition is a session shuffle with no sleep behind
  # it. On Modern Standby hardware that is wrong: a whole Modern Standby cycle can log no 107 at
  # all, only a pair of 566 (SxTransition, then Unknown) - and those are precisely the
  # transitions that kill the digital output. 01.09 and 09.09 both died on a 566 pair
  # while the gate logged "nothing to repair" and skipped, leaving a manual run as the
  # only cure. A false reset costs an audio blip; a false skip costs a silent day.
  $since  = (Get-Date).AddMinutes(-15)
  $resume = Get-WinEvent -FilterHashtable @{
    LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'
    Id = 107, 507; StartTime = $since
  } -MaxEvents 1 -ErrorAction SilentlyContinue
  $sx = $null
  if (-not $resume) {
    $sx = Get-WinEvent -FilterHashtable @{
      LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'
      Id = 566; StartTime = $since
    } -MaxEvents 1 -ErrorAction SilentlyContinue
  }
  $gateD = Get-DState $target.MediaId
  $gateF = Get-DevNodeFlags $target.BusId
  if ((-not $resume) -and (-not $sx) -and ($gateD -eq 1) -and $gateF -and $gateF.Started -and (-not $gateF.NeedRestart)) {
    Log ("wake gate: no resume event in 15 min, codec D0, bus {0} - nothing to repair, skipping" -f $gateF.Text)
    return
  }
  # One repair per standby cycle. A cycle logs several events and the task fires per
  # event, so without this the widened gate would reset the port two or three times
  # in a row. Manual runs are never gated, but they do arm the cooldown - a wake reset
  # 30 seconds after a hand repair fixes nothing and costs another blip.
  $stamp = Join-Path $stateDir 'last-repair.stamp'
  $last  = $null
  if (Test-Path $stamp) { $last = (Get-Item $stamp).LastWriteTime }
  if ($last -and $last -gt (Get-Date).AddMinutes(-$cfg.wakeCooldownMin)) {
    Log ("wake gate: repaired at {0:HH:mm:ss}, within the {1} min cooldown - skipping" -f $last, $cfg.wakeCooldownMin)
    return
  }
  Log ("wake gate: proceeding (resume={0} sx={1} mediaD={2} bus={3})" -f [bool]$resume, [bool]$sx, $gateD,
       $(if ($gateF) { $gateF.Text } else { '<gone>' }))
  # 20.08: a reset fired while the machine was still leaving standby logged success and
  # the output stayed dead until a manual run hours later. Give the transition time to
  # finish before touching the port. Only on the 566 path - a 107 resume is already over.
  if ((-not $resume) -and $sx -and $cfg.wakeDelaySec -gt 0) {
    Log ("wake gate: 566 transition - waiting {0}s for the standby exit to settle" -f $cfg.wakeDelaySec)
    Start-Sleep -Seconds $cfg.wakeDelaySec
  }
}

# The ladder. Every step is idempotent, and every step that can fail is re-verified
# rather than assumed - an earlier version restarted the services after a failed cycle
# and reported success without ever retrying, which hid a real failure in the log.
Set-UsbPowerSettings $target | Out-Null

$ok = $false

$busFlags = Get-DevNodeFlags $target.BusId
if ($busFlags) { Log ("bus node: " + $busFlags.Text) }

# The port reset goes FIRST, not last. It used to be an escalation behind the MEDIA cycle,
# and that ordering is exactly why the wake task ran clean for four days while the sound
# stayed dead: the cycle reported success, so the one step that resets the chip never ran.
# A driver reload cannot fix hardware that never came up, so there is nothing to try
# before it. It runs with the stack down unconditionally - audiodg vetoes the
# query-remove otherwise, and a veto here costs the whole run.
if ($cfg.portReset) {
  Log 'resetting the device on its port (query-remove + re-enumerate)'
  Stop-AudioStack
  $ok = Invoke-PortReset $target
  Start-AudioStack
  # The instance path survives a port reset, but the endpoints are republished, so the
  # endpoint id must be looked up again before the default device is restored.
  $t2 = Resolve-Target $Endpoint
  if ($t2) { $target = $t2 }
}

# Fallback, only if the reset was refused: reload the function driver. Worth a try when
# the device could not be taken off its port, useless once the reset has succeeded.
if (-not $ok) {
  Log 'port reset did not take - falling back to a driver cycle'
  $ok = Invoke-CycleDevice $target

  if (-not $ok -and $cfg.restartServices) {
    $veto = Get-PnpVeto
    if ($veto) { Log "cycle vetoed by: $veto" }
    Log 'retrying the cycle with the audio stack stopped'
    Stop-AudioStack
    $ok = Invoke-CycleDevice $target
    Start-AudioStack
  }
}

# Reinstalling is no longer part of the automatic ladder: the port reset covers every case
# it used to, without touching the driver store. It stays reachable for a genuinely broken
# driver, but only when asked for.
if ($Deep -or (-not $ok -and $cfg.allowDriverReinstall)) {
  Log 'escalating to a driver reinstall'
  Stop-AudioStack
  Invoke-DeepReinstall $target | Out-Null
  Start-AudioStack
  $target = Resolve-Target $Endpoint     # the instance path can change after a reinstall
  if ($target) { $ok = Invoke-CycleDevice $target }
}

# On wake the default device is left alone on purpose: a wake is not a reason to override
# a choice the user made deliberately. A manual run does restore it.
if (-not $OnWake -and -not $NoDefault -and $target) { Set-PreferredDefault $target }

if ($ok -and $target) {
  Log ("done, device re-initialised, endpoint '{0}' = {1}" -f $target.EndpointName,
       (Get-PnpDevice -InstanceId $target.EndpointPnp -ErrorAction SilentlyContinue).Status)
} else {
  # Said plainly: a log line that claims success after every step was refused is worse
  # than no log at all, because the next failure looks like a first occurrence.
  Log 'FAILED: could not re-initialise the audio device, every step was refused - a reboot is the remaining option'
}

# Arms the wake cooldown. Written whether or not the ladder succeeded: a failed run
# still port-reset the device, and repeating it 30 seconds later helps nothing.
Set-Content -Path (Join-Path $stateDir 'last-repair.stamp') -Value (Get-Date -Format 's') -Force -ErrorAction SilentlyContinue

if (-not $OnWake -and -not $OnBoot) { Write-Report $target }
if (-not $ok) { exit 2 }
