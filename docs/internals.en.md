# Technical notes

The WinAPI in use, parsing of package-manager output, command-line switches, what lives in the repository.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](internals.md)

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
- **Single-instance** — a named `Mutex` (`Local\SysDeck.singleinstance`). The
  local TCP port **49876** is only used to ask an already running instance to show its
  window; if the port is taken, the app still starts normally.
- **Monitoring** runs on a background thread (not the UI one) with a configurable period
  (5..300 s, 15 by default). Process data is read directly via `OpenProcess` +
  `GetProcessTimes`/`GetProcessMemoryInfo` rather than `Process.GetProcessById` — the latter
  takes a full system snapshot per call, and on 600+ processes a single pass took tens of
  seconds.
- Consequently "5 minutes idle" is counted from the moment the app started observing the
  process, not from when it launched.
- **Command-line switches:** `/tray` — start minimized, `/auto` — quiet disk cleanup with no
  window for your own scheduled task (recommended categories, honouring the ticks set or
  cleared in the window; the exit code is visible to the scheduler), `/analyze` — measure
  disk junk without deleting anything (the `TOTAL` line is
  free of double counting of nested targets, `sum=` is the plain category sum), `/disk [path]`
  — open the Disk tab and scan the path right away; if the window is already open, the path is
  handed to it and no second window appears.
- **Folder sizes switches** (checked before the window's mutex, so they never disturb it):
  `--foldersize` — background mode (tray, panel, numbers in Explorer; a second start only shows
  the panel), `--foldersize-exit` — stop it, `--foldersize-measure <folder> [--hidden]` — the same
  count printed to the console with per-folder timing (to compare with another tool),
  `--foldersize-probe <hwnd>` — what is read from an Explorer window through UI Automation (the
  Size column, rows, cells), `--foldersize-autostart on|off` — autostart for a script or an
  installer. The window and the background process talk through named events that grant access
  to the current user: an elevated background process hears a non-elevated window, where UIPI
  would drop window messages.
- **Capture switches** (also checked before the window's mutex): `--capture` — the screenshot
  background process (hotkeys, region selection, notifications; a second start exits at once),
  `--capture-exit` — stop it (exit code 1 if it is not running), `--capture-shot region|screen|window`
  — ask the running process for a shot, e.g. from a shortcut or a script (code 1 — not running,
  2 — unknown kind). Same channel: named events that grant access to the current user. Hotkeys
  use `RegisterHotKey`, no global keyboard hook; shots are a `BitBlt` of the desktop in physical
  pixels (the thread runs Per-Monitor DPI v2); the notification is excluded from capture with
  `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- **Background process updates:** the capture, overlay and downloads processes watch the exe's
  modification time. Once the exe is rebuilt or updated and has not changed for 10 seconds, the
  process starts a new copy with a wait switch and exits; the new copy waits until the old one
  releases the mutex. The restart
  waits while a video is recording, a region is being selected, the editor or gallery is open, lag
  is being recorded, the overlay is being dragged, or something is downloading or seeding. Rights are inherited from the old process.
- **Display scaling (DPI):** the window scales to 125/150 % (`AutoScaleMode.Dpi`), button
  bars wrap onto the next line, the settings columns are computed from the label widths, and
  the minimum window size never exceeds the screen working area.
- **Downloads:** a separate `--downloads` process fetches a file in up to 4
  segments (up to 16, at most 8 connections per server), runs up to 3 downloads at once and up to 2
  from one site. Resuming after a close or a crash starts from the bytes already flushed to disk and
  sends `If-Range`: if the file on the server has changed, the download stops instead of splicing two
  versions together. At most 10 redirects; https to http only with consent; cookies go only to the
  host they were issued for. A finished file is checked by SHA-256 (and against the expected hash if
  one is given), renamed from `.wpcpart` and marked as "from the internet" through
  `IAttachmentExecute`. Start conditions: schedule, postponed start, metered network, battery power
  and an idle PC (5 minutes without input, CPU below 30 %, no fullscreen app). Deleting a downloaded
  file goes to the Recycle Bin only.
  The Downloads page is a pipe client: it polls the process every 700 ms (without it, it reads the store every
  3 seconds and never changes it), sends commands from a background thread and starts the process only when a
  command changes the queue. The pipe, mutex, stop event and autostart value names get a suffix from the SHA-256
  of the data folder path when that folder is not the standard one, so a copy with its own folder never drives
  another queue. The signature check before running a program is `WinVerifyTrust` with no UI and no online
  revocation check.
- **Crashes:** an unhandled exception (UI thread and background threads) is written to
  `crash.log` in the data folder, then a single message is shown instead of a silent exit.

## Project files

| File | Purpose |
|------|---------|
| `src\*.cs` | application code, one file per area: `Program.cs` (entry, switches), `Engine*.cs` (logic: processes, cleanup, disk, drivers, winapp2, updates, programs, startup, Docker, health check, tools), `MainForm*.cs` (window, one file per section; Downloads is `MainForm.Downloads*.cs`), `Native.cs` (WinAPI), `Theme.cs`, `Json.cs`, `Browser*.cs`, `FastListView.cs`, `FolderSize.*.cs` (the Folder sizes background mode: counting, $MFT reading, Explorer through UI Automation, the overlay, the panel, the tray), `Downloads.*.cs` (the download engine in a separate process: segments, resume, queue, speed limits, start conditions, the "from the internet" mark, the named pipe; `Downloads.View.cs` is what the page shows, `Downloads.Verify.cs` the signature check, `Downloads.Bridge.cs` the native messaging host for the browser extension: one-request link check, handing the download to the background process, `Downloads.Browsers.cs` registry keys, manifests, unpacking the extension) |
| `extension\` | the browser extension: `src\` (shared code, the popup, `_locales`), `manifest.chromium.json` and `manifest.firefox.json`, `test\` (Node tests: `node --test test/*.test.js`). Embedded into the exe as resources at build time, without `test\` |
| `toolkit\` | the scripts of the "Scripts" page, one folder per item; embedded into the exe as `toolkit/<path>` resources and deployed to disk by a button |
| `resources.bat` | writes the csc response file with every embedded resource (`extension\` minus `test\`, and `toolkit\`); called by `build.bat`, `buildcheck.bat` and `tests\run-tests.bat` — the cmd.exe command line stops at 8191 characters |
| `tools\pack-extension.ps1` | builds the unpacked folders and the store zips in `dist\` |
| `app.manifest` | manifest (asInvoker, DPI, long paths) |
| `installer\*.cs` | installer and uninstaller sources (a separate program with its own manifest) |
| `build-installer.bat` | build both ready-made distributions into `dist\` |
| `tests\*.cs`, `tests\run-tests.bat` | the test suite (runs without administrator rights); how to run it and what it covers — [tests.md](tests.md) |
| `icon.ico` | application icon (embedded into the exe) |
| `build.bat` | builds all `src\*.cs` via the built-in csc.exe |
| `run.bat` | build if needed and run |
| `README.md` / `README.en.md` | project overview (RU / EN) |
| `docs\*.md` / `docs\*.en.md` | the full per-section manual (RU / EN) |
