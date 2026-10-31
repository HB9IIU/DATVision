# Video on the Pi's own screen

This mode runs all three stages on the Pi: RTL-SDR capture, DVB decoding and
video/audio display. The PC is only used for remote development and setup.

For the newer fullscreen interface with a clickable spectrum, use the Pi's
**RTL DATV Player** shortcut and see `PI_FULLSCREEN_PLAYER.md`. The standalone
beacon shortcut below remains available as a simpler alternative.

Double-click **QO-100 Beacon** on the Pi desktop. A movable video window opens
on the connected Pi monitor. Press **F** for fullscreen, **M** to mute, or **Q**
to close. Closing the player ends the capture and decoding pipeline.

From the Pi terminal:

```sh
cd ~/projects/pi-datv-rx
bash scripts/linux/play-beacon.sh
```

To recreate the desktop launcher:

```sh
bash scripts/linux/install-pi-desktop.sh
```

The script supports the existing user's Wayland desktop (labwc, wayland-0) and
falls back to X11 display :0. It must run as the desktop user `daniel`; it does
not start a desktop or change login settings. It requires the V4-compatible
driver/helper setup documented in `PI_LIVE.md`.

Stop the Windows **Pi DATV Player** remote reception before using this mode,
because the two modes share the same USB stick. The initial on-Pi player is
tuned to the beacon at 10491.500 MHz / 1.5 MSym/s; it does not yet include the
Windows player's clickable spectrum interface.

## Verification — 31 October 2026

- Stopped the capture feeding Windows, then launched ffplay in the Pi's Wayland
  session as `daniel`.
- Verified a Pi-desktop capture showing a clear A71A/QARS beacon image in the
  window titled **QO-100 Beacon - PI SCREEN**.
- Receiver reports at 14 seconds: 628 good FEC frames, zero bad, Es/N0 8.9 dB.
- Video startup logs include H.264 missing-reference/concealment warnings;
  sustained playback and audio routing remain to be assessed by the user.

Diagnostics are in `test-output/pi-screen-launch.log`,
`pi-screen-receiver.log` and `pi-screen-video.log` on the Pi.
The verification screenshot is saved in `test-output/pi-screen.png` on the Pi,
with a local copy in `test-output/pi/screen.png`; these are ignored by Git.
