# Raspberry Pi native build and synthetic reception — passed

Date: 31 October 2026.

## Target

- `daniel@pidatvmon.local`, SSH alias `pidatvmon`.
- Raspberry Pi 5 Model B Rev 1.0, 4 GB RAM, aarch64.
- Debian 12 Bookworm; GNU C/C++ 12.2.0 and CMake 3.25.1.
- Rust 1.99.1 / Cargo installed with the official Rustup minimal profile for
  `aarch64-unknown-linux-gnu` in the user's home directory. No sudo was needed.
- Existing FFmpeg provides libx264 and AAC encoding for the test.
- Source: `/home/daniel/projects/pi-datv-rx`.

## Build

```sh
bash scripts/linux/build.sh
```

Built the native ARM64 `build/linux/datvrx` and `build/linux/dvbstool`, Release
configuration, with two parallel compile jobs. The Linux build helper loads
`~/.cargo/env` when needed, including from an already-open VS Code task.

The build emitted GCC `-Wstringop-overread` warnings in existing vector insert
operations in `dvbs_ip.cpp` and `rcs.cpp`. These warnings were not suppressed or
changed. The synthetic test does not establish correctness of those optional
IP/return-channel paths.

## End-to-end test

```sh
DATVRX_BIN=build/linux bash scripts/selftest.sh
```

| Item | Result |
| --- | --- |
| Generated signal | DVB-S2 QPSK 2/3, pilots, 1 MSym/s |
| Input | 2.4 MSps, signed 8-bit I/Q, 15 seconds |
| Channel | Es/N0 6 dB, -35 kHz carrier offset |
| Acquisition | Automatic symbol rate and carrier; TS lock |
| Receiver result | 443 good FEC frames, 1 bad |
| Transport stream | 12,651 packets |
| Video decoded | 459 frames, threshold more than 300 |
| Final result | SELF-TEST PASSED |

The command completed successfully over SSH. Generated test media were cleaned
up by the test script. Logs are retained on the Pi in `test-output/pi-build.log`
and `test-output/pi-selftest.log`; copies are also saved in the Windows project's
`test-output/pi/` directory, ignored by Git.

During compilation the Pi reported about 56°C and `throttled=0x0`.

## Remaining work

An RTL2838 USB device was visible on the Pi. USB access, its driver compatibility
and live RF reception have not been tested in this stage. Native synthetic
decoding is now verified; live reception, sustained real-time performance and
remote tuning/video integration are the next stage.

Installer reference: https://rust-lang.org/tools/install/
