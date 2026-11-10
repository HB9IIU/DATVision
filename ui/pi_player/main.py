#!/usr/bin/env python3
"""Fullscreen touchscreen DATV player for Raspberry Pi. GPL-3.0-or-later."""
import ctypes
from ctypes.util import find_library
import fcntl
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import sys
import threading
import time
import traceback
import urllib.request

from PyQt5.QtCore import Qt, QTimer, QEvent, QObject, QProcess, QRectF, pyqtSignal
from PyQt5.QtGui import QColor, QFont, QKeySequence, QPainter, QPen, QPixmap
from PyQt5.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QLabel,
                             QShortcut, QMessageBox, QFrame, QStackedLayout, QStackedWidget, QSplashScreen)
import bands
import calibration
import ui_scale
from ui_scale import px
from signal_finder import BEACON_MHZ, Signal
from sweeper import Sweeper
from spectrum import Spectrum
from receiver import Receiver, DEFAULT_PLUTO, DEFAULT_STREAM, udp_endpoint, vlc_address
from video import Video
import shutil
from ts_info import TsInfo, callsign
import plutos
import mpv_video
from panels import GREEN, AMBER, RED, DIM, GAINS, REQUIRED_ESN0, InfoPanel, ControlPanel, SettingsPage, BandsPage, RecordingsPage, AboutPage, PlutoPage

REPORT = re.compile(r"(DVB-S2X|DVB-S2|DVB-S) (\S+) (\d+/\d+)\S*(?: short)?(?: pilots)?\s+[\d.]+ Msym/s"
                    r"\s+Es/N0 ([\d.-]+) dB\s+ok \d+\s+bad (\d+)")
RTL_SDR_IDS = {("0bda", "2838"), ("0bda", "2832")}
PLUTO_IDS = {("0456", "b673")}   # ADALM-Pluto and clones such as the PlutoSky
NO_STICK = "RTL-SDR stick not found\n\nUnplug it and plug it back in.\nReception resumes by itself."
PLUTO_NO_POWER = "Pluto is not getting enough power\n\nConnect a 5 V charger to its PWR socket.\nReception resumes by itself."
PLUTO_LOOKING = "Looking for the Pluto…\n\nIt may be restarting."
PLUTO_WIFI = "⚠ The Pi reaches the Pluto over Wi-Fi: the picture may break up. Connect the Pi by cable, or the Pluto via USB."
NO_PLUTO = "Pluto not found\n\nCheck its power supply and its USB or network cable.\nReception resumes by itself."
STICK_BUSY = "RTL-SDR stick is busy\n\nUnplug it and plug it back in.\nIf that does not help, restart the Pi."
SIGNAL_LOST_NOTICE_S = 3   # replace the frozen picture with a clear message
SIGNAL_LOST_GIVE_UP_S = 30  # LOCAL: stop waiting for the station and scan the band again
IDLE_BEACON_S = 60          # QO-100: nothing decoded for this long: back to the beacon by itself
CREDITS = (("DVB-S/S2 decoder", "based on OnAir by Kristijonas Adomaitis (adswill)"),
           ("LDPC decoder", "optimizations by Evariste Courjaud (F5OEO)"),
           ("DVB-S/S2 contributions", "Arclamp"),
           ("BATC spectrum, signal finder", "HB9IIU Experimental H.266 Player"),
           ("Magic eye", "after HB9IIU SDR-Tiouner, inspired by the Lynx DATV Receiver (G8YTZ)"),
           ("Pluto support", "libiio by Analog Devices; tested with the Tezuka firmware by F5OEO"),
           ("Video, interface", "mpv (libVLC fallback), PyQt5"))
LICENCE = "GPL v3 · see LICENSE and the bundled source and notices."

STYLE = """
    QMainWindow, QWidget#surface, QWidget#settings, QWidget#about { background: #0c1620; color: #dde8f1; }
    QFrame#panel { background: #102331; border: 1px solid #1d3647; border-radius: 6px; }
    QLabel { color: #91a7ba; background: transparent; border: none; }
    QLabel#state { font-size: 17px; font-weight: 700; }
    QLabel#service { color: #dde8f1; font-size: 17px; font-weight: 600; }
    QLabel#value { color: #dde8f1; font-size: 13px; }
    QLabel#big { color: #dde8f1; font-size: 26px; font-weight: 600; }
    QLabel#caption { font-size: 11px; }
    QLabel#cardTitle { color: #dde8f1; font-size: 15px; font-weight: 700; }
    QLabel#dnumber { font-size: 17px; font-weight: 700; }
    QLabel#title { color: #dde8f1; font-size: 24px; font-weight: 600; }
    QPushButton { background: #1b3140; color: #dde8f1; border: 1px solid #29485c; border-radius: 6px;
                  font-size: 14px; font-weight: 600; min-height: 40px; }
    QPushButton#symbol { font-size: 26px; }
    QPushButton#danger { color: #ff9b9b; border-color: #8a3b3b; }
    QPushButton#half { font-size: 12px; }
    QPushButton#accent { background: #1f5a44; border-color: #50d7a0; }
    QPushButton#band { min-height: 116px; }
    QPushButton#plutoCard { min-height: 92px; text-align: left; }
    QPushButton#plutoCard[current="true"] { background: #1f5a44; border-color: #50d7a0; }
    QLabel#bandName { color: #dde8f1; font-size: 28px; font-weight: 700; }
    QLabel#bandRange { color: #dde8f1; font-size: 15px; }
    QLabel#bandSource { color: #91a7ba; font-size: 12px; }
    QFrame#readout { background: #142838; border: 1px solid #29485c; border-radius: 6px; }
    QLabel#readoutText { color: #dde8f1; font-size: 13px; font-weight: 600; }
    QPushButton:pressed { background: #31556c; }
    QPushButton:disabled { color: #4c6274; }
    QPushButton:checked { background: #1f5a44; border-color: #50d7a0; }
"""


def decodes_h266():
    """Whether mpv's FFmpeg has an H.266/VVC decoder: FFmpeg 7, e.g. Raspberry Pi OS Trixie, not Bookworm."""
    try:
        listing = subprocess.run(["mpv", "--vd=help"], capture_output=True, text=True, timeout=10).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    return re.search(r"^\s+vvc\b", listing, re.M) is not None


def pi_model():
    try:
        return Path("/proc/device-tree/model").read_text().strip("\0")
    except OSError:
        return ""


# A Pi 4 only just keeps up with 1500 kS/s: a receiver that drops a frame when behind,
# instead of losing stick samples, keeps the beacon decoding there.
SLOW_PI = "Raspberry Pi 4" in pi_model()


def over_wifi(uri):
    """Whether the Pi reaches a network Pluto (ip:…) over Wi-Fi: too little for its samples."""
    if not uri.startswith("ip:"):
        return False
    try:
        address = socket.gethostbyname(uri[3:])
        route = subprocess.run(["ip", "route", "get", address], capture_output=True, text=True, timeout=3).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    found = re.search(r"\bdev (\S+)", route)
    return bool(found) and found.group(1).startswith("wl")


def pluto_starving():
    """A Pluto stuck in its bootloader on USB (solid LED): it restarts for lack of power."""
    for device in Path("/sys/bus/usb/devices").iterdir():
        try:
            if (device / "manufacturer").read_text().strip() == "U-Boot" and \
                    (device / "product").read_text().strip() == "USB download gadget":
                return True
        except OSError:
            pass
    return False


