# Processes and memory

The Home, Scanning, Memory, Video memory and Dev Cleanup pages: what is shown, which rules make a process a termination candidate, and how RAM and video memory usage are broken down.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](processes-and-memory.md)

## Home
The start screen, in the spirit of Microsoft PC Manager: three cards (RAM, system drive,
status after the last check), a big **⚡ Boost** button and a table of checks.

- **Boost** — terminates abandoned processes (the current scan candidates) and purges
  Standby Memory; the result is shown under the cards and written to history. With the
  "and temporary files" box ticked it shows the list of categories and asks before deleting.
- **Smart boost** — a checkbox right there and in Settings: when memory usage exceeds the
  threshold (90 % by default) the app purges Standby Memory on its own, at most once every
  15 minutes, and says so with a tray hint. The checkbox lives in `config.json` and survives
  a reboot; it works while the app is running, so enabling it without autostart makes the
  program offer "Start with Windows" once.
- **Check health** — 12 checks in a few seconds: memory, system drive, uptime, hibernation
  file, startup entries, abandoned processes, size of temporary files, the Downloads
  folder, Defender (enabled, signature age, last scan), the last Windows update, restore
  points, program updates. Every row has a status (fine / tip / attention) and an action:
  double-click opens the relevant tab, runs a tool, or runs the boost itself. The status is
  colour-coded (green / blue / orange). The check runs when Home is first shown and repeats
  on its own once the last one is older than an hour.

## Scanning
For each process it collects: name, PID, PPID, path, uptime, CPU %, RAM, whether
it has a window, listening TCP ports, whether it has child processes, and owner.

## "Abandoned" criteria
A process becomes a termination candidate only when **all** conditions hold:
1. the parent process has exited;
2. CPU below the threshold for longer than the idle time;
3. no user windows;
4. not listening on TCP ports;
5. no child processes;
6. not in the whitelist and older than the minimum lifetime.

## Two scan modes
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

## Cleanup buttons
- **Clean selected** — terminate the checked rows.
- **Auto-clean all inactive** — terminate every found candidate in one click
  (with a confirmation and a list).
- **Select all / Clear selection** — checkbox control.
- **Purge memory** — Standby Memory purge only, without terminating processes.

Termination itself: first gracefully (WM_CLOSE), wait up to 3 seconds, then force.

## Memory

The tab answers one question — **where did the RAM go** — and answers it in full: all blocks add
up to the installed size, including what the hardware took and what cannot be attributed to any
process. Data refreshes once a second (the interval switches between 0.5 and 5 seconds, and there
is a pause).

Five views:

| View | What it shows |
|---|---|
| **Scheme** | a treemap: a block's area is proportional to the bytes it holds, and each block carries its name and size. Processes are grouped by image name ("chrome.exe × 59"); double-click drills into a group, down to individual processes, "← Back" returns |
| **Page lists** | how the kernel sorted every page of physical memory: active, modified, standby across eight priorities, free, zeroed, bad |
| **Processes** | private and total working set, committed, per-tick growth, hard faults per second, threads, handles, session. Click a header to sort |
| **Driver pools** | the four-letter tags drivers stamp on their kernel-pool allocations — this is how a leaking driver becomes visible |
| **Hardware** | modules from SMBIOS (size, type, speed, manufacturer, part number) and the physical address ranges given to RAM |

What the scheme adds up: **processes** (private working sets), **compressed memory**,
**nonpaged pool**, the resident part of the **paged pool**, **kernel and driver code**,
**system cache**, **unattributed**, **standby** by priority, **modified**, **free**,
**hardware reserved**, **bad blocks**.

> The **"Unattributed"** block is not an accounting error. It holds pages locked by drivers and
> virtual machines (WSL, Hyper-V, Docker), page tables, shared DLLs and mapped files. The program
> does not pretend to know more about them than it does: the block's tooltip states the exact
> upper bound of shared pages. A breakdown down to individual files, the way RAMMap does it, was
> deliberately left out — it relies on undocumented structures that change from one Windows build
> to the next.

**What can be emptied** (buttons in the second row): the working sets of all processes, the system
cache, modified pages, the standby list, priority-0 standby, or everything at once. These are the
same kernel commands as RAMMap's Empty menu. Nothing is lost, but after a full empty the system is
noticeably slower for a few seconds — pages have to be read back; that is why a full empty and the
working-set empty ask first.

