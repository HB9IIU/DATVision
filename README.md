# DATV receiver — Windows trials, then Raspberry Pi

Standalone DVB-S/DVB-S2 receiver derived from OnAir, with existing S2X code.
Reads SDR I/Q samples from a recording or pipe and writes MPEG transport-stream
packets for playback with ffplay or VLC. There is no GUI or built-in SDR driver.

```text
rtl_sdr / I/Q recording -> datvrx -> MPEG-TS file / player
```

This directory is the self-contained working project. The original temporary
folder is no longer needed by the build or receiver.

## Start on Windows

Install the prerequisites in [docs/WINDOWS.md](docs/WINDOWS.md), then run:

```powershell
./scripts/windows/build.ps1
```

From an MSYS2 UCRT64 shell in this directory:

```sh
DATVRX_BIN=build/windows bash scripts/selftest.sh
```

Windows build and synthetic DVB-S2 reception have passed on this PC; see the
validation notes for results and remaining hardware tests.

For the clickable-spectrum Windows interface, see [the player guide](docs/PLAYER.md).
Build it with `scripts/windows/build-player.ps1`, then start `RTL DATV Player.cmd`.

## Later: remote Raspberry Pi

Copy the source to the Pi and build natively there:

```sh
bash scripts/linux/build.sh
DATVRX_BIN=build/linux bash scripts/selftest.sh
```

See [docs/REMOTE_PI.md](docs/REMOTE_PI.md) for prerequisites and the staged trial
process. The Pi's performance and live RF reception still need to be measured.

## Project references

- [Project structure and source origin](docs/PROJECT_STRUCTURE.md)
- [Local validation](docs/VALIDATION.md)
- [Original README: options, sample formats and reception examples](docs/UPSTREAM_README.md)

The core is C++20 with a Rust static library. The Rust workspace uses local
crates; CMake builds it with Cargo in offline mode. The same receiver source
is used on Windows and Linux. Build products belong under `build/`.

## Licence and credits

GPL-3.0-or-later; see [LICENSE](LICENSE). The receiver originates from
[OnAir](https://github.com/adswill/OnAir). Its GSE, IP and S2X super-frame parts
are adapted there from DecSAT by Arclamp. Preserve the original copyright and
licence notices and provide the corresponding source when distributing binaries
as required by the licence.
