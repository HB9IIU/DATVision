#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$XDG_RUNTIME_DIR/bus}"
# Native libVLC embedding uses an X11 window inside the existing labwc desktop.
export QT_QPA_PLATFORM=xcb
export DISPLAY="${DISPLAY:-:0}"
export XAUTHORITY="${XAUTHORITY:-$HOME/.Xauthority}"
mkdir -p test-output
if pgrep -x rtl_iq >/dev/null || pgrep -x datvrx >/dev/null; then
  echo 'Stop the other DATV receiver before starting the fullscreen player.' >&2
  exit 1
fi
exec /usr/bin/python3 ui/pi_player/main.py
