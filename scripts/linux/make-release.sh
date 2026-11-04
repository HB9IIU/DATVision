#!/usr/bin/env bash
# Build and pack a release on the Pi (ARM64, Raspberry Pi OS Bookworm):
#   bash scripts/linux/make-release.sh 0.3.0
# Creates in build/release/:
#   pi-datv-rx-VERSION-arm64.tar.gz   ready-to-run programs, V4 driver and player
#   pi-datv-rx-VERSION-source.tar.gz  this project's source (GPL-3.0-or-later)
#   rtl-sdr-driver-source.tar.gz      source of the bundled V4 driver (GPL-2.0)
#   install.sh                        installer with this version filled in
#   SHA256SUMS
# Uploading them to GitHub is a separate, deliberate step.
set -euo pipefail
cd "$(dirname "$0")/../.."
if [[ $# != 1 || ! $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo 'usage: make-release.sh VERSION (for example 0.3.0)' >&2
  exit 1
fi
version=$1
driver_lib="$HOME/.local/datvrx-rtl/lib/librtlsdr.so.0"
driver_src="$HOME/projects/datvrx-rtl-driver"
if [[ -n $(git status --porcelain) ]]; then
  echo 'Commit or stash the changes first: a release is built from a clean tree.' >&2
  exit 1
fi
[[ -f $driver_lib && -d $driver_src/.git ]] || { echo 'The private V4 driver is missing (see docs/building.md).' >&2; exit 1; }

bash scripts/linux/build.sh
out=build/release
stage=$out/pi-datv-rx
rm -rf "$out"
mkdir -p "$stage/build/linux" "$stage/ui/pi_player" "$stage/scripts/linux" "$stage/docs" "$stage/assets/branding"
cp build/linux/datvrx build/linux/rtl_iq build/linux/rtl_sweep build/linux/pluto_iq "$stage/build/linux/"
cp -L "$driver_lib" "$stage/build/linux/librtlsdr.so.0"   # found through the tools' $ORIGIN runpath
cp ui/pi_player/*.py "$stage/ui/pi_player/"
cp scripts/linux/start-pi-player.sh scripts/linux/preview-layout.sh "$stage/scripts/linux/"
cp assets/branding/*.png assets/branding/*.svg assets/branding/README.md "$stage/assets/branding/"
cp LICENSE "$stage/"
cp docs/raspberry-pi.md "$stage/docs/"
printf '%s\n' "$version" > "$stage/VERSION"
printf 'pi-datv-rx %s, %s, built from %s\n' "$version" "$(date -u +%F)" "$(git rev-parse --short HEAD)" > "$stage/BUILD-INFO"

tar -C "$out" -czf "$out/pi-datv-rx-$version-arm64.tar.gz" pi-datv-rx
git archive --format=tar.gz --prefix="pi-datv-rx-$version-source/" -o "$out/pi-datv-rx-$version-source.tar.gz" HEAD
git -C "$driver_src" archive --format=tar.gz --prefix=rtl-sdr-driver-source/ -o "$PWD/$out/rtl-sdr-driver-source.tar.gz" HEAD
sed "s/^VERSION=.*/VERSION=$version/" install.sh > "$out/install.sh"
rm -rf "$stage"
(cd "$out" && sha256sum *.tar.gz install.sh > SHA256SUMS)
ls -l "$out"
