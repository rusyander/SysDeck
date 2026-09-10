# Windows Process Cleaner

[🇷🇺 Русский](README.md) · 🇬🇧 English

Windows maintenance in a single window: **disk cleanup**, **terminating forgotten
processes**, **freeing RAM**, **updating installed programs**, managing **startup items**,
uninstalling programs, and **Docker** cleanup.

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
| **Dev Cleanup** | bulk-kills dev runtimes and frees busy dev ports |
| **Disk Cleanup** | analyzes and deletes junk by category, optional winapp2 rules |
| **Disk** | folder map with sizes, large files, empty folders, duplicates; deletion to the Recycle Bin only |
| **Browsers** | bookmarks by folder, saved tab groups, reading list, currently open tabs |
| **Docker** | disk-usage overview, removal of unused data, vhdx compaction |
| **Programs** | installed software list, uninstall via the program's own uninstaller |
| **Updates** | finds outdated programs and updates them via winget / Chocolatey |
| **Startup** | what launches with Windows, enable and disable |
| **Windows bloat** | telemetry, ads, Copilot, surplus Store apps, services and features: disable, remove, restore |
| **Tools** | Windows quick fixes (DNS, network, SFC, DISM, hibernation…), protection, shortcuts to built-in tools |
| **Settings** | all thresholds, lists and parameters |
| **History** | what was cleaned and when |

## What it can do

- **Processes.** Finds forgotten/abandoned processes (dev runtimes, or — in global
  mode — any of yours) and terminates them safely: gracefully first (WM_CLOSE), then
  forcefully. Shows for each: PID, PPID, path, uptime, CPU, RAM, windows, TCP ports,
  child processes, owner.
- **RAM.** Purges Standby Memory via WinAPI (`NtSetSystemInformation`), no third-party exe.
- **Dev Cleanup.** Bulk-terminates Node/Python/Java/Vite/Webpack/npm/pnpm/yarn and frees
  busy dev ports (3000, 5173, 8080, 4200 …).
- **Disk cleanup.** Analyzes and deletes known junk: dev caches (npm/pnpm/yarn/pip/
  gradle/cargo/go/NuGet), system junk (temp, Recycle Bin, Windows Update, dumps),
  browser and app caches (Discord/Slack/Teams/Spotify), old logs, driver installer
  leftovers and `Windows.old`. Optionally plugs in **winapp2 rules** — an open database
  covering thousands of programs. Shows sizes first, deletes only after confirmation. Every
  category has **contents** — its folders with check boxes — and any folder can be excluded for good.
- **Disk.** Shows where the space went: a folder tree with sizes and shares, large files,
  empty folders and content duplicates (SHA-256). Deletes to the Recycle Bin only.
- **Docker.** Shows disk usage (`docker system df`) and removes unused data: stopped
  containers, images, volumes, build cache, everything at once.
- **Programs.** Lists installed software and uninstalls it via its own uninstaller.
- **Updates.** Scans the machine for programs with newer versions available, rates how big
  each update is (major / minor / patch) and installs the selected ones — using **winget**
  and **Chocolatey**, several packages per command.
- **Browsers.** Shows everything that piles up in browser profiles: the bookmark folder
  tree, saved tab groups, the reading list and the tabs open right now — each with the
  date it was added and the date it was last opened. Bookmarks can be cleaned up: delete,
  move, merge folders, find repeats and check whether the links are still alive.
- **Startup.** Lists all installed programs with a "starts at sign-in" toggle: uncheck to
  disable the way Task Manager does (the entry and its arguments stay), check to re-enable
  or add (registry `Run` keys + the Startup folders).
- **Automation.** Process auto-clean timer (every 1–24 h), start with Windows (via Task
  Scheduler), tray operation, cleanup history, flexible settings.
- **Look & feel.** Dark/light theme (or system) + dark title bar; RU/EN UI language.

## What it does NOT do (safety boundaries)

- **Doesn't break Windows.** System processes (SYSTEM/services), components under
  `C:\Windows`, critical and protected processes (shell, clouds, drivers, messengers)
  are never eligible for termination in global mode; the guards fail safe.
- **Doesn't kill active things by mistake.** A scan candidate is picked only when all
  criteria match at once (dead parent + idle + no windows/ports/children), with
  confirmation. The exception is the Dev Cleanup buttons — a deliberate by-name sledgehammer.
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

---

## Installation

### What you need

| Requirement | Note |
|---|---|
| Windows 10 or 11 (x64) | Windows 8.1 works too |
| .NET Framework 4.x | **already part of Windows**, nothing to install |
| Administrator rights | required to purge Standby Memory |

