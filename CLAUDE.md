# Windows Process Cleaner — agent entry

Windows maintenance app: processes, RAM (RAMMap-class «Память» tab), disk-cleanup categories, folder
map, browsers, updates (winget/choco), programs, startup, Docker, debloat, tools. Single self-contained
`WindowsProcessCleaner.exe`, tracked in git.

## Build & test — no toolchain to install

| What | Command | Notes |
| --- | --- | --- |
| Build | `build.bat` | Windows' own `csc.exe` (.NET Framework 4.x), all `src\*.cs` → repo-root exe |
| Warnings gate | `buildcheck.bat` | same compile with `/warn:4` into `%TEMP%`; **must print nothing** |
| Tests | `tests\run-tests.bat` | compiles `src\*.cs` + `tests\*.cs` into `%TEMP%`, runs it; exit code = failures |
| Installer + portable | `build-installer.bat` | writes `dist\` (git-excluded) |

From Git Bash use `cmd //c "<abs path>\build.bat"` — `cmd /c` and `/reference:` args get path-mangled.

## Language & style constraints (non-negotiable)

- **C# 5 only**: no string interpolation, no `?.`, no `nameof`, no `async/await`. The compiler is the
  one shipped with Windows.
- Zero warnings at `/warn:4`.
- `Engine.*` / `MainForm.*` / `Native.*` are **partial classes over many files** — one field namespace
  per class, so new fields carry an area prefix (`_ram*`, `_disk*`).
- UI strings go through `Tr.S("ru", "en")`. Comments in `src/` are Russian (existing idiom); agent docs
  under `.agent/` are English.

## Rights model

`app.manifest` is **asInvoker**. Rights are raised per operation by relaunching the same exe as
`--elevated-job <dir>` (`src/Elevation*.cs`); the window never prompts on its own initiative — only a
button the user just pressed does. The «Память» tab additionally keeps a **resident** elevated helper
(one UAC per session) whose command set is deliberately limited to `empty <2..7>` and `trim <pids>`:
«kill any pid as admin» through a file in `%TEMP%` would be a privilege-escalation primitive.
Termination uses the one-shot `kill` job instead.

## Safety invariants the tests defend

Deletion is the most expensive decision in this app. `Engine.Clean*` never leaves a target's root, never
follows junctions, refuses protected trees (`src/Engine.Clean.cs` guards + `tests/Tests.Paths.cs`), and
the «Диск» page deletes to the Recycle Bin only. Tests run **without administrator rights**, show no UAC
window, never touch the real `%APPDATA%\WindowsProcessCleaner` (redirected via `WPC_DATA_DIR`), never
purge the Recycle Bin and never reset the machine's memory.

## Where the working state lives

`.agent/` (git-excluded, this disk only): `PROGRESS.md` = current state and per-round history,
`notes.md` = accumulated gotchas, `archive/` = closed rounds verbatim, `tmp/` = lane notes and the
PowerShell tooling for live GUI checks, `screenshots/` = before/after evidence. **Read
`.agent/PROGRESS.md` first** in a new session; it is the restore point after a compaction.

Human documentation, RU/EN in parallel (`.md` = RU, `.en.md` = EN): `README.md` = landing page only
(pitch, tab table, safety boundaries, short install); the manual is split per tab group across
`docs/` — `install`, `processes-and-memory`, `disk`, `programs`, `tools-and-settings`,
`data-and-rights`, `internals`, plus `docs/tests.md` (RU only). Touching a feature ⇒ update its
`docs/` page in BOTH languages, and the README only if the pitch or a boundary changed.
