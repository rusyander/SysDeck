# Capture: screenshots and video by hotkeys

The Capture page: a screenshot or a video with sound of a region, the screen or a window by a hotkey, a notification with a thumbnail, files sorted into per-program folders.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](capture.md)

> The module is being built in stages. Screenshots, the screenshot editor and video recording with sound work now. The
> gallery is still in development: its shortcut is listed, but it is not taken from other programs yet.

## Background process
Shots are taken by a separate process of the same exe (`WindowsProcessCleaner.exe --capture`), so hotkeys work
even while the app window is closed. The process runs **without administrator rights**, even if the window has
them: that way it sees ordinary windows and needs no UAC. Nothing is uploaded anywhere.

- The process **starts together with the app window** — hotkeys work right away, nothing needs pressing.
- **Start / Stop** — buttons at the top of the page; the line above them shows whether the process is running.
  Stop is remembered: until Start is pressed, the process is started neither with the window nor from the menu.
- **Start with Windows** — the `WindowsProcessCleaner.Capture` value in
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`: the process starts at sign-in with ordinary rights.
- **Region screenshot** and **Region video** — check that everything works without pressing the shortcut.
- The tray icon menu has a Capture submenu: region, screen or window screenshot, region or screen video, stop
  recording, screenshots and videos folders, settings. If
  the background process is not running, the menu starts it and runs the command; if it was stopped with the
  button, the menu opens the page instead of starting it: otherwise the process would silently take the hotkeys.

## Hotkeys
By default — the same keys as VK Play GameCenter, so there is nothing to relearn:

| Action | Shortcut |
|---|---|
| Region screenshot | `F3` |
| Screen screenshot | `F4` |
| Region video: start / stop | `F7` |
| Active window screenshot, screen video, pause recording, gallery | not assigned |

- Click a field and press the shortcut. `Backspace` clears it, `Esc` keeps the old one. While a field has focus,
  the background process releases its keys so the key press reaches the field.
- **A taken shortcut.** Windows gives a key to one program only. If another program holds it, a warning appears
  next to the field, naming the owner when it can be guessed (Windows Snipping Tool, Xbox Game Bar, NVIDIA App,
  VK Play GameCenter, the Folder sizes panel). The background process retries every 15 seconds: once that program
  exits or releases the key, the shortcut starts working without a restart.
- **Import from VK Play GameCenter** — reads `%LOCALAPPDATA%\GameCenter\GameCenter.ini`: screenshot and video
  shortcuts and the save folders (a folder is taken only if it exists). On the module's first start the import
  runs by itself when the file is found. While GameCenter is running it holds its keys — the page warns about it.
- **Default shortcuts** — restore the table above.
- **Don't give PrtScn to the Windows Snipping Tool** — needed only if you assigned a shortcut with `PrtScn`.
  Changes the Windows setting “Use the Print screen key to open screen capture”
  (`HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled`); unticking restores the previous value.

## Selecting a region
The screen freezes and dims; the selected part keeps its original brightness. Selection spans all monitors at
once and works in physical pixels at any display scale.

| To do | How |
|---|---|
| Select a region | drag with the mouse; a label shows the size in pixels |
| Take a whole window | click it: the window under the cursor is highlighted without the invisible Windows border |
| Take a whole monitor | double-click |
| Previous region | `L` |
| Change the selection | drag the handles on corners and sides |
| Move the selection | drag inside the frame; while selecting — hold `Space` |
| Aspect ratio | `Shift` while selecting; `A` cycles 1:1 → 16:9 → 4:3 → 21:9 |
| Fine adjustment | arrows move by 1 px (`Shift` — by 10), `Ctrl` + arrows resize |
| Pixel color | the magnifier next to the cursor shows the color and coordinates; `C` copies the color as `#RRGGBB` |
| Done | `Enter` — the “After a shot” action, `Ctrl+C` — clipboard, `Ctrl+S` — file, `E` — the editor, or the panel buttons under the selection |
| Cancel | `Esc`; the right mouse button first clears the selection, a second press closes |

The panel under the selection ends with two labelled buttons: **Save** (blue, same as `Enter`) and **Cancel** (same
as `Esc`; turns red under the cursor). For a video, **Start recording** replaces Save. If the labelled panel is
wider than the monitor, both buttons shrink to icons; every button has a tooltip with its key.

The overlay appears about 0.15 s after the key press even across three monitors of 23 Mpx in total: the frame is
grabbed straight into memory and put on screen with a single GDI operation, without an intermediate copy.

Switching to another window (`Alt+Tab`, a popup) cancels the selection, as in the Windows Snipping Tool.

## Annotating right on the selection
Once a region is selected, the panel under it carries tools besides the actions: box, ellipse, arrow, line, pen,
highlighter, text, step number, five colors and “Another color…”. Picking a tool starts drawing right over the
selection, with no separate window. The shot itself is not changed: annotations lie on top of it.

- **Several annotations in different colors.** A color applies to the next shape: a red box stays red when green
  is picked after it. Only text being typed takes the new color.
