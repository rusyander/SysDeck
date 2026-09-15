# Torrents

The own BitTorrent client of the “Downloads” page: adding torrents and magnet links, seeding, torrent updates.

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [Downloads](downloads.en.md) · [🇷🇺 Русский](torrents.md)

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
- a `.torrent` file downloaded by an ordinary download;
- a `.torrent` file downloaded by the browser when the program could not take it over;
- a new `.torrent` file in the **watch folder**.

The same torrent is never added twice. A file that turns out to be a web page named `.torrent` stays an ordinary file.
The watch folder is checked every few seconds while the background process runs, top level only. A file still being
written waits for the next pass. A torrent removed from the list does not come back from the folder, a restart
included. The `.torrent` file is the delivery slip, not the goods: **Remove the .torrent file once added** is on out of
the box, so the moment the torrent is added the file goes to the Recycle Bin and its download leaves the list (the
browser deletes its own copy). Clear the checkbox to keep `.torrent` files — the download then stays in the list with a
log line. The program keeps the torrent content for itself.

With **Ask where to save** on, the question is asked here too — about the payload, not about the `.torrent` file: until
it is answered the torrent stands still and fetches nothing. Cancelling drops such a torrent from the list: nobody put
it there by hand, so clicking the link on the tracker again starts the conversation from scratch.

In the list a torrent has its own states: **checking data · N %**, **downloading · peers (seeds) · upload speed**,
**seeding**. Its menu has these instead of the single-link commands:
- **Check the data**, **Ask the trackers for peers**, **Download in order**;
- **Copy the magnet link**;
- **Seed again** for a torrent stopped by ratio or time;
- **New version of the torrent** and, once one is found, **Update the torrent…** — see below.

"Download again", refreshing the link, mirrors and moving are not available for a torrent.

## New version of the torrent
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
