# Changelog

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
