#!/usr/bin/env bash
# Show the player for another screen size in a window, for example on the
# TigerVNC desktop :1.
#   bash scripts/linux/preview-layout.sh 800x480 [--live] [X display, default :1]
# Without --live: layout only. The stick, the receiver and saved settings are not
# used, so it can run next to the real player.
# With --live: the complete player (scan, tune, video, sound) in that window. It
# uses the stick, so the real player must be closed first.
set -euo pipefail
cd "$(dirname "$0")/../.."
if [[ $# -lt 1 || ! $1 =~ ^[0-9]+x[0-9]+$ ]]; then
  echo 'usage: preview-layout.sh WIDTHxHEIGHT [--live] [X display, default :1]' >&2
  exit 1
fi
size=$1
shift
mode=--preview
if [[ ${1:-} == --live ]]; then
  mode=--window
  shift
  if pgrep -x rtl_iq >/dev/null || pgrep -x datvrx >/dev/null || pgrep -x rtl_sweep >/dev/null; then
    echo 'The real player is using the stick. Close it before a live window.' >&2
    exit 1
  fi
fi
export QT_QPA_PLATFORM=xcb
export DISPLAY="${1:-:1}"
unset WAYLAND_DISPLAY   # never on the Pi's own screen, only on the chosen X display
if [[ $mode == --window ]]; then
  mkdir -p test-output
  exec >>test-output/pi-player-ui.log 2>&1
  echo "=== $(date '+%F %T') live window $size on $DISPLAY"
  exec /usr/bin/python3 -X faulthandler ui/pi_player/main.py --window "$size"
fi
exec /usr/bin/python3 ui/pi_player/main.py --preview "$size"
