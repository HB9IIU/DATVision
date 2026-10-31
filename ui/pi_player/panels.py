"""Touch panels beside the video and the full-screen settings page. GPL-3.0-or-later."""
import math
import time
from PyQt5.QtCore import Qt, pyqtSignal, QPointF
from PyQt5.QtGui import QColor, QPainter
from PyQt5.QtWidgets import QWidget, QFrame, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QSizePolicy, QStackedWidget
from magic_eye import MagicEye
import calibration

GREEN, AMBER, RED, DIM = "#50d7a0", "#ffbd45", "#ff6b6b", "#91a7ba"
# Tuner gain steps of the R828D in the RTL-SDR Blog V4, dB.
GAINS = (0.0, 0.9, 1.4, 2.7, 3.7, 7.7, 8.7, 12.5, 14.4, 15.7, 16.6, 19.7, 20.7, 22.9, 25.4, 28.0, 29.7,
         32.8, 33.8, 36.4, 37.2, 38.6, 40.2, 42.1, 43.4, 43.9, 44.5, 48.0, 49.6)


def _table(standard, modulation, values):
    return {(standard, modulation, rate): db for rate, db in values}


# Approximate quasi-error-free Es/N0 (dB, AWGN): DVB-S2 from EN 302 307 table 13,
# DVB-S from the EN 300 421 Eb/N0 figures converted to Es/N0.
REQUIRED_ESN0 = {
    **_table("DVB-S", "QPSK", (("1/2", 4.1), ("2/3", 5.9), ("3/4", 6.9), ("5/6", 7.9), ("7/8", 8.5))),
    **_table("DVB-S2", "QPSK", (("1/4", -2.35), ("1/3", -1.24), ("2/5", -0.30), ("1/2", 1.00), ("3/5", 2.23),
                                ("2/3", 3.10), ("3/4", 4.03), ("4/5", 4.68), ("5/6", 5.18), ("8/9", 6.20), ("9/10", 6.42))),
    **_table("DVB-S2", "8PSK", (("3/5", 5.50), ("2/3", 6.62), ("3/4", 7.91), ("5/6", 9.35), ("8/9", 10.69), ("9/10", 10.98))),
    **_table("DVB-S2", "16APSK", (("2/3", 8.97), ("3/4", 10.21), ("4/5", 11.03), ("5/6", 11.61), ("8/9", 12.89), ("9/10", 13.13))),
    **_table("DVB-S2", "32APSK", (("3/4", 12.73), ("4/5", 13.64), ("5/6", 14.28), ("8/9", 15.69), ("9/10", 16.05))),
}


def label(layout, role, text=""):
    widget = QLabel(text, objectName=role)
    layout.addWidget(widget)
    return widget


def button(layout, text, signal, role=""):
    widget = QPushButton(text, objectName=role)
    widget.setFocusPolicy(Qt.NoFocus)
    widget.clicked.connect(lambda: signal.emit())
    layout.addWidget(widget)
    return widget


class QualityBar(QWidget):
    """Es/N0 from -3 to 17 dB; the light tick marks what the current mode needs."""
    def __init__(self):
        super().__init__()
        self.value = self.required = None
        self.setFixedHeight(10)

    def set_value(self, value, required):
        self.value, self.required = value, required
        self.update()

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor("#1b3140"))
        if self.value is None:
            return
        scale = lambda db: round(max(0, min(1, (db + 3) / 20)) * self.width())
        margin = None if self.required is None else self.value - self.required
        color = GREEN if margin is None or margin >= 2 else AMBER if margin >= 0 else RED
        painter.fillRect(0, 0, scale(self.value), self.height(), QColor(color))
        if self.required is not None:
            painter.fillRect(scale(self.required) - 1, 0, 2, self.height(), QColor("#dde8f1"))


