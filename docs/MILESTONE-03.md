# Milestone 03 — local band scan, terrestrial bands and other screens

Saved: 31 October 2026. Git tag: `milestone-03-local-scan-bands`.

## Working state

- **Local spectrum without internet.** New `rtl_sweep` keeps the stick open,
  hops across the band in 1.92 MHz steps, averages FFTs, hides the centre spike
  and prints one stitched spectrum per sweep (QO-100: 5 steps, about two sweeps
  a second). The player starts scanning; a tap watches the station with the
  sweep paused and dimmed; TAP TO RESCAN (semi-transparent, in the middle of the
  strip) or STOP scans again. The height follows the strongest signal; a local
  carrier must stand 3 dB above the noise. BATC remains selectable for QO-100
  in Settings and behaves exactly as before.
- **Bands page** (replacing Full Video; tap the picture for that): QO-100 via the
  LNB; 4 m, 2 m, 70 cm and 23 cm A/B (IARU Region 1 ATV segments) direct from
  the antenna; 6 cm via a downconverter whose LO is stored in the preset (5200
  MHz RX5808, or 5000/4700/4600 MHz DG0VE KON-DWN-5560, for ON1AVO). Bands wider
  than 10 MHz zoom to 3 MHz on the first tap. Each band keeps its own gain; the
  last band is restored at start.
- Terrestrial and converter bands: RF gain with an amplifier symbol and − / + in
  the right column (applied 0.8 s after the last tap); no BEACON or STOP.
  QUIT TO DESKTOP and credits are also on the Bands page.
- Verified live: HB9IIU decoded on 2 m from the local scan (146.500 MHz,
  250 kS/s, DVB-S2 QPSK 3/4, Es/N0 12.9 dB, H.265 960×540 with AAC, no bad
  frames); 23 cm sweeps ran live (noise only, stick on the LNB).
- **D number** under Es/N0 (MiniTioune style), and the bar shows D around the
  mode's threshold in the middle: green to the right, red to the left.
- **Signal lost:** after 3 s the frozen picture becomes "station — signal lost";
  the receiver keeps listening and the picture returns with the station. In
  LOCAL mode it scans again after 30 s (also for a tapped signal that never
  locks); with BATC it keeps waiting. Simulated end to end, not yet seen live.
- Speaker symbol with 0–3 waves, red cross when muted; equal 62 px controls in
  groups; Bands tiles with large names.
- **Other screens:** everything scales from the 1024 x 600 design (800 x 480 →
  0.78, 1280 x 720 → 1.2). `scripts/linux/preview-layout.sh WxH` shows the
  layout in a window on the VNC desktop without the stick; with `--live` it is
  the complete player in that window (beacon locked in 1.2 s at 1280 x 720).
- 12 Python checks pass, including local-sweep detection, band-edge rejection,
  70 cm finding and the 6 cm converter IF ranges.

## Entry points

- `bash scripts/linux/start-pi-player.sh` or the RTL DATV Player icon.
- `bash scripts/linux/preview-layout.sh 800x480 [--live]`
- `bash scripts/linux/build.sh` (now also `rtl_sweep`).
- `python3 -I tests/test_pi_signal_finder.py`

## Known limits at this milestone

- Terrestrial and 6 cm bands are only tested with the stick on the QO-100 LNB,
  except 2 m with a strong local signal. Band edges are first estimates.
- Stitched sweeps show small level steps between segments and an RTL spur
  near 10499 MHz; 23 cm sweeps take about a second.
- No installer yet; the private V4 driver build is still manual (PI_LIVE.md).
- Bluetooth audio, long-duration stability and the Windows checkout remain open.
