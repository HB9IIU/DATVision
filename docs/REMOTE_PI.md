# Later deployment to a remote Raspberry Pi

Build natively on the Pi using the same source. Windows executables cannot be
used on the Pi. No Pi connection or deployment is configured yet.

1. Install Raspberry Pi OS, enable SSH, and confirm the Pi model, architecture,
   available RAM and hostname before choosing performance settings.
2. Copy this project to a working directory on the Pi, excluding `build/`,
   `rust/target/`, captures, test output and the original temporary source folder.
   A Git checkout or an archive of the working source can be used.
3. Install build dependencies on the Pi:

   ```sh
   sudo apt update
   sudo apt install build-essential cmake ffmpeg rtl-sdr
   ```

4. Install Rust 1.70 or newer using the official Rust installer or a sufficiently
   recent distribution package. Then, from the project root:

   ```sh
   bash scripts/linux/build.sh
   DATVRX_BIN=build/linux bash scripts/selftest.sh
   ```

   Use `BUILD_JOBS=1 bash scripts/linux/build.sh` when RAM is limited.
5. Test an I/Q recording, then the RTL-SDR locally on the Pi. Confirm USB access
   for the account running the receiver. Measure real-time decoding on that Pi;
   desktop performance figures do not establish Pi performance.

For the first remote trial, write the transport stream to a file on the Pi and
copy it back for playback. A remote video transport and user interface can be
chosen after reception works reliably. The existing command-line receiver needs
neither a desktop environment nor an attached screen on the Pi.
