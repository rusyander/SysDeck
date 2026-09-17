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

3. **Double-click `run.bat`.** On first launch it builds `SysDeck.exe` and
   opens it right away. After that you can run the `.exe` directly.

   Build only, without running:

   ```
   build.bat
   ```

4. **No UAC prompt on launch.** The app runs with ordinary user rights. Windows asks for
   permission only when you start an operation that genuinely needs it, and the prompt
   makes clear what it is for.

That's it. Nothing is installed into the system: `SysDeck.exe` is
self-contained and can simply live in a folder you carry around.

## Ready-made builds: with an installer and without

`build-installer.bat` produces both at once, side by side in `dist`:

| What you get | Where |
|---|---|
| Installer | `dist\SysDeck-Setup.exe` |
| Portable build | `dist\portable\` (the old folder is removed and built again) |
| Portable build as one archive | `dist\SysDeck-portable.zip` |

The program itself is compiled into `dist\build\` and moved at the end, so this command never touches
`SysDeck.exe` in the repository root.

**A build on every `git push`.** Run `tools\install-git-hooks.bat` once: it puts a `pre-push` handler into
`.git\hooks`. From then on every `git push` builds both editions from the commit being pushed (from a clean copy,
uncommitted edits never get in) and replaces what is in `dist\`. It never stops the push: a failed build shows a
line in the output and the log `dist\last-build.log`. Skip the build once: `SYSDECK_SKIP_BUILD=1 git push`.

**GitHub release.** After every push to `main`, GitHub Actions (`.github/workflows/release.yml`) builds the same on
a Windows machine and replaces the `latest` release: the portable archive and the installer. Permanent link to
the archive — `https://github.com/rusyander/SysDeck/releases/latest/download/SysDeck-portable.zip`.

The builds carry no sources, only the finished `.exe`. .NET programs can still be taken apart with a decompiler
(ILSpy, dnSpy) that shows code close to the original — without an obfuscator that cannot be fully closed, and
Windows ships none.

**Installing.** Run `SysDeck-Setup.exe`. By default it installs for you
only, into `%LOCALAPPDATA%\Programs\SysDeck`, with no administrator prompt
at all. The "for all users" checkbox moves the installation into `Program Files`, and then
Windows asks for permission once. You can pick a different folder and turn off the desktop
shortcut. The installer creates a Start-menu shortcut, an entry in the installed-programs
list and `uninstall.exe` next to the program. Installing over an existing copy just
refreshes the files: shortcuts are not duplicated, settings and history stay. If the
program is running at that moment, the installer says so and offers to close it — it never
terminates anything without your consent.

**Uninstalling.** Settings → Apps → SysDeck → Uninstall, or `uninstall.exe`
from the program folder. Files, shortcuts, the autostart task, the installed-programs
entry, the browser integration keys and the downloads autostart value are removed. The "also remove settings and history" checkbox is off by default:
remembered selections, settings and history live in `%APPDATA%\SysDeck` and
survive until you ask for them to go — leaving the box unticked means a fresh install picks
up exactly where you left off.

**Portable build.** The `portable` folder is the same program without installation: unpack
it anywhere, a USB stick included. Settings, history and logs are written next to the exe,
into a `Data` subfolder; the `portable.marker` file is what switches that on, so keep it
beside the program. To remove the portable build, delete the folder — nothing is left
behind in the system.

## Moving from the old name

Until 15.09.2026 the program was called Windows Process Cleaner (`WindowsProcessCleaner.exe`). The first
start of `SysDeck.exe` moves everything by itself:

- it stops the old build's background processes (Capture, the overlay, Folder sizes, downloads) with their own
  exit command, never by force;
- it renames the data folder `%APPDATA%\WindowsProcessCleaner` to `%APPDATA%\SysDeck`. A junction to the new
  folder stays at the old path: the browser still finds the unpacked extension, and the old window, if it is
  still open, writes to the same place. If some file is busy, the program keeps using the old folder and tries
  again on the next start. What was moved is written to `rebrand.log` in the data folder;
- it moves the autostart values in `HKCU\...\Run` and the `.torrent` / `magnet:` associations to the new name
  and the new exe;
- when the window opens, it asks about the Task Scheduler tasks (autostart, Folder sizes, the elevated overlay)
  and the firewall rule for torrents. After “Yes” Windows asks for administrator rights once, the tasks are
  created again under the SysDeck name and the old ones are removed.

The SysDeck installer run over an old installation removes its shortcut, its installed-programs entry and its
files. A downloaded winapp2 rule base has to be downloaded again: it lived in
`C:\ProgramData\WindowsProcessCleaner`. Internal names that an installed extension and unfinished downloads
depend on stay as they were: the `org.wpc.downloads` host and the `.wpcpart` suffix of partial files.

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

1. In settings, uncheck **Start with Windows** — the `SysDeck` task is
   removed from Task Scheduler.
2. If you turned on the browser integration, uncheck **Browser integration** in the download
   settings — the `NativeMessagingHosts\org.wpc.downloads` keys are removed.
3. Close the program (tray → Exit).
4. Delete the project folder and the settings folder `%APPDATA%\SysDeck\`.

The app creates no services. The registry keeps only what you turned on: autostart values under
`HKCU\...\Run` (Capture, unfinished downloads) and the browser integration keys. The matching
settings remove them.
