#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
if ! command -v cargo >/dev/null 2>&1 && [ -f "$HOME/.cargo/env" ]; then
  . "$HOME/.cargo/env"
fi
# Build natively on the Linux PC or remote Pi. Override for smaller-memory Pis.
# The stick tools need the RTL-SDR V4 driver (docs/building.md); without it only the receiver is built.
rtl=()
driver="$HOME/.local/datvrx-rtl"
if [[ -f $driver/lib/librtlsdr.so && -f $driver/include/rtl-sdr.h ]]; then
  rtl=(-DDATVRX_RTL=ON -DRTLSDR_INCLUDE_DIR="$driver/include" -DRTLSDR_LIBRARY="$driver/lib/librtlsdr.so")
fi
cmake -S . -B build/linux -DCMAKE_BUILD_TYPE=Release "${rtl[@]}"
cmake --build build/linux --parallel "${BUILD_JOBS:-2}"
