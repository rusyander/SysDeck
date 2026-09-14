# Data and administrator rights

Where the program keeps settings, history and logs, which operations need administrator rights and how exactly they are requested.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](data-and-rights.md)

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
| `foldersize\` | the Folder sizes background mode: `settings.json` (its settings), `sizes.json` (remembered sizes), `status.json` (pid and rights of the running process — the tab reads its state from it), its own `crash.log` |
| `capture\` | Capture: `settings.json` (its settings and shortcuts), `status.json` (pid, rights and shortcut state of the running background process), its own `crash.log`. The screenshots themselves are not kept here but in the chosen folder, `Pictures\Screenshots` by default |
| `downloads\` | the downloads background process: `settings.json` (its settings), `index.json` (queue order), `items\<id>.json` with a `.bak` copy (one file per download: address, name, segments and how many bytes of each are already flushed to disk, journal), `engine.log`. Browser cookies are never written here — they live only in the process memory. The files themselves go to the chosen folder, ending in `.wpcpart` until they are complete. Browser integration: `nmh\` (native messaging manifests), `extension\` (the unpacked extension), `bridge-<browser>.json` (when the extension connected and its version) |
| `crash.log` | stack of the last unhandled error, if the app crashed (one message box is shown; attach the file to a bug report) |

---

## Administrator rights

**The window starts with ordinary user rights.** Rights are raised per operation: the
program launches itself as a second process, that process does one job, writes the result
to a file and exits. One operation, one UAC prompt, and the caption at the bottom says
what it was shown for.

Rights are required by: cleaning system folders (`C:\Windows`, `ProgramData`, update
leftovers in the drive root), removing drivers from the DriverStore and components via
DISM, purging Standby Memory, the repair tools (SFC, DISM, chkdsk, Windows Update reset),
creating a restore point, changing Windows components, the autostart task and the firewall rule for incoming torrent
connections.

Never required and never asked for: everything inside your own profile — browser and app
caches, developer folders, the Recycle Bin, terminating your own processes, reading lists.

Cleanup is split by individual folder, not by whole category: if "App caches" holds a
hundred and fifty folders inside your profile and five outside it, the first hundred and
fifty are cleaned straight away and silently, and UAC is asked only for the five. Declining
does not undo the rest — that work is already done, and whatever was skipped is listed by
name in the log.

Background work never shows a UAC prompt: the memory-threshold smart boost and scheduled
auto-clean silently skip whatever they lack rights for and write that to the log. Autostart
at sign-in stays a Task Scheduler task with highest privileges, so a window opened that way
already has rights and needs no helper.

Fast mode of Folder sizes asks for UAC once — to create the `WindowsProcessCleaner FolderSize`
Task Scheduler task with highest privileges. The program then runs that task itself, without
rights and without UAC: at sign-in and on a restart from the tray. Deleting the task needs
rights too.

The Capture background process never gets administrator rights. If the window runs elevated,
it hands the start over to Explorer, so the process starts with the user's ordinary token.
Autostart is a value under `HKCU\...\Run`, with no Task Scheduler and no UAC.

The downloads background process (`--downloads`) also runs with ordinary rights only: downloaded
files must not be owned by the administrator. From an elevated window it is started through
Explorer. The `WindowsProcessCleaner.Downloads` value under `HKCU\...\Run` exists only while the
queue holds unfinished downloads and "resume after sign-in" is on, and is removed once the queue
is empty. The process takes commands over a named pipe open to the current user only, and only
from a process of the same exe; it does not listen to other programs. A copy of the program with
another data folder (portable or with `WPC_DATA_DIR`) has its own pipe and autostart value, with a
suffix computed from that folder's path. While the process is not running, the Downloads page only
reads its files; the one thing it writes itself is `settings.json` when settings change, and the autostart value to match it.

Browser integration is three keys under `HKCU`, with no administrator rights:
`Software\Google\Chrome\NativeMessagingHosts\org.wpc.downloads` (other Chromium browsers read it too),
`Software\Microsoft\Edge\NativeMessagingHosts\org.wpc.downloads` and `Software\Mozilla\NativeMessagingHosts\org.wpc.downloads`.
Each leads to a manifest in `downloads\nmh\`. Through them the browser itself starts `WindowsProcessCleaner.exe` with
the rights of the user the browser runs as. That process talks only to the extension whose id is built into the
program and hands downloads to the background process over the same pipe. Only the copy with the default data folder
writes the keys; the "Browser integration" checkbox and the uninstaller remove them. The uninstaller also removes the
`WindowsProcessCleaner.Downloads` value from `HKCU\...\Run` when it starts the copy being removed.

Torrents. Without a firewall rule the background process does not open a port for incoming connections, so the
Windows "Allow access" dialog never appears by itself. The `Windows Process Cleaner (BitTorrent)` rule is added by the
elevated helper, and only through the **Allow incoming…** button. The rule lets incoming connections through for this
program only. **Close incoming** does not delete it: the process simply stops listening on the port. Opening `.torrent`
files and magnet links with the program uses keys under `HKCU\Software`, with no administrator rights:
`Classes\WindowsProcessCleaner.Torrent`, `Classes\WindowsProcessCleaner.Magnet`, `Classes\.torrent`, `Classes\magnet`,
`WindowsProcessCleaner\Capabilities` and a value in `RegisteredApplications`. The previous handler is saved in
`WindowsProcessCleaner\Associations` and restored when the option is turned off. An app choice already made in
Windows is never overwritten.

---
