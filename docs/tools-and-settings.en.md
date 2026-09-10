# Tools and settings

The Tools page, how long operations behave, remembered selections, language, themes, tray, the auto-clean timer, every setting and the history.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](tools-and-settings.md)

## Tools
Built-in Windows tools behind one button each, with an output log at the bottom of the
page and a "Stop" button for long operations.

- **Quick fixes:** flush the DNS cache, reset the network (Winsock, TCP/IP), restart
  Explorer, rebuild the icon cache and the font cache, `sfc /scannow`, `DISM
  /RestoreHealth`, check the system drive (chkdsk on next boot), reset Windows Update
  components, create a restore point, turn hibernation on/off (the button shows the size of
  hiberfil.sys), clear the print queue, reset the Microsoft Store cache, clear the clipboard.
- **Protection:** Defender quick scan, update antivirus signatures, check for Windows updates.
- **Windows tools:** 28 shortcuts — Windows Security, Disk Cleanup, Storage settings, Task
  Manager, Resource Monitor, Device Manager, Services, Event Viewer, Reliability Monitor,
  Windows Update, Programs and Features, Windows Features, System Restore, System
  Protection, startup apps, network connections, power options, environment variables,
  msconfig, Disk Management, msinfo32, dxdiag, memory diagnostic, regedit, Control Panel,
  Settings, Terminal.
- **Search** — the box above the buttons filters them by title and description (Esc clears);
  sections without matches are hidden.

Anything that changes the system asks first; anything that needs administrator rights
requests them itself, for that one operation. The label at the bottom of the sidebar shows
whether the window already holds those rights or raises them on demand.

## Long operations: progress and stopping
No button ever hangs silently. Anything that takes longer than a second renders the same
line into the page label: **what is running now · how long it has been running (mm:ss) ·
a live detail** — the category, folder, package or command being processed at this very
moment. The label refreshes twice a second, so you can see both that the work is moving
and exactly where it is stuck.

- **The page's buttons go grey while work runs** — a second click cannot start a second
  copy of the same operation.
- **"Stop" really stops** — including creating a restore point and the other tools that
  previously had to be sat out. After the click the label honestly shows "Stopping…"
  while the current step reaches a safe boundary: nothing is left half-deleted or
  half-applied. The single exception is a Docker disk compaction that has already
  started: it cannot be interrupted without leaving the virtual disk mounted, and the
  program says so when you click.
