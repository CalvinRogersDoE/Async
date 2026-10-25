# Setting up a Mac or Linux computer

This lets you punch **into** a Mac or Linux computer from a Windows PC that runs Async. It takes
about ten minutes. You need:

- the Mac or Linux computer, logged in,
- your Windows PC with Async running (its purple icon is in the system tray, by the clock),
- both on the internet. They don't need to be on the same network.

Pick your computer: **[Mac](#mac)** or **[Linux](#linux)**.

---

## Mac

### 1. Turn on Screen Sharing

1. Click the Apple menu  → **System Settings**.
2. Click **General** in the left column, then **Sharing**.
3. Turn on **Screen Sharing**.
4. Click the **ⓘ** button next to it.
5. Turn on **VNC viewers may control screen with password**, and type a password. Use up to
   8 characters (that's all screen sharing uses). Write it down; you'll type it on your Windows PC.
6. Click **Done**.

### 2. Download and install Async

1. Go to the [latest release](https://github.com/CalvinRogersDoE/Async/releases/latest) and download
   **Async-macos-universal.tar.gz**.
2. Open your **Downloads** folder and double-click the file. A folder called
   **Async-macos-universal** appears.
3. Open **Terminal**: press **⌘ Command + Space**, type `Terminal`, and press **Return**.
4. In Terminal, type `cd ` (the letters c and d, then a space). Don't press Return yet.
5. Drag the **Async-macos-universal** folder from Finder onto the Terminal window. Its location
   gets typed in for you. Now press **Return**.
6. Type `./install.sh` and press **Return**.

The installer sets Async to run in the background and start every time you log in. You don't have
to keep Terminal open afterwards. If macOS shows a note about a new background item, that's Async;
it's fine.

> If macOS says async-core "can't be opened", go to **System Settings → Privacy & Security**,
> scroll down, click **Open Anyway**, and run `./install.sh` again.

### 3. Pair it with your Windows PC

1. The installer asks **Pair with another computer now?** Press **Return**.
2. It shows a code like `ABCD-EFGH`.
3. On the **Windows PC**: right-click the Async tray icon → **Pair a device…** → **Enter a code**,
   and type the code.
4. Both sides say it's paired. Close Terminal.

Then finish on the Windows PC: [step 4 below](#4-finish-on-the-windows-pc).

---

## Linux

### 1. Download and install Async

1. Go to the [latest release](https://github.com/CalvinRogersDoE/Async/releases/latest) and download
   **Async-linux-x86_64.tar.gz** (most computers) or **Async-linux-arm64.tar.gz** (ARM, e.g. a
   Raspberry Pi).
2. Open your **Downloads** folder, right-click the file → **Extract Here**.
3. Right-click the new **Async-linux-…** folder → **Open in Terminal**.
4. Type `./install.sh` and press **Enter**.

The installer sets Async to run in the background and start every time you log in. It doesn't need
your password.

### 2. Screen sharing

The installer checks for screen sharing and tells you what's missing:

| Your desktop | What to do |
|---|---|
| **Linux Mint (Cinnamon), MATE, Xfce** | It asks you to run `sudo apt install x11vnc`. Do that and run `./install.sh` again. It sets up the rest, including a password your paired computers get on their own: nothing to type. |
| **GNOME** (Ubuntu, Fedora, Pop!_OS) | **Settings → System → Remote Desktop → Desktop Sharing**: turn on Remote Desktop and Remote Control, and note the user name and password shown there. |
| **KDE Plasma 6** | **System Settings → Remote Desktop**: turn it on and add a user and password. |

Screen sharing set up this way only answers on the computer itself. Nothing is opened to your
network; your Windows PC reaches it through Async's encrypted connection.

### 3. Pair it with your Windows PC

1. The installer asks **Pair with another computer now?** Press **Enter**.
2. It shows a code like `ABCD-EFGH`.
3. On the **Windows PC**: right-click the Async tray icon → **Pair a device…** → **Enter a code**,
   and type the code.

---

## 4. Finish on the Windows PC

1. Right-click the Async tray icon → **Settings…**
2. Under **Devices**, find the new computer. Click **Password…** next to it and enter:
   - **Mac:** the Screen Sharing password from step 1. Leave the user empty.
   - **Linux with x11vnc:** nothing; Async handles it. The card just says "ready, nothing to enter".
   - **Linux with GNOME or KDE Remote Desktop:** the user name and password from its settings.
3. Under **Screen edges**, click the edge of a screen you want to punch through, and choose the
   new computer.
4. Click **Save**.

Now push your mouse hard through that edge. Push out the opposite edge to come back.

The other computer must be awake and logged in: screen sharing shows the desktop you're logged into.

---

## Something not working?

| What you see | What to do |
|---|---|
| The device shows **· screen sharing off** in Settings | Step 1 (Mac) or step 2 (Linux) isn't done yet. |
| **This computer only allows Apple's own screen sharing login** | On the Mac, turn on *VNC viewers may control screen with password* (Mac step 1.5). |
| The device shows **· offline** | Async isn't running on it, or it's asleep or offline. Restart the computer; Async starts on its own at login. |
| A wrong password message | Click **Password…** again in Settings and re-enter it. |

### Handy commands

Linux (in a terminal):

```sh
async-core peers                        # list paired devices
async-core pair-host                    # pair another device
journalctl --user -u async-core         # Async's log
./install.sh --uninstall                # remove Async (from the unpacked folder)
```

Mac (in Terminal):

```sh
"$HOME/Library/Application Support/Async/bin/async-core" peers
"$HOME/Library/Application Support/Async/bin/async-core" pair-host
cat ~/Library/Logs/async-core.log
```

To keep Async running on Linux while nobody is logged in: `loginctl enable-linger "$USER"`.
