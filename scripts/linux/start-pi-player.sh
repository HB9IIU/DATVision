#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
# Started with the desktop: give it a moment to settle first.
if [[ ${1:-} == --autostart ]]; then
  sleep 8
fi
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
# Keep a record however the player is started: desktop icon, terminal or service.
exec >>test-output/pi-player-ui.log 2>&1
echo "=== $(date '+%F %T') player start"
exec /usr/bin/python3 -X faulthandler ui/pi_player/main.py
