# Building from source

Most users do not need this: the Raspberry Pi installer and the Windows ZIP
contain ready-built programs. This guide is for development and for building a
release.

The project has one shared receiver (`datvrx`, C++20 and Rust, built with CMake)
and two interfaces: a Python/PyQt5 touchscreen player for the Raspberry Pi
(`ui/pi_player/`) and a .NET Windows application (`ui/DatvRx.Player/`). Run all
commands from the project root.

## Raspberry Pi

Raspberry Pi OS 64-bit with desktop, Bookworm or Trixie, on a Pi 5 or Pi 4.

```sh
sudo apt install build-essential cmake git pkg-config libusb-1.0-0-dev \
     python3-pyqt5 python3-pyqt5.qtwebsockets mpv fonts-piboto libiio-dev
curl https://sh.rustup.rs -sSf | sh -s -- -y --profile minimal
```

### RTL-SDR driver

The RTL-SDR Blog V4 needs a newer librtlsdr than Raspberry Pi OS ships (the
system's 2022 library reports PLL errors with it). Build Osmocom rtl-sdr 2.0.3
privately under `~/.local/datvrx-rtl`; the system library stays installed:

```sh
git clone https://github.com/osmocom/rtl-sdr ~/projects/datvrx-rtl-driver
git -C ~/projects/datvrx-rtl-driver checkout 797f8143266d983c56d8f35d2d442527529dd8a5
cmake -S ~/projects/datvrx-rtl-driver -B ~/projects/datvrx-rtl-driver/build -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_INSTALL_PREFIX="$HOME/.local/datvrx-rtl" -DINSTALL_UDEV_RULES=OFF -DDETACH_KERNEL_DRIVER=ON
cmake --build ~/projects/datvrx-rtl-driver/build --parallel 2
cmake --install ~/projects/datvrx-rtl-driver/build
```

`DETACH_KERNEL_DRIVER=ON` lets the driver take the stick from the kernel's DVB-T
TV driver itself. Manufacturer reference: https://www.rtl-sdr.com/v4/

### Build and run

```sh
bash scripts/linux/build.sh                     # datvrx, rtl_iq, rtl_sweep in build/linux/
DATVRX_BIN=build/linux bash scripts/selftest.sh   # synthetic DVB-S2 reception, no hardware
bash scripts/linux/start-pi-player.sh           # the touchscreen player
```

`build.sh` finds the driver in `~/.local/datvrx-rtl` and then also builds the
stick tools; without it only the receiver is built. With `libiio-dev` it also
builds `pluto_iq` for the ADALM-Pluto. `BUILD_JOBS=4` builds faster
where memory allows.

For a desktop icon on a development Pi: `bash scripts/linux/install-pi-player-desktop.sh`.

### Other screen sizes

The layout is designed for 1024 x 600 and scales to the screen at start
(800 x 480 gives 0.78, 1280 x 720 gives 1.2). To check a size without that
screen, open it in a window on another X display, for example a TigerVNC desktop:

```sh
bash scripts/linux/preview-layout.sh 800x480 :1          # layout only, no stick
bash scripts/linux/preview-layout.sh 1280x720 --live :1  # complete player; close the real one first
```

### Release

```sh
bash scripts/linux/make-release.sh 1.0.0
```

From a clean working tree, this builds and packs into `build/release/`: the
ready-to-run ARM64 archive with the private driver, the project source, the
driver source (GPL-2.0), `install.sh` with the version filled in, and
`SHA256SUMS`. Uploading them is a separate step. The installer uses a package
lying next to it instead of downloading, to test a version before publishing.

## Windows

Windows 10/11 x64. The receiver is built with MinGW-w64 UCRT64 (MSYS2), not
Visual Studio's compiler; the interface needs the .NET SDK.

### Tools

Install MSYS2, Rust and the .NET SDK with their official installers. In the
MSYS2 UCRT64 shell:

```sh
pacman -Syu
pacman -S --needed mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja \
       mingw-w64-ucrt-x86_64-ffmpeg mingw-w64-ucrt-x86_64-rtl-sdr
```

In PowerShell:

```powershell
rustup toolchain install stable-x86_64-pc-windows-gnu --profile minimal
```

### Build and run

```powershell
./scripts/windows/build.ps1           # datvrx.exe, rtl_iq.exe, rtl_sweep.exe in build/windows/
./scripts/windows/build-monitor.ps1   # the DATVision application
./DATVision.cmd
```

For MSYS2 outside `C:\msys64`, pass `-MsysRoot 'D:\msys64'` to `build.ps1`. It
uses the `stable-x86_64-pc-windows-gnu` toolchain for its own process without
changing Rustup's default; `-RustToolchain` selects another one with the
Windows GNU target.

Synthetic reception test, in the MSYS2 UCRT64 shell:

```sh
DATVRX_BIN=build/windows bash scripts/selftest.sh
```

The RTL-SDR needs the WinUSB driver, installed with Zadig.

### Portable release

`scripts/windows/package-portable.py` packs the portable ZIP with the .NET
runtime and FFmpeg, so users need neither .NET nor MSYS2.
`scripts/windows/build-branding.py` regenerates the icon and logo images from
`assets/branding/`.

## The receiver on its own

`datvrx` reads I/Q samples from a file or pipe and writes an MPEG transport
stream; see [receiver.md](receiver.md) for its options.

```sh
build/linux/rtl_iq 741500000 2560000 40.2 | build/linux/datvrx - --rate 2.56 --format cu8 --rs 1.5 | mpv -
```