Visual Studio, the .NET SDK, Node.js and any other package are **not required** — the
program is built by `csc.exe`, which already sits inside Windows.

Optional, and only for the corresponding tabs:

- **winget** — for the Updates tab. Windows 11 and recent Windows 10 ship with it; if you
  don't have it, install "App Installer" from the Microsoft Store.
- **Chocolatey** — an optional second update source. Without it the tab simply works
  through winget alone.
- **Docker Desktop** — for the Docker tab (a running daemon is needed).

### Steps

1. **Get the files.** Either `git clone` the repository, or download the ZIP and extract it
   anywhere, e.g. `C:\Tools\cleaner`.

2. **If you downloaded a ZIP, unblock it.** Windows marks files that came from the
   internet, and the build may refuse to run. Right-click the ZIP → **Properties** →
   tick **Unblock** at the bottom → OK, and only then extract. Already extracted? Run this
   in PowerShell inside the project folder:

   ```powershell
   Get-ChildItem -Recurse | Unblock-File
   ```

3. **Double-click `run.bat`.** On first launch it builds `WindowsProcessCleaner.exe` and
   opens it right away. After that you can run the `.exe` directly.

   Build only, without running:

   ```
   build.bat
   ```

4. **Accept the UAC prompt.** The app always runs as administrator — that's needed to purge
   Standby Memory. A prompt on every launch is expected.

That's it. Nothing is installed into the system: `WindowsProcessCleaner.exe` is
self-contained and can simply live in a folder you carry around.

### If something goes wrong

**"Windows protected your PC" (SmartScreen).** The file was built locally on your machine
and is not signed with a certificate, so Windows doesn't recognise it. Click **More info**
→ **Run anyway**.

**Antivirus complains about the freshly built `.exe`.** A false positive on an unsigned
binary. Add the project folder to exclusions, or rebuild.

**`[ERROR] csc.exe of .NET Framework 4.x not found`.** .NET Framework 4.x is missing
(happens on heavily stripped Windows builds). Install .NET Framework 4.8 from Microsoft
and retry.

**`[ERROR] Build failed`.** Usually the app is already running and holding its own `.exe`.
Close it — including from the tray (right-click the icon → Exit) — and build again.

**No window appeared after launch.** The app is probably already running: it is
single-instance and just brings the existing window to front. Check the tray by the clock.

**Won't build from a very long path.** Move the project closer to the drive root, e.g.
`C:\Tools\cleaner`.

### How to remove it

1. In settings, uncheck **Start with Windows** — the `WindowsProcessCleaner` task is
   removed from Task Scheduler.
