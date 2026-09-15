# Privacy: the "SysDeck Downloads" extension

What the browser extension for Chrome, Edge, Yandex Browser and Firefox reads, where it sends it, and what it
never does.

[← Overview](../README.en.md) · [All manual sections](README.en.md) · [🇷🇺 Русский](extension-privacy.md)

## In short

Nothing leaves your computer. The extension talks only to the SysDeck program installed on the
same computer, through the browser's native messaging mechanism (host `org.wpc.downloads`). The extension has
no server, analytics, ads or telemetry, loads no code from the network and makes no network requests of its
own — only the browser uses the network, when it downloads a file.

## What is read, and when

| Data | When | Sent to |
|---|---|---|
| Download address, final address after redirects, source page (referrer), file name, type, size, private-window flag, the browser's User-Agent string | when you start a download in the browser yourself | the program on this computer, so it can decide whether to download the file itself |
| Site cookies | only for the address of that download, or for links you picked in the context menu, and only at that moment | the program on this computer, so the site serves the file the same way it serves the browser. The extension does not store cookies |
| Link addresses on a page | only after you choose "Download all links…" or "Download selected links…", only in the tab where you clicked | the program on this computer |
| Site name of the open tab | when you open the extension's popup, to show the "Don't catch on this site" switch | the program — only the site name, and only when you flip that switch |

## What is never read

Browser history, page contents (except link addresses on your menu command), addresses of other tabs,
bookmarks, passwords, form data.

## What is stored

The extension keeps, in the browser's session storage, only the list of downloads the program asked to be told
about when they finish; it is cleared when the browser closes. Interception rules (on/off, excluded sites and
file extensions) are stored by the program itself on the computer. Data is never sold or shared with third
parties.

## Why the permissions are needed

| Permission | Why |
|---|---|
| `downloads` | see a download you started, cancel it in the browser when the program takes the file, or hand a download back to the browser |
| `nativeMessaging` | talk to the program on this computer |
| `cookies` and access to all sites | read cookies for the download address — the file can be on any site |
| `contextMenus` (`menus` in Firefox) | the "Download…" context menu items |
| `activeTab`, `scripting` | collect link addresses in a tab — only on your menu click |
| `storage` | the list of downloads to follow until they finish |
