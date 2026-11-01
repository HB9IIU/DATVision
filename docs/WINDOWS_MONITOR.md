# Standalone Windows DATV Monitor — Pi-style layout

Start **Windows DATV Monitor.cmd**. This application uses an SDR attached to the
Windows PC. The new monitor has no Pi or SSH dependency. Existing Pi tools and
legacy remote-player launchers remain separate.

## Layout and operation

- Floating, resizable window; optional always-on-top.
- Left: receiver state, station/provider from the received stream, frequency,
  symbol rate, modulation/FEC, Es/N0, approximate D reserve, codecs and errors.
- Bottom-left: magic-eye-style indicator; click it for the live constellation.
- Centre: ffplay video embedded in the window. Double-click the video area to
  expand or restore the video layout.
- Right: sound/mute, volume − / +, beacon, Start/Stop/Rescan, Bands, Settings,
  Quit. Volume uses the ffplay process's Windows audio session and does not
  restart reception.
- Bottom: local SDR spectrum, or optional BATC QO-100 reference selected in
  Settings. Click a signal to estimate its frequency and symbol rate.

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
  interactive preview remained open. Reopen with `Windows DATV Monitor.cmd` to
  use the final Rescan action and panel styling changes.

This is a first Windows prototype, not full parity with the later Pi player:
automatic LNB calibration, automatic USB replug recovery and release packaging
are not included. Broader band/hardware coverage and long-duration stability
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
