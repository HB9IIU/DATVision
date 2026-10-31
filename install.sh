#!/usr/bin/env bash
# RTL DATV Player for Raspberry Pi, by HB9IIU: installer, updater and uninstaller.
# GPL-3.0-or-later. Receiver derived from OnAir; see LICENSE and the credits page.
#
#   curl -fsSL https://github.com/HB9IIU/WIP/releases/latest/download/install.sh | bash
#   bash install.sh               install, or update keeping your settings
#   bash install.sh --uninstall   remove everything this installer added
#
# Everything goes into your home folder, except two small system files that give
# you access to the RTL-SDR stick: a udev rule and a DVB-T driver blacklist.
set -euo pipefail

VERSION=0.0.0   # filled in by scripts/linux/make-release.sh
REPO=HB9IIU/WIP
APP_DIR="$HOME/pi-datv-rx"
ARCHIVE="pi-datv-rx-$VERSION-arm64.tar.gz"
URL="https://github.com/$REPO/releases/download/v$VERSION"
UDEV_RULE=/etc/udev/rules.d/60-pi-datv-rx.rules
BLACKLIST=/etc/modprobe.d/pi-datv-rx-blacklist.conf
LAUNCHER="$HOME/.local/share/applications/rtl-datv-player.desktop"
AUTOSTART="$HOME/.config/autostart/rtl-datv-player.desktop"
DESKTOP_DIR=$(xdg-user-dir DESKTOP 2>/dev/null || printf '%s/Desktop' "$HOME")
PACKAGES=(python3-pyqt5 python3-pyqt5.qtwebsockets vlc libusb-1.0-0 usbutils curl ca-certificates)

bold() { printf '\n\033[1m%s\033[0m\n' "$*"; }
note() { printf '  %s\n' "$*"; }
fail() { printf '\n\033[31mStopped: %s\033[0m\n' "$*" >&2; exit 1; }
# Questions come from the keyboard, also when this script arrives through a pipe.
ask() {
  local answer
  read -r -p "  $1 [y/N] " answer < /dev/tty || answer=""
  [[ $answer =~ ^[YyJj] ]]
}

# The player runs as "python3 ... ui/pi_player/main.py"; its receivers stop with it.
stop_player() {
  if pkill -f '^/usr/bin/python3 .*ui/pi_player/main\.py' 2>/dev/null; then
    sleep 2
  fi
}

uninstall() {
  bold "Uninstalling the RTL DATV Player"
  note "Removes $APP_DIR, the icon, the autostart entry, the udev rule and the blacklist."
  note "Installed packages (${PACKAGES[*]}) are kept: other programs may use them."
  ask "Continue?" || exit 0
  if [[ -f $APP_DIR/pi-player-settings.json ]] && ask "Keep a copy of your settings in ~/pi-player-settings.json?"; then
    cp "$APP_DIR/pi-player-settings.json" "$HOME/pi-player-settings.json"
  fi
  stop_player
  rm -rf "$APP_DIR"
  rm -f "$LAUNCHER" "$AUTOSTART" "$DESKTOP_DIR/RTL DATV Player.desktop"
  sudo rm -f "$UDEV_RULE" "$BLACKLIST"
  sudo udevadm control --reload-rules || true
  bold "Uninstalled."
  exit 0
}

[[ ${1:-} == --uninstall ]] && uninstall
[[ $EUID -ne 0 ]] || fail "run this as your normal user, not with sudo; it asks for the password itself."
[[ $VERSION != 0.0.0 ]] || fail "this copy of install.sh has no version; download it from the Release page."

bold "RTL DATV Player $VERSION — installer"

# ---- the system
model=$(tr -d '\0' < /proc/device-tree/model 2>/dev/null || echo unknown)
[[ $(uname -m) == aarch64 ]] || fail "a 64-bit Raspberry Pi OS is needed (found $(uname -m))."
# Read only what is needed: sourcing os-release would overwrite VERSION.
codename=$(sed -n 's/^VERSION_CODENAME=//p' /etc/os-release)
os_name=$(sed -n 's/^PRETTY_NAME=//p' /etc/os-release | tr -d '"')
[[ $codename == bookworm || $codename == trixie ]] ||
  fail "Raspberry Pi OS Bookworm or newer is needed (found ${os_name:-unknown})."
case $model in
  *"Raspberry Pi 5"*) note "$model, $os_name: supported." ;;
  *"Raspberry Pi 4"*)
    note "$model: not verified yet. Lower symbol rates should work; 1500 kS/s may not decode."
    ask "Install anyway?" || exit 0 ;;
  *) fail "this needs a Raspberry Pi 5 (or 4); found: $model." ;;