def stick_present(kind="rtl"):
    ids = PLUTO_IDS if kind == "pluto" else RTL_SDR_IDS
    for device in Path("/sys/bus/usb/devices").iterdir():
        try:
            if ((device / "idVendor").read_text().strip(), (device / "idProduct").read_text().strip()) in ids:
                return True
        except OSError:
            pass
    return False


RELEASES = "https://api.github.com/repos/HB9IIU/DATVision/releases/latest"


def version_tuple(text):
    found = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", text.strip())
    return tuple(int(part) for part in found.groups()) if found else None


class UpdateCheck(QObject):
    """Asks GitHub for the latest release in the background; silent without internet."""
    newer = pyqtSignal(str)

    def __init__(self, version):
        super().__init__()
        self.version = version

    def start(self):
        if version_tuple(self.version):   # a development copy has no version to compare
            threading.Thread(target=self.run, daemon=True).start()

    def run(self):
        try:
            with urllib.request.urlopen(urllib.request.Request(RELEASES, headers={"User-Agent": "DATVision"}),
                                        timeout=5) as answer:
                latest = json.load(answer).get("tag_name", "").lstrip("v")
        except (OSError, ValueError):
            return
        print("UPDATE CHECK: installed %s, latest %s" % (self.version, latest or "?"), flush=True)
        if version_tuple(latest) and version_tuple(latest) > version_tuple(self.version):
            self.newer.emit(latest)


class PlutoSearch(QObject):
    """Runs pluto_find in the background: (uri, name, model, serial, address) per way to a Pluto."""
    found = pyqtSignal(list)

    def __init__(self, root):
        super().__init__()
        self.root = root

    def start(self):
        threading.Thread(target=self.run, daemon=True).start()

    def run(self):
        plutos = []
        try:
            done = subprocess.run([str(self.root / "build/linux/pluto_find")], capture_output=True, text=True, timeout=20)
            for line in done.stdout.splitlines():
                parts = line.split("\t")
                if len(parts) == 6 and parts[0] == "pluto":
                    plutos.append(tuple(parts[1:]))
        except (OSError, subprocess.SubprocessError):
            pass
        print("PLUTO SEARCH: %s" % (", ".join(found[0] for found in plutos) or "none"), flush=True)
        self.found.emit(plutos)