2. Close the program (tray → Exit).
3. Delete the project folder and the settings folder `%APPDATA%\WindowsProcessCleaner\`.

Nothing else is left behind: the app doesn't register itself in the registry and doesn't
create services.

---

## Features

### Home
The start screen, in the spirit of Microsoft PC Manager: three cards (RAM, system drive,
status after the last check), a big **⚡ Boost** button and a table of checks.

- **Boost** — terminates abandoned processes (the current scan candidates) and purges
  Standby Memory; the result is shown under the cards and written to history. With the
  "and temporary files" box ticked it shows the list of categories and asks before deleting.
- **Smart boost** — a checkbox right there and in Settings: when memory usage exceeds the
  threshold (90 % by default) the app purges Standby Memory on its own, at most once every
  15 minutes, and says so with a tray hint.
- **Check health** — 12 checks in a few seconds: memory, system drive, uptime, hibernation
  file, startup entries, abandoned processes, size of temporary files, the Downloads
  folder, Defender (enabled, signature age, last scan), the last Windows update, restore
  points, program updates. Every row has a status (fine / tip / attention) and an action:
  double-click opens the relevant tab, runs a tool, or runs the boost itself. The status is
  colour-coded (green / blue / orange). The check runs when Home is first shown and repeats
  on its own once the last one is older than an hour.

### Scanning
For each process it collects: name, PID, PPID, path, uptime, CPU %, RAM, whether
it has a window, listening TCP ports, whether it has child processes, and owner.

### "Abandoned" criteria
A process becomes a termination candidate only when **all** conditions hold:
1. the parent process has exited;
2. CPU below the threshold for longer than the idle time;
3. no user windows;
4. not listening on TCP ports;
5. no child processes;
6. not in the whitelist and older than the minimum lifetime.

### Two scan modes
- **Dev mode** (default) — only processes from the watchlist (node, python, java,
  vite, webpack, npm, pnpm, yarn, bun, cargo, go, deno, ruby, php…).
- **Global mode** (the "All processes (global)" checkbox) — scans **every**
  process. Here **strengthened guards** apply: only processes owned by the **current
  user**, **not** in Windows system folders and **not** in `Program Files` (installed
  software, configurable), idle for **≥ 30 minutes** (configurable), and not in the
  expanded protected list (Windows core, shell, clouds, messengers, drivers, launchers,
  password managers). This is how orphaned groups are caught — those whose parent died
  long ago while the children keep hanging around, burning CPU/RAM while being used by
  nothing.

### Cleanup buttons
- **Clean selected** — terminate the checked rows.
- **Auto-clean all inactive** — terminate every found candidate in one click
  (with a confirmation and a list).
- **Select all / Clear selection** — checkbox control.
- **Purge memory** — Standby Memory purge only, without terminating processes.

Termination itself: first gracefully (WM_CLOSE), wait up to 3 seconds, then force.

### Dev Cleanup
Bulk termination by group: all Node / Python / Java / Vite / Webpack / npm / pnpm /
yarn·bun / Docker Compose / Go·Cargo·Deno. Plus a list of processes holding popular
dev ports (3000, 5173, 8080, 4200 …) that you can terminate. Listeners are found on both
IPv4 and IPv6 (Node and Vite listen on `::` by default).

> Dev Cleanup terminates **by name regardless of activity** (except the whitelist) —
> a deliberate sledgehammer.

### Disk cleanup (manual only)
A dedicated tab. Flow: **Analyze → preview with sizes → delete selected**. There is
intentionally no automatic disk cleanup (files are less reversible than processes).
Only **known junk paths** are cleaned; the folder map, large files, empty folders and
duplicates live on the neighbouring "Disk" tab. Categories:
- **Dev caches** — npm / pnpm / yarn / bun / pip / uv / poetry / gradle / cargo / go / NuGet /
  Composer / TypeScript, old Playwright browser builds (all regenerated).
- **Dev: downloaded toolchains** — Playwright/Puppeteer/Cypress browsers, electron-builder,
  dotslash, Expo Go, the Maven repository. They re-download, but slowly — unchecked by default.
- **System junk** — `%TEMP%`, `Windows\Temp`, service-profile temp, Recycle Bin, Windows
  Update cache, crash dumps, error reports, Delivery Optimization. `Prefetch` is deliberately
  left alone: it is the program-launch cache, weighs megabytes, and everything starts slower
  after it is deleted until Windows rebuilds it.
- **Windows caches** — thumbcache/iconcache, font cache, notification images, RDP cache —
  Windows rebuilds them itself.
- **GPU shader caches** — DirectX (`D3DSCache`), NVIDIA/AMD/Intel and Steam `shadercache`.
  After cleaning, games and Chrome/Electron recompile their shaders and stutter for the first
  minutes, while the space gained is small — unchecked by default.
- **Microsoft Store app caches** — `INetCache` / `Temp` / `TempState` of every UWP package and
  the WebView cache of the new Teams. `LocalState` and app settings are untouched.
- **Browser caches** — Chrome / Edge / Brave / Yandex / Opera / Vivaldi / Firefox, cache only
  (no passwords, cookies or history).
- **App caches** — Discord / Slack / Teams / Spotify / VS Code / JetBrains / Steam /
  Telegram (incl. `media_cache`) / Figma / game launchers, cache only.
- **NVIDIA: caches and old versions** — old NGX model versions (DLSS, Broadcast, etc.).
  NGX Updater downloads new versions into `ProgramData\NVIDIA\NGX\models\<model>\versions`
  and never removes old ones — gigabytes pile up within a year. Plus the NVIDIA App/Overlay
  cache, the driver downloader, `C:\NVIDIA`. The newest version of each model and the driver
  itself stay. The NVIDIA App game-detection database (`NvBackend\ApplicationOntology`) is
  never touched: without it NVIDIA App stops recognising games until it re-downloads the DB.
- **Old logs** — CBS/DISM/Windows setup logs, Update Orchestrator, npm/yarn/gradle,
  Docker Desktop, OneDrive, Zoom, Chocolatey.
- **Recent file lists** — Recent documents, jump lists (privacy).
- **Old program versions** — previous-version copies left behind by auto-updates: Squirrel
  apps (Postman, Figma, Discord, etc. keep an `app-<version>` folder for every downloaded
  version) and WPS Office (the previous version folder next to the current one). The current
  version is picked by number, for WPS from the registry. Unchecked by default.
- **Windows update and driver leftovers** — `Windows.old`, `$Windows.~BT`, `$WinREAgent`,
  `$GetCurrent`, `ESD`, unpacked AMD/Intel installers, `NVIDIA Installer2`. Update leftovers
  are deleted only when older than 10 days — the period Windows itself keeps them for rollback.
- **Old driver packages (DriverStore)** — versions superseded by newer ones and bound to no
  device. The list comes from `pnputil /enum-drivers`, removal is `pnputil /delete-driver`
  without `/force`: if the system still needs a package, pnputil refuses on its own.
  Folders inside `FileRepository` are never deleted directly.
- **Windows component store (WinSxS)** — superseded component versions left by updates.
  The size comes from `DISM /AnalyzeComponentStore`, cleanup is `DISM /StartComponentCleanup`
  (the only supported way). Superseded updates cannot be rolled back afterwards, so the
  category is unchecked by default.

Guards: locked files and reparse points (junctions) are skipped; `DriverStore` (only via
`pnputil`), `Windows\Installer`, WinSxS, System32, drive roots, code and projects are never
touched directly. Files modified within the last N minutes are kept (N is configurable, 10 by
default). Categories holding several versions of one thing always keep the newest. Every
cleanup is written to `clean-YYYY-MM.log` — the "Log" button opens it.

**Category contents.** Double-click a category (or the "Contents…" button, or Enter) to open
the list of its folders: path, size, file count and a note — contents only or the whole folder,
an age filter, how many folders were inaccessible. Largest first. Untick what should not be
deleted — the choice is stored in `config.json` and applied on every analysis and cleanup until
you tick it back. Excluded folders are left out of the category size, and the category
description gains a "disabled by you: N" marker. Driver packages are excluded the same way,
one by one; the component store (DISM) has no contents list. Double-click a row or "Open
folder" to show it in Explorer.

**winapp2 rules (optional).** The "winapp2 rules" button downloads
[Winapp2.ini](https://github.com/MoscaDotTo/Winapp2) — an open database with thousands of
cleanup rules for specific programs — and adds them to the built-in categories. Only
**file-deletion rules** are taken from it: sections carrying a warning (`Warning=`) or
exclusions (`ExcludeKey`) are skipped whole, and registry rules are ignored — this app never
cleans the registry. winapp2 results are never checked automatically.

The rule file is somebody else's text executed with administrator rights, so it is restricted
separately from the built-in rules:

- **The downloaded database lives in a protected folder**,
  `C:\ProgramData\WindowsProcessCleaner\`, with "Administrators and SYSTEM — full control,
  Users — read only". In its former home (`%APPDATA%`) the file could be rewritten by any
  process running as you — while the cleanup itself runs as administrator. The old copy is
  deleted on the next download. If the folder cannot be created (running without administrator
  rights), the former location is used.
- **A rule from the file has no access to system paths**: inside `C:\Windows` it reaches only
  known junk subfolders (`Temp`, `Prefetch`, `Logs`, `Minidump`, `Debug`,
  `SoftwareDistribution\Download`, `System32\LogFiles`, `Downloaded Program Files`), never
  `Program Files` and never another user's profile, and the `C:\Windows` root is not opened
  even by a narrow mask such as `%WinDir%|*.log`. Built-in rules (MEMORY.DMP in `C:\Windows`,
  `IconCache.db` in `%LOCALAPPDATA%`) work as before: they cannot be tampered with.
- **The guard over saves and application databases applies to every folder of the walk**, not
  only to the rule's root: a `RECURSE` rule over `%LocalAppData%\NVIDIA Corporation` will not
  descend into `NvBackend\ApplicationOntology`, nor into `Saved Games` under the profile.

### Disk
A dedicated tab: **where the space went and what can be removed by hand**. Pick a drive or
folder ("Where to look"), press **Scan** — on the left a folder tree with size, share of the
parent folder and file count (expands on demand), on the right a list in one of three modes:
- **Large files** — files at or above the "Files from N MB" threshold (1 MB by default, the
  value is remembered) in the selected folder and its subfolders, largest first. The threshold
  can be raised after a scan — the list narrows at once; lowering it below the value the scan
  ran with needs a rescan (the app says so).
- **Empty folders** — topmost empties only: if `a\b\c` is empty, `a` is listed, and the number
  of nested empties inside is shown in the line above the list.
- **Duplicates** — files at or above the same threshold with identical content. Comparison runs in three
  steps: size → first 64 KB → full SHA-256, so only real candidates are read in full. The
  oldest file of a group comes first (usually the original); **All** ticks every copy in each
  group except that one.

Deletion goes to the **Recycle Bin** only, as one shell operation and after confirmation;
everything restores normally from the bin. Nothing is ticked by default. Double-clicking a
folder or **Open folder** shows it in Explorer.

Taken care of:
- Inside `Windows`, `Program Files`, `ProgramData`, `AppData\Local\Packages`, `node_modules`
  and `.git` no empty folders or duplicates are offered — identical files and empty
  directories are normal there, and deleting them breaks programs.
- Reparse points (junctions, symlinks, OneDrive folders) are not expanded and never counted
  twice; the tree shows per folder how many links were skipped and where access was denied.
  Paths longer than 260 characters are read, hashed and recycled (the shell receives them
  as 8.3 short names; on a volume with short names disabled such a path cannot go to the
  Recycle Bin — the app reports how many were left).
- Hidden and system files never appear among duplicates.
- Usage bars for all drives sit above the tree. The scan is not cancelled when you switch
  tabs; **Stop** interrupts it.
- The `/disk [path]` switch opens the tab and scans the path right away — handy for a
  shortcut or an Explorer call.

### Programs (uninstall)
A tab listing installed programs (name, version, publisher, size). Check and uninstall
them through the app — the program's own uninstaller is launched. Several checked
programs are uninstalled one after another: the next uninstaller starts once the previous
one has exited (two MSI installers cannot run at the same time). When an uninstaller is
missing (the program was deleted together with its folder, the registry entry remained),
the app reports it with the path; the list is re-read when the run is over. Entries with
the same name (two versions of one program) are shown separately. When a bootstrapper
uninstaller exits at once and leaves a child process running, the app waits for its
descendants and for the registry entry to disappear before re-reading the list.

### Program updates
This tab finds outdated programs across the machine and updates them. Flow:
**Check for updates → tick what you want → Update selected**.

**Where the data comes from.** Package managers are queried: **winget** (Microsoft's
official catalogue, tens of thousands of packages) and, when installed, **Chocolatey**.
Matching an installed program to a package and comparing versions is done by the manager
itself — the app keeps no version database of its own, which would inevitably fall behind
reality.

**The "Impact" column** shows the size of the version jump:

| Value | When |
|---|---|
| **major** | the leading version part changes (14.1.3 → 15.1.1), behaviour may change |
| **minor** | new features, usually still compatible (2.5.1 → 2.7.3) |
| **patch** | fixes and build tweaks (3.14.2 → 3.14.7) |
| **unknown** | the manager doesn't report the exact installed version |

Major updates are listed first and highlighted, so you never have to scroll to find them.
For `0.x` versions a change in the second part also counts as major — under semver that's
exactly where breaking changes land in such projects.

> ⚠️ **"Impact" is the size of the version change, not a security rating.** Neither winget
> nor Chocolatey exposes CVE or severity data, so real criticality cannot be derived from
> them, and the app does not pretend otherwise. If you need to know whether an update
> closes a vulnerability, read the program's own release notes.

**Batch updating.** Ticked packages are handed to the manager in groups rather than one by
one (5 per command by default, configurable 1..20; 1 means strictly one at a time). That
saves one process start and one source-index load per package.

The installers still run **sequentially**, and that's not a limitation of this app: Windows
Installer holds the machine-wide `_MSIExecute` mutex and Chocolatey takes its own global
lock, so two installers launched at once simply fail.

**How the result is determined.** After each group the app re-asks the manager what is
still outdated and diffs the list. That makes the per-package result correct regardless of
system language — parsing the localized output text would be guesswork.

**Also on this tab:**

- **Duplicates.** The same software can be visible through both winget and Chocolatey. Such
  rows are marked and dimmed, and the "All" button skips them — there's no point updating
  the same thing with two managers. The mark is advisory only: you can still tick the row
  manually.
- **"Never offer"** — adds the selected packages to the exclusion list (settings) so they
  stop showing up in future checks.
- **"Log"** — opens `updates-YYYY-MM.log`: what was updated, when, from which version to
  which, and with what result.
- **Nothing is pre-checked.** Updating is your call, so "Update selected" first shows the
  list of what will be updated, with versions, and asks for confirmation.
- Packages whose installed version the manager cannot determine are shown by default; this
  can be turned off in settings.

> An installer may close and restart the program it updates. Save your work before updating
> something that is open.

### Browsers

This tab shows what accumulates in browser profiles over the years and is normally never
visible as a whole. Nothing is installed and nothing attaches to the browser — everything
is read straight from the profile files.

**What is found automatically.** Every profile of every installed Chromium-based browser:
Chrome (including Beta / Dev / Canary), Edge, Yandex Browser, Brave, Vivaldi, Opera and
Opera GX, Chromium. Each profile is shown under its real name, not its folder name.
Firefox is not supported: its bookmarks and sessions live in SQLite (`places.sqlite`) while
the tab reads Chromium formats; the Firefox cache is still cleaned by Disk cleanup.

**What is shown for each profile**

| Section | What is inside | Where it comes from |
|---|---|---|
| **Bookmarks** | the folder tree; each folder shows how many links sit in it directly and how many including subfolders | the `Bookmarks` file |
| **Duplicate URLs** | the same address saved in several folders | computed |
| **Tab groups** | saved groups: name, color, contents, when it last changed | the sync database |
| **Reading list** | saved-for-later pages, read and unread | the sync database |
| **Open tabs** | the current session: windows, tabs and their groups | the `Sessions` files |

Every row shows the name, the address, where it lives, when it was added and when it was
last opened — which is exactly what tells you what can go.

**What you can do**

- **Delete selected** — the ticked links and whole folders.
- **Move to…** — move the ticked items into another folder. The "Merge" checkbox in the
  dialog moves the *contents* of the ticked folders and deletes the folders themselves,
  so two folders about the same thing collapse into one.
- **Duplicates** — lists every repeated address and pre-ticks the redundant copies,
  leaving the one you opened most recently untouched.
- **Check links** — walks the addresses in the current list and reports what the site
  answered (`OK`, `404`, `no DNS`, `timeout`…). It is network work and it is not fast,
  so it only runs on demand and has a "Stop" button. A `403` usually means bot protection
  rather than a dead link: the site answers that way to a request that is not a browser.
  Check such addresses by hand before deleting them.
- Right-click: open in browser, copy the address, check all, uncheck all. In the tree:
  "Delete empty folders", "Delete this folder" and "Save group as bookmarks".

**What cannot be changed, and why**

Tab groups and the reading list live in the browser's sync database. Deleting them from
a file is impossible: the browser holds its own state in memory and would restore the
record from the server. So those sections are view-only. If a group is no longer needed
but its links are worth keeping, use **"Save group as bookmarks"**: it creates a folder
under "Other bookmarks" holding every tab of the group, after which the group itself can
safely be deleted in the browser.

**How bookmarks are kept safe**

- Editing is possible **only while the browser is fully closed**. A running browser keeps
  bookmarks in memory and rewrites the file on exit, so the edit would simply be lost.
- **Before every write** the file is copied into `%APPDATA%\WindowsProcessCleanerrowser-backups\`
  with a timestamp. Those copies are never deleted automatically.
- The bookmarks file carries a checksum computed by the browser itself. The program first
  recomputes it for the **untouched** file and compares it with the stored one. No match
  means this browser build uses its own algorithm, and the profile is switched to
  view-only: better to change nothing than to hand the browser a file it considers broken.
- The write goes through a temporary file rather than over the original.

### Startup
A tab listing all installed programs with a checkbox toggle. Checked = the program starts
at Windows sign-in. Unchecking disables the entry the way Task Manager does: a flag in
`...\Explorer\StartupApproved`, while the `Run` value with its command-line arguments or the
shortcut stays in place, so the toggle can be turned back on. Checking enables an existing
entry, or adds one to the per-user `HKCU\...\Run` when there is none. Reads the registry
(`HKCU\...\Run`, `HKLM\...\Run`, the 32-bit `Run`) and the Startup folders (user + common);
entries disabled in Task Manager or in Settings → Startup apps are shown unchecked with a
"disabled" mark. Startup entries that aren't in the installed-programs list (scripts,
shortcuts) are marked orange and can be toggled too. Nothing is deleted: the program never
removes registry values or shortcuts.

### Windows bloat
A catalogue of roughly 90 items in eleven groups: AI (Copilot, Recall, Click to Do,
Cortana), telemetry and diagnostics, ads and tips (including Bing web search in Start),
widgets and news, preinstalled Store apps, third-party stubs (Candy Crush, TikTok,
Spotify…), Xbox and gaming, OneDrive / Teams / Phone Link, Windows services, Windows
features and PowerToys modules (read from its `settings.json`). A checkbox tree on the
left; on the right, for every item: what it is, why disable it, what you risk, the
recommendation and exactly what will be done (which registry values, services, scheduled
tasks, packages).

Only universal junk is checked by default: telemetry, ads and tips, Bing in Start,
widgets, dead and promotional Store apps, unneeded services and features (PowerShell 2.0,
SMB 1.0, XPS…). Everything debatable — Copilot, Recall, Xbox Game Bar, OneDrive, Teams,
Phone Link, search indexing — is listed unchecked with a ⚠ warning.

Buttons: **"Check state"** (reads the registry, services, scheduled tasks, Store packages
and — with administrator rights only — features via DISM), **"Disable checked"**,
**"Remove checked"**, **"Restore checked"**, "Check recommended", "Uncheck all". Before
the first action on an item its previous state is saved to `debloat-snapshot.json`;
"Restore" rolls back from that snapshot. For Store apps "Disable" removes the package for
the current user (reversible: "Restore" re-registers it from the Windows image), "Remove"
removes it for all users and deprovisions it from the image (only the Microsoft Store can
bring it back; the app opens a Store search). Without administrator rights only the
current-user part runs (HKCU, Store apps, PowerToys); the rest is marked "needs
administrator rights" and, if attempted, is logged as an error.

### Docker
A tab for Docker cleanup (requires the Docker CLI + a running daemon). Buttons: disk
usage overview (`docker system df`), remove stopped containers, unused images, unused
volumes, clear build cache, full cleanup of everything unused. Only **unused** data is
removed (`prune`) — running containers and used images are never touched.

> ⚠️ **Important about disk space.** Docker Desktop stores everything in one growing
> WSL2 virtual disk (`docker_data.vhdx`). `prune` frees space **inside** that disk, but
> the file on Windows **doesn't shrink**. To actually reclaim Windows disk space, use
> the **"Compact Docker disk"** button: it stops Docker (`wsl --shutdown`) and compacts
> the vhdx via `diskpart compact vdisk`, showing size before/after. All running
> containers are stopped in the process.

What to remove before compacting is set by the list next to the button: "nothing",
"safe" (stopped containers, untagged images, build cache — the default), "+ all unused
images" or "everything unused, volumes included". The last option wipes database and
other container data kept in volumes, which the app warns about separately.

Kubernetes is not included (its cleanup affects a live cluster).

### Tools
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
offers to restart as administrator. The label at the bottom of the sidebar always shows
whether those rights are present.

### Long operations: progress and stopping
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

### Remembered selections
What you check stays checked. Previously almost any list reset its checkboxes to the
default when it refreshed, throwing away what you had picked by hand; now one shared
store keeps the selection for the whole window.

- **Survives a restart of the program** (kept in `config.json`): the "and temp files"
  checkbox on Home, the checked categories on the Cleanup tab, the items on the
  "Windows: extras" tab, the selected packages on the Updates tab, and the choice in the
  "Where to look" list on the Disk tab.
- **Survives a list refresh within the session**: the checked programs on the Programs
  tab, the ports in Dev Cleanup, the processes after a re-scan, and the rows on the Disk
  and Browsers tabs. After a restart those lists deliberately start clean: their contents
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

### Interface language
Russian / English — switchable in settings (applied after restart). The documentation
is bilingual too: [README.md](README.md) / [README.en.md](README.en.md).

### Auto-clean timer
Set a number of hours (1..24). Every N hours the app scans **processes** (in the chosen
mode), terminates candidates, purges memory and writes to history. Disk cleanup and
uninstallation are **never** run by the timer — manual only.

### System tray
The tray icon changes color: green — clean, orange — candidates found. Double-click
opens the window. Right-click menu: Scan, Clean, Purge Standby Memory, ⚡ Boost, toggle
auto-clean, restart as administrator, exit. Closing the window minimizes the app to
tray (it keeps running in the background). If an irreversible operation is running at
that moment (deleting files, moving to the Recycle Bin, installing updates, Docker prune
or disk compaction), the tray hint names it, and “Exit” / “Restart as administrator”
ask first: an interrupted compaction would leave Docker stopped, an interrupted install
a half-installed package.

### Start with Windows
A checkbox in settings. Implemented via **Task Scheduler** with highest privileges
(`schtasks /RL HIGHEST`), so there is no UAC prompt at logon.

### Themes
Light / dark / **system** (default — follows the Windows theme, including a live dark/light
switch without a restart). Switchable in settings, applied immediately, including the window
title bar and lists that are already filled (rows never keep the previous theme's colours).
Lists, trees, text fields and drop-downs get a soft rounded border in the theme colour instead of the sharp system line; buttons are drawn with anti-aliased rounded corners.
The opened drop-down list is themed too: roomy rows, the same highlight as the lists, a dark
list window in the dark theme. The multi-line lists on the Settings page stretch to the free
window height.

### Settings (saved)

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

### History
After each cleanup it saves date/time, number of terminated processes, amount of
freed memory and the list of processes.

---

## Is it safe?

**The scan / auto-clean mode — yes.** A candidate is picked only when **all**
criteria match at once, so an active process (with a window, listening on a port,
using CPU, or with a live parent) will never be selected. Global mode additionally
protects system processes and other users' processes. Termination always asks for
confirmation.

**Dev Cleanup — no**, it is intentionally a sledgehammer: the buttons hit everything
by name (except the whitelist). Use consciously.

---

## Where data lives

Everything in one folder — `%APPDATA%\WindowsProcessCleaner\`. The "Data folder" button in
settings opens it.

| File | What it is |
|---|---|
| `config.json` | all settings, including the excluded category folders. Written through a temporary file so a crash cannot leave an empty config; a damaged copy is kept as `config.json.corrupt` |
| `history.json` | process-cleanup history |
| `debloat-snapshot.json` | previous states of the "Windows bloat" items, used by "Restore" |
| `clean-YYYY-MM.log` | disk-cleanup log (what was deleted and how much was freed) |
| `updates-YYYY-MM.log` | program-update log |
| `winapp2.ini` | the downloaded winapp2 rule database, if you fetched it. As of this version it lives in `C:\ProgramData\WindowsProcessCleaner\` instead — a folder no process without administrator rights can write to; the old copy is deleted on the next download |
| `browser-backups\` | copies of the bookmarks files, taken before every edit |
| `crash.log` | stack of the last unhandled error, if the app crashed (one message box is shown; attach the file to a bug report) |

---

## Administrator rights

The app always runs as administrator. This is required to purge Standby Memory via
`ntdll!NtSetSystemInformation`. Terminating the current user's processes also works.

---

## Technical notes

- **WinAPI:** `CreateToolhelp32Snapshot`, `EnumWindows`, `GetExtendedTcpTable`,
  `OpenProcessToken`/`GetTokenInformation` (process owner),
  `SendMessageTimeout(WM_CLOSE)`, `TerminateProcess` (via `Process.Kill`),
  `NtSetSystemInformation` (Standby Memory), `GlobalMemoryStatusEx`,
  `DwmSetWindowAttribute` (dark title bar), `FindFirstFileExW` (disk walk with `\\?\` long
  paths), `SHFileOperationW` (Recycle Bin), `SetWindowTheme` (dark scroll bars).
- **Program updates:** `winget.exe` and `choco.exe` are invoked. `winget upgrade` has no
  machine-readable output (a table only), so the table is parsed by the column start
  positions taken from the header line and mapped by their **order**, not by header names —
  that way parsing also works on a localized winget. Positions are counted in terminal cells,
  not characters: CJK, Hangul and full-width characters take two cells, so a row with such a
  name is shorter than the header. winget's second table («require explicit targeting») is
  parsed by its own header.
- **Uninstall:** the registry `UninstallString` is split into exe and arguments by the app
  itself and started via `ShellExecute`, not `cmd /c`: cmd strips the quotes from a path
  containing `&`, cannot start an unquoted path with spaces (Android Studio, Steam,
  InstallShield) and breaks on commands with four quotes (NVIDIA via `RunDll32`, Docker
  Desktop). `MsiExec /I{GUID}` is the repair dialog, so MSI packages are uninstalled with
  `/X{GUID}` — what "Programs and Features" does.
- **Single-instance** — a named `Mutex` (`Local\WindowsProcessCleaner.singleinstance`). The
  local TCP port **49876** is only used to ask an already running instance to show its
  window; if the port is taken, the app still starts normally.
- **Monitoring** runs on a background thread (not the UI one) with a configurable period
  (5..300 s, 15 by default). Process data is read directly via `OpenProcess` +
  `GetProcessTimes`/`GetProcessMemoryInfo` rather than `Process.GetProcessById` — the latter
  takes a full system snapshot per call, and on 600+ processes a single pass took tens of
  seconds.
- Consequently "5 minutes idle" is counted from the moment the app started observing the
  process, not from when it launched.
- **Command-line switches:** `/tray` — start minimized, `/auto` — run a process auto-clean
  and exit, `/analyze` — measure disk junk without deleting anything (the `TOTAL` line is
  free of double counting of nested targets, `sum=` is the plain category sum), `/disk [path]`
  — open the Disk tab and scan the path right away.
- **Display scaling (DPI):** the window scales to 125/150 % (`AutoScaleMode.Dpi`), button
  bars wrap onto the next line, the settings columns are computed from the label widths, and
  the minimum window size never exceeds the screen working area.
- **Crashes:** an unhandled exception (UI thread and background threads) is written to
  `crash.log` in the data folder, then a single message is shown instead of a silent exit.

## Project files

| File | Purpose |
|------|---------|
| `src\*.cs` | application code, one file per area: `Program.cs` (entry, switches), `Engine*.cs` (logic: processes, cleanup, disk, drivers, winapp2, updates, programs, startup, Docker, health check, tools), `MainForm*.cs` (window, one file per section), `Native.cs` (WinAPI), `Theme.cs`, `Json.cs`, `Browser*.cs`, `FastListView.cs` |
| `app.manifest` | manifest (requireAdministrator, DPI) |
| `icon.ico` | application icon (embedded into the exe) |
| `build.bat` | builds all `src\*.cs` via the built-in csc.exe |
| `run.bat` | build if needed and run |
| `README.md` / `README.en.md` | documentation (RU / EN) |
