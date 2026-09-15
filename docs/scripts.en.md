# Scripts

[🇷🇺 Русский](scripts.md) · [Manual](README.en.md)

The "Scripts" page installs a set of maintenance scripts into Windows. They fix what Windows does not fix on its own:
stuck dev servers, a spinning VS Code file watcher, a digital audio output that stays silent after sleep, cursor freezes
on multi-monitor setups. The scripts are built into SysDeck. A button copies them to disk and creates the Task Scheduler
tasks, so there is no copying PowerShell scripts by hand and no running install-task.ps1.

## How to use it

On the left is the list of scripts with their state and schedule. On the right is the selected script: description,
folder, its Task Scheduler tasks with the last and next run, parameters and buttons.

- **Install checked** installs everything ticked in one go. On first open the page ticks what is worth installing or
  repairing on this computer. For example, the audio repair is ticked only when the matching output device exists.
- **Install / Save / Reinstall** installs the script or applies changed parameters. Files are copied again, tasks are
  re-registered, and tasks no longer needed with the new parameters are removed. A task you disabled stays disabled:
  "Save" changes the parameters, not your decision.
- **Enable / Disable** turns the script's tasks on or off without removing them.
- **Run now** starts a task outside its schedule.
- **Log** opens the script's log in Notepad. **Open folder** opens the folder the script is installed in.
- **Remove** deletes the tasks and files. Settings you edited yourself (for example `reap.config.json`) stay.

States in the list:

| State | Meaning |
|---|---|
| running / installed | files present, tasks exist and are enabled |
| disabled | tasks exist but are disabled |
| files changed | the script on disk differs from the built-in one; "Save" restores the built-in one |
| partly installed | a file or a task is missing; the right pane says which; "Save" adds it |
| not installed | nothing is there |

## What is included

| Script | What it does | When it runs | Parameters |
|---|---|---|---|
| Stuck process reaper | reaps orphaned node/cmd/conhost and dev-server trees; editors, agents and windows are left alone | every 4 h | interval, start time |
| Stuck VS Code file watchers | ends a file-watcher stuck in an idle spin that holds a whole core (an @parcel/watcher bug) | every 2 h | interval, start time |
| Claude agent priority | moves each claude.exe tree to below-normal priority and the upper half of logical CPUs (at 24 threads or more), so parallel sessions do not freeze games | every 10 min and at sign-in | interval |
| Freeze recorder | logs every system stall longer than a second: DPC, interrupts, disk, memory, culprits | always, from sign-in | — |
| TCP port exhaustion trap | on Tcpip event 4231 records which process holds how many ports | on the event | — |
| Silent audio repair | after boot and resume re-initialises the digital output (S/PDIF, HDMI); "Fix sound" shortcut | boot, resume from sleep | device, delay after resume, repair frequency, service restart, port reset, driver reinstall, state recording, shortcut |
| Disable MPO | the DWM value `OverlayTestMode = 5` against cursor freezes on monitors with different refresh rates | registry value; optionally at every sign-in | re-apply at sign-in |
| Docker / WSL2 upkeep | daily removes old stopped containers, dangling images and build cache; never touches volumes or running containers | daily at 04:00 | on/off, time |
| WSL2 memory limits | writes the memory limit, swap, memory reclaim and disk shrinking to `%USERPROFILE%\.wslconfig` | when WSL starts | memory, swap, reclaim mode, disk shrinking |
| Claude Code hook | runs the process reaper when a Claude Code session ends; needs the reaper and Node.js | session end | — |
| HDMI TV switch | detaches and re-attaches an HDMI TV in software, so a switched-off TV stops shaking the desktop | by hand | TV name, hardware id |

Some scripts have buttons of their own: "What it would reap now" (a dry run of the reaper, ends nothing),
"Fix sound now" and "Check the device", "Docker report", "Restart WSL", "TV off", "TV on", "TV status",
"Install module" (DisplayConfig for the TV switch, from the PowerShell Gallery).

## Rights and safety

- Most scripts run as you, without elevation. The window installs them itself and no UAC prompt appears.
- The audio repair and MPO need administrator rights: their tasks run as SYSTEM, and MPO is a value under HKLM.
  Windows asks once, after you press a button. With both ticked there is still only one prompt.
- Scripts that SYSTEM runs are installed into `%ProgramData%\SysDeck\toolkit`. Only administrators and SYSTEM can
  write there; otherwise any user program could swap a script that runs with the highest rights. If the path has been
  replaced with a junction beforehand, installation refuses.
- The page recognises an old audio repair install in `%USERPROFILE%\.claude\tools\audio-fix` and offers to move it.
  The tasks and the shortcut then point at the protected copy; the old folder stays where it is.
- Tasks start through `wscript` and `run-hidden.vbs`: no console window flashes or steals focus from a game.
- Before the first edit of `.wslconfig` a copy `.wslconfig.sysdeck.bak` is saved; before editing the Claude Code
  `settings.json`, `settings.json.bak`. Only this page's keys change; the rest of the files stays.
- WSL limits take effect after WSL restarts. The page offers the restart after saving; it stops every distribution
  and the Docker Desktop containers.

## Where it installs

| Script | Folder |
|---|---|
| reaper, VS Code watchers, agent priority, freeze recorder, port trap, Docker | `%USERPROFILE%\.claude\tools\<name>` |
| audio repair, MPO | `%ProgramData%\SysDeck\toolkit\<name>` |
| Claude Code hook | `%USERPROFILE%\.claude\hooks` |
| TV switch | `%USERPROFILE%\Tools\tv-switch` |

Logs: `%LOCALAPPDATA%\proc-reaper\reap.log`, `%LOCALAPPDATA%\vscode-watcher-reaper\watcher-reap.log`,
`%LOCALAPPDATA%\freeze-canary\stalls.log`, `%LOCALAPPDATA%\port-watch\4231.log`, `%ProgramData%\audio-fix\audio-fix.log`.