class Constellation(QWidget):
    """Recent received symbols after carrier recovery, unit mean power, ±1.5 full scale."""
    tapped = pyqtSignal()

    def __init__(self):
        super().__init__()
        self.points = []
        self.modulation = ""
        self.received = 0
        self.setFixedSize(120, 120)

    def set_points(self, hex_text, modulation):
        raw = bytes.fromhex(hex_text)
        self.points = [((raw[i] - 128) / 64, (raw[i + 1] - 128) / 64) for i in range(0, len(raw) - 1, 2)]
        self.modulation = modulation
        self.received = time.monotonic()
        self.update()

    def clear(self):
        self.points = []
        self.update()

    def mousePressEvent(self, event):
        self.tapped.emit()

    def paintEvent(self, event):
        painter = QPainter(self)
        size = self.width()
        painter.fillRect(self.rect(), QColor("#05141d"))
        painter.fillRect(size // 2, 0, 1, size, QColor("#1d3647"))
        painter.fillRect(0, size // 2, size, 1, QColor("#1d3647"))
        at = lambda i, q: QPointF(size / 2 + i * size / 3, size / 2 - q * size / 3)
        painter.setRenderHint(QPainter.Antialiasing)
        steps = {"QPSK": (4, math.pi / 4), "8PSK": (8, 0)}.get(self.modulation)
        if steps:
            # Where the symbols belong, as faint rings.
            painter.setPen(QColor("#5d7488"))
            for k in range(steps[0]):
                angle = steps[1] + 2 * math.pi * k / steps[0]
                painter.drawEllipse(at(math.cos(angle), math.sin(angle)), 4, 4)
        if self.points and time.monotonic() - self.received < 1.5:
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor(80, 215, 160, 170))
            for i, q in self.points:
                painter.drawEllipse(at(i, q), 1.3, 1.3)


class InfoPanel(QFrame):
    indicator_changed = pyqtSignal(str)

    def __init__(self):
        super().__init__(objectName="panel")
        layout = QVBoxLayout(self)
        layout.setContentsMargins(10, 10, 10, 10)
        layout.setSpacing(2)
        self.state = label(layout, "state", "STARTING")
        self.service = label(layout, "service")
        self.service.setWordWrap(True)
        self.provider = label(layout, "caption")
        layout.addSpacing(4)
        self.frequency = label(layout, "value")
        self.rate = label(layout, "value")
        self.mode = label(layout, "value")
        layout.addSpacing(4)
        label(layout, "caption", "Es/N0")
        self.esn0 = label(layout, "big", "—")
        self.bar = QualityBar()
        layout.addWidget(self.bar)
        self.margin = label(layout, "caption")
        layout.addSpacing(4)
        self.video = label(layout, "value")
        self.audio = label(layout, "value")
        self.errors = label(layout, "caption")
        layout.addStretch()
        self.note = label(layout, "caption")
        self.note.setWordWrap(True)
        # Tap to alternate between the magic eye and the constellation.
        self.eye = MagicEye()
        self.constellation = Constellation()
        self.indicator = QStackedWidget()
        self.indicator.setFixedSize(120, 120)
        for widget in (self.eye, self.constellation):
            self.indicator.addWidget(widget)
            widget.tapped.connect(self.toggle_indicator)
        layout.addWidget(self.indicator, 0, Qt.AlignHCenter)

    def show_indicator(self, name):
        self.indicator.setCurrentWidget(self.constellation if name == "constellation" else self.eye)

    def toggle_indicator(self):
        name = "eye" if self.indicator.currentWidget() is self.constellation else "constellation"
        self.show_indicator(name)
        self.indicator_changed.emit(name)

    def show_state(self, text, color):
        self.state.setText(text)
        self.state.setStyleSheet("color: %s;" % color)

    def reset(self, signal):
        self.frequency.setText("%.3f MHz" % signal.center_mhz if signal else "")
        self.rate.setText("%d kS/s" % signal.symbol_rate_ks if signal else "")
        self.show_reception("Searching…" if signal else "", None, None, None, 0)
        self.show_streams("", "", "", "")
        self.note.setText("")
        self.constellation.clear()
        self.eye.show_quality(signal is not None, False, None, None)

    def show_reception(self, mode, esn0, required, bad, lost):
        self.mode.setText(mode)
        self.esn0.setText("—" if esn0 is None else "%.1f dB" % esn0)
        self.bar.set_value(esn0, required)
        self.margin.setText("" if esn0 is None or required is None else "margin %+.1f dB" % (esn0 - required))
        self.errors.setText("" if bad is None else "bad %d · lost %d" % (bad, lost))

    def show_streams(self, service, provider, video, audio):
        self.service.setText(service)
        self.provider.setText(provider)
        self.video.setText(video)
        self.audio.setText(audio)

    def show_note(self, text):
        self.note.setText(text)


class ControlPanel(QFrame):
    mute = pyqtSignal()
    louder = pyqtSignal()
    quieter = pyqtSignal()
    beacon = pyqtSignal()
    stop_start = pyqtSignal()
    full_video = pyqtSignal()
    settings = pyqtSignal()

    def __init__(self):
        super().__init__(objectName="panel")
        layout = QVBoxLayout(self)
        layout.setContentsMargins(8, 8, 8, 8)
        layout.setSpacing(8)
        self.sound = button(layout, "", self.mute)
        row = QHBoxLayout()
        row.setSpacing(8)
        button(row, "−", self.quieter, "symbol")
        button(row, "+", self.louder, "symbol")
        layout.addLayout(row)
        button(layout, "BEACON", self.beacon)
        self.run = button(layout, "STOP", self.stop_start)
        button(layout, "FULL\nVIDEO", self.full_video)
        button(layout, "SETTINGS", self.settings)
        for widget in self.findChildren(QPushButton):
            widget.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)

    def show_audio(self, volume, muted):
        self.sound.setText("MUTED" if muted else "SOUND\n%d %%" % volume)
        self.sound.setStyleSheet("color: %s;" % RED if muted else "")

    def show_running(self, running):
        self.run.setText("STOP" if running else "START")


