# Project structure and origin

| Path | Purpose |
| --- | --- |
| `core/`, `rust/` | The DVB-S/S2 receiver from OnAir: C++ DSP, demodulation, FEC and transport stream, with its Rust crates |
| `tools/datvrx.cpp` | `datvrx`, the stand-alone receiver: I/Q in, MPEG transport stream out ([receiver.md](receiver.md)) |
| `tools/rtl_iq.cpp` | Binary-safe RTL-SDR I/Q source for `datvrx` |
| `tools/rtl_sweep.cpp` | Stitched RTL-SDR sweep for the local spectrum |
| `tools/dvbstool.cpp` | Test signal generator and offline receiver |
| `ui/pi_player/` | Raspberry Pi touchscreen player (Python, PyQt5, mpv) |
| `ui/DatvRx.Player/` | Windows application (C#, .NET WinForms) |
| `assets/branding/` | DATVision logo, icon and banner |
| `install.sh` | Raspberry Pi installer, updater and uninstaller |
| `scripts/linux/` | Pi build, start, layout preview and release scripts |
| `scripts/windows/` | Windows build, start and portable packaging scripts |
| `scripts/selftest.sh` | Synthetic end-to-end DVB-S2 reception test |
| `tests/` | Signal finder and player checks (Python and .NET) |
| `CMakeLists.txt` | Shared Windows and Linux build of the receiver and tools |
| `build/` | Generated outputs, ignored by Git |

## Origin

The receiver is OnAir by Kristijonas Adomaitis (adswill),
https://github.com/adswill/OnAir, with LDPC decoder optimizations by Evariste
Courjaud (F5OEO) and DVB-S/S2 contributions by Arclamp (GSE, IP and S2X
super-frame parts adapted from DecSAT). Its algorithms are used unchanged, with
two small fixes: the Windows no-GPU stub defines its GPU-memory query, and
acquisition honours an explicit roll-off hint, which the 1.5 MSym/s QO-100
beacon at 2.56 MS/s needs. The original README is kept as
[receiver.md](receiver.md).

The BATC spectrum and the original signal finder come from HB9IIU Experimental
H.266 Player; the magic eye follows HB9IIU SDR-Tiouner, inspired by the Lynx
DATV Receiver (G8YTZ).

## Licence

GPL-3.0-or-later; see [LICENSE](../LICENSE). Keep the original copyright and
licence notices and provide the corresponding source when distributing
binaries. The Raspberry Pi release bundles Osmocom rtl-sdr (GPL-2.0) with its
source.