esac
command -v labwc >/dev/null || command -v wayfire >/dev/null || command -v startlxde-pi >/dev/null ||
  note "No Raspberry Pi desktop found: the player needs the desktop version of the OS."

if [[ -d $APP_DIR ]]; then
  bold "Updating $APP_DIR (your settings are kept)"
else
  bold "This will install"
fi
note "packages:   ${PACKAGES[*]}"
note "player:     $APP_DIR (receiver, private RTL-SDR V4 driver, interface)"
note "stick:      $UDEV_RULE and $BLACKLIST"
note "start:      desktop icon, and automatic start when the desktop starts"
note "remove:     bash install.sh --uninstall"
ask "Continue?" || exit 0

bold "Administrator password (for packages and the two stick files)"
sudo -v < /dev/tty

bold "[1/5] Installing packages"
note "This can take several minutes, especially on a Pi 4; apt shows its progress below."
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y "${PACKAGES[@]}"

bold "[2/5] Giving you access to the RTL-SDR stick"
sudo tee "$UDEV_RULE" >/dev/null <<'EOF'
# RTL-SDR sticks (RTL2832U), for the RTL DATV Player: usable without root.
SUBSYSTEM=="usb", ATTRS{idVendor}=="0bda", ATTRS{idProduct}=="2838", MODE="0666"
SUBSYSTEM=="usb", ATTRS{idVendor}=="0bda", ATTRS{idProduct}=="2832", MODE="0666"
EOF
sudo tee "$BLACKLIST" >/dev/null <<'EOF'
# Keep the DVB-T TV driver off the RTL-SDR stick (RTL DATV Player).
blacklist dvb_usb_rtl28xxu
blacklist rtl2832
blacklist rtl2830
EOF
sudo udevadm control --reload-rules
sudo udevadm trigger --subsystem-match=usb || true
sudo modprobe -r dvb_usb_rtl28xxu 2>/dev/null || true
note "udev rule and DVB-T blacklist installed."

bold "[3/5] Downloading the player"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
curl -fL --progress-bar -o "$work/$ARCHIVE" "$URL/$ARCHIVE" || fail "download of $ARCHIVE failed."
curl -fsSL -o "$work/SHA256SUMS" "$URL/SHA256SUMS" || fail "download of SHA256SUMS failed."
(cd "$work" && grep " $ARCHIVE\$" SHA256SUMS | sha256sum -c --quiet -) || fail "the download is damaged (checksum)."
tar -C "$work" -xzf "$work/$ARCHIVE"

bold "[4/5] Installing into $APP_DIR"
stop_player
if [[ -f $APP_DIR/pi-player-settings.json ]]; then
  cp "$APP_DIR/pi-player-settings.json" "$work/pi-datv-rx/"
fi
rm -rf "$APP_DIR"
mv "$work/pi-datv-rx" "$APP_DIR"
mkdir -p "$APP_DIR/test-output"
note "Player $VERSION installed in $APP_DIR."

bold "[5/5] Desktop icon and automatic start"
mkdir -p "$(dirname "$LAUNCHER")" "$(dirname "$AUTOSTART")" "$DESKTOP_DIR"
cat > "$LAUNCHER" <<EOF
[Desktop Entry]
Type=Application
Name=RTL DATV Player
Comment=DATV receiver with touch spectrum (HB9IIU)
Exec=/usr/bin/bash "$APP_DIR/scripts/linux/start-pi-player.sh"
Path=$APP_DIR
Icon=video-display
Terminal=false
Categories=AudioVideo;Video;HamRadio;
EOF
cp "$LAUNCHER" "$DESKTOP_DIR/RTL DATV Player.desktop"
chmod u+x "$LAUNCHER" "$DESKTOP_DIR/RTL DATV Player.desktop"
sed 's|start-pi-player.sh"|start-pi-player.sh" --autostart|' "$LAUNCHER" > "$AUTOSTART"
note "Icon on the desktop; starts by itself with the desktop."

bold "Checking the stick"
if lsusb | grep -q -E '0bda:(2838|2832)'; then
  if timeout 15 "$APP_DIR/build/linux/rtl_iq" 741500000 2400000 30 240000 2>"$work/stick.log" >/dev/null; then
    note "RTL-SDR stick works: $(grep -m1 -E 'Detected|Found' "$work/stick.log" || echo ok)."
  else
    note "The stick did not open yet. Unplug it, plug it back in, and start the player."
  fi
else
  note "No RTL-SDR stick found. Plug it in before starting the player."
fi

bold "Done: RTL DATV Player $VERSION"
note "Start it with the RTL DATV Player icon on the desktop; from now on it also starts"
note "by itself with the desktop. Remove it again with:  bash install.sh --uninstall"
note "(get install.sh again from https://github.com/$REPO/releases/latest)"