class SettingsPage(QWidget):
    done = pyqtSignal()
    quit = pyqtSignal()
    calibrate = pyqtSignal()

    def __init__(self, credits):
        super().__init__(objectName="settings")
        self.lnb_mhz = 9750.0
        self.gain_db = 40.2
        layout = QVBoxLayout(self)
        layout.setContentsMargins(24, 16, 24, 16)
        layout.setSpacing(10)
        top = QHBoxLayout()
        label(top, "title", "Settings")
        top.addStretch()
        button(top, "DONE", self.done).setFixedSize(150, 60)
        layout.addLayout(top)

        # The LNB is measured on the beacon rather than entered by hand.
        label(layout, "caption", "LNB OSCILLATOR")
        row = QHBoxLayout()
        row.setSpacing(12)
        self.lnb = label(row, "big")
        self.lnb.setFixedWidth(230)
        self.calibrate_button = button(row, "", self.calibrate)
        self.calibrate_button.setFixedSize(200, 60)
        self.undo = QPushButton("UNDO")
        self.undo.setFocusPolicy(Qt.NoFocus)
        self.undo.setFixedSize(110, 60)
        self.undo.clicked.connect(self.undo_calibration)
        self.undo.hide()
        row.addWidget(self.undo)
        row.addStretch()
        layout.addLayout(row)
        self.calibration_note = label(layout, "value")
        self.calibration_note.setWordWrap(True)
        layout.addSpacing(10)

        label(layout, "caption", "RF GAIN")
        row = QHBoxLayout()
        row.setSpacing(8)
        for text, step in (("−", -1), ("+", 1)):
            widget = QPushButton(text, objectName="symbol")
            widget.setFocusPolicy(Qt.NoFocus)
            widget.setFixedSize(110, 60)
            widget.clicked.connect(lambda _, step=step: self.step_gain(step))
            row.addWidget(widget)
            if step < 0:
                self.gain = label(row, "big")
                self.gain.setAlignment(Qt.AlignCenter)
                self.gain.setFixedWidth(160)
        row.addStretch()
        layout.addLayout(row)
        label(layout, "caption", "Changes are applied when you press DONE.")
        layout.addStretch()

        bottom = QHBoxLayout()
        about = QLabel(credits, objectName="caption")
        about.setWordWrap(True)
        bottom.addWidget(about, 1)
        quit_button = button(bottom, "QUIT TO\nDESKTOP", self.quit, "danger")
        quit_button.setFixedSize(170, 64)
        layout.addLayout(bottom)

    def load(self, lnb_mhz, gain_db):
        self.calibration_note.setText("")
        self.undo.hide()
        self.set_lnb(lnb_mhz)
        self.gain_db = min(GAINS, key=lambda value: abs(value - gain_db))
        self.gain.setText("%.1f dB" % self.gain_db)

    def show_calibration(self, result, previous):
        self.previous = previous
        if result is None:
            self.calibration_note.setText("Calibration failed: the beacon did not lock often enough. LNB unchanged.")
            return
        value, spread = result
        if spread > calibration.MAX_SPREAD_KHZ:
            self.calibration_note.setText("Not reliable: the steps disagree by %.1f kHz. LNB unchanged." % spread)
            return
        self.set_lnb(value)
        self.calibration_note.setText("Measured %.3f MHz (was %.3f); steps agree within %.1f kHz.\nDONE keeps it."
                                      % (self.lnb_mhz, previous, spread))
        self.undo.show()

    def undo_calibration(self):
        self.set_lnb(self.previous)
        self.calibration_note.setText("Restored %.3f MHz." % self.previous)
        self.undo.hide()

    def set_lnb(self, value):
        self.lnb_mhz = round(max(9000, min(11000, value)), 3)
        self.lnb.setText("%.3f MHz" % self.lnb_mhz)
        self.calibrate_button.setText("CALIBRATE\nON BEACON" if self.lnb_mhz == 9750 else "RECALIBRATE\nON BEACON")

    def step_gain(self, step):
        index = max(0, min(len(GAINS) - 1, GAINS.index(self.gain_db) + step))
        self.gain_db = GAINS[index]
        self.gain.setText("%.1f dB" % self.gain_db)
