# DATVision on Windows

DATVision on a Windows 10/11 PC with an RTL-SDR stick attached to it. Download
the portable ZIP from the release page, extract the whole folder to a writable
place and open **DATVision.exe**; no installation is needed. The RTL-SDR needs
the WinUSB driver, installed once with Zadig. From the source tree, start
**DATVision.cmd** after building ([building.md](building.md)).

## Layout and operation

- Fixed-size monitor window; optional always-on-top. Raised buttons provide hover and pressed feedback.
- Left: receiver state, station/provider from the received stream, frequency,
  symbol rate, modulation/FEC, Es/N0, approximate D reserve, codecs and errors.
- Bottom-left: magic-eye-style indicator; click it for the live constellation.
- Centre: ffplay video embedded in the window. Double-click the video area to
  open a separate resizable video window. Close it, press Escape, or double-click
  the video again to return it to the monitor without restarting reception.
- Right: sound/mute, volume − / +, beacon, Start/Stop/Rescan, Bands, Settings,
  Quit. Volume uses the ffplay process's Windows audio session and does not
  restart reception.
- Startup tunes the QO-100 beacon at 10491.500 MHz, 1500 kS/s, and selects
  the BATC spectrum reference. Offline BATC reconnects independently of reception.
- Bottom: BATC QO-100 reference, or local SDR spectrum selected in Settings.
  Click a signal to estimate its frequency and symbol rate. The selected carrier
  is filled beneath its live contour: cyan immediately on selection, violet after transport lock while waiting for video,
  and steady green once the embedded video window is visible. Brief telemetry
  changes do not reset the playing colour; sustained signal loss returns to violet.
  The current stage is also labelled above the spectrum.

Local scanning and decoding share one stick. While watching a station the sweep
is paused and dimmed; click it or press RESCAN to scan again. Bands wider than
10 MHz zoom to a 3 MHz window on the first click. Use Bands again to reset it.

Band presets: QO-100 (9750 MHz LNB by default), 4 m, 2 m, 70 cm, 23 cm A/B,
and 6 cm with a 5200 MHz converter preset. These are tuning presets; an antenna,
LNB or converter appropriate to the signal is required. Generic SDR support is
not claimed: this first version uses librtlsdr / RTL-SDR-compatible sticks.

Settings contains spectrum source, LNB frequency, RF gain, always-on-top,
recording playback and source credits. The main window has no editable frequency
or symbol-rate fields. Signals wider than 1500 kS/s are rejected for this setup.

## Build and prerequisites

```powershell
./scripts/windows/build.ps1
./scripts/windows/build-monitor.ps1
```

See `WINDOWS.md` for MSYS2 UCRT64, Rust GNU and RTL-SDR dependencies. The current
development launcher uses MSYS2's ffplay and RTL-SDR runtime libraries; it is not
yet a packaged installer for other PCs. UI requires .NET 10 Windows Desktop.

Native outputs: `build/windows/datvrx.exe`, `rtl_iq.exe`, `rtl_sweep.exe`.
Interface output: `build/windows/monitor/DatvRx.Player.exe`. It is published
separately from the older player so the current preview can stay open during a
build. Close it before starting the new monitor; only one player owns the SDR.

Settings are saved in `windows-monitor-settings.json`; diagnostic logs in
`test-output/player-*.log`. These generated files are ignored by Git.

Use Settings → Open TS recording to review a saved `.ts` file. Replay is clearly
marked **REPLAY / recorded reception**, with no invented RF quality figures.

## Source and validation

Developed on branch `windows-datv-monitor`. The latest Pi layout and three native
helper sources were inspected/copied read-only; no changes were made to the Pi
checkout, tuning, settings or running applications.

The C# interface follows the Pi's information/video/control/spectrum arrangement.
Local sweep and constellation output were ported from the Pi source. Windows
helpers support stdin-close shutdown to release the SDR cleanly before retuning.

Windows native and UI builds pass. Synthetic DVB-S2 video self-test passes
(459 decoded video frames). The expanded C# checks cover local spectrum finding,
sweep protocol validation, telemetry/D arithmetic, fragmented PAT/PMT/SDT
parsing, and metadata from a real beacon recording, in addition to the original
signal-finder checks. Live USB behaviour and visual checks are logged below.

### First live preview — 1 November 2026

- Verified the floating Windows information/video/control/spectrum layout on
  screen. Corrected column sizing found during the first visual inspection.
- Real local SDR sweep ran on the PC, with no Pi receiver or SSH process.
- A click on the local beacon spectrum switched from the scanner to the local
  decoder. Further live tuning received **F6EXX**, 333 kS/s, DVB-S2 QPSK 3/5,
  H.265/AAC; station/provider readouts and a clear live picture were verified.
- At one observed lock: Es/N0 6.9 dB, zero bad frames and zero reported sync
  losses. The eye and D display reflected that local reception.
- All 22 C# checks pass, including real recording metadata. The native synthetic
  reception test also passes with 459 video frames.
