#!/usr/bin/env bash
set -euo pipefail
project_root=$(cd "$(dirname "$0")/../.." && pwd)
desktop_dir=$(xdg-user-dir DESKTOP 2>/dev/null || printf '%s/Desktop' "$HOME")
mkdir -p "$HOME/.local/share/applications" "$desktop_dir"
launcher="$HOME/.local/share/applications/datv-beacon.desktop"
cat > "$launcher" <<EOF
[Desktop Entry]
Type=Application
Name=QO-100 Beacon
Comment=Receive and display the beacon on this Raspberry Pi
Exec=/usr/bin/bash "$project_root/scripts/linux/play-beacon.sh"
Path=$project_root
Icon=video-display
Terminal=false
Categories=AudioVideo;Video;
EOF
chmod u+x "$launcher"
cp "$launcher" "$desktop_dir/QO-100 Beacon.desktop"
chmod u+x "$desktop_dir/QO-100 Beacon.desktop"
printf 'Pi desktop shortcut: %s/QO-100 Beacon.desktop\n' "$desktop_dir"
