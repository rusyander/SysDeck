# Displays

[🇷🇺 По-русски](displays.md) · [Manual](README.en.md)

The “Displays” page lists every display Windows knows about and lets you turn any of them on or off with a
checkbox, without opening the system settings. The **“Active displays”** button on the “Home” page leads here.

## What the list shows

| Column | What it shows |
|---|---|
| Display | how the monitor is named in Windows display settings |
| Resolution | resolution and refresh rate of a display that is on; a dash for one that is off |
| Connector | HDMI, DisplayPort, DVI and so on |
| Graphics card | the card that drives this display |
| Note | “primary”, “on” with a name like `\\.\DISPLAY1`, or “off in Windows” |

The checkbox in a row is the state of the display. Clear it and Windows stops keeping a desktop for that display;
tick it and the desktop comes back. The list is re-read every time you open the page and after every toggle, so it
shows what is true now, not what was true when the program started.

The line at the bottom counts the displays that are on. If two graphics cards composite the desktop, it also points
at “Disable MPO” on the [“Scripts”](scripts.en.md) page: that is what cures cursor stutter when crossing screens.

## Why turn off a display that is already switched off

Turning a display off here is done **in software**: the path “graphics card → monitor” stops being active, the
cable stays in the socket. This matters because a TV switched off with its remote goes into standby but keeps the
HPD line asserted. Windows never sees it detach and keeps compositing a desktop surface for it: the card renders a
screen nobody looks at, and with mixed refresh rates that also holds the card's memory at a high clock.

The same checkbox brings the display back. If by then the TV is truly gone (the cable was pulled), its row
disappears from the list — and returns once Windows sees it again.

## What the page refuses to do

- **The last display that is on** cannot be turned off: the desktop would be left without a screen.
- **The primary display** cannot be turned off: Windows would move it on its own, and there is no telling where.
  Make another display primary in Windows settings first.

In both cases the checkbox returns to its previous state and the reason appears in the line below the buttons.

## How this differs from the “HDMI TV switch” on the “Scripts” page

The “HDMI TV switch” is a script with shortcuts and commands. It does the same thing, but from outside the program
and through the `DisplayConfig` PowerShell module, which has to be installed from the PowerShell Gallery. The
“Displays” page needs nothing installed: it talks to Windows directly (Connecting and Configuring Displays,
`QueryDisplayConfig` / `SetDisplayConfig`). The script stays for scheduled tasks and command-line use.

## Rights

The page needs no administrator rights: switching displays is available to an ordinary user. No UAC prompt appears.
