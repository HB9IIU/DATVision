# DATVision on the Raspberry Pi

A complete DATV receiver on a Raspberry Pi with a touchscreen and an RTL-SDR
stick: spectrum, tuning, video and sound, all on the Pi. Designed for touch
only: no mouse, keyboard or text entry is needed.

## What you need

- Raspberry Pi 5 (recommended) or Pi 4, Raspberry Pi OS 64-bit **with desktop**,
  Bookworm or Trixie.
- A touchscreen. The layout is designed for 1024 x 600 and scales to other
  sizes, from 800 x 480 upwards.
- An RTL-SDR stick: RTL-SDR Blog V4 (R828D) or an R820T stick.
- For QO-100: a dish with a powered LNB. For 6 cm: a downconverter (see Bands).
- For sound, simply pair a **Bluetooth speaker** with the Pi (Bluetooth icon in
  the desktop's top bar). DATVision plays through the Pi's current audio output,
  so HDMI or a USB sound card work as well.

## Install, update, remove

In a terminal on the Pi:

```sh
curl -fsSL https://github.com/HB9IIU/DATVision/releases/latest/download/install.sh | bash
```

The installer shows what it will do and asks before starting. It installs the
packages it needs, gives your user access to the stick, puts **DATVision** on
the desktop and in the menu, and starts it with the desktop from then on.
Everything goes into `~/pi-datv-rx`, except a udev rule and a DVB-T driver
blacklist for the stick.

The same command updates an installation and keeps your settings.
`bash install.sh --uninstall` removes everything the installer added.

## The screen

The video sits in the middle, with two columns beside it and the spectrum below.

**Left: the reception.** State (LOCKED, SEARCHING, REACQUIRING…), station and
provider name, frequency, symbol rate, mode and Es/N0. The **D number** is the
margin above the decoding limit for that mode, as in MiniTioune: green from
2 dB, amber from 0, red below. Then the video and audio codecs, bad frames and
synchronisation losses since tuning.

At the bottom, the **magic eye** closes as the signal improves; tap it to see
the **constellation** instead.

**Right: the controls.** Sound and volume, BEACON (QO-100), ● REC, ▶ PLAY,
BANDS, ⚙ settings and ⏻ quit. ⏻ asks QUIT? first; a second tap within
3 seconds quits.

**Spectrum.** Tap a signal to receive it; the tuned signal turns green. On
QO-100 the spectrum comes from BATC (internet) or from your own stick (LOCAL);
on all other bands it is always your own stick. While the band is scanned a red
**Continuous Scanning** blinks at the top; until the first picture of the band is
there, a bar moves across the spectrum.

On wide bands (more than 10 MHz: 23 cm, 13 cm, 6 cm) a tap **recentres** the view
on that spot and scans again: the first tap from the whole band zooms to 15 MHz,
later taps keep the width. A tap on the middle of the view tunes the signal there.
The buttons **–** and **+** zoom out and in by a factor of three around the middle,
**FULL … Band** shows the whole band again.

On QO-100, when nothing has been decoded for a minute (and no replay, recording or
settings page is open), the beacon is tuned by itself.

**Tap the picture** to see only the video; tap again to return.

## Bands

| Band | Reception |
| --- | --- |
| QO-100 | via the LNB, BATC or local spectrum |
| 10 m, 6 m, 4 m, 2 m, 70 cm, 23 cm A/B | direct from the antenna (IARU Region 1 ATV segments). An ADALM-Pluto or FISHBall tunes from 47 MHz, so 10 m needs the RTL-SDR stick. |
| 13 cm (2300-2450 MHz), 6 cm | direct from the antenna, with an ADALM-Pluto or FISHBall (the RTL-SDR stick tunes up to about 1.7 GHz only) |
| 6 cm + converter | via a downconverter (for the RTL-SDR stick, or a Pluto): RX5808 (LO 5200 MHz) or DG0VE KON-DWN-5560 (LO 5000, 4700 or 4600 MHz), chosen in Settings |

On terrestrial and converter bands the column shows the RF gain with − and +.

## Settings (⚙)

- **Spectrum** (QO-100): LOCAL SCAN or BATC ONLINE. The globe on the BATC
  button is green when the BATC server can be reached, red when there is no
  internet.
- **LNB oscillator** (QO-100): measured, not typed in. RECALIBRATE ON BEACON
  tunes the beacon five times, deliberately detuned, and computes the true LNB
  frequency from the receiver's carrier offset. The steps must agree within
  5 kHz, otherwise nothing changes; UNDO restores the previous value.
- **RF gain**: lower it if strong signals overload the stick.
- **Converter LO** (6 cm).
- **Network stream**: see below.
- **ABOUT**: version, credits, the H.266 status of this Pi, and QUIT TO DESKTOP.

Changes apply when you press DONE.

## ADALM-Pluto

Instead of the RTL-SDR stick, DATVision receives with an ADALM-Pluto or a clone
such as the FISHBall PlutoSky, tested with the Tezuka firmware.
Settings → Receiver → **PLUTO**, then tap the Pluto's name to choose it from
the Plutos found on USB and on the network. Its gain is automatic; the local
scan uses a fixed gain.

- **On USB**, the Pluto needs **its own power supply** on its PWR socket (a 5 V
  charger, 1 A or more). From the Pi's USB alone it keeps restarting, and
  DATVision says so.
- **On the network** (a Pluto with an Ethernet port), the Pi should be on a
  cable too: over Wi-Fi the picture breaks up, and DATVision warns about it.
- The chosen Pluto is found again by its serial number, name or address, so a
  new address after a router restart does not matter. Two Plutos with the same
  name are told apart by their address.
- **One device per Pluto:** a Pluto used by two DATVisions at once (a Pi and a
  PC) receives badly on both.

## Recording and playback

**● REC** records the received transport stream while a station is locked,
to `Record/<callsign>/DATV-<date>-<time>.ts` (the callsign comes from the
service name). The button is red while recording; tap again to finish. Tuning
elsewhere or quitting also finishes it. Recording does not start below 500 MB
free space and stops by itself below 300 MB, so the SD card never fills up.

**▶ PLAY** lists the recordings, newest first. Tap one to play it full screen;
tap the picture, or let it end, to return to reception. DELETE removes a file
after a second tap.

## Network stream

Settings → Network stream **ON** sends live reception to your home network.
On any PC, open VLC → Media → Open Network Stream → `udp://@239.255.42.42:1234`.
Guest Wi-Fi networks may block this kind of multicast traffic. Recordings
played back are not sent.

Another destination (a different multicast group, or one PC's address) can be
set as `udp_destination` in `~/pi-datv-rx/pi-player-settings.json`.

## Video formats

H.264 and H.265 play on every supported system. **H.266 (VVC)** needs Raspberry
Pi OS **Trixie**, whose FFmpeg can decode it; on Bookworm an H.266 station says
so instead of showing a picture. ABOUT shows whether this Pi decodes H.266.
H.266 is decoded in software: a Pi 4 manages 500 kS/s stations at its limit, a
Pi 5 has about twice the power.

Symbol rates above 1500 kS/s are not supported with an RTL-SDR stick.

## Problems

- **NO RTL-SDR**: the stick is missing or dropped off USB. Reception resumes by
  itself two seconds after it is plugged back in.
- **STICK BUSY**: another program uses the stick. Close it.
- **Black picture with a station locked**: an H.266 station on Bookworm (see
  above), or a station that sends no video.
- Logs are in `~/pi-datv-rx/test-output/`: `pi-player-ui.log` (start, tuning,
  problems), `pi-player-receiver.log`, `pi-player-radio.log` and
  `pi-player-video.log`.

On a touchscreen, desktop icons need a double tap: the Raspberry Pi desktop is
made for a mouse. DATVision starts by itself with the desktop anyway.

## If something does not work

Open **⚙ → ABOUT → SAVE DIAGNOSTICS**. A text report is saved in your home
folder (`datvision-diagnostics-….txt`): the version, system, screen, sound and
radio hardware, which programs are running, the video window state, the saved
settings and the end of the logs. IP and MAC addresses, the host name and the
user name are removed. Nothing is sent anywhere: read the file, then attach it
to your message. From a terminal: `bash scripts/linux/diagnose.sh`.
