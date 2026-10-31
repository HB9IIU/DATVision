# Milestone 02 — touch-only Pi player with magic eye and LNB calibration

Saved: 31 October 2026. Git tag: `milestone-02-pi-touch-player`.

## Working state

- Raspberry Pi 5 with the 1024 x 600 DSI touchscreen; operated by touch only,
  with no mouse, keyboard, menus or text entry.
- Layout: stream information on the left, letterboxed video in the centre,
  large controls on the right and the BATC spectrum across the bottom.
- Left column: state, station and provider from the transport stream's SDT,
  frequency, symbol rate, mode, Es/N0 with bar and margin over the mode's
  approximate threshold, video codec and size, audio codec, bad frames and
  synchronisation losses. Bottom: a drawn EM34/6E5-style magic eye or a live
  constellation (256 symbols, four times a second); tap to alternate.
- Right column: Sound/Mute, volume − / +, Beacon, Stop/Start, Full Video and
  Settings. Tapping the video also toggles full video.
- Spectrum: tap snaps to the nearest carrier. The tuned slot is filled blue
  while searching, green when locked and red when lost or not locked in 10 s.
- Settings: measured LNB oscillator with CALIBRATE / RECALIBRATE ON BEACON
  (five detuned locks; the first live run gave 9750.054 MHz with the steps
  agreeing within 0.14 kHz), RF gain, credits and Quit to desktop.
- RTL-SDR stick missing or dropped from USB: large message, NO RTL-SDR state,
  automatic resume after replugging. A stick held elsewhere shows STICK BUSY.
- Faster tuning: the tap is answered on screen at once; the old VLC player is
  retired in the background (stop about 600 ms → under 25 ms); lock and first
  picture are shown as soon as they happen. Typical beacon: lock 1.2 s, picture
  1.5–1.9 s after the tap; a retune keeps the interface busy roughly 0.2–0.4 s.
- `rtl_iq` closes the stick cleanly on SIGTERM/SIGINT/closed output: 20 of 20
  rapid start/stop cycles stopped in about 53 ms without USB errors. Before this,
  rapid retuning made the V4 stop responding and crashed the player.
- `datvrx --cells n`: constellation symbols and, while locked, the tracked
  carrier offset as status lines. Default behaviour without it is unchanged.
- Every launch appends timestamped tuning, lock, picture, calibration and crash
  details to `test-output/pi-player-ui.log`.
- Python checks pass on the Pi: signal finding and snapping, SDT/PMT parsing and
  calibration arithmetic. The private V4 driver in `~/.local/datvrx-rtl` is
  still selected by the CMake cache.

## Entry points

- `bash scripts/linux/start-pi-player.sh`, or the RTL DATV Player desktop icon.
- `bash scripts/linux/build.sh`: receiver and `rtl_iq`.
- `python3 -I tests/test_pi_signal_finder.py`: interface logic checks.
- `docs/PI_FULLSCREEN_PLAYER.md`: interface, behaviour and diagnostics.

## Known limits at this milestone

- Bluetooth audio routing is not set up; the final speaker is not chosen yet.
- The symbol rate comes from the BATC spectrum estimate; a wrong standard rate
  prevents lock.
- Es/N0 thresholds and the magic-eye scale are approximate textbook values.
- The V4 stick dropped off USB twice before the clean-stop fix; long-duration
  stability after the fix is not yet established.
- H.265 playback relies on software decoding in the Pi's VLC; not measured.
- Windows checkout is not updated with these changes.
