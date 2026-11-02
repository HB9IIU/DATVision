# Fullscreen player on the Pi touchscreen

The connected DSI-2 screen was detected as **1024 x 600**, scale 1.0,
58.586 Hz. The Qt application reads the screen geometry at startup and opens
fullscreen; its layout scales with the available space.

Double-click **DATVision** on the Pi desktop. This is a native Linux
interface running on the Pi, not the Windows player or a remote video window.
Capture, DVB decoding, video and audio all run on the Pi.

## Interface

Designed for touch only: no mouse, keyboard, menus or text entry are needed.
The video sits between two columns that use the letterbox space.

- **Left: stream information.** State (LOCKED, SEARCHING, REACQUIRING…),
  station/service and provider name from the transport stream's SDT, frequency,
  symbol rate, mode, Es/N0 and a MiniTioune-style D number: the margin above the
  approximate quasi-error-free threshold for that mode, green from 2 dB, amber
  from 0, red below. The bar is a gauge of D: empty at the decoding limit, full at
  8 dB of reserve, a short red stub below the limit. Then video codec and size,
  audio codec, bad frames and synchronisation losses since tuning.
- **Bottom of the left column: magic eye or constellation; tap to alternate.**
  The magic eye is a drawn EM34/6E5-style tube: cold when stopped, lit with the
  shadow wide open while searching, and closing with the Es/N0 margin once
  locked (fully closed at about 5 dB margin). The constellation shows 256
  recent symbols four times a second, from `datvrx --cells`. The choice is saved.
- **Right: controls.** Sound/Mute, volume − / +, Beacon (QO-100), ● REC and
  ▶ PLAY, Bands, ⚙ Settings and ⏻ quit.
- **Tap the video** (or Full Video) to show only the picture; tap again to return.
- **Settings** is a full-screen page: the LNB oscillator (shown, not typed in)
  with CALIBRATE / RECALIBRATE ON BEACON, RF gain through the R828D steps,
  credits and Quit to desktop. Gain changes are applied, with a retune, on DONE.
- **CALIBRATE ON BEACON** measures the real LNB oscillator. The beacon is tuned
  five times, deliberately detuned by −100, −50, 0, +50 and +100 kHz; after each
  lock, eight readings of the receiver's tracked carrier offset (`datvrx carrier`
  lines) give `true LNB = assumed LNB − offset − detune`. The steps must agree
  within 5 kHz, else nothing changes. The result is shown with UNDO and kept on
  DONE; reception then returns to the previous station. Any tap on the spectrum,
  or BEACON cancels it. Details are logged as `CALIBRATION` lines.
- **BANDS** (replacing Full Video; tap the picture for that): QO-100 via the
  LNB; 4 m, 2 m, 70 cm and 23 cm A/B (the IARU Region 1 ATV segments) direct
  from the antenna; 6 cm via a downconverter. Terrestrial and converter bands
  always use the local scan; their column shows RF gain (amplifier symbol) with
  − / +, applied 0.8 s after the last tap. The 6 cm preset stores the converter
  LO (5200 MHz RX5808, or 5000/4700/4600 MHz DG0VE KON-DWN-5560, chosen in its
  Settings) and tunes RF − LO, e.g. 5705 MHz at 505 MHz. Bands wider than
  10 MHz (23 cm, 6 cm) zoom to 3 MHz on the first tap; the second tunes.
  QUIT TO DESKTOP and the credits are also on this page.
- **● REC** records the received transport stream while a station is locked,
  as in the Windows version: `Record/<callsign>/DATV-<date>-<time>.ts` in the
  player folder (callsign from the service name, else `Unknown`). The button
  turns red while recording; tap again to finish. Tuning elsewhere, a rescan or quit
  also finish it. It will not start below 500 MB free and stops itself below
  300 MB, so the SD card never fills up.
- **▶ PLAY** (next to REC) lists the recordings newest first: tap one to play it
  full screen (REPLAY; the stick and receiver rest); a tap on the picture or the
  end of the file returns to reception. DELETE removes a file after a second tap.
  Swipe to scroll. There is no STOP button: TAP TO RESCAN, a tap on another
  station or BEACON change reception, and ⏻ quits.
- The right column ends with ⚙ (Settings, where the band has any) and ⏻ (quit to
  the desktop): the first tap turns it into QUIT?, a second tap within 3 s quits.
- New installations start with the BATC spectrum on QO-100 (straight to the
  beacon); LOCAL is chosen in Settings and remembered.
