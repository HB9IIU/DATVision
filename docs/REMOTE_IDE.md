# VS Code on the remote Pi

Host: `pidatvmon.local` (SSH alias `pidatvmon`), user `daniel`.
Machine checked on 31 October 2026: Raspberry Pi 5, 4 GB RAM, aarch64,
Debian 12 Bookworm. Approximately 5.8 GB free on the root filesystem.

## Open the project

Double-click `Open Remote Pi.cmd` on the Windows PC, or run:

```powershell
code --new-window --remote ssh-remote+pidatvmon /home/daniel/projects/pi-datv-rx
```

This opens a separate VS Code Remote SSH window. Check that its lower-left
corner says `SSH: pidatvmon`. Files and integrated terminal commands in that
window belong to the Pi. Keep the existing local Windows window for the player.
If VS Code asks whether you trust this project, review and trust your own source
folder to enable development tasks.

The local SSH config selects `~/.ssh/pi_key`; its public key is installed in the
Pi account's authorized_keys. No password is stored in this repository or in
the SSH config. Test access from PowerShell with `ssh pidatvmon`.

## Source checkpoint

The project is copied using a Git bundle, preserving local commit history and
Milestone 01. The Pi checkout is independent: edits on one machine do not
automatically overwrite the other. Agree on a source-sync workflow before
making parallel edits on both machines. No Git hosting service is required.

## Build preparation

VS Code's default build task runs `bash scripts/linux/build.sh` on the Pi.
The synthetic-test task uses `build/linux`. The Windows GUI remains Windows-only;
the Pi will build and run the C++/Rust receiver.

Git, g++, CMake and FFmpeg were found on the Pi. Rust 1.99.1 and Cargo were
subsequently installed for `daniel` with Rustup's minimal aarch64 GNU profile.
The Linux build script loads `~/.cargo/env` if Cargo is not yet on the shell PATH,
so an already-open VS Code window can run it without restarting.
See `PI_VALIDATION.md` for native build/test results and `REMOTE_PI.md` for the
build and test sequence.

VS Code Remote SSH reference: https://code.visualstudio.com/docs/remote/ssh
