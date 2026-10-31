#!/usr/bin/env bash
# Run capture, decoding and display on the Pi itself.
set -euo pipefail
cd "$(dirname "$0")/../.."
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$XDG_RUNTIME_DIR/bus}"
if [[ -S "$XDG_RUNTIME_DIR/${WAYLAND_DISPLAY:-wayland-0}" ]]; then
  export WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}"
  export SDL_VIDEODRIVER=wayland
else
  export DISPLAY="${DISPLAY:-:0}"
  export XAUTHORITY="${XAUTHORITY:-$HOME/.Xauthority}"
fi
mkdir -p test-output
if pgrep -x rtl_iq >/dev/null || pgrep -x datvrx >/dev/null; then
  echo 'A receiver is already running. Stop it before opening the Pi screen player.' >&2
  exit 1
fi
bash scripts/linux/remote-receive.sh 741500000 2560000 1500 2>test-output/pi-screen-receiver.log |
  ffplay -hide_banner -loglevel warning -nostats -autoexit \
    -window_title 'QO-100 Beacon - PI SCREEN' -x 960 -y 540 -left 40 -top 40 \
    -volume 40 -probesize 1000000 -analyzeduration 1000000 -f mpegts -i - \
    2>test-output/pi-screen-video.log
