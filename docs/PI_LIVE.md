# Live video from the Pi on Windows

Double-click **Pi DATV Player.cmd** on the Windows PC. The title and heading
identify **Pi pidatvmon**, so it is clear which machine owns the radio.

```text
Pi: RTL-SDR -> rtl_iq -> datvrx -> MPEG-TS over SSH -> PC: ffplay in the compact player
```

The clickable BATC spectrum remains on the PC. Clicking a signal restarts the
remote pipeline with the detected RF frequency and symbol rate. Diagnostics
return over SSH stderr; video uses binary stdout. Only the decoded transport
stream crosses the network, rather than the much larger raw I/Q stream.

Requirements: the Pi must be reachable through SSH alias `pidatvmon`, the powered
LNB feed must be connected to the Pi's RTL-SDR, and the private V4-compatible
driver and receiver must have been built there. The project path used by this
first remote implementation is `~/projects/pi-datv-rx`.

## V4 driver on this Pi

The Pi's original 2022 librtlsdr did not properly support this Blog V4: it
reported PLL tuning errors, and the first capture did not yield a transport
stream. Built Osmocom rtl-sdr 2.0.3, commit
`797f8143266d983c56d8f35d2d442527529dd8a5`, in
`~/projects/datvrx-rtl-driver` and installed it under `~/.local/datvrx-rtl`.
System packages were left installed.

To reproduce the private driver build on a machine with git, cmake, a C compiler,
pkg-config and libusb development files:

```sh
git clone https://github.com/osmocom/rtl-sdr ~/projects/datvrx-rtl-driver
git -C ~/projects/datvrx-rtl-driver checkout 797f8143266d983c56d8f35d2d442527529dd8a5
cmake -S ~/projects/datvrx-rtl-driver -B ~/projects/datvrx-rtl-driver/build -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$HOME/.local/datvrx-rtl" -DINSTALL_UDEV_RULES=OFF -DDETACH_KERNEL_DRIVER=ON
cmake --build ~/projects/datvrx-rtl-driver/build --parallel 2
cmake --install ~/projects/datvrx-rtl-driver/build
```

`DETACH_KERNEL_DRIVER=ON` lets the driver release the stick from the kernel's
DVB-T TV driver itself, so a fresh system works before the blacklist applies.

Then configure our optional capture helper to use that library:

```sh
cd ~/projects/pi-datv-rx
. ~/.cargo/env
cmake -S . -B build/linux -DDATVRX_RTL=ON -DRTLSDR_INCLUDE_DIR="$HOME/.local/datvrx-rtl/include" -DRTLSDR_LIBRARY="$HOME/.local/datvrx-rtl/lib/librtlsdr.so"
cmake --build build/linux --parallel 2
```

The existing CMake cache retains the library selection on subsequent builds.
Manufacturer driver reference: https://www.rtl-sdr.com/v4/

## First verification — 31 October 2026

- Updated library identified `RTL-SDR Blog V4` correctly.
- A 15-second capture at 741.500 MHz / 2.56 MSps yielded 23,074 TS packets;
  DVB-S2 QPSK 4/5, 1.5 MSym/s, final Es/N0 9.3 dB. Reacquisitions occurred;
  final FEC counters describe the last lock and are not whole-run totals.
- Started the compact Windows player in Pi mode. Verified ARM receiver and USB
  source processes on the Pi, and visually verified beacon launch footage in
  the embedded Windows video panel.
- Initial live reports reached 212 good FEC frames / zero bad at five seconds,
  Es/N0 8.9 dB. Startup video diagnostics included missing-reference/concealment
  messages. Sustained clean playback remains a separate stability test.
- Windows player build and all 12 signal-finder/request checks passed.
- Right-click Stop was verified: the Pi's `rtl_iq` and `datvrx` processes exited.
  Clicking the beacon spectrum was then used to restart remote reception.

Logs: Windows `test-output/player-receiver.log` and `player-video.log`; Pi
`test-output/pi-radio-v4.log` and `pi-beacon-v4-decode.log`. No password is stored
in source, launcher arguments or logs.
