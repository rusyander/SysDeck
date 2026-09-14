# Installing the extension by hand

Step-by-step installation of the "Windows Process Cleaner Downloads" extension in Chrome, Edge, Yandex Browser, Opera
and Opera GX, Brave, Vivaldi and Firefox. What the extension does and how the take-over works is in
[Downloads](downloads.en.md); what it reads is in [Extension privacy](extension-privacy.en.md).

[← Overview](../README.en.md) · [All manual pages](README.en.md) · [🇷🇺 Русский](extension-install.md)

> The extension is not published on the Chrome Web Store or Mozilla Add-ons, so it is installed by hand, as an unpacked
> folder. That takes a minute and is done once.

The extension uses the sign-in the browser already has (the current session's cookies, and only for the address of the
download you started yourself) — it never asks for and never reads site passwords.

## Why one button cannot do it

- **Chrome no longer honours `--load-extension`.** On stable Chrome 152 the command-line switch is ignored even
  together with `--disable-features=DisableLoadExtensionCommandLineSwitch` (verified twice on throwaway profiles). So
  no `.bat` file or script can side-load the extension into the browser.
- **The only automatic route left is an enterprise policy** (`ExtensionInstallForcelist` plus a local CRX). After that
  the browser shows "Managed by your organization" everywhere in its settings — for the whole browser, not just for
  this extension. The program deliberately does not do this.
- **Firefox refuses unsigned add-ons permanently.** In release Firefox and in ESR,
  `about:debugging#/runtime/this-firefox` → "Load Temporary Add-on…" works only until the browser restarts. A
  permanent install of an unsigned add-on is possible only in Developer Edition or Nightly with
  `xpinstall.signatures.required=false`. Firefox is plainly weaker here than the Chromium browsers.

## Step 0. Get the extension folder

This step is the same for every browser, and without it the link to the program will not work: the button not only
unpacks the files, it also makes sure the program is registered in the registry as the native messaging host.

1. Open the program, the **Downloads** page → **download settings** → the **Browsers** section.
2. Make sure the **"Browser integration — the extension hands downloads to the app"** box is ticked (it is on by
   default). The program writes the `NativeMessagingHosts\org.wpc.downloads` keys under `HKCU` for Chrome, Edge and
   Mozilla.
3. Pick a browser in the list and press **"Install the extension…"**.
4. The program unpacks the files and opens their folder in Explorer, and also tries to open the browser's extensions
   page.

There are two folders, one per browser family:

| Browser | Folder |
|---|---|
| Chrome, Edge, Yandex Browser, Opera, Opera GX, Brave, Vivaldi and other Chromium browsers | `%APPDATA%\WindowsProcessCleaner\downloads\extension\chromium` |
| Firefox | `%APPDATA%\WindowsProcessCleaner\downloads\extension\firefox` |

The program's list has only four rows — Google Chrome, Microsoft Edge, Yandex Browser and Mozilla Firefox. For Opera,
Brave and Vivaldi pick any Chromium row (Google Chrome, say): the folder is the same for all of them. If the picked
browser is not installed, the program still unpacks the files and opens the folder, and says the browser was
"not found".

**Do not rename or edit** the files in the folder: the program accepts the extension only with the id
`kfbocmoigekddjcfodbhbiahndahdmal` (Chromium) and `wpc-downloads@windows-process-cleaner` (Firefox). That id comes from
the `key` field in the manifest; editing the manifest changes it, and the program will not accept the extension.

## Chrome — the full sequence

