"""Touch panels beside the video and the full-screen settings page. GPL-3.0-or-later."""
import math
import time
from PyQt5.QtCore import Qt, pyqtSignal, QPointF, QRectF, QSize, QTimer
from PyQt5.QtGui import QColor, QIcon, QPainter, QPen, QPixmap, QPolygonF
from PyQt5.QtWidgets import (QWidget, QFrame, QVBoxLayout, QHBoxLayout, QGridLayout, QLabel, QPushButton, QSizePolicy,
                             QStackedWidget)
from magic_eye import MagicEye
import calibration
from ui_scale import px

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
    if layout is not None:
        layout.addWidget(widget)
    return widget


class QualityBar(QWidget):
    """The D number as a fuel gauge: empty at the decoding limit, full at 8 dB of
    reserve; red below the limit (a short stub), amber up to 2 dB, then green.
    Without a known threshold: Es/N0 from -3 to 17 dB."""
    FULL_DB = 8

    def __init__(self):
        super().__init__()
        self.value = self.required = None
        self.setFixedHeight(px(10))

    def set_value(self, value, required):
        self.value, self.required = value, required
        self.update()

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor("#1b3140"))
        if self.value is None:
            return
        width, height = self.width(), self.height()
        if self.required is None:
            painter.fillRect(0, 0, round(max(0, min(1, (self.value + 3) / 20)) * width), height, QColor(GREEN))
            return
        margin = self.value - self.required
        if margin < 0:
            painter.fillRect(0, 0, max(px(6), round(0.06 * width)), height, QColor(RED))
            return
        length = max(px(3), round(min(1, margin / self.FULL_DB) * width))
        painter.fillRect(0, 0, length, height, QColor(GREEN if margin >= 2 else AMBER))


class Constellation(QWidget):
    """Recent received symbols after carrier recovery, unit mean power, ±1.5 full scale."""
    tapped = pyqtSignal()

    def __init__(self):
        super().__init__()
        self.points = []
        self.modulation = ""
        self.received = 0
        self.setFixedSize(px(120), px(120))

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
        layout.setContentsMargins(px(10), px(10), px(10), px(10))
        layout.setSpacing(px(2))
        self.state = label(layout, "state", "STARTING")
        self.service = label(layout, "service")
        self.service.setWordWrap(True)
        self.provider = label(layout, "caption")
        layout.addSpacing(px(4))
        self.frequency = label(layout, "value")
        self.rate = label(layout, "value")
        self.mode = label(layout, "value")
        layout.addSpacing(px(4))
        label(layout, "caption", "Es/N0")
        self.esn0 = label(layout, "big", "—")
        self.bar = QualityBar()
        layout.addWidget(self.bar)
        self.margin = label(layout, "dnumber")   # MiniTioune-style D: margin over the mode's threshold
        layout.addSpacing(px(4))
        self.video = label(layout, "value")
        self.audio = label(layout, "value")
        self.errors = label(layout, "caption")
        layout.addStretch()
        self.note = label(layout, "caption")
        self.note.setWordWrap(True)
        # Tap to alternate between the magic eye and the constellation.
        self.eye = MagicEye(px(120))
        self.constellation = Constellation()
        self.indicator = QStackedWidget()
        self.indicator.setFixedSize(px(120), px(120))
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
        if esn0 is None or required is None:
            self.margin.setText("")
        else:
            margin = esn0 - required
            self.margin.setText("D  %.1f" % margin)
            self.margin.setStyleSheet("color: %s;" % (GREEN if margin >= 2 else AMBER if margin >= 0 else RED))
        self.errors.setText("" if bad is None else "bad %d · lost %d" % (bad, lost))

    def show_streams(self, service, provider, video, audio):
        self.service.setText(service)
        self.provider.setText(provider)
        self.video.setText(video)
        self.audio.setText(audio)

    def show_note(self, text):
        self.note.setText(text)


