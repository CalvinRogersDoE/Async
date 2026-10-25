# Punching into a Mac or Linux computer

This download lets a **Mac or Linux** computer be punched *into* from a Windows PC running Async.
(Punching *out* of a Mac or Linux computer comes later, with Async's own screen streaming.)

It has three parts:

| File | What it is |
|---|---|
| `async-core` | Async's engine: pairing, and the encrypted tunnel to your other computers |
| `install.sh` | Installs the engine for you and starts it in the background at every login |
| `README.md` | This guide |

Async carries the picture with the screen sharing your computer already has (Screen Sharing on a
Mac, Remote Desktop or VNC on Linux), but only through its own encrypted tunnel and only for
devices you've paired. You don't need to open anything to the network.

## 1. Turn on screen sharing

### macOS

1. **System Settings → General → Sharing →** turn on **Screen Sharing**.
2. Click the **(i)** next to it, turn on **VNC viewers may control screen with password**, and set
   a password. Async uses this password; your Mac login password isn't needed.

### Linux

Pick the one for your desktop:

| Desktop | How | Async connects with |
|---|---|---|
| **GNOME** (Ubuntu, Fedora, Pop!_OS…) | **Settings → System → Remote Desktop → Desktop Sharing**: turn on Remote Desktop and Remote Control, and set the user name and password shown there | Remote Desktop |
| **KDE Plasma 6** | **System Settings → Remote Desktop**: turn it on and add a user | Remote Desktop |
| **Anything else** | Install a VNC server for your display (`wayvnc` on Wayland, `x11vnc` on X11) on port 5900, or `xrdp` (`sudo apt install xrdp`; note it opens a separate session rather than your current screen) | VNC or Remote Desktop |

When a computer offers both, Async prefers Remote Desktop because it's faster.

## 2. Install

Open a terminal in the folder you unpacked and run:

```sh
./install.sh
```

No `sudo` needed; everything goes into your home folder and runs as you. The script:

- copies `async-core` into place (`~/.local/bin` on Linux, `~/Library/Application Support/Async/bin` on macOS),
- sets it to run in the background now and at every login (a systemd user service on Linux, a
  LaunchAgent on macOS),
- checks that screen sharing is on and tells you if it isn't,
- offers to **pair** right away.

> **macOS:** async-core isn't signed by Apple yet. The script clears the "downloaded from the internet"
> flag on it so macOS lets it run. If macOS still blocks it, open **System Settings → Privacy &
> Security** and click **Open Anyway**.

## 3. Pair with your Windows PC

When the script asks, answer **Y** and it shows a code like `ABCD-EFGH`. On the Windows PC, open the
Async tray icon → **Pair a device… → Enter a code** and type it in.

You can pair more devices any time:

```sh
async-core pair-host        # Linux (~/.local/bin is on your PATH on most distributions)
"$HOME/Library/Application Support/Async/bin/async-core" pair-host   # macOS
```

## 4. Save the sign-in on the Windows PC

In Async's **Settings**, next to the new device, click **Password…**:

- **Mac:** leave the user empty and enter the VNC password from step 1.
- **Linux, Remote Desktop:** enter the user name and password you set in Desktop Sharing (GNOME) or
  Remote Desktop (KDE).
- **Linux, VNC:** leave the user empty and enter the VNC password, if you set one.

Then select it under **Punch into** and push your mouse through the top of a screen.

## Handy commands

```sh
async-core peers                 # list paired devices
async-core describe <device-id>  # what a paired device is and which screen sharing it offers
async-core --version
./install.sh --uninstall         # stop and remove it (paired devices are kept)
```

Logs: `journalctl --user -u async-core` on Linux, `~/Library/Logs/async-core.log` on macOS.

### Keep it running when you're logged out (Linux)

A systemd user service stops when you log out. To keep it running, so that you can reach a
computer sitting at its login screen if its screen sharing allows that:

```sh
loginctl enable-linger "$USER"
```

## Troubleshooting

- **"that device has no screen sharing turned on"** on the Windows side: step 1 isn't done, or the
  sharing service isn't listening on `127.0.0.1` port 3389 (Remote Desktop) or 5900 (VNC).
- **"This computer only allows Apple's own screen sharing login"**: on the Mac, turn on *VNC viewers
  may control screen with password* (step 1.2).
- **It can't find the other device**: both computers need internet access (or the same network) and
  Async running. Connections go direct when possible and through an encrypted relay otherwise.