- **Editing what is drawn.** The handles of the shape under the cursor resize it with any tool. A click on a shape
  without dragging selects it; `Delete` or the “Delete” button removes it. Undo and redo — `Ctrl+Z` / `Ctrl+Y` or
  the panel buttons.
- **The region still changes after drawing.** The selection handles grow or shrink the shot while annotations stay
  at their places on the screen. Those left outside the edge are not lost and come back when the region grows
  again.
- **Done.** `Enter` or “Save” — as with a plain shot, by the “After a shot” setting; `Ctrl+C` — to the clipboard
  with the annotations; `E` — into the screenshot editor with the drawing and its undo history (blur, crop and
  line width live there). The chosen color and tool are remembered with the editor settings.
- **Cancel.** `Esc` first deselects the shape, a second press closes the overlay without saving. The right mouse
  button resets nothing once annotations are started. There is no record button with annotations: a video would
  capture the screen, not the annotations.
- **A whole monitor** can be annotated too: a double-click takes the monitor and the panel sits inside the
  selection. `F4` shoots the screen at once, with no overlay.

## Saving
- **Folder** — `Pictures\Screenshots` by default. “Choose folder…”, “Open folder”, “Default”.
- **Sort into per-program folders** — a shot goes into a subfolder named after the program whose window was under
  the selection: `Chrome`, `Visual Studio Code`, a game title. A shot of the desktop or the taskbar goes into
  `Desktop` (`Рабочий стол` with the Russian interface).
- **File name** — a template: `{app}` is the program, anything else in braces is a date/time format
  (`{yyyy-MM-dd_HH-mm-ss}`, `{dd.MM.yy}`, `{HHmm}`). Default `{app}_{yyyy-MM-dd_HH-mm-ss}`; an example is shown under
  the field. Characters not allowed in file names are replaced; if the file exists, `_2`, `_3`… is appended.
