# Disk: cleanup, space map and Docker

The Disk cleanup, Disk and Docker pages: what counts as junk, how the preview and the deletion work, where the space went and how to get it back.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](disk.md)

## Disk cleanup (manual only)
A dedicated tab. Flow: **Analyze → preview with sizes → delete selected**. There is
intentionally no automatic disk cleanup (files are less reversible than processes).
Only **known junk paths** are cleaned; the folder map, large files, empty folders and
duplicates live on the neighbouring "Disk" tab. Categories:
- **Dev caches** — npm / pnpm / yarn / bun / pip / uv / poetry / gradle / cargo / go / NuGet /
  Composer / TypeScript (all regenerated).
- **Dev: downloaded toolchains** — old Playwright browser builds (the current one stays; a
  project pinned to an older version needs `npx playwright install` afterwards),
  Puppeteer/Cypress browsers, electron-builder, dotslash, Expo Go, the Maven repository, NuGet
  packages (`~/.nuget/packages`). They re-download, but slowly — unchecked by default.
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
- **App caches** — Discord / Slack / Teams / Spotify (embedded browser) / VS Code / JetBrains /
  Steam / Telegram (incl. `media_cache`) / Figma / game launchers, cache only.
- **Caches holding offline content** — Spotify `Storage`/`Data`: the streaming cache together
  with Premium offline downloads. Tracks re-download, so the category is unchecked by default.
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
  `$GetCurrent`, `ESD`, unpacked AMD/Intel installers, `NVIDIA Installer2`, the MSI patch
  cache `Windows\Installer\$PatchCache$` (without it, repairing or removing Office, SQL Server
  or Visual Studio patches asks for the original installer). Update leftovers are deleted only
  when older than 10 days — the period Windows itself keeps them for rollback.
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

## Disk
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

## Docker
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
