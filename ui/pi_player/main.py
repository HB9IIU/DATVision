#!/usr/bin/env python3
"""Fullscreen touchscreen DATV player for Raspberry Pi. GPL-3.0-or-later."""
import ctypes
from ctypes.util import find_library
import fcntl
import json
from pathlib import Path
import re
import signal
import socket
import sys
import time
import traceback

from PyQt5.QtCore import Qt, QTimer, QEvent
from PyQt5.QtGui import QFont, QKeySequence
from PyQt5.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QLabel,
                             QShortcut, QMessageBox, QFrame, QStackedLayout, QStackedWidget)
import bands
import calibration
import ui_scale
from ui_scale import px
from signal_finder import BEACON_MHZ, Signal
from sweeper import Sweeper
from spectrum import Spectrum
from receiver import Receiver
from video import Video
from panels import GREEN, AMBER, RED, DIM, GAINS, REQUIRED_ESN0, InfoPanel, ControlPanel, SettingsPage, BandsPage

REPORT = re.compile(r"(DVB-S2X|DVB-S2|DVB-S) (\S+) (\d+/\d+)\S*(?: short)?(?: pilots)?\s+[\d.]+ Msym/s"
                    r"\s+Es/N0 ([\d.-]+) dB\s+ok \d+\s+bad (\d+)")
RTL_SDR_IDS = {("0bda", "2838"), ("0bda", "2832")}
NO_STICK = "RTL-SDR stick not found\n\nUnplug it and plug it back in.\nReception resumes by itself."
STICK_BUSY = "RTL-SDR stick is busy\n\nUnplug it and plug it back in.\nIf that does not help, restart the Pi."
SIGNAL_LOST_NOTICE_S = 3   # replace the frozen picture with a clear message
SIGNAL_LOST_GIVE_UP_S = 30  # LOCAL: stop waiting for the station and scan the band again
CREDITS = ("HB9IIU RTL DATV Player · GPL v3; source and notices in the project.\n"
           "Receiver: OnAir-derived C++/Rust datvrx. Spectrum detection adapted from HB9IIU "
           "Experimental H.266 Player. Magic eye after HB9IIU SDR-Tiouner, inspired by the Lynx DATV "
           "Receiver by G8YTZ. Spectrum: BATC remote reference; quality: local RTL-SDR. "
           "Video: system libVLC. Interface: PyQt5.")
STYLE = """
    QMainWindow, QWidget#surface, QWidget#settings { background: #0c1620; color: #dde8f1; }
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
                  font-size: 14px; font-weight: 600; min-height: 48px; }
    QPushButton#symbol { font-size: 26px; }
    QPushButton#danger { color: #ff9b9b; border-color: #8a3b3b; }
    QPushButton#accent { background: #1f5a44; border-color: #50d7a0; }
    QPushButton#band { min-height: 116px; }
    QLabel#bandName { color: #dde8f1; font-size: 28px; font-weight: 700; }
    QLabel#bandRange { color: #dde8f1; font-size: 15px; }
    QLabel#bandSource { color: #91a7ba; font-size: 12px; }
    QFrame#readout { background: #142838; border: 1px solid #29485c; border-radius: 6px; }
    QLabel#readoutText { color: #dde8f1; font-size: 13px; font-weight: 600; }
    QPushButton:pressed { background: #31556c; }
    QPushButton:checked { background: #1f5a44; border-color: #50d7a0; }
"""


def pi_model():
    try:
        return Path("/proc/device-tree/model").read_text().strip("\0")
    except OSError:
        return ""


# A Pi 4 only just keeps up with 1500 kS/s: a receiver that drops a frame when behind,
# instead of losing stick samples, keeps the beacon decoding there.
SLOW_PI = "Raspberry Pi 4" in pi_model()


