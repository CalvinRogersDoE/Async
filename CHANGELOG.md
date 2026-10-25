# Changelog

## 0.3.0 (unreleased)

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
