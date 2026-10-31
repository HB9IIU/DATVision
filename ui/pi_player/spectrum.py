"""Qt BATC spectrum feed and touch/click tuning view. GPL-3.0-or-later."""
import struct
import time
from PyQt5.QtCore import Qt, QUrl, QTimer, pyqtSignal, QPointF, QRectF
from PyQt5.QtGui import QColor, QPainter, QPainterPath, QPen
from PyQt5.QtNetwork import QNetworkRequest
from PyQt5.QtWebSockets import QWebSocket
from PyQt5.QtWidgets import QWidget
from signal_finder import START_MHZ, END_MHZ, BEACON_MHZ, MIN_RISE, detect_signals, find_signal, nearest_signal
import sweeper
from ui_scale import px


# Tuned slot: fill and trace colours for searching, locked and lost.
SLOT_COLORS = {"searching": ((58, 150, 255), "#8cc4ff"), "locked": ((37, 194, 110), "#50d7a0"),
               "lost": ((230, 70, 70), "#ff8a8a")}


class Spectrum(QWidget):
    clicked = pyqtSignal(float)
    rescan = pyqtSignal()
    zoom_out = pyqtSignal()

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
        self.selected = None
        self.slot_state = "searching"
        self.message = ""
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
        self.clear_message = QTimer(self)
        self.clear_message.setSingleShot(True)
        self.clear_message.timeout.connect(lambda: self.show_message(""))

    def set_band(self, band):
        self.beacon_mhz, self.band_name = band.beacon_mhz, band.name
        self.set_view(band.start_mhz, band.end_mhz, False)

    def set_view(self, start_mhz, end_mhz, zoomed):
        self.start_mhz, self.end_mhz, self.zoomed = start_mhz, end_mhz, zoomed
        self.frame, self.signals, self.view_top, self.selected = (), [], 0, None
        self.update()

    def zoom_out_area(self):
        return QRectF(px(6), px(4), px(150), px(36))

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
        self.accept(struct.unpack("<%dH" % (len(data) // 2), data))

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
        return max(0, min(1, (sample / 65535 - 0.12) / (0.58 - 0.12)))

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
        # Touch target in the middle of the plot while a local sweep is paused.
        return QRectF((self.width() - px(190)) / 2, (px(10) + self.height() - px(24) - px(36)) / 2, px(190), px(36))

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton and self.width():
            if self.zoomed and self.zoom_out_area().adjusted(-6, -4, 10, 10).contains(event.pos()):
                self.zoom_out.emit()
            elif self.source == "local" and self.frozen and self.rescan_area().adjusted(-10, -6, 6, 10).contains(event.pos()):
                self.rescan.emit()
            elif self.fresh:
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
        for area, text, shown in ((self.zoom_out_area(), "✕  ZOOM OUT", self.zoomed),
                                  (self.rescan_area(), "↻  TAP TO RESCAN", self.source == "local" and self.frozen)):
            if not shown:
                continue
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
        painter.setPen(QColor("#91a7ba"))
        painter.drawText(width - painter.fontMetrics().horizontalAdvance(source) - px(6), painter.fontMetrics().ascent() + px(4), source)
        text = self.message
        if not self.fresh and not text:
            if self.source == "local":
                text = "Scanning the band…"
            else:
                text = "BATC spectrum reconnecting…" if not self.online else "BATC spectrum stale…"
        if text:
            rect = painter.fontMetrics().boundingRect(text).adjusted(-px(5), -px(4), px(5), px(4))
            rect.moveTopLeft(self.rect().topLeft())
            painter.fillRect(rect, QColor("#0c1620"))
            painter.setPen(QColor("#dde8f1"))
            painter.drawText(px(6), painter.fontMetrics().ascent() + px(4), text)

    def stop(self):
        try:
            self.socket.disconnected.disconnect(self.on_disconnected)
        except TypeError:
            pass   # already stopped: closing can run twice
        self.retry.stop()
        self.clock.stop()
        self.socket.abort()