class Player(QMainWindow):
    def __init__(self, root, preview=None, window=None):
        super().__init__()
        self.root = root
        self.preview = preview   # (width, height): layout check in a window, without the stick
        window = preview or window   # (width, height): a window of that screen size instead of fullscreen
        self.settings_path = root / "pi-player-settings.json"
        self.volume = 40
        self.muted = False
        self.lnb_mhz = 9750
        self.band = bands.BY_KEY["qo100"]
        self.gains = {}   # RF gain per band
        self.los = {}     # chosen converter LO per band
        self.indicator = "eye"
        self.spectrum_source = "batc"   # QO-100 default; LOCAL scans with the stick
        self.stream_on, self.stream_to = False, udp_endpoint(DEFAULT_STREAM)   # live reception to the home network
        self.device, self.pluto_uri = "rtl", DEFAULT_PLUTO   # the receiver: RTL-SDR stick or ADALM-Pluto
        self.pluto_name, self.pluto_serial, self.pluto_address, self.pluto_model = "", "", "", ""
        self.pluto_resolving = False   # a search to find the saved Pluto again is running
        self.pluto_waiting = False     # start reception once that search is done
        self.pluto_missing_since = None   # when the chosen Pluto stopped answering
        self.pluto_wifi = False           # the chosen Pluto is reached over Wi-Fi
        self.pluto_searched = 0.0
        try:
            settings = json.loads(self.settings_path.read_text())
            self.volume = max(0, min(100, int(settings.get("volume", 40))))
            self.muted = bool(settings.get("muted", False))
            self.lnb_mhz = max(9000, min(11000, float(settings.get("lnb_mhz", 9750))))
            self.gains = {key: max(0, min(49.6, float(value))) for key, value in settings.get("gains", {}).items()
                          if key in bands.BY_KEY}
            if "gain_db" in settings and "qo100" not in self.gains:   # saved before bands existed
                self.gains["qo100"] = max(0, min(49.6, float(settings["gain_db"])))
            self.band = bands.BY_KEY.get(settings.get("band"), self.band)
            self.stream_on = bool(settings.get("udp_enabled", False))
            self.device = "pluto" if settings.get("device") == "pluto" else "rtl"
            self.pluto_uri = str(settings.get("pluto_uri", DEFAULT_PLUTO))
            self.pluto_name = str(settings.get("pluto_name", ""))
            self.pluto_serial = str(settings.get("pluto_serial", ""))
            self.pluto_address = str(settings.get("pluto_address", ""))
            self.pluto_model = str(settings.get("pluto_model", ""))
            try:   # no keyboard on the Pi: another destination is set in this file
                self.stream_to = udp_endpoint(settings.get("udp_destination", DEFAULT_STREAM))
            except ValueError:
                pass
            self.los = {key: float(value) for key, value in settings.get("los", {}).items()
                        if key in bands.BY_KEY and float(value) in bands.BY_KEY[key].converter_los}
            self.indicator = "constellation" if settings.get("indicator") == "constellation" else "eye"
            self.spectrum_source = "local" if settings.get("spectrum") == "local" else "batc"
        except (OSError, ValueError, TypeError):
            pass
        self.current = None
        self.last_signal = Signal(self.band.beacon_mhz, 1500) if self.band.beacon_mhz else None
        self.session_id = 0
        self.last_report = 0
        self.lost = 0
        self.modulation = ""
        self.tuned_at = 0
        self.locked_at = None
        self.lost_since = None   # when a station that was locked went silent
        self.idle_since = None   # QO-100: since when nothing has been decoded
        self.calibration = None
        self.zoom = None   # (start, end) MHz while zoomed into a wide band
        self.full_video = False
        self.stick_busy = False
        self.waiting_for_stick = False
        self.stick_seen = 0
        self.setWindowTitle("DATVision by HB9IIU")
        self.setStyleSheet(ui_scale.style(STYLE))
        self.pages = QStackedWidget()
        self.setCentralWidget(self.pages)
        surface = QWidget(objectName="surface")
        self.pages.addWidget(surface)
        self.surface_layout = QVBoxLayout(surface)
        self.surface_layout.setContentsMargins(px(6), px(6), px(6), px(6))
        self.surface_layout.setSpacing(px(6))
        top = QHBoxLayout()
        top.setSpacing(px(6))
        self.info = InfoPanel()
        self.info.setFixedWidth(px(148))
        self.info.show_indicator(self.indicator)
        self.info.indicator_changed.connect(self.set_indicator)
        top.addWidget(self.info)
        video_container = QWidget()
        self.video_stack = QStackedLayout(video_container)
        self.video_stack.setContentsMargins(0, 0, 0, 0)
        self.placeholder = QLabel("Waiting for beacon video…")
        self.placeholder.setAlignment(Qt.AlignCenter)
        self.placeholder.setWordWrap(True)
        self.video_stack.addWidget(self.placeholder)
        self.show_placeholder("Waiting for beacon video…")
        self.video_surface = QFrame()
        self.video_surface.setAttribute(Qt.WA_NativeWindow)
        self.video_surface.setStyleSheet("background: black;")
        self.video_surface.setMinimumHeight(px(180))
        self.video_stack.addWidget(self.video_surface)
        top.addWidget(video_container, 1)
        self.controls = ControlPanel()
        self.controls.setFixedWidth(px(118))
        top.addWidget(self.controls)
        self.surface_layout.addLayout(top, 1)
        self.spectrum = Spectrum()
        self.spectrum.setFixedHeight(px(140))
        self.surface_layout.addWidget(self.spectrum)
        version_file = root / "VERSION"
        version = version_file.read_text().strip() if version_file.exists() else "development"
        self.settings = SettingsPage()
        self.about_page = AboutPage(CREDITS, LICENCE, version, root / "assets/branding/datvision-wordmark.png")
        self.pages.addWidget(self.about_page)
        self.settings.about.connect(lambda: self.pages.setCurrentWidget(self.about_page))
        self.pluto_page = PlutoPage()
        self.pages.addWidget(self.pluto_page)
        self.pluto_search = PlutoSearch(root)
        self.pluto_search.found.connect(self.plutos_found)
        self.settings.choose_pluto.connect(self.open_pluto_page)
        self.pluto_page.search.connect(self.search_plutos)
        self.pluto_page.back.connect(lambda: self.pages.setCurrentWidget(self.settings))
        self.pluto_page.chosen.connect(self.pluto_chosen)
        self.about_page.back.connect(lambda: self.pages.setCurrentWidget(self.settings))
        self.about_page.diagnose.connect(self.save_diagnostics)
        self.pages.addWidget(self.settings)
        self.bands_page = BandsPage(bands.BANDS, "DATVision by HB9IIU · " + LICENCE + " Credits: ⚙ → ABOUT.")
        self.pages.addWidget(self.bands_page)
        self.bands_page.chosen.connect(self.choose_band)
        self.bands_page.set_receiver(self.device == "pluto")
        self.bands_page.quit.connect(self.close)
        self.recordings_page = RecordingsPage()
        self.pages.addWidget(self.recordings_page)
        self.recordings_page.chosen.connect(self.play_recording)
        self.recordings_page.back.connect(lambda: self.pages.setCurrentIndex(0))
        self.replaying = None   # path of a recording being played back
        self.spectrum.set_band(self.band)
        self.controls.show_band(self.band)
        self.controls.show_gain(self.gain_db)
        self.controls.gain_down.connect(lambda: self.step_gain(-1))
        self.controls.gain_up.connect(lambda: self.step_gain(1))
        # Apply gain steps together, shortly after the last tap.
        self.gain_timer = QTimer(self)
        self.gain_timer.setSingleShot(True)
        self.gain_timer.timeout.connect(self.apply_gain)

        self.receiver = Receiver(root, realtime=SLOW_PI)
        self.receiver.stream_to = self.stream_to if self.stream_on else None
        # LOCAL spectrum: the stick sweeps the band until a signal is tapped.
        self.sweeper = Sweeper(root)
        self.sweep_id = 0
        self.sweeper.frame.connect(self.on_sweep)
        self.sweeper.failed.connect(self.on_sweep_failure)
        self.receiver.status.connect(self.on_status)
        self.receiver.failed.connect(self.on_failure)
        # mpv draws the video; libVLC remains as a fallback (no mpv, or DATVRX_VIDEO=vlc).
        if mpv_video.AVAILABLE and os.environ.get("DATVRX_VIDEO") != "vlc":
            self.video = mpv_video.MpvVideo(self.video_surface.winId(), root / "test-output/pi-player-video.log",
                                            max_fps=25 if SLOW_PI else None)
        else:
            self.video = Video(self.video_surface.winId())
        # VLC ignores the pointer, so taps on the picture reach Qt.
        self.video_surface.installEventFilter(self)
        self.placeholder.installEventFilter(self)
        self.spectrum.clicked.connect(self.select_signal)
        self.spectrum.rescan.connect(self.rescan)
        self.spectrum.navigate.connect(self.navigate)
        self.spectrum.scan_check = lambda: self.sweeper.running
        self.controls.mute.connect(self.toggle_mute)
        self.controls.louder.connect(lambda: self.change_volume(10))
        self.controls.quieter.connect(lambda: self.change_volume(-10))
        self.controls.beacon.connect(lambda: self.cancel_calibration() or self.tune(Signal(BEACON_MHZ, 1500)))
        self.controls.replay.connect(self.open_recordings)
        self.controls.bands.connect(self.open_bands)
        self.controls.settings.connect(self.open_settings)
        self.controls.quit.connect(self.close)
        self.controls.record.connect(self.toggle_recording)
        self.settings.done.connect(self.close_settings)
        self.settings.calibrate.connect(self.start_calibration)
        self.controls.show_audio(self.volume, self.muted)
        # Development keys; the touchscreen needs none of them.
        QShortcut(QKeySequence("F11"), self, activated=self.toggle_fullscreen)
        QShortcut(QKeySequence("Escape"), self, activated=self.showNormal)
        QShortcut(QKeySequence("Q"), self, activated=self.close)
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.tick)
        self.timer.start(1000)
        # Until the first picture, look for it often so the video appears without delay.
        self.picture_timer = QTimer(self)
        self.picture_timer.timeout.connect(self.check_picture)
        if preview:
            # Layout preview: the stick, the receiver and the saved settings are left alone.
            self.receiver.tune = lambda *args: 0
            self.sweeper.start = lambda *args: 0
            self.spectrum_source = "batc"   # something to look at without the stick
        if window:
            self.setWindowTitle("DATVision · %s %dx%d" % ((("layout preview" if preview else "live window"),) + window))
            self.setFixedSize(*window)
            self.show()
        else:
            self.showFullScreen()
        screen = self.screen()
        print("DISPLAY: %dx%d, %.2f Hz, scale %.2f%s" % (screen.size().width(), screen.size().height(), screen.refreshRate(),
                                                       ui_scale.scale, " · preview" if preview else ""), flush=True)
        self.h266 = isinstance(self.video, mpv_video.MpvVideo) and decodes_h266()
        self.about_page.show_h266(self.h266)
        print("PI: %s%s · video: %s%s" % (pi_model() or "unknown", " · real-time receiver" if SLOW_PI else "",
                                         "mpv" if isinstance(self.video, mpv_video.MpvVideo) else "libVLC",
                                         " · H.266" if self.h266 else " · no H.266 decoder"), flush=True)
        QTimer.singleShot(200, lambda: self.spectrum.set_source(self.effective_source()))
        self.spectrum.receiver = "PLUTO" if self.device == "pluto" else "RTL-SDR"
        QTimer.singleShot(500, self.start_reception)

    @property
    def gain_db(self):
        return self.gains.get(self.band.key, self.band.gain_db)

    @gain_db.setter
    def gain_db(self, value):
        self.gains[self.band.key] = value

    def offset_mhz(self):
        # QO-100 is shifted by the LNB; terrestrial bands come straight from the antenna.
        if self.band.via_lnb:
            return self.lnb_mhz
        return self.lo_mhz() if self.band.converter_los else 0

    def lo_mhz(self):
        return self.los.get(self.band.key, self.band.converter_los[0] if self.band.converter_los else 0)

    def effective_source(self):
        # BATC only covers QO-100; elsewhere the stick or the Pluto makes the spectrum.
        return self.spectrum_source if self.band.via_lnb else "local"

    # ---- choosing a Pluto: the ones found on USB and the network
    def open_pluto_page(self):
        self.pages.setCurrentWidget(self.pluto_page)
        self.search_plutos()

    def search_plutos(self):
        self.pluto_page.show_searching(self.settings.pluto_uri)
        self.pluto_search.start()

    def saved_pluto(self):
        return {"uri": self.pluto_uri, "name": self.pluto_name, "serial": self.pluto_serial,
                "address": self.pluto_address, "model": self.pluto_model}

    def plutos_found(self, found):
        wifi = {uri for uri, *_ in found if over_wifi(uri)}
        self.pluto_page.show_found(found, pluto_starving(), wifi)
        if not self.pluto_resolving:
            return
        # Find the saved Pluto again: its address may have changed, or its name be shared.
        self.pluto_resolving = False
        match = plutos.choose(self.saved_pluto(), plutos.offered(found)[0])
        if match and match["uri"] != self.pluto_uri:
            print("PLUTO found again: %s -> %s" % (self.pluto_uri, match["uri"]), flush=True)
            self.use_pluto(match)
            self.save_settings()
        if self.pluto_waiting:
            self.pluto_waiting = False
            self.start_default()

    def start_reception(self):
        # A saved Pluto may have a new address since last time: find it first (about 2 s).
        if self.device == "pluto":
            self.show_placeholder("Looking for the Pluto…")
            self.find_pluto(then_start=True)
        else:
            self.start_default()

    def find_pluto(self, then_start=False):
        """Search in the background and switch to the saved Pluto's current way in."""
        self.pluto_resolving, self.pluto_waiting = True, then_start
        self.pluto_searched = time.monotonic()
        self.pluto_search.start()

    def use_pluto(self, pluto):
        self.pluto_uri, self.pluto_name = pluto["uri"], pluto.get("name", "")
        self.pluto_serial, self.pluto_address = pluto.get("serial", ""), pluto.get("address", "")
        self.pluto_model = pluto.get("model", "")

    def pluto_chosen(self, pluto):
        print("PLUTO chosen: %s (%s, %s)" % (pluto["uri"], pluto["name"], pluto["serial"]), flush=True)
        self.settings.set_pluto(pluto)
        self.settings.set_device("pluto")
        self.pages.setCurrentWidget(self.settings)   # applied with DONE, like every setting

    def device_present(self):
        # A Pluto on the network (ip:…) is not on USB: ask its IIO server instead.
        if self.device == "pluto" and self.pluto_uri.startswith("ip:"):
            try:
                with socket.create_connection((self.pluto_uri[3:], 30431), timeout=0.3):
                    return True
            except OSError:
                return False
        return stick_present(self.device)

    def show_missing_device(self):
        if self.device == "pluto":
            # A Pluto that restarts is gone for half a minute: first say it is being looked for.
            if self.pluto_missing_since is None:
                self.pluto_missing_since = time.monotonic()
            if pluto_starving():
                self.show_problem(PLUTO_NO_POWER, "PLUTO POWER", True)
            elif time.monotonic() - self.pluto_missing_since < 20:
                self.show_problem(PLUTO_LOOKING, "SEARCHING", True, AMBER)
            else:
                self.show_problem(NO_PLUTO, "NO PLUTO", True)
        else:
            self.show_problem(NO_STICK, "NO RTL-SDR", True)

    def start_default(self):
        # BATC: straight to the beacon, as before. LOCAL: scan until a signal is tapped.
        if self.effective_source() == "local":
            self.start_scan()
        else:
            self.tune(Signal(BEACON_MHZ, 1500))

    def start_scan(self):
        if not self.device_present():
            self.show_missing_device()
            return
        start, end = self.zoom or (self.band.start_mhz, self.band.end_mhz)
        print("SCAN: %s %.3f-%.3f MHz" % (self.band.name, start, end), flush=True)
        self.spectrum.begin_rescan()
        self.info.reset(None)
        self.info.show_state("SCANNING", "#8cc4ff")
        self.show_placeholder("Scanning the band\n\nTap a signal to watch it")
        self.sweep_id = self.sweeper.start((start - self.offset_mhz()) * 1e6, (end - self.offset_mhz()) * 1e6, self.gain_db,
                                           self.device, self.pluto_uri)

    def on_sweep(self, sweep_id, values):
        if sweep_id == self.sweep_id and self.sweeper.running:
            self.spectrum.set_local(values)

    def on_sweep_failure(self, sweep_id, message):
        if sweep_id != self.sweep_id:
            return
        self.sweeper.stop()
        if not self.device_present():
            self.show_missing_device()
        else:
            self.show_problem("Spectrum scan stopped\n\nChoose the band again under BANDS", "ERROR", False)

    def toggle_fullscreen(self):
        if self.isFullScreen():
            self.showNormal()
            self.resize(900, 570)
        else:
            self.showFullScreen()

    def eventFilter(self, watched, event):
        if event.type() == QEvent.MouseButtonPress and watched in (self.video_surface, self.placeholder):
            if self.replaying:
                self.end_replay()   # tapping the playing recording ends it
            elif self.current or self.full_video:
                self.toggle_full_video()
            return True
        return super().eventFilter(watched, event)

    def toggle_full_video(self):
        self.full_video = not self.full_video
        for widget in (self.info, self.controls, self.spectrum):
            widget.setVisible(not self.full_video)
        margin = 0 if self.full_video else 6
        self.surface_layout.setContentsMargins(margin, margin, margin, margin)

    def select_signal(self, mhz):
        self.cancel_calibration()
        full = self.band.end_mhz - self.band.start_mhz
        if full > bands.ZOOM_ABOVE_MHZ:
            # Wide band: a tap recentres the view on that spot and scans again, at the same width (from the whole band the
            # first tap zooms to FIRST_ZOOM_MHZ, or a third of a narrower band). A tap on the middle of the view, where a
            # recentred signal now sits, tunes it.
            start, end = self.zoom or (self.band.start_mhz, self.band.end_mhz)
            width = end - start
            if width >= full - 0.01:
                self.show_view(mhz, max(bands.MIN_VIEW_MHZ, min(bands.FIRST_ZOOM_MHZ, full / 3)))
                return
            if abs(mhz - (start + end) / 2) > width * 0.05:
                self.show_view(mhz, width)
                return
        signal = self.spectrum.signal_at(mhz)
        if signal is None:
            self.spectrum.show_message("No signal at %.3f MHz" % mhz)
        elif signal.symbol_rate_ks > 1500:
            self.spectrum.show_message("This signal is too wide for this receiver (1500 kS/s at most)")
        else:
            self.tune(signal)

    def show_placeholder(self, text, color=DIM):
        self.placeholder.setText(text)
        self.placeholder.setStyleSheet("background: black; color: %s; font-size: %dpx; font-weight: normal;" % (color, px(30)))
        self.video_stack.setCurrentWidget(self.placeholder)

    def show_problem(self, message, state, wait_for_stick, color=RED):
        print("PROBLEM: " + message.splitlines()[0], flush=True)
        self.cancel_calibration()
        self.sweeper.stop()
        self.stop(scan=False)
        self.waiting_for_stick = wait_for_stick
        self.stick_seen = 0
        self.info.show_state(state, color)
        self.show_placeholder(message, "#dde8f1")

    def tune(self, signal):
        print("TUNE: %.6f MHz, %d kS/s" % (signal.center_mhz, signal.symbol_rate_ks), flush=True)
        self.tuned_at = time.monotonic()
        self.leave_replay()
        self.last_signal = signal
        if not self.device_present():
            self.show_missing_device()
            return
        # Answer the tap on screen first, then swap the receiver.
        self.session_id = -1
        self.current = signal
        self.waiting_for_stick = self.stick_busy = False
        self.last_report = time.monotonic()
        self.locked_at = None
        self.lost_since = None
        self.lost = 0
        self.modulation = ""
        self.info.reset(signal)
        self.info.show_state("TUNING", AMBER)
        self.show_placeholder("Tuning %.3f MHz…" % signal.center_mhz)
        self.spectrum.set_slot(signal, "searching", now=True)
        self.info.repaint()
        self.placeholder.repaint()
        if self.sweeper.running:
            # The stick receives now; the last sweep stays on screen, paused.
            self.sweeper.stop()
            self.spectrum.frozen = True
            self.spectrum.update()
        stopping = time.monotonic()
        self.end_recording()
        self.receiver.stop()
        stopped = time.monotonic()
        self.video.stop()
        timing = (stopped - stopping, time.monotonic() - stopped)
        self.show_placeholder("Waiting for the picture…")
        self.picture_timer.start(100)
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            self.video.play(port, self.output_volume())
            self.session_id = self.receiver.tune(signal, self.offset_mhz(), port, self.gain_db, self.device, self.pluto_uri)
            if self.device == "pluto":
                self.pluto_missing_since = None
                self.pluto_wifi = over_wifi(self.pluto_uri)
                if self.pluto_wifi:
                    print("PLUTO over Wi-Fi: " + self.pluto_uri, flush=True)
        except Exception as error:
            self.show_problem(str(error), "ERROR", False)
            return
        print("TUNE: interface busy %d ms (receiver stop %d, video stop %d)"
              % ((time.monotonic() - self.tuned_at) * 1000, timing[0] * 1000, timing[1] * 1000), flush=True)

    def mark_lock(self, locked):
        if locked and self.locked_at is None:
            self.locked_at = time.monotonic()
            print("LOCK after %.1f s" % (self.locked_at - self.tuned_at), flush=True)
        if locked:
            self.info.show_state("LOCKED", GREEN)
            if self.lost_since is not None:
                print("SIGNAL back after %.0f s" % (time.monotonic() - self.lost_since), flush=True)
                self.lost_since = None
                self.video_stack.setCurrentWidget(self.video_surface)
        elif self.locked_at is not None and self.lost_since is None:
            self.lost_since = time.monotonic()
            print("SIGNAL lost", flush=True)
        # Red once a lock is lost, or when none has come within ten seconds.
        lost = self.locked_at is not None or time.monotonic() - self.tuned_at > 10
        self.spectrum.set_slot(self.current, "locked" if locked else "lost" if lost else "searching")

    def check_picture(self):
        if self.calibration:
            return   # the progress stays on screen until calibration ends
        if not (self.current or self.replaying):
            self.picture_timer.stop()
        elif self.video.has_picture() and self.lost_since is None:
            self.picture_timer.stop()
            self.video_stack.setCurrentWidget(self.video_surface)
            print("PICTURE after %.1f s" % (time.monotonic() - self.tuned_at), flush=True)

    def on_status(self, session_id, line):
        if session_id != self.session_id or self.current is None:
            return
        if line.startswith("datvrx cells "):
            if not self.full_video:
                self.info.constellation.set_points(line[13:], self.modulation)
        elif line.startswith("datvrx ["):
            self.last_report = time.monotonic()
            report = REPORT.search(line)
            if report:
                standard, modulation, rate, esn0, bad = report.groups()
                self.modulation = modulation
                required = REQUIRED_ESN0.get((standard, modulation, rate))
                self.info.show_reception("%s %s %s" % (standard, modulation, rate), float(esn0), required, int(bad), self.lost)
                locked = "TS lock" in line
                self.info.eye.show_quality(True, locked, float(esn0), required)
                if not locked:
                    self.info.show_state("SEARCHING", AMBER)
                self.mark_lock(locked)
            else:
                self.info.show_reception("Searching…", None, None, None, self.lost)
                self.info.eye.show_quality(True, False, None, None)
                self.info.show_state("SEARCHING", AMBER)
                self.mark_lock(False)
        elif line.startswith("datvrx carrier "):
            self.calibration_sample(float(line[15:]))
        elif line == "datvrx: transport stream lock":
            self.mark_lock(True)   # at once, without waiting for the next report
        elif "usb_claim_interface" in line:
            self.stick_busy = True
        elif "synchronisation lost" in line:
            self.lost += 1
            self.info.show_state("REACQUIRING", AMBER)
            self.mark_lock(False)
        elif "error" in line.lower() or "failed" in line.lower():
            self.info.show_note(line)

    def on_failure(self, session_id, message):
        if session_id != self.session_id:
            return
        if not self.device_present():
            self.show_missing_device()
        elif self.stick_busy:
            self.show_problem(STICK_BUSY, "STICK BUSY", False)
        else:
            self.stop()
            if self.effective_source() == "batc":
                self.show_placeholder("Reception ended\n\nTap a signal to watch it again")

    def log_video(self):
        # Every 5 s while watching: what VLC decodes, shows and drops (diagnosis on slower Pis).
        stats = self.video.stats() if self.current else None
        if stats and int(time.monotonic()) % 5 == 0:
            print("VIDEO: decoded %d, displayed %d, lost %d" % stats, flush=True)

    def tick(self):
        self.log_video()
        if self.receiver.recording and shutil.disk_usage(self.root).free < self.RECORD_STOP_FREE:
            self.end_recording("SD card almost full")
        self.show_recording()
        if self.replaying:
            if self.video.ended:
                self.end_replay()
            return
        self.check_idle_beacon()
        if self.current and self.device == "pluto" and self.pluto_wifi:
            self.spectrum.show_message(PLUTO_WIFI)   # renewed every tick: it stays while tuned
        if self.waiting_for_stick:
            # A missing Pluto may be back under another address: search every 10 s.
            if self.device == "pluto" and not self.pluto_resolving and time.monotonic() - self.pluto_searched > 10:
                self.find_pluto()
            # Give a replugged stick two seconds to settle before opening it.
            self.stick_seen = self.stick_seen + 1 if self.device_present() else 0
            if self.stick_seen >= 2:
                self.waiting_for_stick = False
                self.start_scan() if self.effective_source() == "local" else self.tune(self.last_signal)
            return
        if not self.current:
            return
        if not self.device_present():
            self.show_missing_device()
            return
        step = self.calibration
        if step and step["samples"] is not None and time.monotonic() - step["started"] > calibration.STEP_TIMEOUT_S:
            print("CALIBRATION step %d: no lock" % (step["step"] + 1), flush=True)
            self.next_calibration_step(None)
        info = self.receiver.info
        width, height = self.video.size()
        video = info.video if info else ""
        if width:
            video = ("%s · %d×%d" % (video, width, height)).lstrip(" ·")
        audio = info.audio if info else ""
        if self.muted:
            audio = (audio + " · muted").lstrip(" ·")
        self.info.show_streams(info.service if info else "", info.provider if info else "",
                               video or "waiting for video", audio)
        if info and info.video == "H.266" and not self.h266 and self.lost_since is None \
                and self.video_stack.currentWidget() is self.placeholder:
            # Without a decoder mpv cannot even open the stream: no picture, no sound.
            self.show_placeholder("H.266 video cannot be decoded here.\n"
                                  "It needs Raspberry Pi OS Trixie.", AMBER)
        self.info.constellation.update()
        if time.monotonic() - self.last_report > 5:
            self.info.show_state("WAITING", AMBER)
            self.info.eye.show_quality(True, False, None, None)
            self.mark_lock(False)
        elif self.locked_at is None:
            self.mark_lock(False)   # turns the slot red after ten seconds without a lock
        self.check_signal()

    def check_idle_beacon(self):
        """On QO-100: nothing decoded for a minute -> back to the beacon by itself. Not during a replay or recording, while a page
        (settings, bands, about) or the calibration is open, while waiting for the stick, or when the beacon is already the tuned
        station (retuning it every minute would only disturb the lock)."""
        decoding = self.current is not None and self.locked_at is not None and self.lost_since is None
        beacon_now = self.current is not None and abs(self.current.center_mhz - BEACON_MHZ) < 0.01
        busy = (self.band.key != "qo100" or self.replaying or self.receiver.recording or self.calibration
                or self.waiting_for_stick or self.pages.currentIndex() != 0)
        if decoding or beacon_now or busy:
            self.idle_since = None
            return
        now = time.monotonic()
        if self.idle_since is None:
            self.idle_since = now
        elif now - self.idle_since >= IDLE_BEACON_S:
            self.idle_since = None
            print("IDLE: nothing decoded for %d s, back to the beacon" % IDLE_BEACON_S, flush=True)
            self.tune(Signal(BEACON_MHZ, 1500))
            self.spectrum.show_message("Nothing decoded for a minute · back to the beacon")

    def check_signal(self):
        # A station that goes silent: say so after 3 s instead of showing a frozen picture,
        # keep listening, and in LOCAL mode scan again after 30 s. The same 30 s apply to a
        # tapped signal that never locks.
        if self.calibration:
            return
        now = time.monotonic()
        silent = now - self.lost_since if self.lost_since is not None else now - self.tuned_at if self.locked_at is None else 0
        if silent >= SIGNAL_LOST_GIVE_UP_S and self.effective_source() == "local":
            print("SIGNAL: no lock for %d s, scanning again" % silent, flush=True)
            self.stop()
        elif self.lost_since is not None and silent >= SIGNAL_LOST_NOTICE_S:
            name = self.info.service.text() or "%.3f MHz" % self.current.center_mhz
            wait = ("Scanning again in %d s" % (SIGNAL_LOST_GIVE_UP_S - int(silent)) if self.effective_source() == "local"
                    else "Waiting for the station to return")
            self.show_placeholder("%s — signal lost\n\n%s\n(%d s)" % (name, wait, silent), "#dde8f1")

    def rescan(self):
        print("RESCAN", flush=True)
        self.cancel_calibration()
        self.spectrum.begin_rescan()   # erase the old picture and show the scanning at once
        self.stop()   # in LOCAL mode, stopping returns to scanning

    def stop(self, scan=True):
        self.leave_replay()
        self.session_id = -1
        self.end_recording()
        self.receiver.stop()
        self.video.stop()
        self.current = None
        self.waiting_for_stick = False
        if self.full_video:
            self.toggle_full_video()
        self.picture_timer.stop()
        self.show_placeholder("Stopped\n\nTap a signal to watch it")
        self.spectrum.set_slot(None, "searching")
        self.info.reset(None)
        self.info.show_state("STOPPED", DIM)
        if scan and self.effective_source() == "local":
            self.start_scan()

    def output_volume(self):
        return 0 if self.muted else self.volume

    def toggle_mute(self):
        self.muted = not self.muted
        self.apply_volume()

    def change_volume(self, step):
        self.volume = max(0, min(100, self.volume + step))
        self.muted = False
        self.apply_volume()

    def apply_volume(self):
        self.video.volume(self.output_volume())
        self.controls.show_audio(self.volume, self.muted)
        self.save_settings()

    # ---- LNB calibration: several locks on the beacon, deliberately detuned
    def start_calibration(self):
        self.pages.setCurrentIndex(0)
        if (self.settings.lnb_mhz, self.settings.gain_db) != (self.lnb_mhz, self.gain_db):
            self.lnb_mhz, self.gain_db = self.settings.lnb_mhz, self.settings.gain_db
            self.save_settings()
        if self.full_video:
            self.toggle_full_video()
        print("CALIBRATION start, LNB %.4f MHz" % self.lnb_mhz, flush=True)
        self.calibration = {"return": self.current, "step": 0, "estimates": [], "samples": None, "started": 0}
        self.calibration_step()

    def calibration_step(self):
        step = self.calibration
        if not step:
            return
        if step["step"] == len(calibration.STEPS_KHZ):
            self.finish_calibration()
            return
        self.tune(Signal(BEACON_MHZ + calibration.STEPS_KHZ[step["step"]] / 1000, 1500))
        step["samples"], step["started"] = [], time.monotonic()
        self.show_calibration_progress(None)

    def calibration_sample(self, offset_hz):
        step = self.calibration
        if not step or step["samples"] is None or self.locked_at is None or time.monotonic() - self.locked_at < calibration.SETTLE_S:
            return
        step["samples"].append(offset_hz)
        self.show_calibration_progress(offset_hz)
        if len(step["samples"]) >= calibration.SAMPLES:
            value = calibration.estimate(self.lnb_mhz, step["samples"], calibration.STEPS_KHZ[step["step"]])
            print("CALIBRATION step %d: offset %+.1f Hz, LNB %.4f MHz" % (step["step"] + 1, step["samples"][-1], value), flush=True)
            self.next_calibration_step(value)

    def next_calibration_step(self, value):
        step = self.calibration
        step["estimates"].append(value)
        step["samples"] = None   # ignore readings until the next step is tuned
        step["step"] += 1
        QTimer.singleShot(0, self.calibration_step)

    def show_calibration_progress(self, offset_hz):
        step = self.calibration
        text = "LNB calibration  %d / %d\n\nBeacon %+d kHz\n" % (step["step"] + 1, len(calibration.STEPS_KHZ),
                                                               calibration.STEPS_KHZ[step["step"]])
        text += "Waiting for lock…" if offset_hz is None else "Offset %+.1f kHz" % (offset_hz / 1000)
        self.show_placeholder(text, "#dde8f1")

    def finish_calibration(self):
        step, self.calibration = self.calibration, None
        result = calibration.summarize(step["estimates"])
        print("CALIBRATION result %s from %s" % (result, step["estimates"]), flush=True)
        self.resume_after_calibration(step)
        self.open_settings()
        self.settings.show_calibration(result, self.lnb_mhz)

    def cancel_calibration(self):
        if self.calibration:
            step, self.calibration = self.calibration, None
            print("CALIBRATION cancelled", flush=True)
            self.last_signal = step["return"] or Signal(BEACON_MHZ, 1500)

    def resume_after_calibration(self, step):
        if step["return"]:
            self.tune(step["return"])
        else:
            self.stop()
            self.last_signal = Signal(BEACON_MHZ, 1500)

    def open_settings(self):
        self.settings.load(self.lnb_mhz, self.gain_db, self.spectrum_source, self.band, self.lo_mhz(),
                           self.stream_on, vlc_address(self.stream_to), self.device,
                           self.saved_pluto())
        self.pages.setCurrentWidget(self.settings)

    def save_diagnostics(self):
        """SAVE DIAGNOSTICS on the About page: scripts/linux/diagnose.sh writes a report into the home folder."""
        script = self.root / "scripts/linux/diagnose.sh"
        if not script.exists():
            self.about_page.show_diagnostics("Diagnostics script not found")
            return
        self.about_page.show_diagnostics("Collecting diagnostics, a few seconds...")
        process = QProcess(self)

        def done(*_):
            lines = bytes(process.readAllStandardOutput()).decode(errors="replace").strip().splitlines()
            path = lines[-1] if lines else ""
            if path.endswith(".txt"):
                shown = path.replace(str(Path.home()), "~")
                self.about_page.show_diagnostics("Saved %s  ·  read it, then attach it to your message" % shown)
                print("DIAGNOSTICS: " + path, flush=True)
            else:
                self.about_page.show_diagnostics("Diagnostics could not be saved")
            process.deleteLater()
        process.finished.connect(done)
        process.start("bash", [str(script)])

    def close_settings(self):
        self.pages.setCurrentIndex(0)
        chosen = self.settings.pluto_uri or self.pluto_uri
        device_changed = (self.settings.device, chosen) != (self.device, self.pluto_uri)
        self.device = self.settings.device
        if self.settings.pluto_uri:
            self.use_pluto(self.settings.pluto)
        if self.settings.network_on != self.stream_on:
            self.stream_on = self.settings.network_on
            self.receiver.stream_to = self.stream_to if self.stream_on else None   # applies at once
            print("NETWORK STREAM: %s" % ("%s:%d" % self.stream_to if self.stream_on else "off"), flush=True)
            self.save_settings()
        if self.settings.source != self.spectrum_source:
            self.spectrum_source = self.settings.source
            print("SPECTRUM: " + self.spectrum_source, flush=True)
            self.save_settings()
            self.spectrum.set_source(self.spectrum_source)
            if self.spectrum_source == "batc":
                self.sweeper.stop()
                if not self.current:
                    self.tune(Signal(BEACON_MHZ, 1500))
            elif self.current:
                self.stop()   # a local spectrum needs the stick: stop watching, scan
            else:
                self.start_scan()
        lo = self.settings.lo_mhz if self.band.converter_los else self.lo_mhz()
        changed = (self.settings.lnb_mhz, self.settings.gain_db, lo) != (self.lnb_mhz, self.gain_db, self.lo_mhz())
        self.lnb_mhz, self.gain_db = self.settings.lnb_mhz, self.settings.gain_db
        if self.band.converter_los:
            self.los[self.band.key] = lo
        self.spectrum.receiver = "PLUTO" if self.device == "pluto" else "RTL-SDR"
        self.bands_page.set_receiver(self.device == "pluto")
        if device_changed:
            print("DEVICE: " + self.device, flush=True)
            if not self.band.usable(self.device == "pluto"):
                self.band = bands.BY_KEY["qo100"]   # the Pluto cannot tune this band: back to QO-100
                self.zoom = None
                self.last_signal = Signal(self.band.beacon_mhz, 1500)
                self.controls.show_band(self.band)
                self.controls.show_gain(self.gain_db)
                self.spectrum.set_band(self.band)
                self.bands_page.show_current(self.band.key)
            self.save_settings()
            self.stop(scan=False)
            self.sweeper.stop()
            self.spectrum.set_source(self.effective_source())
            self.start_default()   # the beacon on QO-100, else the band's scan
        elif changed:
            self.save_settings()
            if self.current:
                self.tune(self.current)
            elif self.sweeper.running:
                self.start_scan()

    def show_view(self, centre, width):
        """The view (the whole band or a zoomed window of it) centred on `centre`, `width` MHz wide, and scan it."""
        full = self.band.end_mhz - self.band.start_mhz
        width = max(bands.MIN_VIEW_MHZ, min(width, full))
        start = max(self.band.start_mhz, min(centre - width / 2, self.band.end_mhz - width))
        self.zoom = None if width >= full - 0.01 else (start, start + width)
        print("VIEW: %.3f-%.3f MHz" % (start, start + width), flush=True)
        self.spectrum.set_view(start, start + width, self.zoom is not None)
        self.spectrum.begin_rescan()
        self.stop() if self.current else self.start_scan()   # stopping returns to scanning, now with the new view

    def navigate(self, key):
        """The zoom buttons: - and + (a factor of three around the middle) and FULL (the whole band)."""
        self.cancel_calibration()
        full = self.band.end_mhz - self.band.start_mhz
        start, end = self.zoom or (self.band.start_mhz, self.band.end_mhz)
        width, centre = end - start, (start + end) / 2
        if key == "in":
            self.show_view(centre, width / 3)
        elif key == "out":
            self.show_view(centre, width * 3)
        elif key == "full":
            self.show_view(centre, full)

    def step_gain(self, step):
        index = min(range(len(GAINS)), key=lambda i: abs(GAINS[i] - self.gain_db))
        self.gain_db = GAINS[max(0, min(len(GAINS) - 1, index + step))]
        self.controls.show_gain(self.gain_db)
        self.gain_timer.start(800)

    def apply_gain(self):
        print("GAIN: %.1f dB" % self.gain_db, flush=True)
        self.save_settings()
        if self.current:
            self.tune(self.current)
        elif self.sweeper.running:
            self.start_scan()

    # ---- TS recording, as in the Windows version: Record/<callsign>/DATV-<time>.ts
    RECORD_MIN_FREE = 500 * 2**20    # do not start below this
    RECORD_STOP_FREE = 300 * 2**20   # stop below this: the SD card must never fill up

    def toggle_recording(self):
        if self.receiver.recording:
            self.end_recording()
            return
        if not (self.current and self.locked_at is not None and self.lost_since is None):
            self.spectrum.show_message("Tune and lock a station before recording")
            return
        folder = self.root / "Record" / (callsign(self.info.service.text()) or "Unknown")
        if shutil.disk_usage(self.root).free < self.RECORD_MIN_FREE:
            self.spectrum.show_message("Not enough free space on the SD card to record")
            return
        try:
            folder.mkdir(parents=True, exist_ok=True)
            path = folder / ("DATV-%s-%03d.ts" % (time.strftime("%Y%m%d-%H%M%S"), int(time.time() * 1000) % 1000))
            self.receiver.start_recording(path)
        except (OSError, RuntimeError) as error:
            self.spectrum.show_message("Recording: %s" % error)
            return
        print("RECORD start %s" % path, flush=True)
        self.show_recording()

    def end_recording(self, reason=""):
        result = self.receiver.stop_recording()
        if result:
            path, size = result
            print("RECORD stop %s, %.1f MB%s" % (path, size / 2**20, ", " + reason if reason else ""), flush=True)
            self.spectrum.show_message("%sSaved %s (%.0f MB)" % (reason + " · " if reason else "", path.name, size / 2**20))
        self.show_recording()

    def show_recording(self):
        possible = bool(self.current and self.locked_at is not None and self.lost_since is None)
        self.controls.show_recording(self.receiver.recording, possible)

    # ---- playing recordings back: the stick and the receiver rest meanwhile
    def open_recordings(self):
        self.recordings_page.load(self.root / "Record")
        self.pages.setCurrentWidget(self.recordings_page)

    def play_recording(self, path):
        path = Path(path)
        self.pages.setCurrentIndex(0)
        print("REPLAY %s" % path, flush=True)
        self.cancel_calibration()
        self.sweeper.stop()
        self.stop(scan=False)
        info = TsInfo()
        try:
            with open(path, "rb") as recording:
                info.feed(recording.read(2_000_000))
        except OSError:
            pass
        self.replaying = path
        self.info.show_state("REPLAY", AMBER)
        self.info.show_streams(info.service or path.parent.name, path.name, info.video, info.audio)
        self.spectrum.frozen = True
        self.spectrum.show_message("Playing a recording · reception paused")
        self.show_placeholder("Opening the recording…")
        if not self.full_video:
            self.toggle_full_video()   # full screen; a tap on the picture ends it
        self.video.play_file(path, self.output_volume())
        self.picture_timer.start(100)

    def leave_replay(self):
        if self.replaying:
            self.replaying = None
            self.video.stop()
            if self.full_video:
                self.toggle_full_video()

    def end_replay(self):
        print("REPLAY end", flush=True)
        self.leave_replay()
        self.start_default()   # back to scanning, or to the beacon with BATC

    def open_bands(self):
        self.bands_page.show_current(self.band.key)
        self.pages.setCurrentWidget(self.bands_page)

    def choose_band(self, key):
        self.pages.setCurrentIndex(0)
        band = bands.BY_KEY[key]
        if band == self.band:
            return
        print("BAND: " + band.name, flush=True)
        self.cancel_calibration()
        self.sweeper.stop()
        self.stop(scan=False)
        self.band = band
        self.zoom = None
        self.last_signal = Signal(band.beacon_mhz, 1500) if band.beacon_mhz else None
        self.controls.show_band(band)
        self.controls.show_gain(self.gain_db)
        self.spectrum.set_band(band)
        self.spectrum.set_source(self.effective_source())
        self.spectrum.begin_rescan()   # the new band's scale and the scanning at once, before the receiver has stopped
        self.save_settings()
        self.start_default()

    def set_indicator(self, name):
        self.indicator = name
        self.save_settings()

    def save_settings(self):
        if self.preview:
            return
        try:
            self.settings_path.write_text(json.dumps({"volume": self.volume, "muted": self.muted,
                                                      "lnb_mhz": self.lnb_mhz, "gains": self.gains, "los": self.los, "band": self.band.key,
                                                      "indicator": self.indicator, "spectrum": self.spectrum_source,
                                                      "udp_enabled": self.stream_on,
                                                      "udp_destination": "udp://%s:%d" % self.stream_to,
                                                      "device": self.device, "pluto_uri": self.pluto_uri,
                                                      "pluto_name": self.pluto_name, "pluto_serial": self.pluto_serial,
                                                      "pluto_address": self.pluto_address,
                                                      "pluto_model": self.pluto_model}, indent=2))
        except OSError:
            pass

    def closeEvent(self, event):
        self.timer.stop()
        self.spectrum.stop()
        self.sweeper.stop()
        self.end_recording()
        self.receiver.stop()
        if isinstance(self.video, mpv_video.MpvVideo):
            self.video.close()
        self.save_settings()
        event.accept()
        # End here, without libVLC's own shutdown: on a Pi 4 with Trixie it aborts
        # ("pthread_mutex_lock: assertion failed"), and nothing needs it at exit.
        print("QUIT", flush=True)
        os._exit(0)


