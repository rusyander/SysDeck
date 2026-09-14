# Windows Process Cleaner

[🇷🇺 Русский](README.md) · 🇬🇧 English

Windows maintenance in a single window: **disk cleanup**, **terminating forgotten
processes**, **breaking down and freeing RAM** (where every gigabyte went — as a live scheme),
**updating installed programs**, managing **startup items**, uninstalling programs, and
**Docker** cleanup.

Written in **C# + WinForms** and built with the **compiler that ships with
Windows, `csc.exe`** (.NET Framework 4.x). **Nothing to install** — no Node.js,
no Rust, no Visual Studio. The result is one self-contained `.exe` you can just carry
around in a folder.

---

## What this program is for

One tool instead of a pile of third-party "optimizers": kill processes left hanging, free
up RAM, wipe accumulated disk junk and install the updates that have shipped — without
installing anything into the system, with a preview before every deletion, a Russian and
English UI, tray operation and a schedule.

## What the window contains

Sections live in a sidebar on the left, as in Microsoft PC Manager.

| Section | What it does |
|---|---|
| **Home** | memory and disk cards, the Boost button, a system health check with actions |
| **Scan** | finds abandoned processes and terminates them, purges Standby Memory |
| **Memory** | a live scheme of RAM usage: where every gigabyte went, empty operations and process termination |
| **Video memory** | what takes the graphics card's memory (any card, no vendor tools): a per-process scheme, browser GPU process restart, process termination, graphics driver restart |
| **Dev Cleanup** | bulk-kills dev runtimes and frees busy dev ports |
| **Disk Cleanup** | analyzes and deletes junk by category, optional winapp2 rules |
| **Disk** | folder map with sizes, large files, empty folders, duplicates; deletion to the Recycle Bin only |
| **Folder sizes** | the exact size of every folder right in Explorer's Size column and a side panel next to it; a background mode with its own tray icon |
| **Browsers** | bookmarks by folder, saved tab groups, reading list, currently open tabs |
| **Docker** | disk-usage overview, removal of unused data, vhdx compaction |
| **Programs** | installed software list, uninstall via the program's own uninstaller |
| **Updates** | finds outdated programs and updates them via winget / Chocolatey |
| **Startup** | what launches with Windows, enable and disable |
| **Windows bloat** | telemetry, ads, Copilot, surplus Store apps, services and features: disable, remove, restore |
| **Tools** | Windows quick fixes (DNS, network, SFC, DISM, hibernation…), protection, shortcuts to built-in tools |
| **Capture** | region, screen and window screenshots by hotkeys (F3/F4 by default, as in VK Play GameCenter), a notification with a thumbnail, per-program folders |
| **Downloads** | a download queue with resume, speed limits, a schedule and idle-PC mode; taking over downloads from Chrome, Edge, Yandex Browser and Firefox through an extension; a signature check before a program is run, moving what was downloaded |
| **Settings** | all thresholds, lists and parameters |
| **History** | what was cleaned and when |

## The full manual

This page is the overview. Every section of the window is described in detail in [docs/](docs/README.en.md):

- [Installation](docs/install.en.md) — requirements, building, the installer, the portable build, removal
- [Processes and memory](docs/processes-and-memory.en.md) — Home, Scan, Memory, Video memory, Dev Cleanup
- [Disk: cleanup, space map, folder sizes and Docker](docs/disk.en.md) — what counts as junk, how to get the space back and where it went
- [Programs, updates, browsers, startup and Windows](docs/programs.en.md)
- [Capture](docs/capture.en.md) — screenshots by hotkeys, region selection, notifications
- [Downloads](docs/downloads.en.md) — the queue, resume, limits, start conditions, downloads from the browser, the card and settings
- [Tools and settings](docs/tools-and-settings.en.md) — long operations, remembered selections, themes, tray, history
- [Data and administrator rights](docs/data-and-rights.en.md) — where things are kept and when rights are asked for
- [Technical notes](docs/internals.en.md) and [tests](docs/tests.md) — for those who edit the code

## What it can do

- **Processes** — finds the forgotten and the abandoned, terminates them gracefully first,
  forcefully after.
- **RAM** — shows as a scheme where every gigabyte went, and purges working sets, the system
  cache and the standby lists.
- **Dev Cleanup** — bulk-kills Node/Python/Java/Vite/Webpack and frees busy dev ports.
- **Disk cleanup** — dev caches, system junk, browser and app caches, Windows Update
  leftovers; the open **winapp2** rule database plugs in on request.
- **Disk** — a folder tree with sizes, large files, empty folders, content duplicates.
  Deletes to the Recycle Bin only.
- **Folder sizes** — Explorer shows folder sizes as numbers right in its own Size column, with a
  panel next to it listing folders by size. With administrator rights the NTFS file table is read
  directly and a whole disk is counted in seconds.
