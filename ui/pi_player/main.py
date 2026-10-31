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

from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtGui import QFont, QKeySequence
from PyQt5.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
                             QLabel, QPushButton, QMenu, QShortcut, QSlider, QWidgetAction,
                             QInputDialog, QMessageBox, QFrame, QStackedLayout)
from signal_finder import BEACON_MHZ, Signal, find_signal
from spectrum import Spectrum
from receiver import Receiver
from video import Video


class Player(QMainWindow):
    def __init__(self, root):
        super().__init__()
        self.root = root
        self.settings_path = root / "pi-player-settings.json"
        self.volume = 40
        self.lnb_mhz = 9750
        try:
            settings = json.loads(self.settings_path.read_text())
            self.volume = max(0, min(100, int(settings.get("volume", 40))))
            self.lnb_mhz = max(9000, min(11000, float(settings.get("lnb_mhz", 9750))))
        except (OSError, ValueError, TypeError):
            pass
        self.current = None
        self.session_id = 0
        self.last_report = 0
        self.setWindowTitle("HB9IIU · RTL DATV · Raspberry Pi")
        self.setStyleSheet("""
            QMainWindow, QWidget#surface { background: #0c1620; color: #dde8f1; }
            QLabel { color: #91a7ba; background: transparent; }
            QLabel#heading { color: #dde8f1; font-size: 16px; font-weight: 600; }
            QPushButton { background: #1b3140; color: #dde8f1; border: 1px solid #29485c; border-radius: 5px; font-size: 22px; }
            QPushButton:pressed { background: #31556c; }
            QMenu { background: #142534; color: #dde8f1; border: 1px solid #385064; font-size: 14px; }
            QMenu::item { padding: 10px 24px; }
            QMenu::item:selected { background: #29485c; }
        """)
        surface = QWidget(objectName="surface")
        self.setCentralWidget(surface)
        layout = QVBoxLayout(surface)
        layout.setContentsMargins(12, 8, 12, 8)
        layout.setSpacing(6)
        header = QHBoxLayout()
        header.addWidget(QLabel("HB9IIU  /  RTL DATV · PI", objectName="heading"))
        header.addStretch()
        self.state = QLabel("STARTING")
        self.state.setStyleSheet("color: #ffbd45; font-weight: 600;")
        header.addWidget(self.state)
        self.menu_button = QPushButton("⋮")
        self.menu_button.setAccessibleName("Player menu")
        self.menu_button.setFixedSize(44, 34)
        header.addWidget(self.menu_button)
        layout.addLayout(header)
        video_container = QWidget()
        self.video_stack = QStackedLayout(video_container)
        self.video_stack.setContentsMargins(0, 0, 0, 0)
        self.placeholder = QLabel("Waiting for beacon video…")
        self.placeholder.setAlignment(Qt.AlignCenter)
        self.placeholder.setStyleSheet("background: black; color: #91a7ba;")
        self.video_stack.addWidget(self.placeholder)
        self.video_surface = QFrame()
        self.video_surface.setAttribute(Qt.WA_NativeWindow)
        self.video_surface.setStyleSheet("background: black;")
        self.video_surface.setMinimumHeight(180)
        self.video_stack.addWidget(self.video_surface)
        layout.addWidget(video_container, 1)
        self.status = QLabel("Starting beacon · waiting for a video keyframe…")
        self.status.setMinimumHeight(24)
        layout.addWidget(self.status)
        self.spectrum = Spectrum()
        self.spectrum.setFixedHeight(122)
        layout.addWidget(self.spectrum)
        self.receiver = Receiver(root)
        self.receiver.status.connect(self.on_status)
        self.receiver.failed.connect(self.on_failure)
        self.video = Video(self.video_surface.winId())
        self.menu = self.make_menu()
        self.menu_button.clicked.connect(lambda: self.menu.popup(self.menu_button.mapToGlobal(self.menu_button.rect().bottomRight())))
        surface.setContextMenuPolicy(Qt.CustomContextMenu)
        surface.customContextMenuRequested.connect(lambda point: self.menu.popup(surface.mapToGlobal(point)))
        self.spectrum.clicked.connect(self.select_signal)
        QShortcut(QKeySequence("F11"), self, activated=self.toggle_fullscreen)
        QShortcut(QKeySequence("Escape"), self, activated=self.showNormal)
        QShortcut(QKeySequence("Q"), self, activated=self.close)
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.tick)
        self.timer.start(1000)
        self.showFullScreen()
        screen = self.screen()
        print("DISPLAY: %dx%d, %.2f Hz, fullscreen" % (screen.size().width(), screen.size().height(), screen.refreshRate()), flush=True)
        QTimer.singleShot(200, self.spectrum.connect_feed)
        QTimer.singleShot(500, lambda: self.tune(Signal(BEACON_MHZ, 1500)))

    def make_menu(self):
        menu = QMenu(self)
        fullscreen = menu.addAction("Fullscreen  ·  F11")
        fullscreen.triggered.connect(self.toggle_fullscreen)
        menu.addAction("Beacon", lambda: self.tune(Signal(BEACON_MHZ, 1500)))
        menu.addAction("Stop reception", self.stop)
        volume_menu = menu.addMenu("Volume")
        slider = QSlider(Qt.Horizontal)
        slider.setRange(0, 100)
        slider.setValue(self.volume)
        slider.setMinimumSize(220, 52)
        slider.valueChanged.connect(self.set_volume)
        volume_action = QWidgetAction(volume_menu)
        volume_action.setDefaultWidget(slider)
        volume_menu.addAction(volume_action)
        volume_menu.addAction("Mute", lambda: slider.setValue(0))
        menu.addAction("LNB configuration…", self.configure_lnb)
        menu.addSeparator()
        menu.addAction("About / credits", lambda: QMessageBox.information(self, "RTL DATV",
            "HB9IIU RTL DATV Player\n\nReceiver: OnAir-derived C++/Rust datvrx.\n"
            "Spectrum detection adapted from HB9IIU Experimental H.266 Player.\n"
            "Spectrum: BATC remote reference. Quality: local RTL-SDR.\n"
            "Video: system libVLC. Interface: PyQt5.\n\nGPL v3; source and notices in the project."))
        menu.addAction("Quit to desktop", self.close)
        return menu

    def toggle_fullscreen(self):
        if self.isFullScreen():
            self.showNormal()
            self.resize(900, 570)
        else:
            self.showFullScreen()

    def select_signal(self, mhz):
        signal = find_signal(self.spectrum.frame, mhz)
        if signal is None:
            self.spectrum.show_message("No signal at %.3f MHz" % mhz)
        elif signal.symbol_rate_ks > 1500:
            self.spectrum.show_message("This signal is too wide for the RTL-SDR setup")
        else:
            self.tune(signal)

    def tune(self, signal):
        print("TUNE: %.6f MHz, %d kS/s" % (signal.center_mhz, signal.symbol_rate_ks), flush=True)
        self.receiver.stop()
        self.video.stop()
        self.current = signal
        self.last_report = time.monotonic()
        self.state.setText("TUNING")
        self.placeholder.setText("Waiting for video keyframe…")
        self.video_stack.setCurrentWidget(self.placeholder)
        self.state.setStyleSheet("color: #ffbd45;")
        self.status.setText("%.3f MHz · %d kS/s · waiting for video keyframe…" % (signal.center_mhz, signal.symbol_rate_ks))
        self.spectrum.selected = signal
        self.spectrum.update()
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            self.video.play(port, self.volume)
            self.session_id = self.receiver.tune(signal, self.lnb_mhz, port)
        except Exception as error:
            self.stop()
            self.state.setText("ERROR")
            self.status.setText(str(error))

    def on_status(self, session_id, line):
        if session_id != self.session_id or self.current is None:
            return
        if line.startswith("datvrx ["):
            self.last_report = time.monotonic()
            mode = re.search(r"(DVB-S\S*) (.*?)\s+[\d.]+ Msym/s", line)
            quality = re.search(r"Es/N0 ([\d.-]+) dB", line)
            errors = re.search(r"bad (\d+)", line)
            parts = ["%.3f MHz" % self.current.center_mhz, "%d kS/s" % self.current.symbol_rate_ks]
            parts.append(" ".join(mode.groups()) if mode else "Searching")
            if quality:
                parts.append("Es/N0 %s dB" % quality[1])
            if errors:
                parts.append("%s bad" % errors[1])
            if not self.video.has_picture():
                parts.append("waiting for video")
            self.status.setText(" · ".join(parts))
            locked = "TS lock" in line
            self.state.setText("LOCKED" if locked else "SEARCHING")
            self.state.setStyleSheet("color: #50d7a0;" if locked else "color: #ffbd45;")
        elif "synchronisation lost" in line:
            self.state.setText("REACQUIRING")
        elif "error" in line.lower() or "failed" in line.lower():
            self.status.setText(line)

    def on_failure(self, session_id, message):
        if session_id == self.session_id:
            self.stop()
            self.status.setText(message)

    def tick(self):
        if self.current and self.video.has_picture():
            self.video_stack.setCurrentWidget(self.video_surface)
        if self.current and time.monotonic() - self.last_report > 5:
            self.state.setText("WAITING")
            self.state.setStyleSheet("color: #ffbd45;")

    def stop(self):
        self.session_id = -1
        self.receiver.stop()
        self.video.stop()
        self.current = None
        self.placeholder.setText("Stopped · tap a signal to resume")
        self.video_stack.setCurrentWidget(self.placeholder)
        self.spectrum.selected = None
        self.spectrum.update()
        self.state.setText("STOPPED")
        self.state.setStyleSheet("color: #91a7ba;")
        self.status.setText("Stopped · tap a signal to resume")

    def set_volume(self, value):
        self.volume = value
        self.video.volume(value)
        self.save_settings()

    def configure_lnb(self):
        value, accepted = QInputDialog.getDouble(self, "LNB configuration", "Oscillator (MHz)", self.lnb_mhz, 9000, 11000, 3)
        if accepted:
            self.lnb_mhz = value
            self.save_settings()
            if self.current:
                self.tune(self.current)

    def save_settings(self):
        try:
            self.settings_path.write_text(json.dumps({"volume": self.volume, "lnb_mhz": self.lnb_mhz}, indent=2))
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