def speaker_icon(volume, muted):
    """A drawn speaker: one to three waves by volume, or a red cross when muted."""
    pixmap = QPixmap(64, 64)
    pixmap.fill(Qt.transparent)
    p = QPainter(pixmap)
    p.setRenderHint(QPainter.Antialiasing)
    color = QColor(RED if muted else "#dde8f1")
    p.setPen(Qt.NoPen)
    p.setBrush(color)
    p.drawPolygon(QPolygonF([QPointF(6, 24), QPointF(18, 24), QPointF(32, 11), QPointF(32, 53), QPointF(18, 40), QPointF(6, 40)]))
    p.setBrush(Qt.NoBrush)
    p.setPen(QPen(color, 4.5, Qt.SolidLine, Qt.RoundCap))
    if muted:
        p.drawLine(QPointF(41, 24), QPointF(57, 40))
        p.drawLine(QPointF(57, 24), QPointF(41, 40))
    else:
        for radius in (10, 18, 26)[:0 if volume == 0 else 1 if volume <= 33 else 2 if volume <= 66 else 3]:
            p.drawArc(QRectF(32 - radius, 32 - radius, 2 * radius, 2 * radius), -45 * 16, 90 * 16)
    p.end()
    return QIcon(pixmap)


def symbol_icon(kind, color):
    """Drawn symbols for the bottom row: 'gear' (settings) or 'power' (quit)."""
    pixmap = QPixmap(64, 64)
    pixmap.fill(Qt.transparent)
    p = QPainter(pixmap)
    p.setRenderHint(QPainter.Antialiasing)
    p.setPen(QPen(QColor(color), 5, Qt.SolidLine, Qt.RoundCap))
    if kind == "power":
        p.drawArc(QRectF(12, 14, 40, 40), 120 * 16, 300 * 16)
        p.drawLine(QPointF(32, 8), QPointF(32, 32))
    else:
        for tooth in range(8):
            p.save()
            p.translate(32, 32)
            p.rotate(tooth * 45)
            p.drawLine(QPointF(0, -17), QPointF(0, -25))
            p.restore()
        p.drawEllipse(QPointF(32, 32), 14, 14)
        p.drawEllipse(QPointF(32, 32), 4, 4)
    p.end()
    return QIcon(pixmap)


