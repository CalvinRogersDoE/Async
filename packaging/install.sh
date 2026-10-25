#!/usr/bin/env bash
# Installs Async on macOS or Linux: async-core (so paired computers can punch into this one) and,
# when it's in the download, async-desktop (so this computer can punch into the others).
#
#   ./install.sh              install, start at login, check screen sharing, offer to pair
#   ./install.sh --uninstall  stop it and remove it (paired devices are kept unless you delete
#                             the data folder it prints)
#
# It never needs sudo: everything goes in your home folder and runs as you.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
os="$(uname -s)"
say() { printf '\033[1;35m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*"; }

case "$os" in
  Darwin)
    bin_dir="$HOME/Library/Application Support/Async/bin"
    data_dir="$HOME/Library/Application Support/Async/core"
    agent="$HOME/Library/LaunchAgents/async-core.plist"
    desktop_agent="$HOME/Library/LaunchAgents/async-desktop.plist"
    ;;
  Linux)
    bin_dir="$HOME/.local/bin"
    data_dir="${XDG_CONFIG_HOME:-$HOME/.config}/async/core"
    unit="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/async-core.service"
    autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/async-desktop.desktop"
    menu_entry="${XDG_DATA_HOME:-$HOME/.local/share}/applications/async.desktop"
    ;;
  *) echo "This script is for macOS and Linux. On Windows, run Async.exe." >&2; exit 1 ;;
esac
exe="$bin_dir/async-core"
desktop="$bin_dir/async-desktop"

stop_service() {
  if [ "$os" = Darwin ]; then
    launchctl bootout "gui/$(id -u)" "$agent" 2>/dev/null || true
    launchctl bootout "gui/$(id -u)" "$desktop_agent" 2>/dev/null || true
  else
    systemctl --user disable --now async-core.service 2>/dev/null || true
    pkill -x async-desktop 2>/dev/null || true
  fi
}

if [ "${1:-}" = "--uninstall" ]; then
  stop_service
  [ "$os" = Linux ] && systemctl --user disable --now async-vnc.service 2>/dev/null || true
  rm -f "$exe" "$desktop" "${agent:-}" "${desktop_agent:-}" "${unit:-}" "${unit:+$(dirname "$unit")/async-vnc.service}" "${autostart:-}" "${menu_entry:-}"
  [ "$os" = Linux ] && systemctl --user daemon-reload 2>/dev/null || true
  say "Removed Async. Your identity and paired devices are still in: $data_dir"
  exit 0
fi

[ -f "$here/async-core" ] || { echo "async-core isn't next to this script." >&2; exit 1; }

say "Installing Async to $bin_dir"
stop_service
mkdir -p "$bin_dir"
for prog in async-core async-desktop; do
  [ -f "$here/$prog" ] || continue
  cp "$here/$prog" "$bin_dir/$prog"
  chmod +x "$bin_dir/$prog"
  # A program downloaded from the internet is quarantined by macOS; these aren't signed by Apple.
  [ "$os" = Darwin ] && xattr -d com.apple.quarantine "$bin_dir/$prog" 2>/dev/null || true
done

if [ "$os" = Darwin ]; then
  mkdir -p "$(dirname "$agent")"
  cat > "$agent" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>async-core</string>
  <key>ProgramArguments</key>
  <array><string>$exe</string><string>serve</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardErrorPath</key><string>$HOME/Library/Logs/async-core.log</string>
  <key>StandardOutPath</key><string>$HOME/Library/Logs/async-core.log</string>
</dict>
</plist>
EOF
  launchctl bootstrap "gui/$(id -u)" "$agent"
  say "Async's engine is running in the background and at every login (log: ~/Library/Logs/async-core.log)"

  if [ -x "$desktop" ]; then
    cat > "$desktop_agent" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>async-desktop</string>
  <key>ProgramArguments</key>
  <array><string>$desktop</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
  <key>LimitLoadToSessionType</key><string>Aqua</string>
  <key>StandardErrorPath</key><string>$HOME/Library/Logs/async-desktop.log</string>
  <key>StandardOutPath</key><string>$HOME/Library/Logs/async-desktop.log</string>
</dict>
</plist>
EOF
    sleep 1 # let the engine open its socket first
    launchctl bootstrap "gui/$(id -u)" "$desktop_agent"
    say "Async's menu-bar icon (a purple ring) is running, and starts at every login."
    warn "macOS will ask to let async-desktop use Input Monitoring: allow it, so Async notices"
    warn "you pushing through a screen edge (System Settings > Privacy & Security > Input Monitoring)."
  fi
