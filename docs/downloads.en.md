# Downloads

The Downloads page: a queue of files from the internet with resume, speed limits, start conditions, a check of the
finished file and moving what was downloaded.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](downloads.md)

> The module is being built in stages. The engine, the background process, this page, taking over downloads from
> Chrome, Edge, Yandex Browser and Firefox through the extension and the built-in BitTorrent client work now. Video
> from web pages comes in the next stage.

## Background process
The window does not download anything itself: a separate process of the same exe does
(`WindowsProcessCleaner.exe --downloads`), so downloads continue while the program window is closed. It runs
**without administrator rights** even when the window has them: downloaded files must belong to you, not to the
administrator.

- The process **starts on demand**, with the first action on downloads ("Add", "Resume" and so on). An open page
  and changing settings do not start it: while it is not running, the list is read from disk.
- When there is nothing to download or seed and the page is closed, the process **exits by itself after a minute**.
- The line above the list shows whether the process is running, how many downloads are active (torrents included),
  how many torrents are seeding, the total speed,
  the limit and what holds the queue (schedule, battery, metered network, idle).
- If unfinished downloads remain in the queue, the process starts at Windows sign-in (a value under
  `HKCU\...\Run` while the queue is not empty; can be turned off in the settings).
- **Stop the process** is in the settings. What was downloaded is kept; downloads continue at the next start.

## Adding a download
- **Add…** opens a dialog. One link per line, `http` and `https` only. Lines without a link are shown as
  skipped; repeats are removed.
- **Paste links** and `Ctrl+V` in the list take links from the clipboard. The clipboard is read only on that
  action; the program does not watch it.
- You can **drag** a link from a browser or an internet shortcut (`.url`) onto the list.

In the dialog:
- **Folder**: empty means "by the settings rules". Below the field you see where the file will go and how much
  space is free there (a warning below 1 GB). "Last used" fills in the folder of the previous add.
- **File name**, **expected hash** (`sha256:…`, `sha1:…`, `md5:…` or just the hex string) and **mirrors** apply
  to a single link only.
- **Start**: now, paused, in N minutes, at a set time or when the computer is idle.
- The **speed limit**, **number of streams** and **priority** of this download.

If a link is already in the list, the program asks whether to download it again; the new file gets a different
name.

## Torrents
Torrents are downloaded by the same background process with its own BitTorrent client, no third-party programs. A
torrent sits in the common list next to ordinary downloads.

Adding a torrent:
- **Torrent…** on the toolbar picks a `.torrent` file.
- `Ctrl+V` or dragging onto the list: magnet links from text and `.torrent` files. The same torrent pasted twice with
  different trackers is added once.
- Double-clicking a `.torrent` file or a magnet link in the browser, if the program is set for them in the settings.

The torrent dialog:
- The file list with check boxes and sizes, **Select all / Clear all**. For a magnet link the list comes from the
  peers, so everything downloads at first; unwanted files are unchecked later on the Files tab.
- **Folder** and the free space on the disk next to the size of the selected files, so a shortage shows at once.
- **Torrent folder name** (the file name for a single-file torrent).
- If the folder already holds this torrent's data, the **check it, download what is missing and seed** box uses it
  instead of a new copy.
- **Start** now or paused, **priority**, **download pieces in order**: a video can be watched while it downloads, but
  seeding gets slower.

Without a dialog, with default options, these become a torrent:
- a `.torrent` file downloaded by an ordinary download. The download record itself stays in the list with a log line;
- a `.torrent` file downloaded by the browser when the program could not take it over;
- a new `.torrent` file in the **watch folder**.

The same torrent is never added twice. A file that turns out to be a web page named `.torrent` stays an ordinary file.
The watch folder is checked every few seconds while the background process runs, top level only. A file still being
written waits for the next pass. A torrent removed from the list does not come back from the folder, a restart
included. With **Remove the .torrent file once added** on, the file goes to the Recycle Bin, and the browser deletes
its own copy. The program keeps the torrent content for itself.

In the list a torrent has its own states: **checking data · N %**, **downloading · peers (seeds) · upload speed**,
**seeding**. Its menu has these instead of the single-link commands:
- **Check the data**, **Ask the trackers for peers**, **Download in order**;
- **Copy the magnet link**;
- **Seed again** for a torrent stopped by ratio or time;
- **New version of the torrent** and, once one is found, **Update the torrent…** — see below.