def card_icon(kind):
    """Small symbols for the settings cards, drawn in the accent colour."""
    pixmap = QPixmap(64, 64)
    pixmap.fill(Qt.transparent)
    p = QPainter(pixmap)
    p.setRenderHint(QPainter.Antialiasing)
    p.setPen(QPen(QColor("#8cc4ff"), 5, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
    p.setBrush(Qt.NoBrush)
    if kind == "spectrum":
        p.drawPolyline(QPolygonF([QPointF(4, 50), QPointF(14, 48), QPointF(20, 20), QPointF(36, 20), QPointF(42, 48),
                                  QPointF(50, 46), QPointF(54, 36), QPointF(58, 46), QPointF(62, 48)]))
    elif kind == "dish":
        p.drawArc(QRectF(6, 6, 44, 52), 100 * 16, 160 * 16)
        p.drawLine(QPointF(14, 32), QPointF(52, 18))
        p.drawEllipse(QPointF(54, 17), 4, 4)
        p.drawLine(QPointF(26, 56), QPointF(40, 56))
    elif kind == "gain":
        p.drawLine(QPointF(3, 32), QPointF(15, 32))
        p.drawPolygon(QPolygonF([QPointF(15, 12), QPointF(15, 52), QPointF(49, 32)]))
        p.drawLine(QPointF(49, 32), QPointF(61, 32))
    elif kind == "mixer":
        p.drawEllipse(QPointF(32, 32), 22, 22)
        p.drawLine(QPointF(17, 17), QPointF(47, 47))
        p.drawLine(QPointF(47, 17), QPointF(17, 47))
    else:   # about
        p.drawEllipse(QPointF(32, 32), 26, 26)
        p.drawLine(QPointF(32, 28), QPointF(32, 46))
        p.drawPoint(QPointF(32, 18))
    p.end()
    return QIcon(pixmap)


def amplifier_icon():
    """The schematic RF amplifier: a triangle with input and output leads."""
    pixmap = QPixmap(64, 64)
    pixmap.fill(Qt.transparent)
    p = QPainter(pixmap)
    p.setRenderHint(QPainter.Antialiasing)
    p.setPen(QPen(QColor("#dde8f1"), 4.5, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
    p.drawLine(QPointF(3, 32), QPointF(15, 32))
    p.drawPolygon(QPolygonF([QPointF(15, 12), QPointF(15, 52), QPointF(49, 32)]))
    p.drawLine(QPointF(49, 32), QPointF(61, 32))
    p.end()
    return QIcon(pixmap)


CONTROL_HEIGHT = 52


class ControlPanel(QFrame):
    mute = pyqtSignal()
    louder = pyqtSignal()
    quieter = pyqtSignal()
    beacon = pyqtSignal()
    stop_start = pyqtSignal()
    bands = pyqtSignal()
    settings = pyqtSignal()
    gain_down = pyqtSignal()
    gain_up = pyqtSignal()
    quit = pyqtSignal()

    def __init__(self):
        super().__init__(objectName="panel")
        layout = QVBoxLayout(self)
        layout.setContentsMargins(px(8), px(8), px(8), px(8))
        layout.setSpacing(px(6))
        # Groups of equal-height controls: audio, then reception, then pages.
        self.sound = button(layout, "", self.mute)
        self.pair(layout, self.quieter, self.louder)
        layout.addSpacing(px(8))
        # Terrestrial bands: RF gain right here instead of BEACON, STOP and SETTINGS.
        self.gain = QWidget()
        gain = QVBoxLayout(self.gain)
        gain.setContentsMargins(0, 0, 0, 0)
        gain.setSpacing(px(8))
        readout = QFrame(objectName="readout")
        readout.setFixedHeight(px(CONTROL_HEIGHT))
        row = QHBoxLayout(readout)
        row.setContentsMargins(px(4), 0, px(4), 0)
        row.setSpacing(px(4))
        row.addStretch()
        symbol = QLabel()
        symbol.setPixmap(amplifier_icon().pixmap(px(26), px(26)))
        row.addWidget(symbol)
        self.gain_value = label(row, "readoutText")
        row.addStretch()
        gain.addWidget(readout)
        self.pair(gain, self.gain_down, self.gain_up)
        layout.addWidget(self.gain)
        self.beacon_button = button(layout, "BEACON", self.beacon)
        self.run = button(layout, "STOP", self.stop_start)
        layout.addStretch()
        button(layout, "BANDS", self.bands)   # full video: tap the picture
        # Settings and quit as symbols, one above the other; quit asks for a second tap.
        self.settings_button = button(layout, "", self.settings)
        self.settings_button.setIcon(symbol_icon("gear", "#dde8f1"))
        self.quit_button = QPushButton(objectName="danger")
        self.quit_button.setFocusPolicy(Qt.NoFocus)
        self.quit_button.clicked.connect(self.quit_tapped)
        layout.addWidget(self.quit_button)
        self.quit_timer = QTimer(self)
        self.quit_timer.setSingleShot(True)
        self.quit_timer.timeout.connect(self.disarm_quit)
        self.disarm_quit()
        for widget in self.findChildren(QPushButton):
            widget.setFixedHeight(px(CONTROL_HEIGHT))
            widget.setIconSize(QSize(px(34), px(34)))

    def quit_tapped(self):
        if self.quit_timer.isActive():
            self.quit.emit()
        else:
            self.quit_button.setIcon(QIcon())
            self.quit_button.setText("QUIT?")
            self.quit_timer.start(3000)

    def disarm_quit(self):
        self.quit_button.setText("")
        self.quit_button.setIcon(symbol_icon("power", "#ff9b9b"))

    def pair(self, layout, minus, plus):
        row = QHBoxLayout()
        row.setSpacing(px(8))
        button(row, "−", minus, "symbol")
        button(row, "+", plus, "symbol")
        layout.addLayout(row)

    def show_band(self, band):
        self.beacon_button.setVisible(bool(band.beacon_mhz))
        self.run.setVisible(band.via_lnb)
        self.settings_button.setVisible(band.via_lnb or bool(band.converter_los))
        self.gain.setVisible(not band.via_lnb)

    def show_gain(self, gain_db):
        self.gain_value.setText("%.1f dB" % gain_db)

    def show_audio(self, volume, muted):
        self.sound.setIcon(speaker_icon(volume, muted))
        self.sound.setIconSize(QSize(px(38), px(38)))
        self.sound.setText("" if muted else " %d %%" % volume)

    def show_running(self, running):
        self.run.setText("STOP" if running else "START")


class BandsPage(QWidget):
    chosen = pyqtSignal(str)
    quit = pyqtSignal()

    def __init__(self, bands, credits):
        super().__init__(objectName="settings")
        self.current = bands[0].key
        layout = QVBoxLayout(self)
        layout.setContentsMargins(px(24), px(16), px(24), px(16))
        layout.setSpacing(px(14))
        top = QHBoxLayout()
        label(top, "title", "Bands")
        top.addStretch()
        back = QPushButton("BACK")
        back.setFocusPolicy(Qt.NoFocus)
        back.setFixedSize(px(150), px(60))
        back.clicked.connect(lambda: self.chosen.emit(self.current))
        top.addWidget(back)
        layout.addLayout(top)
        grid = QGridLayout()
        grid.setSpacing(px(14))
        self.buttons = {}
        for index, band in enumerate(bands):
            widget = QPushButton(objectName="band")
            widget.setCheckable(True)
            widget.setFocusPolicy(Qt.NoFocus)
            # Three lines of different weight: the band name stands out.
            lines = QVBoxLayout(widget)
            lines.setSpacing(px(2))
            lines.addStretch()
            for text, role in ((band.name, "bandName"), ("%g – %g MHz" % (band.start_mhz, band.end_mhz), "bandRange"),
                               ("via LNB" if band.via_lnb else "via converter" if band.converter_los else "direct antenna",
                                "bandSource")):
                line = label(lines, role, text)
                line.setAlignment(Qt.AlignCenter)
                line.setAttribute(Qt.WA_TransparentForMouseEvents)
            lines.addStretch()
            widget.clicked.connect(lambda _, key=band.key: self.chosen.emit(key))
            grid.addWidget(widget, index // 3, index % 3)
            self.buttons[band.key] = widget
        layout.addLayout(grid)
        layout.addStretch()
        bottom = QHBoxLayout()
        about = QLabel(credits, objectName="caption")
        about.setWordWrap(True)
        bottom.addWidget(about, 1)
        button(bottom, "QUIT TO\nDESKTOP", self.quit, "danger").setFixedSize(px(170), px(64))
        layout.addLayout(bottom)

    def show_current(self, key):
        self.current = key
        for name, widget in self.buttons.items():
            widget.setChecked(name == key)


class SettingsPage(QWidget):
    done = pyqtSignal()
    quit = pyqtSignal()
    calibrate = pyqtSignal()

    def __init__(self, credits, version):
        super().__init__(objectName="settings")
        self.lnb_mhz = 9750.0
        self.gain_db = 40.2
        self.source = "local"
        self.lo_buttons = {}
        self.lo_mhz = 0
        self.band = None
        layout = QVBoxLayout(self)
        layout.setContentsMargins(px(24), px(16), px(24), px(16))
        layout.setSpacing(px(16))
        top = QHBoxLayout()
        heading = QVBoxLayout()
        heading.setSpacing(px(2))
        self.title = label(heading, "title", "Settings")
        label(heading, "caption", "Changes are applied when you press DONE.")
        top.addLayout(heading)
        top.addStretch()
        button(top, "DONE", self.done, "accent").setFixedSize(px(150), px(60))
        layout.addLayout(top)

        # A grid of equal cards; each band shows only the cards that apply to it.
        self.grid = QGridLayout()
        self.grid.setSpacing(px(16))
        self.grid.setColumnStretch(0, 1)
        self.grid.setColumnStretch(1, 1)
        layout.addLayout(self.grid)

        self.spectrum_card, card, _ = self.card("spectrum", "Spectrum",
            "Local scan: your own stick sweeps the band, no internet needed. "
            "BATC: the BATC web spectrum of the whole transponder.")
        row = QHBoxLayout()
        row.setSpacing(px(8))
        self.source_buttons = {}
        for source, text in (("local", "LOCAL SCAN"), ("batc", "BATC ONLINE")):
            widget = QPushButton(text)
            widget.setCheckable(True)
            widget.setFocusPolicy(Qt.NoFocus)
            widget.setFixedHeight(px(56))
            widget.clicked.connect(lambda _, source=source: self.set_source(source))
            row.addWidget(widget)
            self.source_buttons[source] = widget
        card.insertLayout(1, row)

        self.gain_card, card, _ = self.card("gain", "RF gain",
            "Lower it if strong signals overload the stick, raise it for weak stations.")
        row = QHBoxLayout()
        row.setSpacing(px(8))
        for text, step in (("−", -1), ("+", 1)):
            widget = QPushButton(text, objectName="symbol")
            widget.setFocusPolicy(Qt.NoFocus)
            widget.setFixedSize(px(96), px(56))
            widget.clicked.connect(lambda _, step=step: self.step_gain(step))
            row.addWidget(widget)
            if step < 0:
                self.gain = label(row, "big")
                self.gain.setAlignment(Qt.AlignCenter)
        row.setStretchFactor(self.gain, 1)
        card.insertLayout(1, row)

        # The LNB is measured on the beacon rather than entered by hand.
        self.lnb_card, card, _ = self.card("dish", "LNB oscillator",
            "Measured on the QO-100 beacon: five locks, deliberately detuned.")
        self.lnb = label(card, "big")
        card.insertWidget(1, self.lnb)
        row = QHBoxLayout()
        row.setSpacing(px(8))
        self.calibrate_button = button(row, "", self.calibrate)
        self.calibrate_button.setFixedHeight(px(56))
        self.undo = QPushButton("UNDO")
        self.undo.setFocusPolicy(Qt.NoFocus)
        self.undo.setFixedSize(px(100), px(56))
        self.undo.clicked.connect(self.undo_calibration)
        self.undo.hide()
        row.addWidget(self.undo)
        card.insertLayout(2, row)
        self.calibration_note = label(card, "value")
        self.calibration_note.setWordWrap(True)
        card.insertWidget(3, self.calibration_note)

        # Downconverter bands: the LO the receive frequency is shifted by.
        self.converter_card, card, self.lo_note = self.card("mixer", "Converter LO", "")   # the hint shows RF − LO
        self.lo_grid = QGridLayout()
        self.lo_grid.setSpacing(px(8))
        card.insertLayout(1, self.lo_grid)

        self.about_card, card, _ = self.card("about", "About · version " + version, credits)
        quit_button = button(None, "QUIT TO DESKTOP", self.quit, "danger")
        quit_button.setFixedHeight(px(56))
        card.addWidget(quit_button)
        layout.addStretch()

    def card(self, icon, title, hint):
        # Title row with a symbol, content inserted from index 1, then the hint at the bottom.
        frame = QFrame(objectName="panel")
        lines = QVBoxLayout(frame)
        lines.setContentsMargins(px(16), px(12), px(16), px(14))
        lines.setSpacing(px(10))
        head = QHBoxLayout()
        head.setSpacing(px(8))
        symbol = QLabel()
        symbol.setPixmap(card_icon(icon).pixmap(px(22), px(22)))
        head.addWidget(symbol)
        label(head, "cardTitle", title)
        head.addStretch()
        lines.addLayout(head)
        lines.addStretch()
        note = label(lines, "caption", hint)
        note.setWordWrap(True)
        frame.hide()
        return frame, lines, note

    def arrange(self, band):
        cards = ([self.spectrum_card, self.lnb_card, self.gain_card] if band.via_lnb else
                 [self.gain_card, self.converter_card] if band.converter_los else [self.gain_card]) + [self.about_card]
        for frame in (self.spectrum_card, self.lnb_card, self.gain_card, self.converter_card, self.about_card):
            self.grid.removeWidget(frame)
            frame.hide()
        for index, frame in enumerate(cards):
            row, column = divmod(index, 2)
            last_alone = index == len(cards) - 1 and column == 0
            self.grid.addWidget(frame, row, column, 1, 2 if last_alone else 1)
            frame.show()

    def set_lo(self, lo_mhz):
        self.lo_mhz = lo_mhz
        for lo, widget in self.lo_buttons.items():
            widget.setChecked(lo == lo_mhz)
        example = (self.band.start_mhz + self.band.end_mhz) / 2
        self.lo_note.setText("The stick receives RF − LO: %g MHz is tuned at %g MHz." % (example, example - lo_mhz))

    def set_source(self, source):
        self.source = source
        for name, widget in self.source_buttons.items():
            widget.setChecked(name == source)

    def load(self, lnb_mhz, gain_db, source, band, lo_mhz):
        self.title.setText("Settings · " + band.name)
        self.arrange(band)
        self.band = band
        for widget in self.lo_buttons.values():
            widget.deleteLater()
        self.lo_buttons = {}
        for lo, name in zip(band.converter_los, band.converter_names):
            widget = QPushButton("%g MHz\n%s" % (lo, name))
            widget.setCheckable(True)
            widget.setFocusPolicy(Qt.NoFocus)
            widget.setFixedHeight(px(60))
            widget.clicked.connect(lambda _, lo=lo: self.set_lo(lo))
            self.lo_grid.addWidget(widget, len(self.lo_buttons) // 2, len(self.lo_buttons) % 2)
            self.lo_buttons[lo] = widget
        if band.converter_los:
            self.set_lo(lo_mhz)
        self.set_source(source)
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
