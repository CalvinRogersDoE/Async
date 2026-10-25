<p align="center">
  <img src="app/assets/async.ico" width="96" alt="Async icon">
</p>

<h1 align="center">Async</h1>

<p align="center"><b>Push your mouse through the top of your screen and punch a portal into another computer.</b></p>

![The portal opening, then closing](docs/preview.png)

Async turns any monitor into a window onto another PC. Shove the mouse against the top edge and a portal
tears open where you pushed, showing the other computer's desktop on that monitor. Push down out of the
bottom and it collapses back. No desk-hopping, and no separate remote-desktop app to dig through.

Async runs on every computer you want to move between. The copies pair with a short code and then talk
through their own end-to-end encrypted tunnel. There's no account to make, no VPN to set up (Tailscale,
Hamachi…) and no ports to open on your router.

## Features

- **Punch through any monitor:** each screen with a free top edge works, and the remote desktop resizes
  itself to fit the one you punched.
- **Deliberate pushes only:** resting at the edge, flicking past it, dragging a window to the top, or moving
  the camera in a game never triggers it. How hard and how long you push are adjustable.
- **Pair with a code:** one PC shows `ABCD-EFGH`, you type it on the other, done.
- **Its own secure tunnel**, built on [iroh](https://github.com/n0-computer/iroh). Connections go direct
  between your computers when possible (through home routers via hole punching) and fall back to an
  encrypted relay when not. Only paired devices are let in.
- **Portal effects:** a crackling rim, vortex, shockwave and sparks, in colours you pick.
- **Sounds:** a built-in whoosh, the portal entry/exit/open/close sounds from your own copy of
  *Portal* or *Portal 2*, or any `.wav` files.
- `Ctrl+Alt+Up` (configurable) or double-clicking the tray icon opens the portal too.

![Settings](docs/settings.png)

## Getting started

1. Download the latest release (or build it, below) and run `Async.exe` on **both** computers. It sits in
   the system tray.
2. On one computer: tray icon → **Pair a device…** → **Show a code**. On the other: **Pair a device…** →
   **Enter a code**.
3. In **Settings**, next to the computer you'll punch into, enter the Windows user to sign in as, then
   **Password…**. The password is kept in Windows Credential Manager on this PC only.
4. Push your mouse up through the top of a screen.

Turn on **Start Async with Windows** on any computer you want to be able to reach while you're away from it.

### Requirements

| | |
|---|---|
| Computer you punch **from** | Windows 10 or 11 |
| Computer you punch **into** | Windows 10/11 **Pro** or Enterprise with Remote Desktop turned on (Settings → System → Remote Desktop) |
| Both | Async running, and an internet connection (or the same network) |

Today the remote screen is carried by Windows' own Remote Desktop, sent through Async's tunnel. Because
Async tunnels it, you can block Remote Desktop's port (3389) to the network entirely; the tunnel delivers it
locally. Windows Remote Desktop signs into the remote PC's account, so its own screen shows the lock screen
while you're connected; its programs keep running.

## Roadmap

- [x] Push-through trigger, portal effects, sound packs
- [x] Pairing codes and an encrypted peer-to-peer tunnel (iroh)
- [x] Remote Desktop through the tunnel
- [ ] Async's own screen streaming, so Windows Home, macOS and Linux PCs can be punched into
- [ ] Terminal mode: punch into a Linux or macOS shell
- [ ] Map edges to computers (top → desktop, left → laptop…)
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
  edge (`EdgeWatcher.cs`), draws the portal on a click-through overlay while growing the remote window's
  circular shape (`PortalFx.cs`, `App.cs`), and hosts Windows' Remote Desktop ActiveX control
  (`PortalForm.cs`).
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

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Rust](https://rustup.rs).

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

Developer helpers: `ASYNC_SLOWMO=6` slows the animations, `ASYNC_DEBUG=1` logs raw mouse input, `--demo`
opens the portal 1.2 s after launch, and `Async.exe --render-docs <folder>` redraws the README images and
the icon. Logs and settings live in `%APPDATA%\Async`.

## Credits

Made by Claude via Calvin. The tunnel is [iroh](https://github.com/n0-computer/iroh) by n0 (Apache-2.0/MIT).

*Portal* and *Portal 2* are trademarks of Valve Corporation. Async isn't affiliated with or endorsed by
Valve, ships none of Valve's files, and only reads the sounds from a copy of the game you own, if you point
it at one.

## License

[MIT](LICENSE)