- BATC spectrum across the bottom, without markers. A tap near a carrier snaps
  to it; the tuned signal is filled green, the others stay amber.
- If the RTL-SDR stick is missing or drops off USB, the video area says so in
  large text and the state shows NO RTL-SDR. Reception resumes by itself two
  seconds after the stick is plugged back in. A stick held by another program
  is reported as STICK BUSY.
- F11, Escape and Q remain only as development keys.

The spectrum needs internet and represents BATC reception; quality readouts
come from the local receiver. Stale spectrum data cannot trigger tuning.
Signals above 1500 kS/s are rejected for this RTL-SDR setup.

## Video engine

Video is drawn by mpv (one process per stream, embedded in the video area and
driven through its IPC socket; `ui/pi_player/mpv_video.py`). libVLC remains as
a fallback when mpv is missing or with `DATVRX_VIDEO=vlc`. Raspberry Pi's VLC
build could not display the beacon (H.264 1280x720 at 50 fps) on a Pi 4
("failed to create video converter"); mpv plays it there with all pictures
shown, using its GPU output; on a Pi 4 at most 25 pictures/s are shown, since
the beacon's 50/s left the picture black there.
`DATVRX_MPV_OPTIONS` adds mpv options for diagnosis; mpv messages go to
`test-output/pi-player-video.log`.

## Other screen sizes

The layout is designed for 1024 x 600 and scaled to the screen at start
(`ui/pi_player/ui_scale.py`): 800 x 480 gives 0.78, 1280 x 720 gives 1.2. All
sizes, fonts, the magic eye and the spectrum follow. To check a size without
that screen, open a layout preview in a window, e.g. on the TigerVNC desktop:

```sh
bash scripts/linux/preview-layout.sh 800x480      # default X display :1
bash scripts/linux/preview-layout.sh 1280x720 :1
```

The preview leaves the stick, the receiver and saved settings alone, so it can
run beside the real player; it shows the BATC spectrum for something to look at.

## Implementation and dependencies

Source is in `ui/pi_player/`. It uses Python 3, PyQt5, Qt WebSockets and the
system libVLC through ctypes. Video embedding uses Qt's X11 backend inside the
existing labwc/Xwayland desktop. The decoded TS is sent to a random local UDP
port bound by VLC on loopback; video does not cross the network.

Debian dependencies:

```sh
sudo apt install python3-pyqt5 python3-pyqt5.qtwebsockets vlc
```

The Pi already had PyQt5 and VLC; only Qt WebSockets and its small shared-library
dependency were added. The receiver/helper and V4-compatible library from
`PI_LIVE.md` must also be built.

Start from a Pi terminal:

```sh
cd ~/projects/pi-datv-rx
bash scripts/linux/start-pi-player.sh
```

Install or recreate the desktop shortcut:

```sh
bash scripts/linux/install-pi-player-desktop.sh
```

Stop any standalone beacon or Windows remote-player session first: only one
receiver can own the stick. Settings are stored in ignored
`pi-player-settings.json`. Diagnostics are in `test-output/pi-player-radio.log`
and `pi-player-receiver.log`. Every launch (desktop icon, terminal or the
development service `datv-touch-player.service`) appends a timestamped start
line, tuning events, problems and any Python crash trace to `pi-player-ui.log`.

## Verification — 31 October 2026

- Pi screenshot verified the interface fills 1024 x 600, with clear beacon
  video, local quality readouts, a live amber spectrum, and the selected band.
- Touch-sized menu was opened and visually checked on the actual Pi desktop.
- Menu Stop was exercised: neither `rtl_iq` nor `datvrx` remained running.
- The stopped state was visually verified as a black video area with a clear
  message. Tapping the beacon in the spectrum restarted the Pi capture/decoder.
- Python source compilation and four tuning test cases passed on Windows and
  ARM64 Pi, covering carrier estimates, blank/invalid input and wide signals.
- Initial fullscreen reception reached 910 good frames / zero bad at 20 seconds,
  Es/N0 9.5 dB. Approximately 56°C and no reported throttling at that check.
- Software video decode is selected for this first version. Measured process
  CPU percentages are short observations, not a sustained performance benchmark.
- Audio routing, touch feel and long-duration stability still need user feedback.
- The Pi's installed VLC determines codec support; H.266 playback is not claimed.

Signal detection is adapted from HB9IIU Experimental H.266 Player (GPL v3),
whose code was read but left unchanged. OnAir receiver attribution and licensing
remain in the project README and LICENSE; the menu also exposes credits.
