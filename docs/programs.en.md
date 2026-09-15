# Programs, updates, browsers, startup and Windows

The Programs, Program updates, Browsers, Startup and Windows bloat pages.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](programs.md)

## Programs (uninstall)
A tab listing installed programs (name, version, publisher, size). Check and uninstall
them through the app — the program's own uninstaller is launched. Several checked
programs are uninstalled one after another: the next uninstaller starts once the previous
one has exited (two MSI installers cannot run at the same time). When an uninstaller is
missing (the program was deleted together with its folder, the registry entry remained),
the app reports it with the path; the list is re-read when the run is over. Entries with
the same name (two versions of one program) are shown separately. When a bootstrapper
uninstaller exits at once and leaves a child process running, the app waits for its
descendants and for the registry entry to disappear before re-reading the list.

## Program updates
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

## Browsers

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
- **Before every write** the file is copied into `%APPDATA%\SysDeckrowser-backups\`
  with a timestamp. Those copies are never deleted automatically.
- The bookmarks file carries a checksum computed by the browser itself. The program first
  recomputes it for the **untouched** file and compares it with the stored one. No match
  means this browser build uses its own algorithm, and the profile is switched to
  view-only: better to change nothing than to hand the browser a file it considers broken.
- The write goes through a temporary file rather than over the original.

## Startup
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

## Windows bloat
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
XPS…). Xbox Game Bar and the PowerToys modules are checked too, except the File Explorer and
context-menu add-ons (File Explorer, File Locksmith, Image Resizer, Peek, PowerRename,
Registry Preview); a new unknown module is checked as well. Everything debatable — Copilot,
Recall, OneDrive, Teams, Phone Link, search indexing, SMB 1.0 — is listed unchecked with a ⚠
warning. Apps that hold offline
content (Spotify, Netflix, Prime Video, Disney+) and live working apps (Power Automate,
OneNote for Windows 10) are unchecked as well.

Buttons: **"Check state"** (reads the registry, services, scheduled tasks, Store packages
and — with administrator rights only — features via DISM), **"Disable checked"**,
**"Remove checked"**, **"Restore checked"**, "Check recommended", "Uncheck all". Before
the first action on an item its previous state is saved to `debloat-snapshot.json`;
"Restore" rolls back from that snapshot. For Store apps "Disable" removes the package for
the current user (reversible: "Restore" re-registers it from the Windows image), "Remove"
removes it for all users and deprovisions it from the image (only the Microsoft Store can
bring it back; the app opens a Store search). Items that change the system as a whole
(services, components, capabilities, scheduled tasks, OneDrive) run through the elevated
helper: the whole checked set goes out as a single job, so UAC appears once instead of once
per item across a hundred and fifty of them. Declining is logged as a line with the reason.