def stick_present():
    for device in Path("/sys/bus/usb/devices").iterdir():
        try:
            if ((device / "idVendor").read_text().strip(), (device / "idProduct").read_text().strip()) in RTL_SDR_IDS:
                return True
        except OSError:
            pass
    return False


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
        self.calibration = None
        self.zoom = None   # (start, end) MHz while zoomed into a wide band
        self.full_video = False
        self.stick_busy = False
        self.waiting_for_stick = False
        self.stick_seen = 0
        self.setWindowTitle("HB9IIU · RTL DATV · Raspberry Pi")
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
        self.settings = SettingsPage(CREDITS, version)
        self.pages.addWidget(self.settings)
        self.bands_page = BandsPage(bands.BANDS, CREDITS)
        self.pages.addWidget(self.bands_page)
        self.bands_page.chosen.connect(self.choose_band)
        self.bands_page.quit.connect(self.close)
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
        # LOCAL spectrum: the stick sweeps the band until a signal is tapped.
        self.sweeper = Sweeper(root)
        self.sweep_id = 0
        self.sweeper.frame.connect(self.on_sweep)
        self.sweeper.failed.connect(self.on_sweep_failure)
        self.receiver.status.connect(self.on_status)
        self.receiver.failed.connect(self.on_failure)
        self.video = Video(self.video_surface.winId())
        # VLC ignores the pointer, so taps on the picture reach Qt.
        self.video_surface.installEventFilter(self)
        self.placeholder.installEventFilter(self)
        self.spectrum.clicked.connect(self.select_signal)
        self.spectrum.rescan.connect(self.rescan)
        self.spectrum.zoom_out.connect(self.zoom_out)
        self.controls.mute.connect(self.toggle_mute)
        self.controls.louder.connect(lambda: self.change_volume(10))
        self.controls.quieter.connect(lambda: self.change_volume(-10))
        self.controls.beacon.connect(lambda: self.cancel_calibration() or self.tune(Signal(BEACON_MHZ, 1500)))
        self.controls.stop_start.connect(self.stop_start)
        self.controls.bands.connect(self.open_bands)
        self.controls.settings.connect(self.open_settings)
        self.controls.quit.connect(self.close)
        self.settings.done.connect(self.close_settings)
        self.settings.calibrate.connect(self.start_calibration)
        self.settings.quit.connect(self.close)
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
            self.setWindowTitle("RTL DATV · %s %dx%d" % ((("layout preview" if preview else "live window"),) + window))
            self.setFixedSize(*window)
            self.show()
        else:
            self.showFullScreen()
        screen = self.screen()
        print("DISPLAY: %dx%d, %.2f Hz, scale %.2f%s" % (screen.size().width(), screen.size().height(), screen.refreshRate(),
                                                       ui_scale.scale, " · preview" if preview else ""), flush=True)
        print("PI: %s%s" % (pi_model() or "unknown", " · real-time receiver" if SLOW_PI else ""), flush=True)
        QTimer.singleShot(200, lambda: self.spectrum.set_source(self.effective_source()))
        QTimer.singleShot(500, self.start_default)

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
        # BATC only covers QO-100; elsewhere the stick makes the spectrum.
        return self.spectrum_source if self.band.via_lnb else "local"

    def start_default(self):
        # BATC: straight to the beacon, as before. LOCAL: scan until a signal is tapped.
        if self.effective_source() == "local":
            self.start_scan()
        else:
            self.tune(Signal(BEACON_MHZ, 1500))

    def start_scan(self):
        if not stick_present():
            self.show_problem(NO_STICK, "NO RTL-SDR", True)
            return
        start, end = self.zoom or (self.band.start_mhz, self.band.end_mhz)
        print("SCAN: %s %.3f-%.3f MHz" % (self.band.name, start, end), flush=True)
        self.info.reset(None)
        self.info.show_state("SCANNING", "#8cc4ff")
        self.controls.show_running(False)
        self.show_placeholder("Scanning the band\n\nTap a signal to watch it")
        self.sweep_id = self.sweeper.start((start - self.offset_mhz()) * 1e6, (end - self.offset_mhz()) * 1e6, self.gain_db)

    def on_sweep(self, sweep_id, values):
        if sweep_id == self.sweep_id and self.sweeper.running:
            self.spectrum.set_local(values)

    def on_sweep_failure(self, sweep_id, message):
        if sweep_id != self.sweep_id:
            return
        self.sweeper.stop()
        if not stick_present():
            self.show_problem(NO_STICK, "NO RTL-SDR", True)
        else:
            self.show_problem("Spectrum scan stopped\n\nPress START or BEACON", "ERROR", False)

    def toggle_fullscreen(self):
        if self.isFullScreen():
            self.showNormal()
            self.resize(900, 570)
        else:
            self.showFullScreen()

    def eventFilter(self, watched, event):
        if event.type() == QEvent.MouseButtonPress and watched in (self.video_surface, self.placeholder):
            if self.current or self.full_video:
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
        if self.band.end_mhz - self.band.start_mhz > bands.ZOOM_ABOVE_MHZ and not self.zoom:
            self.zoom_to(mhz)   # wide band: the first tap zooms, the second tunes
            return
        signal = self.spectrum.signal_at(mhz)
        if signal is None:
            self.spectrum.show_message("No signal at %.3f MHz" % mhz)
        elif signal.symbol_rate_ks > 1500:
            self.spectrum.show_message("This signal is too wide for the RTL-SDR setup")
        else:
            self.tune(signal)

    def show_placeholder(self, text, color=DIM):
        self.placeholder.setText(text)
        self.placeholder.setStyleSheet("background: black; color: %s; font-size: %dpx; font-weight: 600;" % (color, px(30)))
        self.video_stack.setCurrentWidget(self.placeholder)

    def show_problem(self, message, state, wait_for_stick):
        print("PROBLEM: " + message.splitlines()[0], flush=True)
        self.cancel_calibration()
        self.sweeper.stop()
        self.stop(scan=False)
        self.waiting_for_stick = wait_for_stick
        self.stick_seen = 0
        self.info.show_state(state, RED)
        self.show_placeholder(message, "#dde8f1")

    def tune(self, signal):
        print("TUNE: %.6f MHz, %d kS/s" % (signal.center_mhz, signal.symbol_rate_ks), flush=True)
        self.tuned_at = time.monotonic()
        self.last_signal = signal
        if not stick_present():
            self.show_problem(NO_STICK, "NO RTL-SDR", True)
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
        self.controls.show_running(True)
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
        self.receiver.stop()
        stopped = time.monotonic()
        self.video.stop()
        timing = (stopped - stopping, time.monotonic() - stopped)
        self.show_placeholder("Waiting for video keyframe…")
        self.picture_timer.start(100)
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            self.video.play(port, self.output_volume())
            self.session_id = self.receiver.tune(signal, self.offset_mhz(), port, self.gain_db)
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
        if not self.current:
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
        if not stick_present():
            self.show_problem(NO_STICK, "NO RTL-SDR", True)
        elif self.stick_busy:
            self.show_problem(STICK_BUSY, "STICK BUSY", False)
        else:
            self.stop()
            if self.effective_source() == "batc":
                self.show_placeholder("Reception ended\n\nPress START or tap a signal")

    def log_video(self):
        # Every 5 s while watching: what VLC decodes, shows and drops (diagnosis on slower Pis).
        stats = self.video.stats() if self.current else None
        if stats and int(time.monotonic()) % 5 == 0:
            print("VIDEO: decoded %d, displayed %d, lost %d" % stats, flush=True)

    def tick(self):
        self.log_video()
        if self.waiting_for_stick:
            # Give a replugged stick two seconds to settle before opening it.
            self.stick_seen = self.stick_seen + 1 if stick_present() else 0
            if self.stick_seen >= 2:
                self.waiting_for_stick = False
                self.start_scan() if self.effective_source() == "local" else self.tune(self.last_signal)
            return
        if not self.current:
            return
        if not stick_present():
            self.show_problem(NO_STICK, "NO RTL-SDR", True)
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
        self.info.constellation.update()
        if time.monotonic() - self.last_report > 5:
            self.info.show_state("WAITING", AMBER)
            self.info.eye.show_quality(True, False, None, None)
            self.mark_lock(False)
        elif self.locked_at is None:
            self.mark_lock(False)   # turns the slot red after ten seconds without a lock
        self.check_signal()

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
        self.stop()   # in LOCAL mode, stopping returns to scanning

    def stop_start(self):
        self.cancel_calibration()
        if self.current:
            self.stop()
        elif self.last_signal:
            self.tune(self.last_signal)

    def stop(self, scan=True):
        self.session_id = -1
        self.receiver.stop()
        self.video.stop()
        self.current = None
        self.waiting_for_stick = False
        if self.full_video:
            self.toggle_full_video()
        self.picture_timer.stop()
        self.show_placeholder("Stopped\n\nPress START or tap a signal")
        self.spectrum.set_slot(None, "searching")
        self.info.reset(None)
        self.info.show_state("STOPPED", DIM)
        self.controls.show_running(False)
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
        self.settings.load(self.lnb_mhz, self.gain_db, self.spectrum_source, self.band, self.lo_mhz())
        self.pages.setCurrentWidget(self.settings)

    def close_settings(self):
        self.pages.setCurrentIndex(0)
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
        if changed:
            self.save_settings()
            if self.current:
                self.tune(self.current)
            elif self.sweeper.running:
                self.start_scan()

    def zoom_to(self, mhz):
        start = max(self.band.start_mhz, min(mhz - bands.ZOOM_MHZ / 2, self.band.end_mhz - bands.ZOOM_MHZ))
        self.zoom = (start, start + bands.ZOOM_MHZ)
        print("ZOOM: %.3f-%.3f MHz" % self.zoom, flush=True)
        self.spectrum.set_view(*self.zoom, True)
        self.stop() if self.current else self.start_scan()   # stopping returns to scanning, now zoomed

    def zoom_out(self):
        print("ZOOM: out", flush=True)
        self.zoom = None
        self.spectrum.set_view(self.band.start_mhz, self.band.end_mhz, False)
        self.stop() if self.current else self.start_scan()

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
                                                      "indicator": self.indicator, "spectrum": self.spectrum_source}, indent=2))
        except OSError:
            pass

    def closeEvent(self, event):
        self.timer.stop()
        self.spectrum.stop()
        self.sweeper.stop()
        self.receiver.stop()
        self.video.close()
        self.save_settings()
        event.accept()


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
        globals()["stick_present"] = lambda: True
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
            QMessageBox.information(None, "RTL DATV", "The Pi player is already running.")
            return 1
    player = Player(root, preview, window)
    # Stopped from outside (an update, a shutdown): close like QUIT, which stops the receiver.
    signal.signal(signal.SIGTERM, lambda *args: player.close())
    signal.signal(signal.SIGINT, lambda *args: player.close())
    return app.exec_()


if __name__ == "__main__":
    sys.exit(main())