- **Format** — PNG (lossless) or JPG with a quality choice.
- **After a shot** — save and copy, save only, copy to the clipboard only, or open the
  [editor](#screenshot-editor) without saving anything.

## Shot
- **“Screen screenshot” captures** the monitor under the cursor, all monitors, or the active window.
- **Delay** — none, 3, 5 or 10 seconds: time to open a menu or a tooltip.
- **Include the mouse cursor** and **shutter sound** — optional, off by default.

## Video
Recording goes to MP4 (H.264, HEVC or AV1) on the graphics card when its encoder passes a test encode, otherwise on
the Windows software encoder — the notification then says so.

- **Starting.** `F7` opens the same selection as a screenshot: drag a region, click a window or double-click a
  monitor, then `Enter` or the “Start recording” button. “Screen video” (no shortcut by default) records the monitor
  under the cursor right away. While selecting a screenshot, `R` turns the selected region into a recording.
- **Stopping.** Press the same key again (either recording key), the Stop button on the recording panel, a click on
  the red tray icon, or “Stop recording” in the menu. Pause — the panel button, the red icon's menu or its own
  shortcut; paused time does not go into the file, and sound and picture stay in sync.
- **The recording panel** with a timer and the file size sits under the region (above it when there is no room), and
  a thin frame outlines the region. Neither the panel, the frame nor notifications get into the video. On systems
  older than Windows 10 2004, where a window cannot be excluded from capture, a panel that would cover the recorded
  region is not shown — the tray icon remains.
- **A window recording** follows the window itself: it can be moved and covered, and the video keeps the window's
  size at the start. If the window is closed or minimized, the recording is saved and stops.
- **Codec, encoder, quality, frames per second** (30 or 60), **size** (as the source, at most 1080p or 720p — only
  downscaling), a **countdown** before the start (3 or 5 seconds), **at most** (10–120 minutes). “Check encoders”
  shows which of them actually work on this PC.
- **If a recording is interrupted** (a crash, a power loss): H.264 and AV1 are written in fragments, and the next
  start of the background process repairs the file — the notification says “recovered after an interrupted
  recording”; the original goes to the Recycle Bin. HEVC is written as a plain MP4 that cannot be recovered after an
  interruption — the page warns about it.
- The recording stops and saves the file by itself when less than 1 GB is left on the disk.
- Exclusive fullscreen games and protected video give a black picture — the notification suggests switching to
  borderless window mode.
- **Include the mouse cursor in videos** — on by default.

## Sound in videos
- **System sound** — everything you hear. **When recording a window — only its sound**: the sound of the window's
  process with its child processes (Windows 10 2004 and newer; older systems record all sound, and the notification
  says so).
- **Microphone** — the device (Windows' default by default), volume from 50 to 300 %, **mono** (the voice in both
  channels). A disconnected headset stays in the list as “not connected”, and the recording then takes the default
  microphone. “Test the microphone” shows the signal level for 15 seconds.
- If microphone access is blocked in Settings → Privacy → Microphone, the page warns, and the video is recorded
  without the microphone. A busy or missing device does not stop the recording either: the notification gives the
  reason.
- **Separate tracks for editing**: track 1 — mixed sound (any player plays it), 2 — system, 3 — microphone.

## Notification
After a shot a card with a thumbnail appears in the bottom-right corner of the monitor where the shot was taken.

- For a video the title is “Video saved · duration · size”, and the recording warnings, if any, replace the file
  name. The copy button puts the file itself on the clipboard; videos have no editor.
- Clicking the card opens the file. The main button is **Gallery**: it opens the common folder of all screenshots
  (for a video, the videos folder) in Explorer, not the program's subfolder. The app has no gallery of its own yet.
  Other buttons: **edit**, **copy** to the clipboard, **show in folder**, **move to Recycle Bin**, **close**. The
  deletion happens when the card closes, and until then “Undo” cancels it.
- The card stays while the cursor is over it. Up to three cards are shown per monitor.
- The card does not get into later screenshots or screen recordings (Windows 10 2004 and newer; on older systems
  it hides for the moment of the shot).
- **Keep for** — 3 to 15 seconds. Errors are always shown and stay at least 8 seconds, even with notifications
  turned off.
- **Settings from early builds.** In a `capture\settings.json` without a `Version` field, “notifications off” may
  not have been the user's choice, and no card appeared after a shot at all. Such a file turns notifications back
  on when first read; the next save writes the version, and notifications turned off after that stay off.
- **In a fullscreen game — show after leaving it**: the card does not pop up over the game.

## Screenshot editor
It opens three ways: the “Edit” button (`E`) on the panel under the selection, the “open the editor” value of the
“After a shot” setting, or the “Edit” button in the notification. In the first two cases the shot is not saved
anywhere yet; from the notification the file is opened, and the editor does not keep it locked. The window appears
on the monitor where the shot was taken and runs in the background process, so closing the app window leaves it
alone.

| Tool | Key | What it does |
|---|---|---|
| Select | `V` | a click selects a shape, dragging moves it, handles resize it, a double-click on text edits it |
| Pen, highlighter | `P`, `H` | freehand drawing, the highlighter is translucent; `Shift` for a straight line |
| Line, arrow | `L`, `A` | `Shift` snaps to 45° |
| Rectangle, ellipse | `R`, `O` | `Shift` for a square or a circle |
| Step number | `N` | a click places a circle with the next number: 1, 2, 3… |
| Text | `T` | `Enter` — done, `Shift+Enter` — new line, `Esc` — cancel |
| Blur, pixelate | `B`, `X` | hide a password, a card number, a face |
| Crop | `C` | select what to keep; `Enter` or a double-click crops |

- **Blur and pixelation** are baked into the saved file: none of the original pixels under the area remain in it.
  While the shot is open in the editor, the area can still be moved or deleted.
- **Color** — the palette on the panel or “Another color…”; **width** — three buttons; **fill** (`F`) paints the
  rectangle and the ellipse solid and draws a backing under text; `A−` / `A+` — text size. With a shape selected,
  all of these change that shape. The chosen color, width, text size, fill and tool are remembered for next time.
- **Rotate** left and right — buttons on the panel.
- **Undo** — `Ctrl+Z`, **redo** — `Ctrl+Y` or `Ctrl+Shift+Z`; crop and rotation can be undone too. `Delete`
  removes the selected shape, arrows move it by 1 px (`Shift` — by 10).
- **Zoom**: `Ctrl`+wheel, `Ctrl+0` — fit, `Ctrl+1` — 100 %, `Ctrl` `+` / `−`; clicking the percentage in the status
  bar toggles fit and 100 %. Pan with the middle mouse button or while holding Space; the wheel scrolls
  (`Shift`+wheel — horizontally).
- **Copy** (`Ctrl+C`) puts the shot with its annotations on the clipboard.
- **Save** (`Ctrl+S`). A new shot is saved into the screenshots folder by the name template, like any other shot.
  If the shot was opened from a file, the editor asks once whether to replace it: the new file is first written
  next to it, the old one goes to the Recycle Bin, and only then does the new one take its place. “No” saves under
  another name. **Save as…** (`Ctrl+Shift+S`) — PNG or JPG anywhere. **Folder** shows the file in Explorer, or the
  screenshots folder for an unsaved shot.
- Closing the window with unsaved changes offers to save them. The “Stop” button on the page asks this in every open
  editor; “Cancel” in any of them keeps the background process running.

## Where the data is
The module keeps its settings apart from the main ones — in `%APPDATA%\WindowsProcessCleaner\capture\`:
`settings.json`, `status.json` (pid, rights and shortcut state of the running process — the page reads its status
from it), `recording.txt` (the path of the running recording; left behind by a process, it means the recording was
interrupted and will be repaired), `video-encoders.cache` (the test-encode result, so it is not repeated at every
start) and its own `crash.log`. Videos are saved to “Videos\Captures” by default. The “Data folder” button at the bottom of the page opens it. More in
[Data and rights](data-and-rights.en.md).

## Compatibility
Windows 10 and 11 on any PC: everything that depends on the system version is checked at start. Button icons come
from the Segoe MDL2 Assets font; if it is missing, text is shown instead. Video is recorded through
Windows.Graphics.Capture (Windows 10 1903 and newer); where it is missing, the screen is recorded through Desktop
Duplication, and recording a single window is unavailable.
