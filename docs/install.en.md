# Installation

Requirements, building from source, the ready-made installer and the portable build, removal.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](install.md)

## What you need

| Requirement | Note |
|---|---|
| Windows 10 or 11 (x64) | Windows 8.1 works too |
| .NET Framework 4.x | **already part of Windows**, nothing to install |
| Administrator rights | **not needed to start.** Asked for per operation — cleaning system folders, purging Standby Memory, repairing system files |

Visual Studio, the .NET SDK, Node.js and any other package are **not required** — the
program is built by `csc.exe`, which already sits inside Windows.

Optional, and only for the corresponding tabs:

- **winget** — for the Updates tab. Windows 11 and recent Windows 10 ship with it; if you
  don't have it, install "App Installer" from the Microsoft Store.
- **Chocolatey** — an optional second update source. Without it the tab simply works
  through winget alone.
- **Docker Desktop** — for the Docker tab (a running daemon is needed).

## Steps

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

4. **No UAC prompt on launch.** The app runs with ordinary user rights. Windows asks for
   permission only when you start an operation that genuinely needs it, and the prompt
   makes clear what it is for.

That's it. Nothing is installed into the system: `WindowsProcessCleaner.exe` is
self-contained and can simply live in a folder you carry around.

## Ready-made builds: with an installer and without

`build-installer.bat` produces both at once, side by side in `dist`:

| What you get | Where |
|---|---|
| Installer | `dist\WindowsProcessCleaner-Setup.exe` |
| Portable build | `dist\portable\` |

**Installing.** Run `WindowsProcessCleaner-Setup.exe`. By default it installs for you
only, into `%LOCALAPPDATA%\Programs\WindowsProcessCleaner`, with no administrator prompt
at all. The "for all users" checkbox moves the installation into `Program Files`, and then
Windows asks for permission once. You can pick a different folder and turn off the desktop
shortcut. The installer creates a Start-menu shortcut, an entry in the installed-programs
list and `uninstall.exe` next to the program. Installing over an existing copy just
refreshes the files: shortcuts are not duplicated, settings and history stay. If the
program is running at that moment, the installer says so and offers to close it — it never
terminates anything without your consent.

**Uninstalling.** Settings → Apps → Windows Process Cleaner → Uninstall, or `uninstall.exe`
from the program folder. Files, shortcuts, the autostart task, the installed-programs
entry, the browser integration keys and the downloads autostart value are removed. The "also remove settings and history" checkbox is off by default:
remembered selections, settings and history live in `%APPDATA%\WindowsProcessCleaner` and
survive until you ask for them to go — leaving the box unticked means a fresh install picks
up exactly where you left off.

**Portable build.** The `portable` folder is the same program without installation: unpack
it anywhere, a USB stick included. Settings, history and logs are written next to the exe,
into a `Data` subfolder; the `portable.marker` file is what switches that on, so keep it
beside the program. To remove the portable build, delete the folder — nothing is left
behind in the system.

## If something goes wrong

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

## How to remove it

1. In settings, uncheck **Start with Windows** — the `WindowsProcessCleaner` task is
   removed from Task Scheduler.
2. If you turned on the browser integration, uncheck **Browser integration** in the download
   settings — the `NativeMessagingHosts\org.wpc.downloads` keys are removed.
3. Close the program (tray → Exit).
4. Delete the project folder and the settings folder `%APPDATA%\WindowsProcessCleaner\`.

The app creates no services. The registry keeps only what you turned on: autostart values under
`HKCU\...\Run` (Capture, unfinished downloads) and the browser integration keys. The matching
settings remove them.