- Final build is in `build/windows/monitor`, published separately while the
  interactive preview remained open. Reopen with `DATVision.cmd` to
  use the final Rescan action and panel styling changes.

This is a first Windows prototype, not full parity with the later Pi player:
Automatic USB replug recovery is not included. Broader band/hardware coverage and long-duration stability
remain to be tested. This work did not update the Pi checkout.

## Credits

GPL v3; see root LICENSE and README. Receiver is derived from OnAir. Layout,
scan helper and receiver telemetry build on HB9IIU's Pi player. The existing
BATC spectrum code and Windows audio-session code come from HB9IIU Experimental
H.266 Player (https://github.com/HB9IIU/HB9IIU-Experimental-H266-Player).
Magic-eye inspiration follows the Pi's attribution to HB9IIU SDR-Tiouner and
the Lynx DATV Receiver by G8YTZ. D/Es/N0 thresholds are approximate AWGN
references, not a calibrated hardware measurement. Some S2X modes have no D
reference and display a dash.

## Frequency calibration

Settings → Calibrate frequency using beacon measures the local QO-100 beacon, using the
same procedure as the newer Linux player: five tunes at −100, −50, 0, +50 and
+100 kHz, one second to settle after lock, then eight tracked carrier-offset
readings per step. Each step has a 12-second measurement timeout. The median
rejects isolated outliers. At least three steps must agree within 5 kHz.

Settings shows one **Effective LO · MHz** value: the total downconversion
frequency including combined downconverter and SDR frequency error. The SDR tune
frequency is `RF − effective LO` (MHz). Both older oscillator settings and the
split nominal/correction settings migrate without changing the tuned frequency.

Calibration determines `assumed effective LO − measured CFO − deliberate detune`.
A reliable result is proposed in Settings with the measured spread. Save settings
applies it and retunes the current station. Cancel discards it. Failure or
cancellation leaves the saved frequency settings unchanged and restores previous reception.
Calibration is available on QO-100 during live reception or scanning, not replay.
The estimate includes the combined frequency error of the LNB and SDR reference.

## Transmission ending while watching

Brief fades keep the receiver running. After three seconds of sustained loss,
the frozen picture is covered by a signal-lost notice, quality readings clear,
and the carrier becomes amber. Detection uses decoder lock and recent transport
stream activity. LOCAL shows a countdown and resumes scanning after 30 seconds;
BATC stays tuned waiting for the station to return. Returning reception clears
the notice automatically. A recording stays open during the wait and closes when
scanning resumes or the user retunes or quits. Receiver process failures are
reported separately as receiver errors. Replay is excluded from this policy.

## UDP streaming to VLC

Open Settings → UDP streaming, tick Enable UDP streaming, then choose Use these
settings and Apply and save in the main Settings dialog. Streaming is off by
default and follows the current live station, including retunes. Local video
and recording continue normally. Replay files are not forwarded.

The default destination is `udp://239.255.42.42:1234` (IPv4 multicast, TTL 1).
Copy VLC address gives `udp://@239.255.42.42:1234`. On another PC on the same LAN,
open VLC → Media → Open Network Stream, paste that address and Play. Multiple
PCs can receive the same multicast stream. The sender uses the Windows-selected
network route; VPNs or several active adapters may require route configuration.

Allow VLC on the receiving PC's private-network firewall. The LAN must pass
multicast; guest Wi-Fi, client isolation and some access points can block it.
If multicast is unavailable, enter a single receiving PC's IPv4 address instead,
for example `udp://192.168.1.42:1234`; Copy VLC address then gives `udp://@:1234`
to open on that receiving PC. No router port forwarding is needed on the LAN.

The original MPEG transport stream is forwarded without transcoding, seven
188-byte TS packets per UDP datagram (1316 bytes). A bounded independent sender
queue prevents a slow network from stalling local reception. UDP can lose data,
and playback starts once VLC receives stream tables and a video keyframe.

## Portable GitHub release assets

Build with `python scripts/windows/package-portable.py --version 0.1.0-preview.1`.
The script builds native tools and a self-contained single-file .NET Windows x64
app, follows native DLL imports recursively, includes exact MSYS2 source packages
and license notices, and captures the actual project working tree as source.
Use `--resume` after a failed download; cached source archives are reused.
Build inputs: Python 3, .NET 10 SDK, MSYS2 UCRT64 tools/libraries, GNU Rust target.
Native builds must use `DATVRX_NATIVE=OFF` for portable CPU compatibility.

Outputs in `dist/<version>/`: portable ZIP, application source ZIP, third-party
source ZIP and SHA256SUMS.txt. Upload all four together as GitHub release assets;
GitHub's automatic source ZIP alone does not contain uncommitted changes.
No publishing is performed by the packaging script. The portable package runs
DATVision.exe directly with bundled .NET, FFmpeg and receiver libraries; the
RTL-SDR still needs the Windows WinUSB driver. Do not copy user settings,
recordings or logs into a public release. Store the extracted app in a writable
folder. Preview builds are unsigned. `DATVision.exe --check-installation`
reports the root and runtime directory as JSON without starting the receiver.