- **External programs cannot freeze the window.** `DISM`, `winget`, `choco`, `pnputil`,
  `sfc` and `docker` are started with standard input closed (otherwise a "restart now?
  (Y/N)" prompt would wait for an answer a GUI process has no way to give), their output
  is read as it arrives, every command has its own timeout, and success is decided by the
  parsed report rather than by the exit code alone.
- **When nothing can be done** — no administrator rights, no winget installed, the disk
  is busy — the program says so in the label or the log immediately instead of leaving a
  button unresponsive.

## Remembered selections
What you check stays checked. Previously almost any list reset its checkboxes to the
default when it refreshed, throwing away what you had picked by hand; now one shared
store keeps the selection for the whole window.

- **Survives a restart of the program and a reboot** (kept in `config.json` until the
  program is uninstalled): the "and temp files" and "smart boost" checkboxes on Home, the
  checked categories on the Cleanup tab, the items on the "Windows: extras" tab, the
  selected packages on the Updates tab, the choice in the "Where to look" list and the list
  mode ("Large files" / "Empty folders" / "Duplicates") on the Disk tab, the checked programs
  on the Programs tab, the ports in Dev Cleanup, the "before compacting remove" choice on
  the Docker tab, and every field of the Settings page.
- **Survives a list refresh within the session**: the processes after a re-scan and the
  rows on the Disk and Browsers tabs. After a restart those lists deliberately start clean: their contents
  are discovered anew every time, and restoring a "delete" tick onto a different file
  that happens to sit at the same path would be worse than ticking it again.
- **The defaults are unchanged** and apply only until you decide something yourself: the
  Cleanup tab starts with the recommended non-empty categories checked, the Updates tab
  with nothing checked. Once you move a checkbox the program keeps your choice, and a
  repeated analysis no longer overrides it.
- **The Startup tab's checkboxes are not remembered** — there a checkbox is not a
  selection but the live state of the entry in Windows, and "remembering" it would mean
  lying about the system.

"Recommended", "All" and "None" work as before — they set the whole selection, and that
is remembered too.

## Interface language
Russian / English — switchable in settings (applied after restart). The documentation
is bilingual too: [README.md](README.md) / [README.en.md](README.en.md).

## Auto-clean timer
Set a number of hours (1..24). Every N hours the app scans **processes** (in the chosen
mode), terminates candidates, purges memory and writes to history. Disk cleanup and
uninstallation are **never** run by the timer — manual only.

## System tray
The tray icon changes color: green — clean, orange — candidates found. Double-click
opens the window. Right-click menu: Scan, Clean, Purge Standby Memory, ⚡ Boost, toggle
auto-clean, restart as administrator (for when raising rights once for the whole window is simpler), exit. Closing the window minimizes the app to
tray (it keeps running in the background). If an irreversible operation is running at
that moment (deleting files, moving to the Recycle Bin, installing updates, Docker prune
or disk compaction), the tray hint names it, and “Exit” / “Restart as administrator”
ask first: an interrupted compaction would leave Docker stopped, an interrupted install
a half-installed package.

## Start with Windows
A checkbox in settings. Implemented via **Task Scheduler** with highest privileges, so there
is no UAC prompt at logon. The task is created from XML rather than the `schtasks` command
line so it does not inherit the scheduler defaults: no execution time limit (otherwise the
tray app was simply killed after 72 hours), no "don't start on battery" and no "stop on
battery". Smart boost and the auto-clean timer only work inside the running app, so enabling
either without autostart makes the program offer it once.

## Themes
Light / dark / **system** (default — follows the Windows theme, including a live dark/light
switch without a restart). Switchable in settings, applied immediately, including the window
title bar and lists that are already filled (rows never keep the previous theme's colours).
Lists, trees, text fields and drop-downs get a soft rounded border in the theme colour instead of the sharp system line; buttons are drawn with anti-aliased rounded corners.
The opened drop-down list is themed too: roomy rows, the same highlight as the lists, a dark
list window in the dark theme. The multi-line lists on the Settings page stretch to the free
window height.

## Settings (saved)

Every change saves itself a second after the last touch — no button needed. The "Save
settings" button stays: it also re-creates the autostart task (useful after moving the exe
to another folder).

- **Abandonment criteria** — CPU threshold, idle time, minimum lifetime, idle time for
  global mode.
- **Automation** — process auto-clean interval (1..24 h), auto-clean on/off, "global: don't
  touch Program Files", start with Windows, start minimized to tray.
- **Performance** — background CPU monitoring and its period (5..300 s, 15 by default),
  emptying the working sets of all processes (off by default — it slows the system down),
  smart boost and its RAM-usage threshold (50..99 %, 90 by default).
  Idle time is measured only by monitoring ticks: with monitoring off there can be no
  termination candidates, and the scan summary says so explicitly.
- **Disk cleanup** — "keep files newer than N minutes", cleanup logging, list of paths to
  never clean (a path may be written via its short 8.3 name or a junction — the real on-disk
  path is compared).
- **Program updates** — show packages with an unknown installed version, query Chocolatey,
  how many packages to hand the manager per command (1..20), list of packages to never
  offer for update.
- **Lists** — watchlist, whitelist, dev ports.
- **Look & feel** — theme and UI language.

## History
After each cleanup it saves date/time, number of terminated processes, amount of
freed memory and the list of processes.
