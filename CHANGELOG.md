# Changelog

## Unreleased

- **Extend instead of a portal:** each edge can either open a portal or move the pointer and keyboard
  over to the other computer, like a monitor beside yours (Windows to Mac and Linux for now). Copied
  text comes along both ways. Choose it per edge in Settings.
- **Send files and folders:** drop them onto a portal, or drag them across an extending edge. The other
  computer asks where to put them, starting at the folder picked last time.
- Smoother portals into a Mac: mouse moves go out at most every 17 ms, as noVNC and TigerVNC do
  (a fast mouse flooded the Mac and made everything move in jumps). The log says whether the link
  is direct or relayed.

## 0.3.2 (2026-10-25)

- Much smoother portals into a Mac or Linux screen: screen updates are decoded straight into memory
  (about five times faster) and drawn with a cheaper scaler, and the next update is asked for
  before the current one is drawn.
- Uses continuous updates when the screen sharing server offers them, so it streams changes
  without waiting to be asked each time.
- The remote cursor is drawn locally from its shape, so it follows the mouse without waiting
  for screen updates.

## 0.3.1 (2026-10-25)

- Punching into a Mac signs in with the Mac's own user name and password (Apple's screen sharing
  sign-in), which newer macOS needs: the old VNC password was often turned away. No separate VNC
  password to set up any more.
- Clearer messages when a sign-in is turned down.

## 0.3.0 (2026-10-25)

- **Screen edges:** every outer edge of every monitor can open a different paired computer. Settings
  shows your monitors as arranged; click an edge to pick. Pushing through an edge mapped to another
  computer while a portal is open switches to it.
- Pushing out the bottom always closes the portal, whichever edge you came in through; the bottom
  can't be assigned to a computer.
- Linux desktops without built-in screen sharing (Linux Mint/Cinnamon, MATE, Xfce): `install.sh` sets up x11vnc with a password it makes up and hands only to paired devices, so nothing is typed. It sets
  up x11vnc, reachable only from the computer itself.
- `async-core pair-host` and `peers` run alongside the background service.
- **Async for macOS and Linux** (`async-desktop`): punch out of a Mac or Linux computer into your Windows
  PCs, with the same edge pushes, portal, screen-edge map and settings. A menu-bar/tray icon, Keychain or
  keyring for passwords, and it starts at login. The Mac download has a double-click installer.
- Punching into a Windows PC with several screens opens Remote Desktop with that PC's own screen layout
  and shows one screen; each edge can pick which, and the sides slide between them.
- Windows settings window rebuilt with a sidebar, one page per section.
- Rewritten step-by-step guide for Mac and Linux.

## 0.2.0 (2026-10-25)

- The screen edge glows as a push builds, in the colour of what the push will do.
- The remote cursor lands where you punched through.
- Punch into **macOS** (Screen Sharing, through Async's new built-in VNC viewer) and **Linux**
  (GNOME/KDE Remote Desktop, xrdp, or VNC).
- Devices report their OS and screen sharing; Async picks Remote Desktop or VNC automatically.
- macOS and Linux downloads: `async-core` plus `install.sh` (background service, screen sharing
  check, pairing) and a hosting guide.

## 0.1.0 (2026-10-25)

First version.

- Push through the top of any monitor to open a portal into a paired computer; push out the bottom to close it.
- Deliberate-push detection from raw mouse input (strength, minimum duration, pause reset), ignoring window
  drags and games that capture the cursor.
- Portal animation: rim, vortex, shockwave, sparks; configurable colours.
- Sound packs: built-in, Portal, Portal 2 (read from your own install), custom `.wav` files.
- `async-core`: device identity, pairing codes, paired-device list, encrypted peer-to-peer tunnel over iroh.
- Remote Desktop carried through the tunnel; passwords stored in Windows Credential Manager.
- Settings window, pairing window, tray menu, start with Windows.