1. Do [step 0](#step-0-get-the-extension-folder) — the `…\downloads\extension\chromium` folder is open in Explorer.
2. Type `chrome://extensions` in Chrome's address bar and press `Enter`. Browsers do not open their internal addresses
   from a link — the address has to be typed or pasted.
3. Turn on the **"Developer mode"** switch in the top right corner of the page. While it is off, the button that loads
   an unpacked extension is not on the page at all.
4. Press **"Load unpacked"** — top left, it appears together with developer mode.
5. In the folder picker choose **the folder itself**,
   `%APPDATA%\WindowsProcessCleaner\downloads\extension\chromium`, and confirm. **Do not go inside it and do not pick
   `manifest.json`** — that is the most common mistake: Chrome expects a folder, not a file.
6. A card named **"Windows Process Cleaner Downloads"** appears in the list (in a browser with Russian as its language,
   "Windows Process Cleaner — загрузки") with the id `kfbocmoigekddjcfodbhbiahndahdmal`.
7. Press the extension's button on the browser toolbar. The popup should say **"Connected to the app"**. If it offers
   **"Allow"** for access to all sites, allow it: without that, cookies are not handed over and sites with a sign-in
   will give the file to the browser only.
8. If you want downloads taken over in private windows, open the extension's details on the same page and allow it in
   incognito, and tick **"In incognito (private) windows too"** in the program's settings.

**Checking it.** In the program: the **Browsers** section, the **Refresh** button — the browser's row shows
"registered" in the "Link to the app" column and "connected" in the "Extension" column, with the time of the last
connection and the extension version. In the browser: the card in the list and "Connected to the app" in the popup.

**Removing it.** On `chrome://extensions` press **"Remove"** on the extension's card. The folder in `%APPDATA%` can be
deleted separately; the registry keys are removed by the **"Browser integration"** box in the program's settings.

> Chrome and other Chromium browsers occasionally remind you that developer mode is on and offer to disable such
> extensions. Accepting that breaks the link to the program, and the extension has to be enabled again.

## Edge

| What | Where |
|---|---|
| Extensions page address | `edge://extensions` |
| Developer mode | a switch in the left column of the page, near the bottom |
| Differences | the button that loads an unpacked extension appears in the main part of the page once developer mode is on; Edge may additionally ask you to confirm the install |
| In the program's list | the **Microsoft Edge** row (Edge has its own registry key, and the program writes that one too) |
| Removing | **"Remove"** on the extension's card |

Every other step is as in [Chrome](#chrome--the-full-sequence), including picking **the folder**, not `manifest.json`.

## Yandex Browser

| What | Where |
|---|---|
| Extensions page address | `browser://extensions` |
| Developer mode | a switch in the upper part of the page |
| Differences | the browser may warn about an extension that is not from its catalogue — the install has to be confirmed |
| In the program's list | the **Yandex Browser** row (it reads Chrome's registry key) |
| Removing | **"Remove"** on the extension's card |

Every other step is as in [Chrome](#chrome--the-full-sequence).

## Opera and Opera GX

| What | Where |
|---|---|
| Extensions page address | `opera://extensions` (the same address in Opera GX) |
| Developer mode | a switch in the upper part of the page |
| Differences | there is no row for it in the program's list; after the install Opera usually hides the extension's button in the extensions menu on the sidebar — pin it to see the connection state |
| In the program's list | not shown; check the extension's popup and the `Chromium` source in the download list |
| Removing | **"Remove"** on the extension's card |

Opera is the one browser here about which the program does not know whether it reads Chrome's registry key. If the
popup says "App not found" while Chrome on the same computer is connected, copy the registry key
`HKCU\Software\Google\Chrome\NativeMessagingHosts\org.wpc.downloads` into the `NativeMessagingHosts` branch of your
Opera build (the default value is the path to
`%APPDATA%\WindowsProcessCleaner\downloads\nmh\org.wpc.downloads.chromium.json`).

## Brave

| What | Where |
|---|---|
| Extensions page address | `brave://extensions` |
| Developer mode | a switch in the top right corner of the page |
| Differences | none; Brave reads Chrome's registry key |
| In the program's list | not shown; the download source is `Chromium` |
| Removing | **"Remove"** on the extension's card |

## Vivaldi

| What | Where |
|---|---|
| Extensions page address | `vivaldi://extensions` |
| Developer mode | a switch in the top right corner of the page |
| Differences | none; Vivaldi reads Chrome's registry key |
| In the program's list | not shown; the download source is `Chromium` |
| Removing | **"Remove"** on the extension's card |

## Firefox

In Firefox the extension is installed differently and lives **until the browser restarts** — that is a Firefox
limitation, not the program's.

1. Do [step 0](#step-0-get-the-extension-folder) with **Mozilla Firefox** picked in the list: the folder will be
   `%APPDATA%\WindowsProcessCleaner\downloads\extension\firefox`.
2. Type `about:debugging#/runtime/this-firefox` in the address bar.
3. Press **"Load Temporary Add-on…"**.
4. Pick the **`manifest.json`** file from the `…\downloads\extension\firefox` folder. Here, unlike in the Chromium
   browsers, the manifest file itself is what you select.
5. The add-on **"Windows Process Cleaner Downloads"** appears in the list of temporary extensions with the id
   `wpc-downloads@windows-process-cleaner`.
6. For private windows, open `about:addons` → this add-on and allow it to run in private windows.

**Checking it.** The **Mozilla Firefox** row in the Browsers section shows "registered" and "connected".

**Removing it.** The **Remove** button on `about:debugging`, or simply restart Firefox — the temporary add-on goes away
on its own.

**A permanent install.** There is none in release Firefox or ESR: signatures are mandatory and cannot be turned off. In
Developer Edition or Nightly set `xpinstall.signatures.required=false` on `about:config`, pack the **contents** of the
`…\downloads\extension\firefox` folder into a zip archive (`manifest.json` must be at the root of the archive), rename
the archive to `.xpi` and install it through `about:addons` → "Install Add-on From File…".

## If it did not connect

- **The wrong folder was picked.** In Chromium browsers pick the `…\extension\chromium` folder itself, not
  `manifest.json` and not the folder above it (`…\extension`). The sign: the browser complains about a missing or
  invalid manifest.
- **Developer mode is off.** Without it the button that loads an unpacked extension is simply not on the page.
- **The "Install the extension…" button in the program was never pressed.** Then the extension is loaded but the
  native messaging host is not registered, and the popup says **"App not found"**. Press the button — and the
  **Refresh** button next to it.
- **Browser integration is off.** The box in the Browsers section removes the registry keys at once — tick it again.
- **The program was moved, or the wrong copy is running.** Only the copy with the default data folder registers the
  link; a portable copy and a copy with `WPC_DATA_DIR` say in the Browsers section that browsers do not connect to
  them. The path to the exe is re-checked when the section is opened — after moving the program, open it once more.
- **The browser was running a different profile.** The extension is installed into the profile that was active. Make
  sure the card is visible in the same profile you download in.
- **The popup says "The app did not accept this extension".** That is a foreign id: the manifest was edited or the
  folder was assembled by hand. Unpack it again with the button in the program.
- **The browser downloads the file anyway.** See the conditions in
  [Downloads](downloads.en.md#downloads-from-the-browser): site and file-type exceptions, `Alt` held on the click, a
  private window without permission, the server giving the program a different file.
