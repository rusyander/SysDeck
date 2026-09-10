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
creating a restore point, changing Windows components and the autostart task.

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

---
