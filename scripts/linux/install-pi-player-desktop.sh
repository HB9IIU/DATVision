#!/usr/bin/env bash
set -euo pipefail
project_root=$(cd "$(dirname "$0")/../.." && pwd)
desktop_dir=$(xdg-user-dir DESKTOP 2>/dev/null || printf '%s/Desktop' "$HOME")
mkdir -p "$HOME/.local/share/applications" "$desktop_dir"
launcher="$HOME/.local/share/applications/rtl-datv-player.desktop"
cat > "$launcher" <<EOF
[Desktop Entry]
Type=Application
Name=DATVision
Comment=Fullscreen DATV video and touch-to-tune spectrum on this Raspberry Pi
Exec=/usr/bin/bash "$project_root/scripts/linux/start-pi-player.sh"
Path=$project_root
Icon=$project_root/assets/branding/datvision-icon.png
Terminal=false
Categories=AudioVideo;Video;
EOF
chmod u+x "$launcher"
rm -f "$desktop_dir/RTL DATV Player.desktop"   # the name before DATVision
cp "$launcher" "$desktop_dir/DATVision.desktop"
chmod u+x "$desktop_dir/DATVision.desktop"
printf 'Pi desktop shortcut: %s/DATVision.desktop\n' "$desktop_dir"
