# Overlay: metrics on top of games and programs

A column with frame rate, load, temperatures and other metrics on top of every window, lag recording and the
NVIDIA frame limiter. Configured on the “Overlay” page.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [Capture](capture.en.md) · [🇷🇺 Русский](overlay.md)

A column in a screen corner with the state of the computer. Toggle it with its hotkey (`Alt+R`; the old
`Ctrl+Alt+F12` is replaced on update by itself), the tray
menu item or the “Show / hide” button (here and on the “Overlay” page); from the command line —
`SysDeck.exe --capture-shot hud`. The column runs as a separate process: it lives while the program
window or Capture's background process is running, and comes back if it was on.

- **On top of everything, in nobody's way.** The window is always on top, the mouse passes through it and it never
  takes focus. It is visible over ordinary windows and over games in windowed or borderless mode. It is not visible
  in exclusive fullscreen DirectX: that would take injecting into the game process, which the program does not do.
- **Borderless full screen game — `Ctrl+Alt+B`.** When the column is not visible (for example in Dark Souls III set to
  “Fullscreen”), switch the game to windowed mode at the monitor resolution and press `Ctrl+Alt+B` in the game: the
  window loses its frame and title and stretches over its monitor, and the column shows on top. Pressing again
  restores the window. Nothing is injected into the game — only the window style and size change. A game running as
  administrator can only be changed by the elevated overlay; the column says so when Windows refuses.
- **What to show — the “Overlay” page.** On the left is a tree of groups: frames, game, CPU, CPU cores, GPU, memory,
  temperatures, disk, network, system information, other, plus HWiNFO and MSI Afterburner sensors when those
  programs provide data. Every row shows its current value and a mark: “(graph)” — the row is drawn as a graph in the column,
  “(number, no graph)” — as a number only, “(text, no graph)” — such a row never has a graph. An unticked numeric row
  has no mark. A graph is turned on with the “Graph” tick of the selected row. A tick puts the row into the column, a group tick —
  all its rows. Cores have “first N”: load and/or frequency of the first 1–16 cores or of all of them.
  On the right, under the name of the selected row or group, an explanation says what the number is, why to watch
  it and at which value it points to a problem (hitches, a CPU bottleneck, running out of VRAM, throttling).
  In a short window the whole page scrolls with the wheel; the row tree scrolls on its own.
- **Basic set.** By default the column holds only the essentials: FPS and 1% low, CPU and GPU load and temperature,
  GPU power, system video memory and RAM, the game's RAM and video memory. Everything else is ticked in by hand.
  The “Presets” button replaces the rows with one of the sets: minimal (frames only), basic, gaming, lag hunt,
  graphics card in full, temperatures and power (position, size and look stay).
- **Three row sets.** “Row set: 1 / 2 / 3” — three independent row lists, say a gaming one and a detailed one.
  The “next row set” hotkey (`Ctrl+Alt+N`) switches them right in the game; empty sets are skipped.
- **Game (active window).** Metrics of the active window's process together with its child processes (browsers and
  launchers keep memory and frames in children; “+N” in the “Window” row is how many are counted): RAM (working
  set), private bytes, video memory and shared GPU memory, CPU and GPU share, read and write per second, threads,
  handles, running time. Switch to another window and the numbers follow it. No administrator rights needed.
- **Every row is set up on its own:** value, graph or both (the graph is a strip under the row, full column
  width, and the label of such a row gets “(graph)”); “Min/avg/max” — three numbers to the right of the value; its own refresh interval (0.25 s to a minute —
  say, memory every 2 s, frequencies every second); value and label colour — by level (by group), from the palette
  or custom; its place in the column — the “Up” and “Down” buttons.
- **Rows come in blocks:** CPU (with cores), GPU, frames, memory, then the rest — game, temperatures, disk, network,
  system information, HWiNFO and Afterburner. Within a block rows keep the order they were ticked in; “Up” and
  “Down” move a row only inside its block. The groups on the page list follow the same order.
- **Highlight thresholds.** Every numeric row has “Yellow at” and “red at” fields; empty means the default, shown
  next to them (say, load 70 / 90 %, CPU temperature 85 / 95 °C). For FPS and 1 % lows lower is bad. “No
  highlighting” turns colour by level off for the row. A row in the red gets a red mark on the left; FPS draws its
  target as a dotted line on the graph.