- **Docker** — shows the space in use and removes what is unused, compacts the vhdx.
- **Programs and updates** — uninstall via the program's own uninstaller, a scan for newer
  versions and installation through **winget** and **Chocolatey**.
- **Browsers** — bookmarks, tab groups, the reading list and the tabs open right now;
  finds repeats and checks whether links are still alive.
- **Startup** — what launches with Windows, enable and disable without deleting the entries.
- **Windows bloat** — telemetry, ads, Copilot, surplus apps and services; restorable.
- **Automation and looks** — a process-cleanup timer, tray operation, start with Windows,
  dark and light themes, a Russian and an English UI.

## What it does NOT do (safety boundaries)

- **Doesn't break Windows.** System processes (SYSTEM/services), components under
  `C:\Windows`, critical and protected processes (shell, clouds, drivers, messengers)
  are never eligible for termination in global mode; the guards fail safe.
- **Doesn't kill active things by mistake.** A scan candidate is picked only when all
  criteria match at once (dead parent + idle + no windows/ports/children), with
  confirmation. The Dev Cleanup buttons hit by name regardless of activity, but they too show
  the list of matching processes first and ask.
- **Doesn't do disk-wide duplicate search** and never deletes anything from your projects,
  code, System32 or drive roots. Driver packages in `DriverStore` are removed only via
  `pnputil` and only superseded ones. Only known junk paths are cleaned.
- **Does not treat saves and settings as junk.** Game-save folders (`Saved Games`, `My Games`,
  `saves` / `SaveGames`, Steam cloud saves under `userdata`, Xbox saves) never become a target,
  neither via built-in rules nor via winapp2 — and are not descended into during a recursive walk
  even when a rule points at a folder above them — and profile roots (Documents, Desktop, AppData) are
  never deleted. Any folder of any category can be excluded from cleanup for good via "Contents…".
- **Doesn't clean disk, uninstall programs or update them on a schedule** — manual only,
  with preview and confirmation. The timer only runs process cleanup.
- **Doesn't touch the registry when updating programs.** The package manager (winget /
  Chocolatey) performs the install; the app only lists what is available and hands it
  the command.
- **Docker: removes only unused data** (`prune`) — running containers and used images are
  never touched. Kubernetes is not included.

## Is it safe?

**The scan / auto-clean mode — yes.** A candidate is picked only when **all**
criteria match at once, so an active process (with a window, listening on a port,
using CPU, or with a live parent) will never be selected. Global mode additionally
protects system processes and other users' processes. Termination always asks for
confirmation.

**Dev Cleanup — no**, it is intentionally a sledgehammer: the buttons hit everything
by name (except the whitelist), but they show the list and wait for confirmation first.

## Installation in short

| Requirement | Note |
|---|---|
| Windows 10 or 11 (x64) | Windows 8.1 works too |
| .NET Framework 4.x | **already part of Windows**, nothing to install |
| Administrator rights | **not needed to start**, asked for per operation |

Visual Studio, the .NET SDK and Node.js are not required: the program is built by `csc.exe`,
which already sits inside Windows.

**From source.** Download the repository (for a ZIP, right-click → **Properties** →
**Unblock** first) and run `run.bat`: it builds `WindowsProcessCleaner.exe` and opens it.
After that you can run the `.exe` itself — it is self-contained and can live in a folder you
carry around.

**Ready-made builds.** `build-installer.bat` produces the installer
`dist\WindowsProcessCleaner-Setup.exe` and the portable build in `dist\portable\`. Installing
for the current user only asks for no rights at all; the portable build keeps its settings
next to itself and leaves no trace in the system.

Details, build errors and removal — [docs/install.en.md](docs/install.en.md).

## Administrator rights

The window starts with ordinary user rights. They are raised per operation: the program
launches itself as a second process, that process does one job and exits — one UAC prompt per
operation, and the prompt makes clear what it is for. Everything inside your own profile
(browser and app caches, developer folders, the Recycle Bin, your own processes) needs no
rights and never asks for them; background work shows no UAC at all and silently skips what it
cannot reach, listing the skipped items in the log. More in
[docs/data-and-rights.en.md](docs/data-and-rights.en.md).

## For developers

| Command | What it does |
|---|---|
| `build.bat` | builds every `src\*.cs` with the built-in `csc.exe` |
| `run.bat` | build if needed, then run |
| `build-installer.bat` | both ready-made builds into `dist\` |
| `tests\run-tests.bat` | the test suite, no administrator rights — [docs/tests.md](docs/tests.md) |

What lives in the repository, the WinAPI in use and the command-line switches —
[docs/internals.en.md](docs/internals.en.md).
