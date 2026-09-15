<#
.SYNOPSIS
  Runs a command inside a Windows Job Object so the OS kills the whole process
  tree the moment this wrapper exits. Prevents orphans at the source.

.DESCRIPTION
  Job membership is inherited by children, so the trick is to put THIS process
  into the job before spawning anything: every descendant then joins
  automatically and there is no race window. The job carries
  JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so when the last handle closes - normal
  exit, Ctrl+C, taskkill, or a crash - Windows terminates every member.

  This is the fix for `npm run dev` leaving node -> cmd -> node -> conhost
  behind when its shell dies.

.EXAMPLE
  .\dev-run.ps1 npm run dev
  .\dev-run.ps1 pnpm --filter @my-app/web dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$Command
)

$ErrorActionPreference = 'Stop'

if (-not ('ProcReaper.JobMgr' -as [type])) {
    Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;

namespace ProcReaper {
  [StructLayout(LayoutKind.Sequential)]
  public struct IO_COUNTERS {
    public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
    public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
  }

  public static class JobMgr {
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const int  JobObjectExtendedLimitInformation  = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr a, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint len);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetCurrentProcess();

    // Creates the job, arms kill-on-close, and enrolls the caller so that every
    // process spawned from here on inherits membership.
    public static IntPtr AttachSelf() {
      IntPtr job = CreateJobObject(IntPtr.Zero, null);
      if (job == IntPtr.Zero) throw new Exception("CreateJobObject failed: " + Marshal.GetLastWin32Error());

      var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
      ext.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

      int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
      IntPtr ptr = Marshal.AllocHGlobal(len);
      try {
        Marshal.StructureToPtr(ext, ptr, false);
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
          throw new Exception("SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
      } finally {
        Marshal.FreeHGlobal(ptr);
      }

      if (!AssignProcessToJobObject(job, GetCurrentProcess()))
        throw new Exception("AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());

      return job;
    }
  }
}
'@
}

[void][ProcReaper.JobMgr]::AttachSelf()

function Quote([string]$s) {
    if ($s -match '[\s"]') { return '"' + ($s -replace '"', '\"') + '"' }
    return $s
}

$exe  = $Command[0]
$rest = @()
if ($Command.Count -gt 1) { $rest = $Command[1..($Command.Count - 1)] }

# npm/pnpm/yarn/vite are .cmd or .ps1 shims on Windows: Start-Process cannot exec
# them ("%1 is not a valid Win32 application"), so anything that does not resolve
# to a real .exe goes through cmd.
$resolved = Get-Command $exe -ErrorAction SilentlyContinue
$useShell = $true
if ($resolved -and $resolved.CommandType -eq 'Application' -and $resolved.Source -match '\.exe$') {
    $useShell = $false
    $exe = $resolved.Source
}

$argList = @($rest | ForEach-Object { Quote $_ })
if ($useShell) {
    $argList = @('/d', '/s', '/c', (Quote $exe)) + $argList
    $exe = 'cmd.exe'
}

Write-Host "[dev-run] job-guarded: $($Command -join ' ')" -ForegroundColor DarkGray

$startArgs = @{ FilePath = $exe; NoNewWindow = $true; PassThru = $true }
if ($argList.Count -gt 0) { $startArgs.ArgumentList = $argList }
$proc = Start-Process @startArgs
try {
    $proc.WaitForExit()
    exit $proc.ExitCode
} finally {
    # Handle closes with this process; the job then terminates every survivor.
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
