<#
.SYNOPSIS
  Reports and reclaims Docker / WSL2 resources. Report-only by default.

.DESCRIPTION
  Two different resources get confused here, so they are handled separately:

  RAM - already automatic, no script can do better. `autoMemoryReclaim=gradual`
  in ~/.wslconfig makes the WSL2 kernel hand freed guest memory back to Windows
  on its own. Before that setting exists, VmmemWSL holds its peak until reboot.
  This script only reports the number.

  DISK - not automatic. Deleting files inside the VM does not shrink the .vhdx,
  and Docker keeps stopped containers, dangling images and build cache forever.
  -Prune clears those; -Compact shrinks the virtual disk itself.

.EXAMPLE
  .\docker-maint.ps1              # report only
  .\docker-maint.ps1 -Prune       # drop stopped containers, dangling images, build cache
  .\docker-maint.ps1 -Compact     # shrink the .vhdx (stops WSL first - kills containers)
#>
[CmdletBinding()]
param(
    [switch]$Prune,
    [switch]$Compact,
    [switch]$Auto,
    [switch]$Quiet
)

# -Auto is the scheduled-task entry point: age-filtered, never touches volumes,
# never touches a running container (the k8s node and the MCP container are safe).
if ($Auto) { $Quiet = $true }

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

function Get-VmmemMB {
    $p = Get-Process -Name 'vmmemWSL', 'vmmem' -ErrorAction SilentlyContinue
    if (-not $p) { return 0 }
    return [math]::Round((($p | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 0)
}

function Get-VhdxPath {
    $candidates = Get-ChildItem "$env:LOCALAPPDATA\Docker\wsl" -Recurse -Filter '*.vhdx' -ErrorAction SilentlyContinue
    if (-not $candidates) {
        $candidates = Get-ChildItem "$env:LOCALAPPDATA\wsl" -Recurse -Filter '*.vhdx' -ErrorAction SilentlyContinue
    }
    return $candidates | Sort-Object Length -Descending | Select-Object -First 1
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Warning "docker CLI not found - nothing to do."
    exit 0
}

if ($Auto) {
    $log = Join-Path $env:LOCALAPPDATA 'docker-maint.log'
    $df  = (docker system df 2>&1) -join "`n"
    # Safety net for crashed/leaked agent sessions: RUNNING containers that a
    # Claude agent marked single-task (label claude.ephemeral=1) get stopped
    # after 12h - prune below only sees stopped ones. Unlabeled containers are
    # never touched: the label is the consent.
    $ephemeral = @(docker ps --filter 'label=claude.ephemeral=1' --format '{{.ID}} {{.CreatedAt}}' 2>$null)
    $stopped = @()
    foreach ($e in $ephemeral) {
        $ephId, $rest = $e -split ' ', 2
        $created = $null
        if ([datetime]::TryParse(($rest -replace ' [+-]\d{4}.*$', ''), [ref]$created) -and
            ((Get-Date) - $created).TotalHours -gt 12) {
            docker stop $ephId 2>&1 | Out-Null
            $stopped += $ephId
        }
    }
    # Age filters keep today's work intact; only week-old leftovers and
    # 3-day-old build cache go. No -a on images: tagged images stay.
    # No volume pruning ever - that is where the databases live.
    $out = @(
        if ($stopped.Count) { "stopped ephemeral: $($stopped -join ', ')" }
        docker container prune -f --filter until=168h 2>&1
        docker image     prune -f 2>&1
        docker network   prune -f --filter until=168h 2>&1
        docker builder   prune -f --filter until=72h  2>&1
    ) -join "`n"
    $after = (docker system df 2>&1) -join "`n"
    "=== $(Get-Date -Format 'yyyy-MM-dd HH:mm') ===`nBEFORE`n$df`n$out`nAFTER`n$after`n" |
        Add-Content -Path $log -Encoding UTF8
    # Keep the log from becoming the next thing that grows unbounded.
    $lines = @(Get-Content $log -ErrorAction SilentlyContinue)
    if ($lines.Count -gt 2000) { $lines[-2000..-1] | Set-Content $log -Encoding UTF8 }
    exit 0
}

$before = Get-VmmemMB
$vhdx   = Get-VhdxPath

if (-not $Quiet) {
    Write-Host "VmmemWSL now: $before MB"
    if ($vhdx) { Write-Host "Virtual disk: $($vhdx.FullName) - $([math]::Round($vhdx.Length / 1GB, 1)) GB" }
    if (-not (Test-Path "$env:USERPROFILE\.wslconfig")) {
        Write-Warning "~/.wslconfig missing: WSL2 will hold its peak memory. Add autoMemoryReclaim=gradual."
    }
    Write-Host ""
    Write-Host "--- docker system df ---"
    docker system df
}

if ($Prune) {
    Write-Host ""
    Write-Host "--- pruning (stopped containers, dangling images, unused networks, build cache) ---"
    # No -a: tagged images stay, so nothing has to be pulled or rebuilt afterwards.
    docker system prune -f
    docker builder prune -f
}

if ($Compact) {
    if (-not $vhdx) { Write-Warning "No .vhdx found, cannot compact."; exit 1 }

    Write-Host ""
    Write-Host "--- compacting $($vhdx.Name) (Docker Desktop and WSL will be stopped) ---"

    # Docker Desktop restarts WSL on its own, so `wsl --shutdown` alone leaves the
    # .vhdx locked and diskpart fails. The GUI and the backend both have to go first.
    $wasRunning = [bool](Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue)
    foreach ($proc in 'Docker Desktop', 'com.docker.backend', 'com.docker.build') {
        Get-Process -Name $proc -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 5
    wsl.exe --shutdown
    Start-Sleep -Seconds 10

    # Mark the disk sparse so Windows reclaims freed blocks by itself from now on.
    # Only works on an existing disk via --manage; harmless if the WSL build lacks it.
    try { wsl.exe --manage docker-desktop --set-sparse true 2>&1 | Write-Host } catch { }

    $sizeBefore = (Get-Item $vhdx.FullName).Length

    # Optimize-VHD needs the Hyper-V module, which is absent on Home editions;
    # diskpart's `compact vdisk` is the portable equivalent.
    if (Get-Command Optimize-VHD -ErrorAction SilentlyContinue) {
        Optimize-VHD -Path $vhdx.FullName -Mode Full
    } else {
        $script = @"
select vdisk file="$($vhdx.FullName)"
attach vdisk readonly
compact vdisk
detach vdisk
exit
"@
        $tmp = Join-Path $env:TEMP 'wsl-compact.txt'
        Set-Content -Path $tmp -Value $script -Encoding ASCII
        diskpart /s $tmp
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }

    $sizeAfter = (Get-Item $vhdx.FullName).Length
    Write-Host ("Disk: {0:N1} GB -> {1:N1} GB" -f ($sizeBefore / 1GB), ($sizeAfter / 1GB))

    if ($wasRunning) {
        $exe = "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe"
        if (Test-Path $exe) {
            Write-Host "--- restarting Docker Desktop (k8s pods come back on their own) ---"
            Start-Process $exe
        } else {
            Write-Warning "Docker Desktop.exe not found - start it by hand."
        }
    }
}

if (($Prune -or $Compact) -and -not $Quiet) {
    Write-Host ""
    Write-Host "--- after ---"
    docker system df
}
