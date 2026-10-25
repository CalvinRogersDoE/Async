#!/usr/bin/env bash
# Installs async-core on macOS or Linux so paired computers can punch into this one.
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
    ;;
  Linux)
    bin_dir="$HOME/.local/bin"
    data_dir="${XDG_CONFIG_HOME:-$HOME/.config}/async/core"
    unit="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/async-core.service"
    ;;
  *) echo "This script is for macOS and Linux. On Windows, run Async.exe." >&2; exit 1 ;;
esac
exe="$bin_dir/async-core"

stop_service() {
  if [ "$os" = Darwin ]; then
    launchctl bootout "gui/$(id -u)" "$agent" 2>/dev/null || true
  else
    systemctl --user disable --now async-core.service 2>/dev/null || true
  fi
}

if [ "${1:-}" = "--uninstall" ]; then
  stop_service
  [ "$os" = Linux ] && systemctl --user disable --now async-vnc.service 2>/dev/null || true
  rm -f "$exe" "${agent:-}" "${unit:-}" "${unit:+$(dirname "$unit")/async-vnc.service}"
  [ "$os" = Linux ] && systemctl --user daemon-reload 2>/dev/null || true
  say "Removed async-core. Your identity and paired devices are still in: $data_dir"
  exit 0
fi

[ -f "$here/async-core" ] || { echo "async-core isn't next to this script." >&2; exit 1; }

say "Installing async-core to $exe"
stop_service
mkdir -p "$bin_dir"
cp "$here/async-core" "$exe"
chmod +x "$exe"
# A program downloaded from the internet is quarantined by macOS; this one isn't signed by Apple.
[ "$os" = Darwin ] && xattr -d com.apple.quarantine "$exe" 2>/dev/null || true

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
  say "Running in the background and at every login (log: ~/Library/Logs/async-core.log)"
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
  say "Running in the background and at every login (log: journalctl --user -u async-core)"
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
  say "Remote Desktop is on: paired Windows PCs will connect with it."
elif listening 5900; then
  say "VNC screen sharing is on: paired Windows PCs will connect with it."
else
  warn "No screen sharing is turned on yet, so there's nothing to show."
  if [ "$os" = Darwin ]; then
    warn "Turn on System Settings > General > Sharing > Screen Sharing, click (i), turn on"
    warn "\"VNC viewers may control screen with password\" and set a password."
  else
    warn "GNOME: Settings > System > Remote Desktop > Desktop Sharing (Remote Desktop, with a user and password)."
    warn "KDE Plasma 6: System Settings > Remote Desktop. Others: install xrdp, or a VNC server on port 5900."
  fi
  warn "See docs/hosting.md for details. No need to reinstall afterwards."
fi

if [ -t 0 ]; then
  printf 'Pair with another computer now? [Y/n] '
  read -r answer
  case "${answer:-y}" in
    [Nn]*) ;;
    *) "$exe" pair-host ;;
  esac
fi
say "Done. Pair more devices any time with: \"$exe\" pair-host"