**What you can do with processes:** check them in the list (or pick a block on the scheme) and
either empty their working sets or terminate them. Termination always shows the list and asks for
confirmation.

Rights: **reading** the memory state needs no administrator — the tab works fully right after
launch. Rights are only needed for empty operations, and it is the button you press that asks for
them, not the window. The first empty raises a resident helper — **one UAC prompt per session**,
after which empties run instantly and silently. The "Get rights" button does the same thing ahead
of time. The helper lives exactly as long as the program does: it gets a signal on exit, watches for
its parent process disappearing, and quits after eight idle hours in any case — a process with
administrator rights has no business hanging around forever. The helper understands exactly two
commands — an empty operation, and trimming the working sets of the listed pids. Termination
deliberately never goes through it: a "kill any pid as administrator" command sitting in a file is a
ready-made privilege-escalation hole. If a process is protected or the rights were not enough, the
program says so.

## Video memory

The Memory tab's twin for the graphics card: **what takes video memory, on which card and how
much**. It works with any graphics card — NVIDIA, AMD, Intel, integrated or discrete — because it
reads Windows' own counters (the same ones Task Manager shows under Performance → GPU) instead of a
vendor tool. No administrator rights are needed. Updates once a second, the interval goes from
0.5 to 5 seconds, and there is a pause.

At the top you pick the card (when there are several; by default the one with the most dedicated memory, that is the
discrete card rather than the integrated one) and the memory: **dedicated** — the card's
own memory (VRAM), **shared** — the part of RAM Windows lends to the card.

| View | What it shows |
|---|---|
| **Scheme** | a treemap of the memory in use on the selected card: processes grouped by image name, browser GPU processes (teal) and a "System and driver" block — memory in use on the card but attributed to no process. Free memory is in the bar above the scheme, where the whole card is shown |
| **Processes** | dedicated and shared memory, committed total, load and engine type (3D, video decode, compute), "GPU process" and "system" marks. Checkboxes, sorting by column |
| **Graphics cards** | every card at once: size, in use, load, process count. A double click opens the card's scheme |

At the bottom is a chart of the last minutes: the fill is memory in use, the line is load. The scale
follows the peak, so growth stays visible even on a 24 GB card.

> The Windows counter sometimes attributes more memory to a process than the whole card has (that
> is how virtual reservations are counted). The tab takes the smaller of "committed to the process"
> and "resident on the card" and shows the inflated value in the tooltip. When the card's counter
> lags behind the process sum, "in use" is raised to that sum — the header and the scheme always
> name the same number.

**Only gentle resets**, each one confirmed. A few seconds after a reset the tab reports how much
video memory there was and how much there is now, as measured.

- **Restart the GPU process** — for Chrome, Edge, Yandex Browser, VS Code, Telegram, Discord and other
  Chromium/Electron programs. The program starts its GPU process again by itself: windows, tabs and
  typed text stay, the picture may blink. A restart is allowed only when the process really is a
  child GPU process of the same application; once every three minutes per application at most,
  otherwise Chromium turns hardware acceleration off after several "crashes" in a row.
- **Terminate processes** — the ones selected on the scheme or ticked in the list, listed in the
  confirmation. System processes (dwm.exe, csrss.exe, svchost.exe and the like) are never
  terminated. When an ordinary attempt is not enough, the program offers to retry with administrator
  rights.
- **Restart the graphics driver** — the same as Win+Ctrl+Shift+B: the screen goes dark for a second
  or two and comes back, the driver's memory is released. Before that it lists the programs drawing
  through 3D or holding a lot of video memory right now — games and 3D editors may close with an
  error, so save your work in them. At most once a minute. The desktop (DWM) is not restarted
  separately.

## Dev Cleanup
Bulk termination by group: all Node / Python / Java / Vite / Webpack / npm / pnpm /
yarn·bun / Docker Compose / Go·Cargo·Deno. Plus a list of processes holding popular
dev ports (3000, 5173, 8080, 4200 …) that you can terminate. Listeners are found on both
IPv4 and IPv6 (Node and Vite listen on `::` by default).

> Dev Cleanup terminates **by name regardless of activity** (except the whitelist) —
> a deliberate sledgehammer. Before the hit it shows the list of matching processes and
> asks; exactly what was listed gets terminated.
