# RTL DATV Player

Double-click `RTL DATV Player.cmd`. The player opens on the QO-100 beacon.
Click a signal in the spectrum to change station; its frequency and symbol rate
are estimated automatically. The highlight shows the selected signal.

The main window contains video, one local reception-status line, and the
clickable spectrum. Frequency and symbol rate are readouts, not editable fields.
The spectrum is supplied by BATC over the internet; the reported Es/N0 and error
counts come from the local receiver. If the spectrum connection is lost,
reception of the current station continues, but click-to-tune waits for fresh data.

Right-click the picture or spectrum for:

- Always on top.
- Volume presets, including mute. Changing volume restarts the selected station's
  playback; a new keyframe may be needed before video resumes.
- LNB configuration (default oscillator: 9750 MHz). Saved locally.
- Stop reception; click a signal to resume.
- About/source credits and Quit.

This RTL-SDR setup accepts 25–1500 kS/s; wider detected signals are rejected with
a message. Detection is an estimate. USB access and actual signal quality still
determine whether a selected signal can be decoded.

## Build and checks

Install the .NET 10 SDK as well as the receiver prerequisites in `WINDOWS.md`:

```powershell
./scripts/windows/build.ps1
./scripts/windows/build-player.ps1
dotnet run --project tests/PlayerChecks/PlayerChecks.csproj -c Release --artifacts-path build/dotnet-checks
```

Close the player before rebuilding it. Generated binaries are in
`build/windows/player/`. Configuration is stored in `player-settings.json`;
logs are in `test-output/player-*.log`. These generated files are ignored by Git.
Use `-MsysRoot` with `scripts/windows/start-player.ps1` for a nonstandard MSYS2 path.

## Source and platform boundaries

`ui/DatvRx.Player/` is the Windows C# / WinForms interface. `Receiver.cs` owns the
local capture, decoder and player processes behind `IReceiverBackend`; the Pi
backend is not implemented yet. The C++/Rust receiver remains independently
buildable on Linux/Pi.

`Spectrum.cs` is adapted from HB9IIU Experimental H.266 Player (GPL v3), with
click bounds and WebSocket message validation changes. The original project is
at https://github.com/HB9IIU/HB9IIU-Experimental-H266-Player. The source project
beside this workspace was only read and remains unchanged. Receiver provenance
and GPL notices are documented in the root README and LICENSE.
