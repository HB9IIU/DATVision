# DATVision v0.1.0-preview.1 — Windows portable preview

DATVision by HB9IIU turns an RTL-SDR into a dedicated DATV receiver on Windows.

Download **DATVision-0.1.0-preview.1-win-x64.zip**, extract the entire folder to a
writable location, and open **DATVision.exe**. No .NET or MSYS2 installation is
needed. Requires Windows 10/11 x64 and an RTL-SDR using the WinUSB driver. QO-100
reception requires a suitable dish, powered LNB/downconverter and RF connection.

- DVB-S/S2 reception, integrated video and audio, including H.266/VVC decoding.
- QO-100 beacon startup and selectable BATC / LOCAL spectrum with click-to-tune.
- RF gain slider, effective-LO adjustment and beacon-based calibration.
- Pi-inspired animated magic eye and live constellation.
- Resizable pop-out video, mute and volume controls.
- One-click TS recording under Record/<callsign>/ and TS file playback.
- UDP multicast or unicast output to VLC on the home network.
- Signal-loss notice after 3 seconds; LOCAL resumes scanning after 30 seconds.

This is an unsigned preview. Hardware coverage and long-duration stability are
still being tested. This setup supports 25–1500 kS/s; higher rates are rejected.
The local sweep pauses while watching because one RTL stick supplies both.
BATC requires Internet access; local reception does not. LAN multicast depends
on router/Wi-Fi/firewall configuration. UDP replay forwarding and automatic
USB replug recovery are not included. H.266 performance depends on the PC and
stream; live H.266 reception has not yet been verified for this preview.

Decoder based on OnAir by Kristijonas Adomaitis (adswill). LDPC optimizations by
Evariste Courjaud (F5OEO). Additional DVB-S/S2 contributions by Arclamp. See the
in-app Credits and bundled licenses for further attributions.

Release assets include the portable ZIP, matching application source ZIP,
third-party source ZIP and SHA256SUMS.txt. Publish them together.
