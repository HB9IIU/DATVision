#!/usr/bin/env bash
# Self-test without a radio: makes a test video, turns it into a simulated QO-100 style DVB-S2 signal
# (1 Msym/s, QPSK 2/3, Es/N0 6 dB, 35 kHz off tune, 8-bit RTL-SDR samples), receives it with datvrx and checks the video.
# Works on Linux and in the MSYS2 UCRT64 shell on Windows. Needs ffmpeg for the test video and the check.
set -euo pipefail
cd "$(dirname "$0")/.."
BIN=${DATVRX_BIN:-build}
[ -x "$BIN/datvrx" ] || [ -x "$BIN/datvrx.exe" ] || { echo "build first: cmake -S . -B build && cmake --build build"; exit 1; }
command -v ffmpeg >/dev/null || { echo "ffmpeg is needed for this test"; exit 1; }
T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT

echo "1/4 making a 15 s test video (H.264 + AAC in MPEG-TS)"
ffmpeg -loglevel error -y -f lavfi -i testsrc2=size=640x360:rate=25 -f lavfi -i sine=frequency=1000 -t 15 \
  -c:v libx264 -preset veryfast -b:v 500k -maxrate 500k -bufsize 500k -g 25 -c:a aac -b:a 32k \
  -f mpegts -muxrate 1000k -metadata service_name="SELFTEST" "$T/in.ts"

echo "2/4 turning it into a DVB-S2 signal with noise"
"$BIN/dvbstool" gen "$T/sig.cs8" --secs 15 --rate 2.4 --std s2 --mod qpsk --fec 2/3 --rs 1.0 --roll 0.25 --pilots \
  --snr 6 --cfo -35000 --ts "$T/in.ts" --format cs8 | tail -1

echo "3/4 receiving it with datvrx"
"$BIN/datvrx" "$T/sig.cs8" --rate 2.4 --format cs8 -o "$T/out.ts" 2>&1 | grep -v '^datvrx \['

echo "4/4 checking the received video"
frames=$(ffmpeg -loglevel fatal -i "$T/out.ts" -map 0:v -f null - -stats 2>&1 | tr '\r' '\n' | grep -o 'frame= *[0-9]*' | tail -1 | grep -o '[0-9]*' || echo 0)
echo "video frames decoded: $frames (the generator repeats the 15 s clip where the channel has room, so this can be above 375)"
if [ "${frames:-0}" -gt 300 ]; then echo "SELF-TEST PASSED"; else echo "SELF-TEST FAILED"; exit 1; fi
