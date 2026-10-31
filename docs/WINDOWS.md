# Windows development and trials

Run commands from the project root. The receiver uses MinGW-w64 UCRT64,
not Visual Studio's C++ compiler. C++20, CMake, Ninja and Rust are required.

## Tool setup

Install MSYS2 and Rust using their official installers. In the MSYS2 UCRT64 shell:

```sh
pacman -Syu
pacman -S --needed mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-ffmpeg mingw-w64-ucrt-x86_64-rtl-sdr
```

Restart the shell if the update requests it. In PowerShell:

```powershell
rustup toolchain install stable-x86_64-pc-windows-gnu --profile minimal
./scripts/windows/build.ps1
```

For MSYS2 outside `C:\msys64`, pass `-MsysRoot 'D:\msys64'`.
Outputs are `build/windows/datvrx.exe` and `build/windows/dvbstool.exe`.

The build helper selects `stable-x86_64-pc-windows-gnu` for its process without
changing Rustup's global default. To use another installed compatible toolchain,
pass `-RustToolchain 'toolchain-name'` (it must have the Windows GNU target).
This avoids a damaged default MSVC Rust installation found on this PC: Rustup
listed its compiler component as installed, but its `rustc.exe` was missing.

## First test without hardware

In the MSYS2 UCRT64 shell, change into the project root, then:

```sh
DATVRX_BIN=build/windows bash scripts/selftest.sh
```

This generates a temporary video and noisy DVB-S2 I/Q signal, decodes them,
and requires more than 300 video frames. Temporary files are removed on exit.

## Recordings and live RTL-SDR

### Verified beacon setup

With the tested dish feed and 9750 MHz LNB oscillator, double-click
`Live Beacon.cmd` in the project root, or run:

```powershell
./scripts/windows/live-beacon.ps1
```

This tunes 741.500 MHz at 2.56 MSps and decodes the 1.5 MSym/s beacon into a
640 x 360 resizable, movable, always-on-top ffplay window. Audio starts at 30%.
Press Q or close the video window to stop; F toggles full screen and M mutes.
Diagnostics are saved as `test-output/live-beacon-*.log`. The launcher rejects
starting while another `rtl_sdr`, `rtl_iq` or `datvrx` process is running.
The Windows build also produces `rtl_iq.exe`, an optional SDR source linked to
MSYS2's librtlsdr. It explicitly writes binary output; the installed `rtl_sdr`
version inserted extra bytes in our Windows stdout-pipe test. Live playback
uses native Windows pipes and blocking decode to avoid dropping queued frames.

### Other signals

A recording can be decoded directly from PowerShell:

```powershell
./build/windows/datvrx.exe captures/example.cu8 --rate 2.4 -o test-output/video.ts
```

Create the `captures` and `test-output` directories before using them. They are
ignored by Git because recordings and results can be large.

For binary live pipelines, use the MSYS2 UCRT64 shell:

```sh
./build/windows/rtl_iq.exe 745250000 2400000 40.2 | ./build/windows/datvrx.exe - --rate 2.4 --format cu8 --no-realtime | ffplay -
```

Adjust frequency and gain for the actual signal. USB access may require the
WinUSB driver installed with Zadig. Validate synthetic reception before RF trials.
