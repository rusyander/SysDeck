# TV streaming

[🇷🇺 По-русски](tv-stream.md) · [Manual](README.en.md)

Video from the computer on a Hisense (VIDAA) TV over the home network: no cables, USB sticks or DLNA. The computer
becomes a small video server, and the TV opens it in the **Media Station X** app or in its built-in browser. Movie
folders are picked in the app window; on the TV every folder is its own section with previews and «Continue watching».

The same window comes in two forms:

- **in SysDeck** — the **«TV streaming»** page in the left menu (after «Downloads»). The «📺 TV streaming» button
  on the «Tools» tab («Media» section) and the item of the same name in the tray icon menu lead there too.
  The server runs as long as SysDeck runs, even with the window closed or another page open;
- **as the standalone PC Stream** (`PcStream.exe`) — for a computer without SysDeck. It lives in the tray and can
  start with Windows.

## Connecting the TV

The **«Connect the TV — step by step»** button opens a window that walks through every step and checks each
one itself: a green tick means the step is done. It opens by itself the first time «TV streaming» is opened. In
PC Stream the «How to connect the TV…» item of the tray menu opens it too.

| Step | What to do | What the window checks |
|---|---|---|
| 1. Folders | «+ Add folder…» or drag a folder into the window | how many folders are added |
| 2. Streaming | «▶ Start streaming» | that the server listens on the port |
| 3. Firewall | «Allow in firewall», once | whether Windows lets inbound connections reach the port on the current network |
| 4. ffmpeg | «Download ffmpeg» — needed for MKV, AVI and previews | whether ffmpeg is found |
| 5. Media Station X | on the TV: the VIDAA button or App Store → find and install **Media Station X** | — |
| 6. Address in MSX | in MSX: Settings → Start Parameter → Setup → enter the address **without http://**, confirm | shows the real address in large type |
| 7. Watch | open MSX: the computer's folders appear on the left | when the TV last connected |
| 8. Fallback | the TV browser, the address with `http://` | — |

The address looks like `192.168.0.101:8090`: the computer's IP on the home network and the port. Buttons next to the
address in the main window: «Copy address», «Open as on the TV» (what the TV will see, in the computer's browser) and
«Allow in firewall». A printable guide with the same steps is the «Guide (PDF)» button in the steps window.

## What plays and how

| Files | How they are served |
|---|---|
| MP4, M4V, WebM, MOV | directly, with instant seeking |
| MKV, AVI, TS, M2TS, WMV, FLV, MPG, 3GP, VOB | transcoded on the fly by ffmpeg; the first seconds go to start-up |

Without ffmpeg only the first row plays and tiles have no frame previews. The «Watching now» card shows which device
watches what and in which mode: «direct» or «transcoding».

## ffmpeg in one click

«Download ffmpeg» fetches the official gyan.dev *essentials* build (~115 MB) and puts `ffmpeg.exe` and `ffprobe.exe`
into an `ffmpeg` folder inside the app's data folder. Nothing is installed and no administrator rights are needed.

- The source is the GitHub releases of the same build (`GyanD/codexffmpeg`): they download dozens of times faster than
  the gyan.dev site. If GitHub is unreachable, the app takes the archive from gyan.dev.
- Before unpacking, the archive is checked by SHA-256: for GitHub the sum comes from the release description, for
  gyan.dev from its `.sha256` file. On a mismatch the archive is discarded and the previous ffmpeg, if any, stays.
- While downloading, the ffmpeg line shows the progress and the button turns into «Cancel». Partial files are deleted.

If ffmpeg is already on the system (in `PATH` or installed with winget), the app finds it, but checks its own copy first.

## Firewall

Without permission Windows does not let the TV reach the computer. «Allow in firewall» asks for administrator rights
once and does two things:

1. removes **blocking** rules for this program — Windows creates them when «Cancel» is pressed in its «Allow access»
   prompt, and they beat any allow rule;
2. adds the rule **«PC Stream TCP <port>»**: inbound TCP to the streaming port, only from addresses of the own subnet
   (`LocalSubnet`), in any network profile.

The rule is deliberately not tied to the «Private» profile: Windows often marks a home network as «Public», and a
private-only rule would then do nothing. The port is still open only to devices on the own subnet. Other rules and
other programs' permissions are left alone. The steps window reads the state without administrator rights and shows
the network name and type.

## Security

- The server answers only devices on the local network; a request from an outside address gets a 403 refusal.
- Only the added folders are visible. A path such as `../` leading outside a folder is rejected.
- Files are only read; nothing is copied or uploaded to the computer.
- The server stops with the «Stop streaming» button or together with the app («Exit» in the tray menu).

## Settings

| Setting | What it does |
|---|---|
| Port | 8090 by default; after a change press «Apply» and enter the new address on the TV |
| Start streaming when the app starts | the server comes up by itself |
| Frame previews on tiles | a frame at 10 % of the video's length via ffmpeg; made once and kept in a cache |
| Start PC Stream with Windows | PC Stream only: starts in the tray at sign-in |

Settings live in `tvstream.json` in the data folder: `%APPDATA%\SysDeck\` for SysDeck
(see [Data and rights](data-and-rights.en.md)), `%APPDATA%\PcStream\` for PC Stream. The `ffmpeg` folder sits there too.

## When something does not work

| Symptom | What to check |
|---|---|
| The TV does not see the server | is the indicator green? are both on the same network? the «Firewall» line in the steps window |
| The address stopped working | the router gave the computer a new IP: the new address is in the window, enter it in MSX again; to keep it fixed, reserve the IP in the router |
| «Port … is already in use» | pick another port in the settings and enter the new address on the TV |
| MKV does not open | no ffmpeg — «Download ffmpeg» |
| Picture missing, sound only | a rare codec: try the TV browser or convert the file to MP4 |
