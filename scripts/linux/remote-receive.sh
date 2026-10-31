#!/usr/bin/env bash
# SSH transport: only MPEG-TS on stdout; local Pi receiver diagnostics on stderr.
set -euo pipefail
cd "$(dirname "$0")/../.."
if [[ $# != 3 || ! $1 =~ ^[0-9]+$ || ! $2 =~ ^[0-9]+$ || ! $3 =~ ^[0-9]+$ ]]; then
  echo 'usage: remote-receive.sh frequency_Hz sample_rate_Hz symbol_rate_kSps' >&2
  exit 1
fi
rate=$(awk -v rate="$2" 'BEGIN { printf "%.6f", rate / 1000000 }')
symbols=$(awk -v symbols="$3" 'BEGIN { printf "%.6f", symbols / 1000 }')
./build/linux/rtl_iq "$1" "$2" 40.2 |
  ./build/linux/datvrx - --rate "$rate" --format cu8 --rs "$symbols" --std 0 --roll 0.25 --no-realtime
