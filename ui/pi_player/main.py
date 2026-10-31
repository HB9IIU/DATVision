#!/usr/bin/env python3
"""Fullscreen touchscreen DATV player for Raspberry Pi. GPL-3.0-or-later."""
import ctypes
from ctypes.util import find_library
import fcntl
import json
from pathlib import Path
import re
import socket
import sys
import time
import traceback

from PyQt5.QtCore import Qt, QTimer, QEvent
from PyQt5.QtGui import QFont, QKeySequence
from PyQt5.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QLabel,
                             QShortcut, QMessageBox, QFrame, QStackedLayout, QStackedWidget)
import calibration
from signal_finder import BEACON_MHZ, Signal, find_signal, nearest_signal
from spectrum import Spectrum
from receiver import Receiver
from video import Video
from panels import GREEN, AMBER, RED, DIM, REQUIRED_ESN0, InfoPanel, ControlPanel, SettingsPage

REPORT = re.compile(r"(DVB-S2X|DVB-S2|DVB-S) (\S+) (\d+/\d+)\S*(?: short)?(?: pilots)?\s+[\d.]+ Msym/s"
                    r"\s+Es/N0 ([\d.-]+) dB\s+ok \d+\s+bad (\d+)")
RTL_SDR_IDS = {("0bda", "2838"), ("0bda", "2832")}
NO_STICK = "RTL-SDR stick not found\n\nUnplug it and plug it back in.\nReception resumes by itself."
STICK_BUSY = "RTL-SDR stick is busy\n\nClose the other receiver program,\nthen press START."
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
    QLabel#title { color: #dde8f1; font-size: 24px; font-weight: 600; }
    QPushButton { background: #1b3140; color: #dde8f1; border: 1px solid #29485c; border-radius: 6px;
                  font-size: 14px; font-weight: 600; min-height: 48px; }
    QPushButton#symbol { font-size: 26px; }
    QPushButton#danger { color: #ff9b9b; border-color: #8a3b3b; }
    QPushButton:pressed { background: #31556c; }
"""


def stick_present():
    for device in Path("/sys/bus/usb/devices").iterdir():
        try:
            if ((device / "idVendor").read_text().strip(), (device / "idProduct").read_text().strip()) in RTL_SDR_IDS:
                return True
        except OSError:
            pass
    return False


class Player(QMainWindow):
    def __init__(self, root):
        super().__init__()
        self.root = root
        self.settings_path = root / "pi-player-settings.json"
        self.volume = 40
        self.muted = False
        self.lnb_mhz = 9750
        self.gain_db = 40.2
        self.indicator = "eye"
        try:
            settings = json.loads(self.settings_path.read_text())
            self.volume = max(0, min(100, int(settings.get("volume", 40))))
            self.muted = bool(settings.get("muted", False))
            self.lnb_mhz = max(9000, min(11000, float(settings.get("lnb_mhz", 9750))))
            self.gain_db = max(0, min(49.6, float(settings.get("gain_db", 40.2))))
            self.indicator = "constellation" if settings.get("indicator") == "constellation" else "eye"
        except (OSError, ValueError, TypeError):
            pass
        self.current = None
        self.last_signal = Signal(BEACON_MHZ, 1500)
        self.session_id = 0
        self.last_report = 0
        self.lost = 0
        self.modulation = ""
        self.tuned_at = 0
        self.locked_at = None
        self.calibration = None
        self.full_video = False
        self.stick_busy = False
        self.waiting_for_stick = False
        self.stick_seen = 0
        self.setWindowTitle("HB9IIU · RTL DATV · Raspberry Pi")
        self.setStyleSheet(STYLE)
        self.pages = QStackedWidget()
        self.setCentralWidget(self.pages)
        surface = QWidget(objectName="surface")
        self.pages.addWidget(surface)
        self.surface_layout = QVBoxLayout(surface)
        self.surface_layout.setContentsMargins(6, 6, 6, 6)
        self.surface_layout.setSpacing(6)
        top = QHBoxLayout()
        top.setSpacing(6)
        self.info = InfoPanel()
        self.info.setFixedWidth(148)
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
        self.video_surface.setMinimumHeight(180)
        self.video_stack.addWidget(self.video_surface)
        top.addWidget(video_container, 1)
        self.controls = ControlPanel()
        self.controls.setFixedWidth(118)
        top.addWidget(self.controls)
        self.surface_layout.addLayout(top, 1)
        self.spectrum = Spectrum()
        self.spectrum.setFixedHeight(140)
        self.surface_layout.addWidget(self.spectrum)
        self.settings = SettingsPage(CREDITS)
        self.pages.addWidget(self.settings)

        self.receiver = Receiver(root)
        self.receiver.status.connect(self.on_status)
        self.receiver.failed.connect(self.on_failure)
        self.video = Video(self.video_surface.winId())
        # VLC ignores the pointer, so taps on the picture reach Qt.
        self.video_surface.installEventFilter(self)
        self.placeholder.installEventFilter(self)
        self.spectrum.clicked.connect(self.select_signal)
        self.controls.mute.connect(self.toggle_mute)
        self.controls.louder.connect(lambda: self.change_volume(10))
        self.controls.quieter.connect(lambda: self.change_volume(-10))
        self.controls.beacon.connect(lambda: self.cancel_calibration() or self.tune(Signal(BEACON_MHZ, 1500)))
        self.controls.stop_start.connect(self.stop_start)
        self.controls.full_video.connect(self.toggle_full_video)
        self.controls.settings.connect(self.open_settings)
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
        self.showFullScreen()
        screen = self.screen()
        print("DISPLAY: %dx%d, %.2f Hz, fullscreen" % (screen.size().width(), screen.size().height(), screen.refreshRate()), flush=True)
        QTimer.singleShot(200, self.spectrum.connect_feed)
        QTimer.singleShot(500, lambda: self.tune(Signal(BEACON_MHZ, 1500)))

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
        signal = find_signal(self.spectrum.frame, mhz) or nearest_signal(self.spectrum.signals, mhz)
        if signal is None:
            self.spectrum.show_message("No signal at %.3f MHz" % mhz)
        elif signal.symbol_rate_ks > 1500:
            self.spectrum.show_message("This signal is too wide for the RTL-SDR setup")
        else:
            self.tune(signal)

    def show_placeholder(self, text, color=DIM):
        self.placeholder.setText(text)
        self.placeholder.setStyleSheet("background: black; color: %s; font-size: 30px; font-weight: 600;" % color)
        self.video_stack.setCurrentWidget(self.placeholder)

    def show_problem(self, message, state, wait_for_stick):
        print("PROBLEM: " + message.splitlines()[0], flush=True)
        self.cancel_calibration()
        self.stop()
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
        self.lost = 0
        self.modulation = ""
        self.info.reset(signal)
        self.info.show_state("TUNING", AMBER)
        self.controls.show_running(True)
        self.show_placeholder("Tuning %.3f MHz…" % signal.center_mhz)
        self.spectrum.set_slot(signal, "searching", now=True)
        self.info.repaint()
        self.placeholder.repaint()
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
            self.session_id = self.receiver.tune(signal, self.lnb_mhz, port, self.gain_db)
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
        # Red once a lock is lost, or when none has come within ten seconds.
        lost = self.locked_at is not None or time.monotonic() - self.tuned_at > 10
        self.spectrum.set_slot(self.current, "locked" if locked else "lost" if lost else "searching")

    def check_picture(self):
        if self.calibration:
            return   # the progress stays on screen until calibration ends
        if not self.current:
            self.picture_timer.stop()
        elif self.video.has_picture():
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
            self.show_placeholder("Reception ended\n\nPress START or tap a signal")

    def tick(self):
        if self.waiting_for_stick:
            # Give a replugged stick two seconds to settle before opening it.
            self.stick_seen = self.stick_seen + 1 if stick_present() else 0
            if self.stick_seen >= 2:
                self.tune(self.last_signal)
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

    def stop_start(self):
        self.cancel_calibration()
        if self.current:
            self.stop()
        else:
            self.tune(self.last_signal)

    def stop(self):
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
        self.settings.load(self.lnb_mhz, self.gain_db)
        self.pages.setCurrentWidget(self.settings)

    def close_settings(self):
        self.pages.setCurrentIndex(0)
        changed = (self.settings.lnb_mhz, self.settings.gain_db) != (self.lnb_mhz, self.gain_db)
        self.lnb_mhz, self.gain_db = self.settings.lnb_mhz, self.settings.gain_db
        if changed:
            self.save_settings()
            if self.current:
                self.tune(self.current)

    def set_indicator(self, name):
        self.indicator = name
        self.save_settings()

    def save_settings(self):
        try:
            self.settings_path.write_text(json.dumps({"volume": self.volume, "muted": self.muted,
                                                      "lnb_mhz": self.lnb_mhz, "gain_db": self.gain_db,
                                                      "indicator": self.indicator}, indent=2))
        except OSError:
            pass

    def closeEvent(self, event):
        self.timer.stop()
        self.spectrum.stop()
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
    app.setFont(QFont("DejaVu Sans", 9))
    root = Path(__file__).resolve().parents[2]
    (root / "test-output").mkdir(exist_ok=True)
    lock = (root / "test-output/pi-player.lock").open("w")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        QMessageBox.information(None, "RTL DATV", "The Pi player is already running.")
        return 1
    player = Player(root)
    return app.exec_()


if __name__ == "__main__":
    sys.exit(main())