- **Common settings:** position (four corners, middle left or right, top or bottom centre, custom; top right by
  default), monitor, background opacity, column size, graph window (30 s to 10 min), min/avg/max window (the graph
  window or 30 s to 10 min; reset with `Ctrl+Alt+R`) and “Visible in own screenshots and videos” (off by default:
  Capture's own shots and recordings do not see the column).
- **Drag with the mouse.** The button frames the column and lets it catch the mouse for the moment: drag and
  release — the position becomes “custom”, remembering the monitor and the point on it as fractions of the screen.
- **Look:** font and size, bold labels, labels in group colours, text shadow and “As a line along the screen” — all
  rows on one line, like the strip at the top in games.
- **Preview.** Bottom right shows the whole column and the selected row or group with a graph, drawn by the same
  code as the real column, on live data of this computer. Everything saves by itself after half a second.
- **Where the numbers come from.** CPU (per core too), disk, network and GPU — Windows performance counters, as in
  Task Manager; the main GPU is the one with the most dedicated memory. NVIDIA GPU temperature, power, fan, clocks
  and P-state — its driver library. CPU temperatures, power and voltage, hot spot, memory timings — HWiNFO and MSI Afterburner
  shared memory, when they run; the program installs no driver of its own. HWiNFO's interface language does not matter: sensors are recognised by their English names. Afterburner's card is matched to the
  Windows main GPU by vendor and model, so voltage and fan speed come from the discrete card even when Afterburner
  lists the integrated one first. A sensor without data shows “—”.
- **HWiNFO in the background.** With “Run HWiNFO in the background for sensors”, if HWiNFO is installed and not
  running, the column starts it hidden, sensors only, and closes it on exit, restoring its settings. An HWiNFO you
  started yourself is left alone — it is only read. HWiNFO needs administrator rights, so this works only with the
  next box ticked.
- **With administrator rights.** The box creates a Task Scheduler task (one UAC prompt when ticked); from then on
  the column starts through it elevated without prompts. Unticking removes the task.
- **System information** is the page's second view: CPU model, cores and threads, socket, board and BIOS, memory
  modules (size, type, speed, voltage, part number), GPUs, driver, VBIOS, PCIe link, plus current sensor readings.
  “Copy all” puts the table on the clipboard.
- **Frames (FPS).** Counted for the program in the active window from Windows events (ETW: DXGI — Direct3D
  10/11/12, D3D9, and graphics-kernel events for OpenGL and Vulkan), with no injection into the game. Browsers and
  Electron programs present from a child GPU process — its frames are counted then, and the row keeps the window's program name. Rows: frames
  per second (over the last second), frame time (average over 0.25 s), 1 % and 0.1 % lows (average of the longest
  frames over 30 s, as FPS), stutters per minute (a frame of at least 25 ms and 2.5× the median of the previous
  ones) and the program name. The frame-time graph is drawn per frame — the sawtooth, dips and single long frames
  are visible. When Afterburner has RTSS on and own frames are not counted, the rows take its numbers.
- **Rights for frame counting.** Windows allows an ETW session to administrators and the “Performance Log Users”
  group. The column with administrator rights counts frames right away. Without rights the page shows “Allow frame
  counting (FPS)…”: one UAC prompt adds the user signed in to Windows to that group, and after signing out and back
  in frames are counted without rights. The state is shown in the line above the settings.
- **How frames are presented.** The “How the game presents frames (API)” (DXGI, D3D9, OpenGL/Vulkan), “V-Sync
  and tearing” and “Present mode” (composed or direct) rows come from the same events. “Frames per GPU watt”
  divides FPS by GPU power. “What limits the frame rate” hints what holds frames back: the CPU (one core pegged with the GPU idle), the GPU (load
  near 100 %), a frame limit or vertical sync (FPS exactly at the refresh rate).
- Exclusive fullscreen does not hide frames (the events still come), but the column itself is not visible over
  such a game.

## Lag recording
The “Record lags” button on the “Overlay” page or `Ctrl+Alt+L` in the game: the column starts writing every frame
of the active game and, once a second, system metrics and the processes that ate CPU and disk. A second press stops
the recording (it stops by itself after 2 hours); meanwhile “● Lag recording” with a timer is shown above the column. The result is the folder
`Documents\SysDeck\Lag reports\<date>_<game>\`, opened by itself afterwards (“Lag reports” opens
the parent folder):

- `report.md` — an English summary meant to be handed to an AI or read yourself: average FPS, 1 % and 0.1 % lows,
  stutter count, a table of the longest frames with what the system was doing in that second (load, temperatures,
  hard page faults, the culprit process) and hints about what was found;
- `frames.csv` — every frame time, `system.csv` — per-second metrics, `processes.csv` — the busiest processes per
  second, `events.csv` — foreground window
  changes, processes started, CPU and GPU throttling.

The recording never overwrites existing files; folder links (junctions) are refused.

## NVIDIA frame limiter
The row appears only with an NVIDIA driver installed. “NVIDIA frame limit” writes Max Frame Rate into the driver
profile — the same as in the NVIDIA Control Panel: for all games (empty exe field) or for one exe (“Active game”
fills in the game the frames are counted for). The driver itself holds the limit, nothing is injected into the
game; a game already running needs a restart. “no limit” removes the setting and deletes the profile the program
created. If the driver demands administrator rights, the write goes through one UAC prompt.
