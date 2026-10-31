# Local validation — 2026-10-31

## Restructuring checks

- Inventoried all 170 files in the supplied source package.
- Copied the complete source package into the project root.
- Compared SHA-256 hashes: all original files except the intentionally updated
  root README, .gitignore and self-test script match their source counterparts.
- Preserved the original README byte-for-byte in `docs/UPSTREAM_README.md`.
- Confirmed no references to the temporary folder in CMakeLists.txt, core/,
  rust/, tools/ or scripts/.
- CMake successfully configured and generated the Windows build from the root
  using GNU C/C++ 15.2.0 and Ninja. Its recorded source directory is the project
  root, outside the temporary folder.
- Bash syntax checks passed for the self-test and Linux build script.

The original temporary folder is not an input to the restructured project and
may be removed. It has been left intact for the owner to delete.

## Windows build and reception test — passed

Installed the `x86_64-pc-windows-gnu` Rust standard-library target needed by the
existing MinGW build. Compilation then stopped when Cargo invoked `rustc`:
the active `stable-x86_64-pc-windows-msvc` toolchain lists its compiler component
as installed, but `bin/rustc.exe` is missing. `rustc --version` also fails outside
the build. `rustup component add rustc` reports the component is up to date.

Installed a separate `stable-x86_64-pc-windows-gnu` toolchain, Rust 1.99.1.
The Windows build helper selects this toolchain without changing the global
default. The damaged MSVC toolchain remains installed; attempting to remove its
compiler component also failed because the executable was already missing.

The full build then exposed a missing Windows-only `dedicatedMemoryMb()`
definition in the supplied no-GPU LDPC stub. Added a zero return value for that
stub, consistent with its unavailable GPU backend. No receiver algorithm changed.

Successfully built `build/windows/datvrx.exe` and `build/windows/dvbstool.exe`
with GNU C/C++ 15.2.0, Rust 1.99.1 and Ninja, using the default Release settings.
`datvrx --help` and `dvbstool ranges` also run successfully from PowerShell.
Inspection of the receiver's PE imports found Windows system DLLs only, with
no MinGW runtime DLL imports.

Ran `DATVRX_BIN=build/windows bash scripts/selftest.sh` in MSYS2:

| Item | Result |
| --- | --- |
| Generated signal | DVB-S2 QPSK 2/3, pilots, 1 Msym/s, 2.4 Msps |
| Channel | Es/N0 6 dB, carrier offset -35 kHz, signed 8-bit I/Q |
| Duration | 15 seconds |
| Acquisition | Automatic carrier and symbol rate; transport-stream lock |
| Measured Es/N0 | 6.1 dB |
| Frames reported by receiver | 443 good, 1 bad |
| Transport-stream packets | 12,651 |
| Video frames decoded by FFmpeg | 459 (pass threshold: more than 300) |
| Final result | SELF-TEST PASSED |

The local test log is `build/windows/selftest.log` (generated, ignored by Git).
The synthetic test's temporary media files were removed by its cleanup trap.

GCC emitted `-Wstringop-overflow` warnings in existing vector operations in
`dvbs_vlsnr.cpp` and `dsmcc.cpp`. These were not suppressed or changed. The test
does not validate S2X VL-SNR or DSM-CC functionality; those warnings remain to
be investigated if that functionality enters scope.

## Still to validate

- Continuous live RTL-SDR-to-player pipeline on Windows.
- Native build, synthetic test and real-time performance on the chosen remote Pi.
- S2X behavior for any specific modes required later.

## First RF reception on Windows — passed

RTL-SDR Blog V4 (R828D tuner, serial 00000001), WinUSB driver, standard
9750 MHz LNB feed. Installed MSYS2's `mingw-w64-ucrt-x86_64-rtl-sdr` package.
Captured 30 seconds at 742.752 MHz, 2.4 Msps, gain 40.2 dB, unsigned 8-bit I/Q.
Decoded the recording with `--rs 0.5 --std 2`.

- Service identified in the recovered transport stream: **2E0ILY**.
- DVB-S2 QPSK 2/3, 500 kS/s, reported roll-off 0.20.
- Carrier offset recovered: -54.2 kHz; final reported Es/N0: 7.1 dB.
- 457 good FEC frames, zero bad; 13,052 transport-stream packets.
- FFmpeg decoded 1,735 video frames; HEVC 1280 x 720 video and AAC audio detected.
- Extracted and visually checked a clear frame showing the 2E0ILY bird feeder.
- FFmpeg reported an initial HEVC PPS warning; input-side seeking during an
  initial frame extraction also produced missing-reference warnings. Extracting
  after decoding from the beginning produced a clear frame.

Generated files (ignored by Git): `captures/qo100-742752000-2400000.cu8`,
`test-output/qo100-500ks.ts`, `test-output/qo100-frame.png` and capture/decode logs.
This establishes reception and offline decoding of a real RF recording; a
continuous live playback pipeline remains to be tested.

## Beacon trial at 2.56 MSps — passed

Captured 20 seconds at 741.500 MHz, 2.56 MSps, gain 40.2 dB, using the same
RTL-SDR Blog V4 and LNB feed. The unmodified receiver rejected the requested
1.5 MSym/s because acquisition always checked bandwidth using roll-off 0.35,
even when `--roll` specified another value. Updated that check to honor an
explicit roll-off; automatic operation still defaults to 0.35.

Decoded the same recording after rebuilding:

```powershell
./build/windows/datvrx.exe captures/beacon-741500000-2560000.cu8 --rate 2.56 --format cu8 --rs 1.5 --std 2 --roll 0.25 -o test-output/beacon.ts
```

- Service **A71A**, provider **QARS**; DVB-S2 QPSK 4/5 at 1.5 MSym/s.
- Stream reported roll-off 0.20; final Es/N0 8.6 dB, carrier offset -54.6 kHz.
- 912 good FEC frames, zero bad; 31,269 transport-stream packets.
- H.264 1280 x 720 video, MP2 audio; FFmpeg decoded 965 video frames.
- Visually checked `test-output/beacon-frame.png`: clear launch footage.
- Synthetic self-test rerun after the change: passed, 459 video frames
  (442 good / 2 bad FEC frames, 12,624 packets in this run).

Capture and logs are retained under `captures/` and `test-output/`, ignored by
Git. This establishes beacon decoding from this stick's recording at 2.56 MSps;
it does not yet establish long-duration, continuous live playback.
The supplied upstream README's categorical claim that this beacon needs 4 MSps
does not apply to this verified configuration.

## Live beacon window

Added `Live Beacon.cmd` and the Windows launcher/pipeline scripts. ffplay opens
a movable, resizable, always-on-top 640 x 360 window with volume at 30%.
The receiver uses blocking decode, and the final pipeline uses native Windows
pipes. Window title observed: `QO-100 Beacon - A71A - LIVE`.

A one-second stdout test of the packaged Windows `rtl_sdr` produced 5,120,032
bytes instead of 5,120,000. Added optional `rtl_iq`, a minimal librtlsdr source
that explicitly selects binary stdout and uses asynchronous USB reads. Its
one-second binary-output check produced exactly 5,120,000 bytes. The helper is
built by the Windows build script using `DATVRX_RTL=ON`; Linux builds keep it
optional and can continue using their normal rtl_sdr utility.

Initial live trials had synchronisation losses. After switching to the binary
helper and native pipes, initial receiver reports reached 214 good frames with
zero bad frames at 5 seconds, Es/N0 9.3 dB. ffplay opened its named window.
Long-duration stability and user confirmation of smooth playback remain pending.
Closing the player in earlier trials caused the receiver and source to exit.