def welcome_card(version, wordmark, update=""):
    """The welcome window's picture: logo, receivers, version and an available update."""
    logo = wordmark.scaledToWidth(px(560), Qt.SmoothTransformation)
    card = QPixmap(px(640), logo.height() + px(120) + (px(40) if update else 0))
    card.fill(Qt.transparent)
    radius = px(22)
    painter = QPainter(card)
    painter.setRenderHint(QPainter.Antialiasing)
    painter.setPen(QPen(QColor("#1d3446"), px(2)))
    painter.setBrush(QColor("#0c1620"))
    painter.drawRoundedRect(QRectF(card.rect()).adjusted(1, 1, -1, -1), radius, radius)
    painter.drawPixmap((card.width() - logo.width()) // 2, px(18), logo)
    font = QFont("DejaVu Sans")
    lines = [("for RTL-SDR and Pluto / FISHBall", 20, "#dde8f1", logo.height() + px(30)),
             ("Version " + version, 16, "#9cb4c5", logo.height() + px(64))]
    if update:
        lines.append(("Update available: DATVision %s  ·  github.com/HB9IIU/DATVision" % update, 17, "#57f59f",
                      logo.height() + px(104)))
    for text, size, color, top in lines:
        font.setPixelSize(px(size))
        painter.setFont(font)
        painter.setPen(QColor(color))
        painter.drawText(QRectF(0, top, card.width(), px(size) * 1.6), Qt.AlignHCenter | Qt.AlignVCenter, text)
    painter.end()
    return card


def welcome(root):
    """The DATVision logo and version for 4 s at start; a tap closes it sooner. A window
    of its own, so that the video appearing meanwhile cannot cover it."""
    wordmark = QPixmap(str(root / "assets/branding/datvision-wordmark.png"))
    if wordmark.isNull():
        return None
    version_file = root / "VERSION"
    version = version_file.read_text().strip() if version_file.exists() else "development"
    card = welcome_card(version, wordmark)
    # Without a frame or title bar: the desktop's window manager is told to leave it alone.
    splash = QSplashScreen(card, Qt.WindowStaysOnTopHint | Qt.FramelessWindowHint | Qt.X11BypassWindowManagerHint)
    screen = QApplication.primaryScreen().geometry()
    splash.move(screen.center() - splash.rect().center())
    # Rounded corners: only the card itself belongs to the window.
    splash.setMask(card.mask())
    splash.show()
    QTimer.singleShot(4000, splash.close)

    def show_update(latest):   # the same window, one line longer
        if splash.isVisible():
            card = welcome_card(version, wordmark, latest)
            splash.setPixmap(card)
            splash.setMask(card.mask())
            splash.move(screen.center() - splash.rect().center())
    splash.show_update = show_update
    return splash


def main():
    # Xlib is shared by Qt and VLC; initialize its threading support first.
    ctypes.CDLL(find_library("X11")).XInitThreads()
    # PyQt aborts when an exception escapes a slot; log it and keep the player running.
    sys.excepthook = lambda kind, value, trace: traceback.print_exception(kind, value, trace)
    app = QApplication(sys.argv)
    # --preview WIDTHxHEIGHT: check the layout for another screen in a window, e.g. on a VNC desktop.
    # --window WIDTHxHEIGHT: the complete player in a window of that size, using the stick.
    sizes = {flag: tuple(int(value) for value in sys.argv[sys.argv.index(flag) + 1].lower().split("x"))
             for flag in ("--preview", "--window") if flag in sys.argv[:-1]}
    preview, window = sizes.get("--preview"), sizes.get("--window")
    if preview:
        globals()["stick_present"] = lambda *args: True
    size = app.primaryScreen().size()
    ui_scale.set_for(*(preview or window or (size.width(), size.height())))
    font = QFont("DejaVu Sans")
    font.setPointSizeF(9 * ui_scale.scale)
    app.setFont(font)
    root = Path(__file__).resolve().parents[2]
    (root / "test-output").mkdir(exist_ok=True)
    if not preview:
        lock = (root / "test-output/pi-player.lock").open("w")
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            QMessageBox.information(None, "DATVision", "DATVision is already running.")
            return 1
    player = Player(root, preview, window)
    splash = None if preview else welcome(root)   # kept referenced until it closes
    version_file = root / "VERSION"
    check = UpdateCheck(version_file.read_text().strip() if version_file.exists() else "development")

    def update_available(latest):
        print("UPDATE available: " + latest, flush=True)
        player.about_page.show_update(latest)
        if splash is not None:
            splash.show_update(latest)
    check.newer.connect(update_available)
    if not preview:
        check.start()
    # Stopped from outside (an update, a shutdown): close like QUIT, which stops the receiver.
    signal.signal(signal.SIGTERM, lambda *args: player.close())
    signal.signal(signal.SIGINT, lambda *args: player.close())
    return app.exec_()


if __name__ == "__main__":
    sys.exit(main())
