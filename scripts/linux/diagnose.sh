#!/usr/bin/env bash
# Collects what is needed to help with a problem into one text file in the home folder.
# Nothing is sent anywhere: read the file, then attach it to your message.
# IP addresses, MAC addresses and the user name are replaced before anything is written.
# GPL-3.0-or-later.
root="$(cd "$(dirname "$0")/../.." && pwd)"
stamp="$(date +%Y%m%d-%H%M%S)"
out="$HOME/datvision-diagnostics-$stamp.txt"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export DISPLAY="${DISPLAY:-:0}"
export XAUTHORITY="${XAUTHORITY:-$HOME/.Xauthority}"

redact() {
  sed -E \
    -e 's/\b([0-9]{1,3}\.){3}[0-9]{1,3}\b/x.x.x.x/g' \
    -e 's/\b([0-9A-Fa-f]{2}[:_-]){5}[0-9A-Fa-f]{2}\b/xx:xx:xx:xx:xx:xx/g' \
    -e "s#/home/$USER#/home/<user>#g" \
    -e "s/\b$USER\b/<user>/g" \
    -e "s/\b$(hostname)\b/<host>/g"
}
section() { printf '\n===== %s =====\n' "$1"; }
run() { timeout 10 "$@" 2>&1 | head -${LINES_MAX:-60}; }

{
  echo "DATVision diagnostics, $(date '+%F %T')"
  echo "(IP and MAC addresses, the host name and the user name are removed)"

  section "DATVision"
  echo "VERSION: $(cat "$root/VERSION" 2>/dev/null || echo unknown)"
  cat "$root/BUILD-INFO" 2>/dev/null | head -20
  echo "Installed in: $root"

  section "System"
  run uname -a
  grep -E '^(PRETTY_NAME|VERSION)=' /etc/os-release
  grep -m1 -E '^Model' /proc/cpuinfo
  run uptime
  run free -m
  run df -h /
  command -v vcgencmd >/dev/null && { vcgencmd measure_temp; vcgencmd get_throttled; echo "(throttled=0x0 means no power or heat problem since boot)"; }
  echo "Session: ${XDG_SESSION_TYPE:-?}  display: $DISPLAY  wayland: ${WAYLAND_DISPLAY:-none}"
  run xrandr
  python3 -c 'from PyQt5.QtCore import PYQT_VERSION_STR, QT_VERSION_STR; print("PyQt", PYQT_VERSION_STR, "Qt", QT_VERSION_STR)' 2>&1
  run mpv --version
  run ffmpeg -version

  section "Running programs (DATVision, receivers, video)"
  LINES_MAX=30 run bash -c 'ps -eo pid,etime,pcpu,pmem,args | grep -E "pi_player|main.py|mpv|rtl_|pluto_|datvrx|ffplay" | grep -v grep | cut -c1-170'

  section "Radio hardware"
  run bash -c 'lsusb'
  run bash -c 'ls /dev/bus/usb 2>/dev/null | head -3'
  run bash -c 'lsmod | grep -i -E "dvb|rtl|r820" '

  section "Video window (mpv)"
  run bash -c 'xwininfo -root -tree 2>/dev/null | grep -i -E "mpv|DATVision|main.py"'
  echo "(Map State IsUnMapped on the mpv window would mean the video is hidden)"
  for window in $(xwininfo -root -tree 2>/dev/null | grep -i '"gl" "mpv"' | awk '{print $1}'); do
    echo "mpv window $window: $(xwininfo -id "$window" 2>/dev/null | grep 'Map State')"
  done

  section "Sound"
  run pactl info
  run pactl list short sinks
  run pactl list short sink-inputs
  run bash -c 'bluetoothctl devices Connected 2>/dev/null'

  section "Settings (as saved)"
  cat "$root/pi-player-settings.json" 2>/dev/null | head -40

  for log in pi-player-ui pi-player-receiver pi-player-video pi-player-radio pi-player-sweep; do
    section "Log: $log (last 80 lines)"
    tail -n 80 "$root/test-output/$log.log" 2>/dev/null | cut -c1-200
  done

  section "Kernel messages about USB and power (last 30)"
  (dmesg 2>/dev/null || journalctl -k -n 200 --no-pager 2>/dev/null) | grep -i -E "usb|under-voltage|throttl|rtl|pluto" | tail -n 30
} 2>&1 | redact > "$out"

echo "$out"
