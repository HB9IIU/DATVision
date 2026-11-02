# datvrx: a stand-alone DVB-S / DVB-S2 receiver for DATV

`datvrx` is the DVB-S/S2 receiver of [OnAir](https://github.com/adswill/OnAir) taken out of the application: no GUI, no radio drivers,
no FFmpeg. It reads I/Q samples from an SDR (through a pipe) or a recording, finds the carrier, symbol rate, modulation and code rate
by itself, and writes the decoded MPEG transport stream. A player (ffplay, VLC, mpv) shows the picture.

```
SDR program  ──I/Q──▶  datvrx  ──MPEG-TS──▶  ffplay / VLC
(rtl_sdr, hackrf_transfer, rx_sdr)
```

All the DVB-S/S2 code is OnAir's, unchanged. Only `tools/datvrx.cpp`, `core/src/stub/demo_ts_stub.cpp`, the build files, `rust/Cargo.toml`,
`rust/datvrx_rust` and `scripts/` are new.

## 1. Build on Linux (Debian, Ubuntu, Raspberry Pi OS)

```sh
sudo apt install build-essential cmake ffmpeg
curl https://sh.rustup.rs -sSf | sh          # Rust 1.70 or newer (or: sudo apt install cargo, if your distribution's is new enough)
cd datv-rx
cmake -S . -B build
cmake --build build -j
./scripts/selftest.sh                         # no radio needed: must end with SELF-TEST PASSED
```

The program is `build/datvrx`. `-DDATVRX_NATIVE=ON` builds for your own CPU only, which is faster but won't run on other computers.

## 2. Build on Windows 10 / 11

The same compiler as OnAir: MinGW-w64 in MSYS2.

1. Install [MSYS2](https://www.msys2.org) and open the **MSYS2 UCRT64** shell. Run `pacman -Syu` (twice if the window closes).
2. Install the tools: `pacman -S --needed mingw-w64-ucrt-x86_64-{gcc,cmake,ninja,ffmpeg}`
3. Install Rust from [rustup.rs](https://rustup.rs), then in the UCRT64 shell: `rustup target add x86_64-pc-windows-gnu`
   (if `cargo` isn't found there, add it to the path: `export PATH="$PATH:/c/Users/$USER/.cargo/bin"`).
4. Build and test:
   ```sh
   cd /c/path/to/datv-rx
   cmake -S . -B build -G Ninja
   cmake --build build
   ./scripts/selftest.sh
   ```

The result is one `build/datvrx.exe` with no DLLs needed. For a USB SDR, install the WinUSB driver once with [Zadig](https://zadig.akeo.ie).
The SDR programs are in MSYS2 too: `pacman -S mingw-w64-ucrt-x86_64-{rtl-sdr,hackrf,soapysdr}`.

Status of the Windows build: every C++ file was compiled with MinGW-w64 and the Linux build is fully tested, but the complete `.exe`
has not been linked and run yet (the Rust part could not be built for Windows on the test machine).

## 3. Receive

`datvrx` decodes what is at the centre of the input, or the carrier you name with `--carrier`. Status lines go to the terminal,
the video goes to the pipe.

**RTL-SDR** (8-bit unsigned, at most about 2.4 Msps, so up to about 1.4 Msym/s):
```sh
rtl_sdr -f 745250000 -s 2400000 -g 40 - | ./build/datvrx - --rate 2.4 --format cu8 | ffplay -
```

**HackRF** (8-bit signed):
```sh
hackrf_transfer -r - -f 745250000 -s 4000000 -l 32 -g 30 | ./build/datvrx - --rate 4 --format cs8 | ffplay -
```

**Pluto, LimeSDR, Airspy, SDRplay ... through SoapySDR** (`rx_sdr` from rx_tools, 16-bit):
```sh
rx_sdr -d driver=plutosdr -f 745250000 -s 4000000 -F CS16 - | ./build/datvrx - --rate 4 --format cs16 | ffplay -
```

**A recording**: `./build/datvrx capture.cu8 --rate 2.4 -o video.ts`, then play `video.ts`. The format is taken from the file name
(`.cu8`, `.cs8`, `.cs16`, `.cf32`) or given with `--format`.

**VLC instead of ffplay**: `... | vlc -`

### QO-100 with a 9750 MHz LNB

The radio's frequency is the downlink minus 9750 MHz. The wideband transponder (10491 to 10499 MHz) becomes **741 to 749 MHz**:

| Downlink | Radio | What |
|---|---|---|
| 10491.500 MHz | 741.500 MHz | the beacon, DVB-S2 1.5 Msym/s: needs 4 Msps or more (not an RTL-SDR) |
| 10492.750 to 10499.250 MHz | 742.750 to 749.250 MHz | the stations: often 333, 500 or 1000 kS/s |

An LNB's oscillator is often 10 to 50 kHz off. The receiver searches ±a few tens of kHz by itself, so tune to the nominal frequency first.

### Useful options

| Option | What it does |
|---|---|
| `--rs 0.333` | the symbol rate in Msym/s. Needed for narrow carriers: the automatic search works down to about 60 kS/s at 2.4 Msps, 244 kS/s at 10 Msps |
| `--carrier 300` | decode the carrier 300 kHz above the centre. Useful with an RTL-SDR: tune 300 kHz below the station so its centre spike isn't on the carrier |
| `--std 2` | DVB-S2 only (`1` DVB-S only); a bit quicker to lock |
| `-o file.ts` | write the stream to a file instead of the pipe |
| `--secs 60` | stop after 60 s of signal |
| `--quiet` | no status lines |

`./build/dvbstool ranges` lists the symbol rates each sample rate allows. `./build/dvbstool gen ...` makes test signals (see the top of `tools/dvbstool.cpp`).

### Reading the status line

```
datvrx [  12.0 s] DVB-S2 QPSK 2/3 pilots  1.000 Msym/s  Es/N0 5.9 dB  ok 562  bad 1  packets 16050  TS lock, TS packets 16079
```

`Es/N0` is the signal quality. `ok` / `bad` count error-corrected frames: a few bad while it locks is normal, a steadily climbing `bad`
means the signal is too weak or the computer is too slow. `TS lock` means video is flowing.

## What is inside

| Part | Files | From OnAir |
|---|---|---|
| The receiver: carrier and symbol-rate search, timing and carrier recovery, DVB-S (Viterbi, Reed-Solomon) and DVB-S2/S2X (LDPC, BCH) | `core/src/dvbs/` | unchanged |
| Shared error correction | `core/src/dsp/ldpc.cpp`, `core/src/t2/`, `core/src/dvbt/`, `core/src/fec/`, `rust/onair_fec` | unchanged |
| Transport stream tables (service names and more) | `core/src/tv/`, `rust/onair_ts`, `rust/onair_dvbsi` | unchanged |
| GPU decoders | `core/src/dsp/gpu_*_stub.cpp` | OnAir's own "no GPU" versions: everything runs on the CPU |
| Command-line program | `tools/datvrx.cpp` | new |

Measured on a 2-core Xeon at 2.1 GHz: 2.4 Msps live from a pipe uses about a third of one core; a 10 Msps capture decodes about 2.8 times
faster than real time.

## Licence and credits

GPL-3.0-or-later, like OnAir (`LICENSE`). The receiver is the work of the OnAir project (https://github.com/adswill/OnAir); its GSE, IP and
S2X super-frame parts are adapted there from DecSAT by Arclamp (GPL-3.0-or-later). If you share this program, keep this notice and the
licence, and offer the source code with it.