"Download again", refreshing the link, mirrors and moving are not available for a torrent.

### New version of the torrent
Trackers re-upload torrents: episodes get added, a file gets replaced. The old one stops being seeded, and the new
one has a different info-hash. The program finds the new version and moves the same list item onto it: what was
downloaded stays in place, and only the changes download.

The program takes the topic link from the `.torrent` itself (the comment or publisher URL field) and shows it in the
card as **Topic**. It checks only rutracker on its own, through rutracker's public API `api.rutracker.cc`, without
signing in:
- every 6 hours while **Check rutracker torrents for new versions every 6 hours** is on;
- at once when the tracker answers "Torrent not registered", but no more than once an hour per torrent.

For other trackers (nnmclub, kinozal, rutor and so on) take the new version as a file from the topic page.

When a new version is found:
- a notification arrives, the list state gets **new version available**, and the card gets a **New version** row;
- the program asks the swarm for the new version's metadata through the old version's trackers, with the same
  passkey. Nothing is written to the download folder for that. With no peers, it retries in an hour;
- the torrent itself does not change until you press **Update**.

In the item's menu, under **New version of the torrent**:
- **Check rutracker now** — only for torrents with a rutracker topic;
- **Take it from a .torrent file…** — any tracker; the topic is not compared. A file of the same version is
  refused, and what happens to the files is shown in the window before updating;
- **Open the topic page** — in the default browser.

Opening a `.torrent` of the same topic as a listed torrent but with a different info-hash asks first: **Yes**
updates that torrent, **No** adds it as a separate download. Such a file in the watch folder, or one fetched by a
regular download, becomes the new version without asking, and it still replaces nothing until the button.

**Update the torrent…** opens the "New version of the torrent" window. It shows the info-hash, a summary by files
(stay, move, changed, new, to the Recycle Bin) and what happens to each file. The buttons:
- **Update** stops the torrent, sends the files missing from the new version to the Recycle Bin, moves renamed ones
  and checks the data against the new version. Only changed and new files download, files unticked as "don't
  download" stay unticked, and the folder keeps its name;
- **Don't update** — this version is not offered again; the next one will be;
- **Close** changes nothing;
- **Open the topic page** and **From a .torrent file…** — when a different version is needed.

While the metadata has not arrived, the window says so and **Update** is unavailable. An update is also refused in
these cases (the window states the reason above the buttons):
- it is the same version;
- the torrent became a folder, or a single file — add that one separately;
- the new version is BitTorrent v2 only;
- foreign files sit where the new files go.

While an update runs, the item cannot be started, checked or removed. If the process stops halfway, it finishes the
update on the next start.

## Video
Video on the web almost never sits in a single file: the page serves a playlist, and the clip itself is cut into
hundreds of pieces that have to be downloaded and glued back together. The program does that on its own.

What it can download:
- **HLS** (`.m3u8`) and **DASH** (`.mpd`) streams, live broadcasts included;
- **direct links** to video and audio files;
- **video-site pages** — those need `yt-dlp` (see below).

How to add one:
- in the **Add…** dialog tick **"Download as video"**. For links to `.m3u8` and `.mpd` it is ticked automatically;
- from the browser — the extension shows the streams it found on the page in its popup, and they can be handed to
  the program from there.

What happens next: the program parses the playlist, picks a quality, downloads the pieces (after a break it resumes
where it stopped instead of starting over), then glues them into one file and checks that it plays. While the
recording runs, the list shows **pieces and recorded time** rather than bytes — the file size is only known after
the muxing.

Two entries appear in the item's menu (right click):
- **Watch now** — open the unfinished file in a player without waiting for the end;
- **Stop recording** — for a broadcast: finish the pieces already started and assemble the file. A broadcast has no
  end of its own, a person always stops it.

Subtitles are saved as separate files next to the video (`movie.en.vtt`, `movie.en.srt`); they are not burned into
the clip.

### yt-dlp and Deno
Video-site pages are parsed by `yt-dlp`, a separate open-source program. `Deno` is installed along with it: some
sites require their JavaScript to be executed before they hand out the video link.

