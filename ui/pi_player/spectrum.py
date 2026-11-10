"""Qt BATC spectrum feed and touch/click tuning view. GPL-3.0-or-later."""
import struct
import time
from PyQt5.QtCore import Qt, QUrl, QTimer, pyqtSignal, QPoint, QPointF, QRectF
from PyQt5.QtGui import QColor, QFontMetrics, QLinearGradient, QPainter, QPainterPath, QPen
from PyQt5.QtNetwork import QNetworkRequest
from PyQt5.QtWebSockets import QWebSocket
from PyQt5.QtWidgets import QWidget
import bands
from signal_finder import START_MHZ, END_MHZ, BEACON_MHZ, MIN_RISE, detect_signals, find_signal, nearest_signal
import sweeper
from ui_scale import px


# Tuned slot: fill and trace colours for searching, locked and lost.
SLOT_COLORS = {"searching": ((58, 150, 255), "#8cc4ff"), "locked": ((37, 194, 110), "#50d7a0"),
               "lost": ((230, 70, 70), "#ff8a8a")}


class Spectrum(QWidget):
    clicked = pyqtSignal(float)
    rescan = pyqtSignal()
    navigate = pyqtSignal(str)   # "in", "out", "full": the zoom buttons on the wide bands

    def __init__(self, parent=None):
        super().__init__(parent)
        self.frame = ()
        self.last_frame = 0
        self.signals = []
        self.last_detection = 0
        self.online = False
        self.source = "batc"    # or "local": sweeps from the stick
        self.frozen = False     # local spectrum while the stick is busy receiving
        self.view_top = 0       # local: frame level drawn near the top (follows the strongest signal)
        self.start_mhz, self.end_mhz, self.beacon_mhz, self.band_name = START_MHZ, END_MHZ, BEACON_MHZ, "QO-100"
        self.zoomed = False
        self.pan_zoom = False       # a band wider than bands.ZOOM_ABOVE_MHZ: touch recentres, buttons - + FULL
        self.full_name = "QO-100"
        self.scan_check = lambda: False   # set by the player: is the local scan running?
        self.pending_until = 0      # a rescan was asked for and is about to start: show it at once
        self.beat = 0
        self.selected = None
        self.slot_state = "searching"
        self.message = ""
        self.receiver = "RTL-SDR"   # or PLUTO: shown with the source
        self.setMinimumHeight(px(108))
        self.setCursor(Qt.CrossCursor)
        self.socket = QWebSocket()
        self.socket.connected.connect(self.on_connected)
        self.socket.disconnected.connect(self.on_disconnected)
        self.socket.binaryMessageReceived.connect(self.on_frame)
        self.retry = QTimer(self)
        self.retry.setSingleShot(True)
        self.retry.timeout.connect(self.connect_feed)
        self.clock = QTimer(self)
        self.clock.timeout.connect(self.update)
        self.clock.start(1000)
        self.animation = QTimer(self)   # the moving bar of the first scan, the blink of "Continuous Scanning"
        self.animation.timeout.connect(self.animate)
        self.animation.start(60)
        self.clear_message = QTimer(self)
        self.clear_message.setSingleShot(True)
        self.clear_message.timeout.connect(lambda: self.show_message(""))

    def set_band(self, band):
        self.beacon_mhz, self.band_name, self.full_name = band.beacon_mhz, band.name, band.name
        self.pan_zoom = band.end_mhz - band.start_mhz > bands.ZOOM_ABOVE_MHZ
        self.set_view(band.start_mhz, band.end_mhz, False)

    def set_view(self, start_mhz, end_mhz, zoomed):
        self.start_mhz, self.end_mhz, self.zoomed = start_mhz, end_mhz, zoomed
        self.frame, self.signals, self.view_top, self.selected = (), [], 0, None
        self.update()

    def nav_font(self):
        font = self.font()
        font.setBold(True)
        font.setPointSizeF(font.pointSizeF() * 1.2)
        return font

    def nav_buttons(self):
        """[(key, text, rectangle)] of the zoom buttons in the top-left corner, on the wide bands."""
        if not self.pan_zoom:
            return []
        full = "FULL %s Band" % self.full_name
        metrics = QFontMetrics(self.nav_font())
        out, x = [], px(6)
        for key, text, width in (("out", "–", px(46)), ("in", "+", px(46)),
                                 ("full", full, metrics.horizontalAdvance(full) + px(26))):
            out.append((key, text, QRectF(x, px(4), width, px(36))))
            x += width + px(6)
        return out

    @property
    def scanning(self):
        """The local scan is running, or is about to: "Continuous Scanning" shows, with a moving bar until the first picture."""
        if self.source != "local":
            return False
        if self.frozen:
            self.pending_until = 0
            return False
        return time.monotonic() < self.pending_until or bool(self.scan_check())

    def begin_rescan(self):
        """A rescan was asked for: erase the old picture and show the scanning at once (stopping the receiver takes a moment)."""
        if self.source == "local":
            self.frame, self.signals, self.view_top, self.selected, self.frozen = (), [], 0, None, False
            self.pending_until = time.monotonic() + 6
            self.repaint()

    def animate(self):
        if self.scanning:
            self.beat += 1
            if not self.frame or self.beat % 4 == 0:   # smooth without a picture, a blink's pace with one
                self.update()

    def signal_at(self, mhz):
        # Finger tolerance in pixels, so wide bands stay as easy to tap as QO-100.
        per_px = (self.end_mhz - self.start_mhz) / max(1, self.width())
        signal = find_signal(self.frame, mhz, self.min_rise, self.start_mhz, self.end_mhz, self.beacon_mhz, max(0.1, 8 * per_px))
        return signal or nearest_signal(self.signals, mhz, max(0.25, 25 * per_px))

    def set_source(self, source):
        self.source = source
        self.frame, self.signals, self.frozen, self.view_top = (), [], False, 0
        if source == "batc":
            self.connect_feed()
        else:
            self.retry.stop()
            self.socket.abort()
            self.online = False
        self.update()

    def connect_feed(self):
        if self.source != "batc":
            return
        request = QNetworkRequest(QUrl("wss://eshail.batc.org.uk/wb/fft"))
        request.setRawHeader(b"Sec-WebSocket-Protocol", b"fft")
        self.socket.open(request)

    def on_connected(self):
        self.online = True
        self.update()

    def on_disconnected(self):
        self.online = False
        self.update()
        if self.source == "batc":
            self.retry.start(2000)

    def on_frame(self, data):
        data = bytes(data)
        if self.source != "batc" or not 32 <= len(data) <= 65536 or len(data) % 2:
            return
        values = struct.unpack("<%dH" % (len(data) // 2), data)
        # BATC too: the height follows the strongest signal (a robust peak, rising at
        # once and falling slowly), so the beacon is not cut off at the top.
        peak = sorted(values)[int(len(values) * 0.995)]
        self.view_top = peak if peak > self.view_top else 0.9 * self.view_top + 0.1 * peak
        self.accept(values)

    def set_local(self, values):
        if self.source == "local":
            self.frozen = False
            # Height follows the strongest signal, normally the beacon: a robust peak
            # (one narrow spike cannot set it), rising at once and falling slowly.
            peak = max(sorted(values)[int(len(values) * 0.995)], sweeper.NOISE_LEVEL + 6 * sweeper.PER_DB)
            self.view_top = peak if peak > self.view_top else 0.9 * self.view_top + 0.1 * peak
            self.accept(values)

    def normalize(self, sample):
        if self.source == "local" and self.view_top:
            # Noise floor at 15 % of the height, the strongest signal at 90 %.
            return max(0, min(1, 0.15 + 0.75 * (sample - sweeper.NOISE_LEVEL) / (self.view_top - sweeper.NOISE_LEVEL)))
        # BATC: its noise floor at 0.12 of full scale; the strongest signal at 88 % of the
        # height, leaving a margin at the top; weak bands keep the former fixed scale.
        floor, top = 0.12 * 65535, max(0.58 * 65535, self.view_top)
        return max(0, min(1, 0.88 * (sample - floor) / (top - floor)))

    def accept(self, frame):
        self.frame = frame
        self.last_frame = time.monotonic()
        if self.last_frame - self.last_detection >= 1:
            self.signals = detect_signals(self.frame, self.min_rise, self.start_mhz, self.end_mhz, self.beacon_mhz)
            self.last_detection = self.last_frame
        self.update()

    @property
    def min_rise(self):
        return sweeper.MIN_RISE if self.source == "local" else MIN_RISE

    @property
    def fresh(self):
        if self.source == "local":
            return bool(self.frame)   # a paused sweep stays valid for tuning
        return self.online and self.frame and time.monotonic() - self.last_frame < 5

    def set_slot(self, signal, state, now=False):
        if (signal, state) != (self.selected, self.slot_state):
            self.selected, self.slot_state = signal, state
            self.repaint() if now else self.update()

    def show_message(self, message):
        self.message = message
        if message:
            self.clear_message.start(4000)
        self.update()

    def rescan_area(self):
        # Touch target in the upper part of the plot while a local sweep is paused, above
        # the signals that are usually lower down (a centred button sat on the tuned one).
        top = px(10) + (self.height() - px(24) - px(10) - px(36)) * 0.25
        return QRectF((self.width() - px(190)) / 2, top, px(190), px(36))

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton and self.width():
            pressed = next((key for key, _, rect in self.nav_buttons() if rect.adjusted(-6, -4, 6, 8).contains(event.pos())), None)
            if pressed:
                self.navigate.emit(pressed)
            elif self.source == "local" and self.frozen and self.rescan_area().adjusted(-10, -6, 6, 10).contains(event.pos()):
                self.rescan.emit()
            elif self.fresh or (self.source == "local" and self.pan_zoom):   # a wide band can be zoomed before the first scan is done
                self.clicked.emit(self.start_mhz + event.x() / self.width() * (self.end_mhz - self.start_mhz))
            else:
                self.show_message("Waiting for the first scan…" if self.source == "local" else "Waiting for fresh BATC spectrum data…")
        super().mousePressEvent(event)

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor("#05141d"))
        painter.setRenderHint(QPainter.Antialiasing)
        width = self.width()
        bottom = self.height() - px(24)
        top = px(10)
        plot = max(1, bottom - top)
        x = lambda mhz: (mhz - self.start_mhz) / (self.end_mhz - self.start_mhz) * width
        band = None
        fill_rgb, trace = SLOT_COLORS[self.slot_state]
        if self.selected:
            half = self.selected.symbol_rate_ks * 1.35 / 2000
            band = QRectF(x(self.selected.center_mhz - half), 0,
                          x(self.selected.center_mhz + half) - x(self.selected.center_mhz - half), bottom)
        if self.frame:
            line = QPainterPath()
            for pixel in range(width):
                index = pixel * (len(self.frame) - 1) / max(1, width - 1)
                left = int(index)
                following = int((pixel + 1) * (len(self.frame) - 1) / max(1, width - 1))
                if following > left + 1:
                    # Several points per pixel: draw the strongest, so narrow carriers are not lost.
                    sample = max(self.frame[left:following])
                else:
                    sample = self.frame[left] + (index - left) * (self.frame[min(left + 1, len(self.frame) - 1)] - self.frame[left])
                normalized = self.normalize(sample)
                point = QPointF(pixel, bottom - normalized * plot)
                if pixel == 0:
                    line.moveTo(point)
                else:
                    line.lineTo(point)
            fill = QPainterPath(line)
            fill.lineTo(width, bottom)
            fill.lineTo(0, bottom)
            fill.closeSubpath()
            painter.fillPath(fill, QColor(255, 176, 0, 20 if self.frozen else 45))
            painter.setPen(QPen(QColor(255, 176, 0, 110 if self.frozen else 255), 1))
            painter.drawPath(line)
            if band:
                # The tuned slot: the signal itself filled in its state colour.
                painter.save()
                painter.setClipRect(band)
                painter.fillPath(fill, QColor(*fill_rgb, 170))
                painter.setPen(QPen(QColor(trace), 1.5))
                painter.drawPath(line)
                painter.restore()
        elif band:
            painter.fillRect(band, QColor(*fill_rgb, 60))
        painter.setPen(QColor("#91a7ba"))
        span = self.end_mhz - self.start_mhz
        tick = next((t for t in (0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50) if span / t <= 10), 100)
        decimals = 1 if tick < 1 or self.start_mhz % 1 else 0
        for index in range(int(span / tick + 1e-6) + 1):
            frequency = self.start_mhz + index * tick
            text = "%.*f" % (decimals, frequency)
            size = painter.fontMetrics().horizontalAdvance(text)
            left = max(0, min(width - size, x(frequency) - size / 2))
            painter.drawText(QPointF(left, self.height() - px(5)), text)
        scanning = self.scanning
        if scanning:
            # Red and blinking at the top centre: the scan repeats by itself until a signal is tapped. Until the first picture of
            # the band is there (the scanner reports a whole pass at a time, which takes a while on a wide band) a bar moves
            # across the plot, so that it is clear that something is going on.
            lit = int(time.monotonic() * 1000) % 1000 < 600
            title_font = self.font()
            title_font.setPointSizeF(title_font.pointSizeF() * 1.15)
            painter.setFont(title_font)
            painter.setPen(QColor(255, 107, 107, 235 if lit else 90))
            title = QRectF(0, px(2), width, QFontMetrics(title_font).height())
            painter.drawText(title, Qt.AlignHCenter | Qt.AlignTop, "Continuous Scanning")
            painter.setFont(self.font())
            if not self.frame:
                bar = (time.monotonic() % 2.5) / 2.5 * width
                trail = QLinearGradient(bar - px(80), 0, bar, 0)
                trail.setColorAt(0, QColor(255, 189, 69, 0))
                trail.setColorAt(1, QColor(255, 189, 69, 110))
                painter.fillRect(QRectF(bar - px(80), top, px(80), plot), trail)
                painter.setPen(QPen(QColor(255, 189, 69, 230), 2))
                painter.drawLine(QPointF(bar, top), QPointF(bar, bottom))
                painter.setPen(QColor("#91a7ba"))
                painter.drawText(QRectF(0, title.bottom() + px(2), width, QFontMetrics(self.font()).height() + px(2)),
                                 Qt.AlignHCenter | Qt.AlignTop, "first scan in progress…")
        areas = [(rect, text) for _, text, rect in self.nav_buttons()]
        if self.source == "local" and self.frozen:
            areas.append((self.rescan_area(), "↻  TAP TO RESCAN"))
        for area, text in areas:
            painter.setPen(QPen(QColor(140, 196, 255, 170), 1))
            painter.setBrush(QColor(12, 22, 32, 95))   # see-through: the signal stays visible
            painter.drawRoundedRect(area, 6, 6)
            painter.setPen(QColor("#dde8f1"))
            font = painter.font()
            font.setBold(True)
            font.setPointSizeF(font.pointSizeF() * 1.2)
            painter.setFont(font)
            painter.drawText(area, Qt.AlignCenter, text)
            painter.setFont(self.font())
            painter.setBrush(Qt.NoBrush)
        source = self.band_name + (" · ZOOM" if self.zoomed else "") + " · " + ("BATC" if self.source == "batc" else "LOCAL · PAUSED" if self.frozen else "LOCAL")
        source += " · " + self.receiver   # which radio the data comes from
        painter.setPen(QColor("#91a7ba"))
        painter.drawText(width - painter.fontMetrics().horizontalAdvance(source) - px(6), painter.fontMetrics().ascent() + px(4), source)
        text = "" if scanning else self.message   # "Continuous Scanning" takes the place of the message
        if not self.fresh and not text and not scanning:
            if self.source == "local":
                text = "Scanning the band…"
            else:
                text = "BATC spectrum reconnecting…" if not self.online else "BATC spectrum stale…"
        if text:
            # Centred at the top of the plot.
            rect = painter.fontMetrics().boundingRect(text).adjusted(-px(5), -px(4), px(5), px(4))
            rect.moveTopLeft(QPoint((width - rect.width()) // 2, 0))
            painter.fillRect(rect, QColor("#0c1620"))
            painter.setPen(QColor("#dde8f1"))
            painter.drawText(rect.left() + px(5), painter.fontMetrics().ascent() + px(4), text)

    def stop(self):
        try:
            self.socket.disconnected.disconnect(self.on_disconnected)
        except TypeError:
            pass   # already stopped: closing can run twice
        self.retry.stop()
        self.clock.stop()
        self.animation.stop()
        self.socket.abort()
