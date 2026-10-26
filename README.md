<p align="center">
  <img src="app/assets/async.ico" width="96" alt="Async icon">
</p>

<h1 align="center">Async</h1>

<p align="center"><b>Push your mouse through the edge of your screen and punch a portal into another computer.</b></p>

![The portal opening, then closing](docs/preview.png)

Async turns any monitor into a window onto another computer. Shove the mouse against the edge of a screen
and a portal tears open where you pushed, showing the other computer's desktop on that monitor. Push down out
of the bottom and it collapses back. The top and sides of each monitor can each lead to a different
computer: the top of your main screen to your desktop, the right side of your laptop screen to a Mac, and
so on.

Async runs on every computer you want to move between. The copies pair with a short code and then talk
through their own end-to-end encrypted tunnel. There's no account to make, no VPN to set up (Tailscale,
Hamachi…) and no ports to open on your router.

## Features

- **Pick an edge for each computer:** Settings shows your monitors as they're arranged; click the top or a
  side of a screen and choose which paired computer it opens. The bottom is always the way back. The
  remote desktop resizes itself to fit the monitor.
- **Portal or extend, per edge:** an edge can open a portal (the other computer's screen shows here), or
  extend onto the other computer: the pointer and keyboard simply move over to it, like a monitor beside
  yours, and come back when you move back out. Text you copy comes along.
- **Send files and folders:** drop them onto a portal, drag them out through the bottom of a portal back
  to your own computer, or drag them across an extending edge. The other computer asks where they should
  go (starting at the folder you picked last time).
- **Deliberate pushes only:** resting at the edge, flicking past it, dragging a window to the top, or moving
  the camera in a game never triggers it. How hard and how long you push are adjustable.
- **Edge glow:** the edge lights up as you push, so you can feel the portal about to open.
- **Lands where you punched:** the other computer's cursor appears right under the spot you pushed through.
- **Windows, macOS and Linux, every direction:** portals, extending and files work from any of them into
  any other.
- **Multi-screen PCs:** punching into a PC with several screens shows one of them; slide to the next by
  pushing the side of the screen (Mac and Linux), and pick which screen each edge opens.
- **Pair with a code:** one PC shows `ABCD-EFGH`, you type it on the other, done.
- **Its own secure tunnel**, built on [iroh](https://github.com/n0-computer/iroh). Connections go direct
  between your computers when possible (through home routers via hole punching) and fall back to an
  encrypted relay when not. Only paired devices are let in.
- **Portal effects:** a crackling rim, vortex, shockwave and sparks, in colours you pick.
- **Sounds:** a built-in whoosh, the portal entry/exit/open/close sounds from your own copy of
  *Portal* or *Portal 2*, or any `.wav` files.
- `Ctrl+Alt+Up` (configurable) or double-clicking the tray icon opens the portal too.

![Settings](docs/settings.png)

## Set it up

About ten minutes. Install Async on **every computer** you want to move between, pair them once, then
pick which screen edge leads where.

All downloads are on the **[latest release](https://github.com/CalvinRogersDoE/Async/releases/latest)**
page, under *Assets*.

### Step 1: install Async on each computer

#### Windows

1. Download **Async-win-x64.zip**.
2. Right-click it → **Extract All** → **Extract**.
3. In the folder that opens, double-click **Async.exe**.
   - If a blue box says *"Windows protected your PC"*: click **More info** → **Run anyway**.
4. A **purple ring** appears in the system tray, by the clock. (Don't see it? Click the **^** arrow
   there.)
5. Right-click the ring and tick **Start with Windows** so it's always there.
6. **To let your other computers punch into this PC:** open Windows **Settings → System → Remote
   Desktop** and turn it **On**. (This needs Windows Pro. On Windows Home you can still punch *out*.)

#### Mac

1. Download **Async-macos-universal.tar.gz**. (Works on every Mac, Apple Silicon or Intel.)
2. Open your **Downloads** folder and double-click that file. A folder called
   **Async-macos-universal** appears next to it. (If Safari already unpacked it, the folder is already
   there.)
3. Open that folder and double-click **Install Async.command**.
   - If macOS says it *"can't be opened"* or *"Apple could not verify"* it, that's because Async
     isn't sold through Apple. Click **Done**, then open the **Apple menu  → System Settings →
     Privacy & Security**, scroll down, and click **Open Anyway** next to *Install Async.command*.
     Type your Mac password if asked, then click **Open**.
4. A Terminal window opens and installs everything by itself. When it says **All done**, press any
   key to close it.
5. A **purple ring** appears in the menu bar at the top right of the screen. That's Async; it starts
   by itself every time the Mac turns on.
6. macOS asks to let **async-desktop** use **Input Monitoring**. Click **Open System Settings** and
   switch **async-desktop** on. (It may mention keystrokes; Async only reads the mouse moving, to
   notice you pushing through a screen edge.)
7. **To let your other computers punch into this Mac:** **Apple menu  → System Settings → General →
   Sharing** and turn on **Screen Sharing**. That's all: your other computers sign in with the name and
   password you log into this Mac with. (If the ⓘ next to Screen Sharing lists who's allowed in, make
   sure that's you, or choose **All users**.)

#### Linux

Linux Mint, Ubuntu (on Xorg), MATE, Xfce and other X11 desktops.

1. Download **Async-linux-x86_64.tar.gz** (or **Async-linux-arm64.tar.gz** on a Raspberry Pi or other
   ARM computer).
2. Right-click it → **Extract Here**.
3. Right-click the new folder → **Open in Terminal**, type `./install.sh` and press **Enter**. No
   password needed.
4. A **purple ring** appears in the tray. It starts by itself at every login, and **Async** is in your
   applications menu.
5. **To let your other computers punch into this one:** on Linux Mint, MATE or Xfce, run
   `sudo apt install x11vnc` and then `./install.sh` again; Async sets up the rest, password included.
   On GNOME or KDE, turn on Remote Desktop in their settings
   ([details](docs/hosting.md#2-screen-sharing)).

### Step 2: pair your computers

Do this once for each pair of computers.

1. On one computer, click the purple ring → **Pair a device…** (on a Mac or Linux computer: **Settings…
   → Show a code**). It shows a code like `ABCD-EFGH`.
2. On the other computer, type that code in: on Windows, **Pair a device… → Enter a code**; on a Mac or
   Linux computer, **Settings…**, the box next to **Enter code**.

### Step 3: say where each edge goes

On the computer you'll punch **from**, click the purple ring → **Settings…**

1. Under **Devices**, next to the computer you'll punch **into**, enter how to sign in to it:
   - **A Windows PC:** its Windows user name and password. If you sign into Windows with a Microsoft
     account, that's the account's email and password, not your PIN.
   - **A Mac:** the name and password you log into that Mac with. (Its short name works too: it's the
     name of the Mac's home folder, e.g. `nicole`.)
   - **Linux with x11vnc:** nothing. It says *ready, nothing to enter*.
2. Under **Screen edges**, click the top or a side of one of your screens and pick that computer. If
   it has several screens, pick which one to show.
3. In the same menu, choose what going through that edge does:
   - **Show its screen here (portal):** the other computer's screen opens on your monitor.
   - **Move the pointer over to it (extend):** your mouse and keyboard move over to the other computer,
     and you use it on its own screen. A small arrow on the edge marks these. (A Mac asks once to let
     **async-desktop** use **Accessibility**; allow it in System Settings → Privacy & Security.)
4. Click **Save**.

### Step 4: punch through

Push your mouse **hard** through that edge, as if you were trying to shove it off the screen. A portal
opens onto the other computer. **Push down out of the bottom of the screen to come back.**

On an extending edge a light push is enough: the pointer appears on the other computer's screen. Move it
back out through the side it came in by to return. Async's hotkey (Ctrl+Alt+Up) also brings it back.

To send files, drop them onto a portal, or drag them to an extending edge and carry on over. The other
computer asks where to put them.

Good to know:

- Both computers need to be on, logged in, and online. They don't need to be on the same network,
  and there's nothing to set up on your router.
- When you punch into a Windows PC, its own screen shows the lock screen while you're in. Its programs
  keep running.
- To get a file out of a portal, drag it to the very bottom edge of the screen inside the portal and let
  go there: it comes back to the computer you're looking from, which asks where to put it.
- Stuck? The [full guide](docs/hosting.md) has more detail and a troubleshooting table.

## Roadmap

- [x] Push-through trigger, portal effects, sound packs
- [x] Pairing codes and an encrypted peer-to-peer tunnel (iroh)
- [x] Remote Desktop through the tunnel
- [x] Punch into macOS (Screen Sharing over VNC) and Linux (Remote Desktop or VNC)
- [x] The edge glows as you push; the cursor lands where you punched through
- [x] Map edges to computers (top → desktop, right → laptop…)
- [x] Punching *out* of macOS and Linux (into Windows PCs)
- [x] Extend mode (the pointer and keyboard move over) and sending files, every direction
- [x] From macOS and Linux into other Macs and Linux computers
- [ ] Drop files exactly where the pointer is on the other computer
- [ ] The edge glow on macOS and Linux
- [ ] Wayland on Linux
- [ ] Async's own screen streaming: one format on every system, and Windows Home as a host
- [ ] Terminal mode: punch into a Linux or macOS shell
- [ ] Installer and auto-update

## How it works

```
 This PC                                                      Other PC
┌──────────────────────────┐                     ┌──────────────────────────┐
│ Async.exe (tray, C#)     │                     │ Async.exe                │
│  edge watcher → portal   │                     │                          │
│  Remote Desktop control ─┼─► 127.0.0.2:port    │                          │
│           ▲              │        │            │                          │
│ async-core.exe (Rust)    │        ▼            │ async-core.exe           │
│  pairing · peers · tunnel├── iroh QUIC (E2EE) ─┼► only paired devices ──► │──► Remote Desktop (localhost:3389)
└──────────────────────────┘   direct or relayed └──────────────────────────┘
```

- **`app/`**: the Windows tray app (C#, WinForms). It watches raw mouse input for pushes against a screen
  edge (`EdgeWatcher.cs`) and looks up which computer that edge leads to (`EdgeMap.cs` is the picker). It
  draws the portal on a click-through overlay while growing the remote window's circular shape
  (`PortalFx.cs`, `App.cs`), and hosts Windows' Remote Desktop ActiveX control (`PortalForm.cs`) or its
  own VNC viewer (`VncView.cs`).
- **`desktop/`**: `async-desktop`, the macOS and Linux app (Rust). The same edge pushes and portal as on
  Windows: raw mouse motion from XInput2 (Linux) or a CoreGraphics event tap (macOS), a window cut to the
  opening circle (X11 Shape, or a Core Animation mask), and Remote Desktop through
  [IronRDP](https://github.com/Devolutions/IronRDP). It drives the async-core service over a local socket.
- **`core/`**: `async-core`, the part that runs on every machine (Rust). It holds the device identity, pairs
  devices, accepts tunnels from paired devices and opens tunnels into them. The app drives it with JSON
  lines over stdin/stdout (`core/src/control.rs`). It also works on its own, which is handy on headless
  machines: `async-core pair-host`, `pair-join CODE`, `peers`, `serve`, `tunnel PEER`.

**Pairing** needs no server of ours. Both sides derive a throwaway iroh identity from the code: the side
showing the code listens as that identity, and the other side finds it through iroh's normal address
lookup. Each then proves it knows the code with an HMAC bound to its real, permanent identity, which the
QUIC/TLS handshake already authenticates. After that the code is useless; devices remember each other's
public keys. Codes have about 39 bits of entropy and expire after 10 minutes.

## Building

Windows: the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Rust](https://rustup.rs).

```
cd core
cargo build --release
cd ../app
dotnet build -c Release
```

The app build copies `core/target/release/async-core.exe` next to `Async.exe` (pass
`-p:AsyncCoreExe=<path>` if your Cargo target folder is elsewhere). For a release:

```
dotnet publish app -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist
```

macOS and Linux: [Rust](https://rustup.rs) (and CMake, for the TLS library), then `cargo build --release`
in `core/` and `desktop/`. CI builds every download (`.github/workflows/build.yml`).

Developer helpers: `ASYNC_SLOWMO=6` slows the animations, `ASYNC_DEBUG=1` logs raw mouse input, `--demo`
opens the portal 1.2 s after launch, and `Async.exe --render-docs <folder>` redraws the README images and
the icon. Logs and settings live in `%APPDATA%\Async`.

## Credits

Made by HallucinatesTigers. The tunnel is [iroh](https://github.com/n0-computer/iroh) by n0, and the Mac and
Linux app's Remote Desktop is [IronRDP](https://github.com/Devolutions/IronRDP) by Devolutions (both
Apache-2.0/MIT).

*Portal* and *Portal 2* are trademarks of Valve Corporation. Async isn't affiliated with or endorsed by
Valve, ships none of Valve's files, and only reads the sounds from a copy of the game you own, if you point
it at one.

## License

[MIT](LICENSE)