else
  mkdir -p "$(dirname "$unit")"
  cat > "$unit" <<EOF
[Unit]
Description=Async: lets paired computers punch into this one
After=network-online.target

[Service]
ExecStart=$exe serve
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF
  systemctl --user daemon-reload
  systemctl --user enable --now async-core.service
  say "Async's engine is running in the background and at every login (log: journalctl --user -u async-core)"

  if [ -x "$desktop" ]; then
    mkdir -p "$(dirname "$autostart")" "$(dirname "$menu_entry")"
    cat > "$autostart" <<EOF
[Desktop Entry]
Type=Application
Name=Async
Comment=Push the mouse through a screen edge to punch into another computer
Exec=$desktop
Icon=network-workgroup
X-GNOME-Autostart-enabled=true
EOF
    # The menu entry opens Settings (a second launch while Async runs does exactly that).
    cat > "$menu_entry" <<EOF
[Desktop Entry]
Type=Application
Name=Async
Comment=Push the mouse through a screen edge to punch into another computer
Exec=$desktop
Icon=network-workgroup
Categories=Network;RemoteAccess;
EOF
    if [ -n "${DISPLAY:-}" ]; then
      sleep 1 # let the engine open its socket first
      (nohup "$desktop" >/dev/null 2>&1 &)
      say "Async's tray icon (a purple ring) is running, and starts at every login."
    else
      say "Async's tray icon starts at your next login (or run: async-desktop)."
    fi
  fi
fi

# Is there screen sharing for paired computers to reach?
listening() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

# Desktops without built-in sharing (Cinnamon, MATE, Xfce… on X11): share the screen with x11vnc,
# reachable only from this computer, so only Async's tunnel can get to it.
# Also re-run when Async set it up before, to bring that setup up to date.
if [ "$os" = Linux ] && [ "${XDG_SESSION_TYPE:-x11}" != wayland ] && { [ -f "$(dirname "$unit")/async-vnc.service" ] || { ! listening 3389 && ! listening 5900; }; }; then
  if command -v x11vnc >/dev/null; then
    # Async makes up the password and hands it only to paired devices (through the encrypted
    # tunnel), so nobody has to type it anywhere.
    conf="${XDG_CONFIG_HOME:-$HOME/.config}/async"
    if [ ! -s "$conf/vnc.passwd" ] || [ ! -s "$conf/vnc-secret" ]; then
      mkdir -p "$conf"
      ( umask 077
        # (head stops tr early, which pipefail would treat as a failure)
        { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 8 > "$conf/vnc-secret"; } || true
        x11vnc -storepasswd "$(cat "$conf/vnc-secret")" "$conf/vnc.passwd" >/dev/null 2>&1 )
    fi
    cat > "$(dirname "$unit")/async-vnc.service" <<EOF
[Unit]
Description=Screen sharing for Async (x11vnc on the desktop, local only)

[Service]
ExecStart=$(command -v x11vnc) -display :0 -auth guess -localhost -rfbport 5900 -rfbauth $conf/vnc.passwd -forever -shared -repeat -quiet
Restart=always
RestartSec=5

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload
    systemctl --user enable async-vnc.service
    systemctl --user restart async-vnc.service
    sleep 2
  else
    warn "Your desktop has no screen sharing built in. Install it with:"
    warn "    sudo apt install x11vnc     (Fedora: sudo dnf install x11vnc)"
    warn "then run ./install.sh again and it sets the rest up."
  fi
fi
if listening 3389; then
  say "Remote Desktop is on: paired computers will connect with it."
elif listening 5900; then
  say "Screen sharing is on: paired computers will connect with it."
else
  warn "Screen sharing isn't on yet, so paired computers can't see this one."
  if [ "$os" = Darwin ]; then
    warn "Turn on System Settings > General > Sharing > Screen Sharing, click (i), turn on"
    warn "\"VNC viewers may control screen with password\" and set a password."
  else
    warn "GNOME: Settings > System > Remote Desktop > Desktop Sharing (Remote Desktop, with a user and password)."
    warn "KDE Plasma 6: System Settings > Remote Desktop. Others: install xrdp, or a VNC server on port 5900."
  fi
  warn "The guide (README.md next to this script) walks through it. No need to reinstall afterwards."
fi

if [ -t 0 ]; then
  printf 'Pair with another computer now? [Y/n] '
  read -r answer
  case "${answer:-y}" in
    [Nn]*) ;;
    *) "$exe" pair-host ;;
  esac
fi
say "Done. Pair more devices any time from Async's settings, or with: \"$exe\" pair-host"
