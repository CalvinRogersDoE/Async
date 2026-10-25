# Changelog

## 0.1.0 (unreleased)

First version.

- Push through the top of any monitor to open a portal into a paired computer; push out the bottom to close it.
- Deliberate-push detection from raw mouse input (strength, minimum duration, pause reset), ignoring window
  drags and games that capture the cursor.
- Portal animation: rim, vortex, shockwave, sparks; configurable colours.
- Sound packs: built-in, Portal, Portal 2 (read from your own install), custom `.wav` files.
- `async-core`: device identity, pairing codes, paired-device list, encrypted peer-to-peer tunnel over iroh.
- Remote Desktop carried through the tunnel; passwords stored in Windows Credential Manager.
- Settings window, pairing window, tray menu, start with Windows.
