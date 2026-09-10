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
- **Command-line switches:** `/tray` — start minimized, `/auto` — quiet disk cleanup with no
  window for your own scheduled task (recommended categories, honouring the ticks set or
  cleared in the window; the exit code is visible to the scheduler), `/analyze` — measure
  disk junk without deleting anything (the `TOTAL` line is
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
| `app.manifest` | manifest (asInvoker, DPI, long paths) |
| `installer\*.cs` | installer and uninstaller sources (a separate program with its own manifest) |
| `build-installer.bat` | build both ready-made distributions into `dist\` |
| `tests\*.cs`, `tests\run-tests.bat` | the test suite (runs without administrator rights); how to run it and what it covers — [tests.md](tests.md) |
| `icon.ico` | application icon (embedded into the exe) |
| `build.bat` | builds all `src\*.cs` via the built-in csc.exe |
| `run.bat` | build if needed and run |
| `README.md` / `README.en.md` | project overview (RU / EN) |
| `docs\*.md` / `docs\*.en.md` | the full per-section manual (RU / EN) |