Neither one **is part of the program, and neither downloads itself**. They are installed only by the
**"Install yt-dlp…"** button in the downloads settings, "Video" section. Before the first run the checksum of the
downloaded file is verified; if it does not match, the file is not started and the previous version stays in place.
Without them HLS/DASH streams and direct links still work; video-site pages do not.

## Downloads from the browser
The "Windows Process Cleaner — downloads" extension hands downloads started in Chrome, Edge, Yandex Browser and Firefox
to the program. The browser and the program talk through native messaging: the browser itself starts
`WindowsProcessCleaner.exe` and exchanges messages with it on this computer; nothing goes to the network. What the
extension reads is described in [Extension privacy](extension-privacy.en.md).

Setting it up:
1. **Download settings → Browsers → Browser integration** (on by default). The program writes the
   `NativeMessagingHosts\org.wpc.downloads` keys under `HKCU` for Chrome, Edge and Firefox; turning it off removes them at
   once. While the integration is on, the background process checks at every start that the keys lead to the current
   exe.
2. Pick a browser in the list and press **Install the extension…**. The program unpacks the extension into
   `%APPDATA%\WindowsProcessCleaner\downloads\extension\` and opens the browser's extensions page. The steps are shown
   below the list:
   - Chrome, Edge, Yandex Browser: turn on "Developer mode", press "Load unpacked" and pick the folder;
   - Firefox: `about:debugging` → "This Firefox" → "Load Temporary Add-on…" → `manifest.json` from the folder. A
     temporary add-on works until Firefox restarts; only a signed file from the Mozilla add-ons catalogue installs
     permanently.

   Click by click, for every browser (Opera, Brave and Vivaldi included), with the common mistakes explained —
   in [Installing the extension by hand](extension-install.en.md).
3. The "Extension" column shows "connected" with the time of the last connection and the extension version.

Only the copy of the program with the default data folder registers the integration. A portable copy and a copy with
`WPC_DATA_DIR` say in that section that browsers do not connect to them.

What happens to a download:
- When you start a download, the extension shows it to the program: in Chrome, Edge and Yandex Browser before the
  browser starts writing the file, in Firefox right after it starts. With one request carrying the site's cookies the
  program checks whether the server gives it the same file.
- If it does, the download joins the program's queue and starts at once, while the browser cancels its own and removes
  it from its list (Firefox also deletes the started file). A **"Download taken over"** notification appears with the
  **Open downloads** and **Give back to browser** buttons. "Give back to browser" removes the download from the
  program's list (the unfinished part goes to the Recycle Bin), and the browser downloads the file again itself. The
  button works while the download is not finished.
- The download stays with the browser when:
  - interception is off in the settings or in the extension window;
  - the site or the file extension is in the "Leave to the browser…" lists;
  - the window is private and "In incognito windows too" is off;
  - `Alt` is held on the click;
  - the link is neither `http` nor `https`;
  - the server gives the program a different file: a sign-in page, an error, another size;
  - the program did not answer within 5 seconds.

  The browser loses nothing in these cases: its download runs as usual. A torrent file the server will not give to the
  program also stays with the browser.
- The program keeps site cookies only in the background process memory. If after a restart of that process the server
  asks for a sign-in again, the program shows "A fresh link is needed" and, while the browser with the extension is
  open, asks it for the site's fresh cookies by itself.

The extension window (its button on the browser toolbar): connection state, the **Take over downloads** switch (the
same as "Take every browser download" in the settings), **Don't take over on this site** (adds the site to the
program's exceptions), active and queued downloads, speed and **Open downloads**. If the extension has no access to all
sites, the window offers **Allow**: without that access cookies are not passed, and sites that need a sign-in give the
file to the browser only.

The page context menu has **Download the link**, **the image**, **all links** and **the selected links with Windows
Process Cleaner**: the chosen links join the program's queue together with the site's cookies.

## The list
- Columns: name, size, progress, speed, time left, state, source, folder, when added. Under the progress bar a thin
  map shows which parts of the file are already downloaded.
- Filters above the list: **All / Downloading / Waiting / Paused / Done / Errors** with record counts, plus file
  type and source: manual, Chrome, Edge, Yandex Browser, Firefox, another Chromium browser or the torrent watch
  folder. The filters, column widths and the card height are remembered.
- Keys: `Enter` opens a finished file, `Space` pauses or resumes, `Delete` removes the record from the list,
  `Shift+Delete` removes it together with the file, `Ctrl+A` selects all.
- **Pause / Resume / Remove** act on the selected records, **Pause all / Resume all** on the whole queue. The
  **Speed** mode switches between the normal limit, quiet ("spare the network") and unlimited.

The record's context menu:
- **Open the file**, **Show in folder**, **Copy the link**.
- **Download again from zero** deletes the downloaded part after a question.
- **Refresh the link**: for a download whose link expired. What was downloaded is kept if the server returns a
  file of the same size.
- **Add a mirror**: a spare link to the same file.
- **Limit speed**, **Priority**, **Only when the PC is idle**, **Postpone** for 10 minutes, an hour, 3 or 8
  hours.
- **Move to… / Copy to… / Cancel the move.** Moving runs in the background. To another drive the file is first
  copied and verified by SHA-256, and only then does the source go to the Recycle Bin. An interrupted move
  continues at the next start. The "from the internet" mark travels with the file.
- **Compute a hash**: MD5, SHA-1 or SHA-256. The result is copied to the clipboard and compared with the expected
  one if it was given.
- **Remove from the list**: files stay on disk. **Remove with the file** sends the file (for unfinished ones, the
  partial `.wpcpart`) to the Recycle Bin. On a volume without a Recycle Bin nothing is deleted at all.
- **Clear history**: removes records of finished downloads older than 30 days, those whose files are gone, or all
  finished ones. Files are not touched.

## Opening programs
A file that can run code (`.exe`, `.msi`, `.bat`, `.ps1` and the like) is never opened without a question. First
the program checks the digital signature (without going online) and shows:
- the publisher and the signature state: signed, not signed, invalid (the file was changed after signing) or not
  trusted;
- the site the file came from;
- the "from the internet" mark.

The default button is "No". Windows itself starts the file, so SmartScreen and the internet-file warning work as
usual.

## Download card
Below the list are the details of the selected record:
- **Overview**: state, size, file path, site, limit, priority, attempts, last error, expected hash. A finished
  program also shows its signature and the "from the internet" mark.
- **Segments**: a map of the file and a table of its parts. It shows how much was downloaded and how much is
  already safely flushed to disk (resume after a crash starts from there).
- **Network**: link, redirects, mirrors, referrer, User-Agent, resume support, ETag and Last-Modified. Tokens in
  links are hidden. Cookies are never shown, only "present" or "none".
- **Log**: the download's events, newest first.

A torrent has its own tabs instead of Segments and Network:
- **Overview** adds downloaded and uploaded bytes, ratio, peers and seeds, the piece count and a map of verified
  pieces, availability, info-hash, format (v1, v2 or hybrid), the port and whether incoming connections are open.
  When the torrent links to a topic, it also has **Topic** and **New version** rows (available, none with the check
  time, not checked yet).
- **Files**: path, size, how much is done and priority. The context menu has **Download first**, **Download** and
  **Don't download**.
- **Peers**: address, client, how much the peer has, download and upload speed, where the peer came from (tracker,
  DHT, peer exchange, local network, incoming) and encryption.
- **Trackers**: status, seeders and leechers, peers received, when the next request goes and the tracker's message.

While the background process is not running, the torrent card is built from the torrent file: files and sizes are
shown, progress is not.

`Ctrl+C` copies the selected card rows.

## Settings
The **Download settings** button opens them in place of the list. Changes save by themselves. If the process is
running, it applies them at once. If not, they are written to the file, and the queue does not start because of
it.

- **Where to save**: the default folder (Windows Downloads) and rules by site or by extension. Site rules are
  checked first, then extension rules, each group top to bottom. `example.com` means that site only;
  `*.example.com` adds all its subdomains. Downloads are never written into system folders.
- **Ask for the folder** (on by default): a download taken over from the browser first shows the
  **«Where to download?»** window. The list holds the folder from the settings, the Windows Downloads folder and
  every remembered place; **«Browse…»** adds a new one, **«Remove from list»** (or `Delete`) drops one from the
  remembered places at once and for good. **«Remember this folder»** adds the chosen one to that list,
  **«Stop asking»** turns the question off entirely. Until the answer comes the download waits and has not fetched
  a byte; a window closed without an answer leaves it paused with «no folder chosen» — carry on with **Resume**
  or by picking a folder through **Move to…**.
- **Speed**: mode, overall limit, quiet-mode limit (256 KB/s by default), a separate night limit.
- **Queue and connections**:
  - up to 3 downloads at once and 2 from one site;
  - up to 4 streams per file and 8 connections per server;
  - files under 5 MB skip the queue;
  - up to 8 retries after a failure, waiting at most 5 minutes.
- **When to download**:
  - a schedule by days and hours;
  - the whole queue only while idle (5 minutes without input, CPU below 30 %, no fullscreen game or movie);
  - pause on a metered connection and on battery;
  - keep the computer awake while downloading.
- **Safety**: the "from the internet" mark, a Windows Defender scan, a custom User-Agent.
- **Browsers**: browser integration, taking over every download, private windows, sites and file extensions left to the
  browser, the list of browsers and installing the extension. More in
  [Downloads from the browser](#downloads-from-the-browser).
- **Video**:
  - **Quality** — never above the one chosen ("best available" by default); when it is missing, the closest lower one
    is taken. An interrupted recording continues in the quality it started with;
  - **File** — the container: "match the tracks" (MP4, or WebM for WebM tracks), MP4, WebM, M4A or MP3 (the last two
    are audio only);
  - **Save subtitles as separate files next to the video**;
  - **Install yt-dlp…** and **Check for an update**, **Keep yt-dlp up to date** (checked at most once a day).
    More in [Video](#video).
- **Background process and notifications**: continue after signing in to Windows, notifications about finished
  downloads and errors. A notification has a "Folder" button; clicking the notification itself never opens the
  file.
- **Torrents**:
  - **Allow incoming…** asks for administrator rights once and adds the Windows Firewall rule
    `Windows Process Cleaner (BitTorrent)` for this program only. Without incoming connections a torrent still
    downloads and seeds, but only with peers it connected to itself. **Close incoming** stops listening on the port;
    the rule stays, but with no open port it lets nothing in. The line above the buttons shows the firewall state;
  - opening the port on the router (UPnP / NAT-PMP) while incoming connections are allowed;
  - the port (0 means a free one is picked on the first start and remembered) and encryption: prefer, encrypted
    connections only or none;
  - DHT, peer exchange and local network discovery. Private torrents never use them;
  - how many torrents download at once (3 by default, seeding ones do not count), the upload limit, connections in
    total and per torrent, upload slots. Downloading follows the overall download limit;
  - seeding after downloading and when to stop: at a ratio or after N minutes;
  - the watch folder and **Remove the .torrent file once added**, see [Torrents](#torrents);
  - **Check rutracker torrents for new versions every 6 hours** (on by default), see
    [New version of the torrent](#new-version-of-the-torrent);
  - **Open .torrent files** and **magnet links with this program**. Entries go to `HKCU` only; turning one off
    restores the previous app. If another app is already chosen in Windows, the program does not override that
    choice and offers the **Default apps…** button.
- **History**: the same three clearing options as in the menu.

## Where the data lives
Everything is in `%APPDATA%\WindowsProcessCleaner\downloads\`: `settings.json`, `index.json` (queue order), one
file per download in `items\` and the process log `engine.log`. Browser cookies are never written to disk. An
unfinished file sits in its folder with the `.wpcpart` ending. The browser integration adds `nmh\` (manifests for
the browsers), `extension\` (the unpacked extension) and `bridge-<browser>.json` (when the extension connected and its
version). An unfinished video sits next to its future file in a folder ending in `.wpcmedia`: the pieces and a journal
the recording resumes from after a break; once muxed, the folder is removed. `yt-dlp.exe` and `deno.exe` (if they were
installed) live in `tools\`, together with `tools.json` holding their checksums — a file changed after the install is
not started. Torrents keep a copy of the torrent file (`<info-hash>.torrent`), the piece state for a quick resume
(`<info-hash>.resume.json`) and `watch-seen.txt` (which watch-folder files were already handled) in `torrents\`.
A found new version waits there as `update-<id>.torrent`, an unfinished update as `update-<id>.json` (the process
uses it to finish the update after a crash), and `rutracker.json` keeps rutracker's recent answers so it is not asked
again for nothing.

A copy of the program with its own data folder (portable, or redirected with `WPC_DATA_DIR`) gets its own
background process and its own pipe. It neither sees nor touches the main program's queue. More in
[Data and rights](data-and-rights.en.md).
