# agent-governor

Why: parallel Claude sessions + their MCP/node/vitest trees grabbed all 32
threads at Normal priority and lagged the whole machine (2026-08-31: 660 procs,
62 node, vitest at ~4 cores mid-game). Isolation, not killing: every claude.exe
tree goes to BelowNormal priority + upper-half CPU affinity (CCD1 on 7950X).
Both inherit to children, so new spawns inside a governed session are governed
from birth; the 5-min task catches new sessions.

- `governor.ps1` — one sweep; `-Report` prints trees without changing anything.
- `install-task.ps1 [-Minutes 5]` — registers task `AgentGovernor` (logon + every 5 min).
- Config `governor.config.json` (optional): RootNames, MinLogicalForAffinity, LogPath.
- Log: `%LOCALAPPDATA%\agent-governor\governor.log` — only actual changes.
- Portability: affinity only applies on >= 24 logical CPUs (MinLogicalForAffinity);
  smaller machines get priority-only. Nothing hardcodes 32 threads.
- Companion measures: `VITEST_MAX_THREADS=8` / `VITEST_MAX_FORKS=8`
  (user env vars) cap test worker pools; proc-reaper handles leaked/idle trees.
