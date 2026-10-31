# Milestone 01 — compact Windows RTL DATV player

Saved: 31 October 2026. Git tag: `milestone-01-windows-click-to-tune`.

## Working state

- Self-contained project; the original temporary folder is no longer required.
- Windows C++/Rust receiver builds with MSYS2 UCRT64 and the separate Windows
  GNU Rust toolchain. Linux/Pi source and build scripts remain available.
- RTL-SDR Blog V4 reception through a normal 9750 MHz LNB setup.
- QO-100 beacon decoded at 1.5 MSym/s using 2.56 MSps: 912 good FEC frames,
  zero bad in the recorded trial, with clear 720p video.
- 2E0ILY decoded at 500 kS/s: 457 good FEC frames, zero bad in the recorded trial.
- Compact floating Windows player: embedded live video, one local reception
  status line, and clickable BATC spectrum. No main-window tuning fields.
- Frequency and symbol rate estimated from a spectrum click. A 333 kS/s live
  signal was visually verified after tuning.
- Always-on-top, volume/mute, LNB configuration, Stop and Quit are accessible
  through the right-click menu.
- Synthetic reception self-test passed; all 12 player checks passed.

## Entry points

- `RTL DATV Player.cmd`: compact player, initially tuned to the beacon.
- `Live Beacon.cmd`: standalone ffplay beacon window.
- `scripts/windows/build.ps1`: receiver and binary-safe RTL-SDR source.
- `scripts/windows/build-player.ps1`: Windows interface.
- `docs/PLAYER.md`: interface use, build and credits.
- `docs/VALIDATION.md`: detailed receiver measurements and test scope.

## Known limits at this milestone

- Remote Pi deployment/backend is not implemented or tested.
- BATC spectrum needs internet and represents BATC reception; local quality is
  reported separately. Signal detection is an estimate.
- Volume presets restart playback; startup can wait for a keyframe.
- Long-duration live stability is not established; earlier live trials had
  occasional reacquisition. Zero-error capture results do not promise zero-error
  continuous reception.
- Original S2X/DSM-CC compiler warnings remain; broad S2X support is not validated.
- The owner's final physical Escape stopped computer-use checks. Compact layout,
  live picture and the right-click menu were visually checked before that stop.

## Saved checkpoint

The Git commit and tag preserve all tracked source, scripts, documentation and
licence files. A source ZIP is also saved in the ignored `milestones/` directory.
Generated builds, personal settings, RF recordings and logs are excluded from
the source checkpoint. Nothing is uploaded or published by saving this milestone.
