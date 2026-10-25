# Async on a Mac or Linux computer

On a Mac or Linux computer, Async does two things:

- **Punch out:** push the mouse through a screen edge and a portal opens onto one of your Windows PCs.
- **Be punched into:** your other computers can punch into this one.

It takes about ten minutes. You need the computer, logged in, and an internet connection. Your
computers don't need to be on the same network.

Pick your computer: **[Mac](#mac)** or **[Linux](#linux)**.

---

## Mac

### 1. Install

1. Go to the [latest release](https://github.com/CalvinRogersDoE/Async/releases/latest) and download
   **Async-macos-universal.tar.gz** (it works on Apple Silicon and Intel Macs).
2. Open your **Downloads** folder and double-click the file. A folder called **Async-macos-universal**
   appears.
3. Open that folder and double-click **Install Async.command**. A Terminal window opens and does the
   rest; you don't type anything.

> **If macOS says it "can't be opened" or "Apple could not verify" it:** Async isn't signed by Apple
> yet. Click **Done**, open **System Settings → Privacy & Security**, scroll down to the message about
> *Install Async.command*, click **Open Anyway**, and double-click it again.

When it's done, a **purple ring** sits in the menu bar at the top of the screen: that's Async. It
starts by itself every time you log in.

### 2. Allow Input Monitoring

macOS asks whether **async-desktop** may use **Input Monitoring**. Allow it: that's how Async notices
you pushing the mouse through a screen edge (it only looks at mouse movement). If you missed the
question: **System Settings → Privacy & Security → Input Monitoring** → turn on **async-desktop**.

### 3. Let your other computers in (optional)

Skip this if you only want to punch *out* of the Mac.

1. **System Settings → General → Sharing →** turn on **Screen Sharing**.
2. That's it. Your Windows PC signs in with the name and password you log into this Mac with. If the
   **ⓘ** next to Screen Sharing lists who's allowed in, make sure that includes you (or pick
   **All users**).

Then go to **[Pair and set up](#pair-and-set-up)**.

---

## Linux

Works on X11 desktops: Linux Mint (Cinnamon), Ubuntu on Xorg, MATE, Xfce, KDE on X11. (Wayland comes
later.)

### 1. Install

1. Go to the [latest release](https://github.com/CalvinRogersDoE/Async/releases/latest) and download
   **Async-linux-x86_64.tar.gz** (most computers) or **Async-linux-arm64.tar.gz** (ARM, e.g. a
   Raspberry Pi).
2. Open your **Downloads** folder, right-click the file → **Extract Here**.
3. Right-click the new **Async-linux-…** folder → **Open in Terminal**.
4. Type `./install.sh` and press **Enter**. It doesn't need your password.

A **purple ring** appears in the tray: that's Async. It starts by itself at every login, and there's
an **Async** entry in your applications menu.

### 2. Screen sharing

So your other computers can punch into this one. The installer tells you what's missing:

| Your desktop | What to do |
|---|---|
| **Linux Mint (Cinnamon), MATE, Xfce** | Run `sudo apt install x11vnc`, then `./install.sh` again. It sets everything up, including a password your paired computers get on their own: nothing to type. |
| **GNOME** (Ubuntu, Fedora, Pop!_OS) | **Settings → System → Remote Desktop → Desktop Sharing**: turn on Remote Desktop and Remote Control, and note the user name and password shown there. |
| **KDE Plasma 6** | **System Settings → Remote Desktop**: turn it on and add a user and password. |

Screen sharing set up this way only answers on the computer itself. Nothing is opened to your network;
your other computers reach it through Async's encrypted connection.

---

## Pair and set up

### Pair

Click the purple ring → **Settings…** → **Pair a device** → **Show a code**. On the other computer,
enter that code (on Windows: tray icon → **Pair a device… → Enter a code**). Or the other way round:
show the code on the other computer and type it here.

### Punch out of this computer

1. In Async's **Settings**, next to your Windows PC, type the Windows user and password to sign in
   with. (If you sign into Windows with a Microsoft account, that's its email and password, not your
   PIN.) The password is kept in this computer's keychain only.
2. Under **Screen edges**, click the top or a side of a screen and pick the computer it opens. If that
   PC has several screens, pick which one to show. The bottom is always the way back.
3. Click **Save**, then push the mouse hard through that edge.

The Windows PC needs **Remote Desktop** on (**Settings → System → Remote Desktop**, Windows Pro).
While you're connected, its own screen shows the lock screen; that's how Remote Desktop works. With
the portal open, push the side of the screen to slide to the PC's next screen, and push out the bottom
to come back.

Punching from a Mac or Linux computer into another Mac or Linux computer isn't there yet; Windows PCs
work now.

### Let the Windows PC punch into this computer

In Async's **Settings** on the Windows PC, next to this computer:

- **Mac:** click **Password…** and enter the name and password you log into the Mac with.
- **Linux with x11vnc:** nothing; Async handles it. The card says "ready, nothing to enter".
- **Linux with GNOME or KDE Remote Desktop:** the user name and password from its settings.

Then give it an edge under **Screen edges**, save, and push through that edge.

The computer being punched into must be awake and logged in: screen sharing shows the desktop you're
logged into.

---

## Something not working?

| What you see | What to do |
|---|---|
| Pushing through an edge does nothing (Mac) | Allow Input Monitoring (Mac step 2), then quit Async from the menu-bar ring and log out and back in. |
| The device shows **· screen sharing is off** in Settings | The screen sharing step isn't done yet on that computer. |
| **The Mac turned down that user name and password** | Click **Password…** next to the Mac in Settings on the Windows PC and enter the name and password you log into the Mac with. |
| **This Mac needs its user name and password** | The same: the Mac's own name and password, with **Password…**. |
| The device shows **· offline** | Async isn't running on it, or it's asleep or offline. Restarting it starts Async again. |
| A wrong password message | Enter the password again in Settings. |

### Handy commands

Linux (in a terminal):

```sh
async-desktop settings                  # Async's settings window
async-core peers                        # list paired devices
journalctl --user -u async-core         # the engine's log
./install.sh --uninstall                # remove Async (from the unpacked folder)
```

Mac (in Terminal):

```sh
cat ~/Library/Logs/async-desktop.log ~/Library/Logs/async-core.log
"$HOME/Library/Application Support/Async/bin/async-core" peers
```

To uninstall on a Mac, run `./install.sh --uninstall` from the unpacked folder in Terminal.
