#!/usr/bin/env bash
set -euo pipefail
project_root=$(cd "$(dirname "$0")/../.." && pwd)
desktop_dir=$(xdg-user-dir DESKTOP 2>/dev/null || printf '%s/Desktop' "$HOME")
mkdir -p "$HOME/.local/share/applications" "$desktop_dir"
launcher="$HOME/.local/share/applications/rtl-datv-player.desktop"
cat > "$launcher" <<EOF
[Desktop Entry]
Type=Application
Name=RTL DATV Player
Comment=Fullscreen DATV video and touch-to-tune spectrum on this Raspberry Pi
Exec=/usr/bin/bash "$project_root/scripts/linux/start-pi-player.sh"
Path=$project_root
Icon=video-display
Terminal=false
Categories=AudioVideo;Video;
EOF
chmod u+x "$launcher"
cp "$launcher" "$desktop_dir/RTL DATV Player.desktop"
chmod u+x "$desktop_dir/RTL DATV Player.desktop"
printf 'Pi desktop shortcut: %s/RTL DATV Player.desktop\n' "$desktop_dir"
