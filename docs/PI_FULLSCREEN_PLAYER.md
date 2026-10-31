# Fullscreen player on the Pi touchscreen

The connected DSI-2 screen was detected as **1024 x 600**, scale 1.0,
58.586 Hz. The Qt application reads the screen geometry at startup and opens
fullscreen; its layout scales with the available space.

Double-click **RTL DATV Player** on the Pi desktop. This is a native Linux
interface running on the Pi, not the Windows player or a remote video window.
Capture, DVB decoding, video and audio all run on the Pi.

## Interface

- Large embedded video with letterboxing to preserve the source aspect ratio.
- One reception-status line: frequency, symbol rate, mode, Es/N0 and bad frames.
- BATC spectrum across the bottom; tap a signal to estimate its frequency and
  symbol rate and retune. The green band marks the selected signal.
- The **⋮** button opens a touch-sized menu for fullscreen, beacon, Stop,
  immediate volume/mute, LNB configuration, credits and Quit to desktop.
- F11 toggles fullscreen, Escape returns to a normal window, Q quits.

The spectrum needs internet and represents BATC reception; quality readouts
come from the local receiver. Stale spectrum data cannot trigger tuning.
Signals above 1500 kS/s are rejected for this RTL-SDR setup.

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
and `pi-player-receiver.log`. A supervised development launch also writes
`pi-player-ui.log` and uses the user service `datv-touch-player.service`.

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
