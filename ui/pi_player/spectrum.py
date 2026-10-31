"""Qt BATC spectrum feed and touch/click tuning view. GPL-3.0-or-later."""
import struct
import time
from PyQt5.QtCore import Qt, QUrl, QTimer, pyqtSignal, QPointF, QRectF
from PyQt5.QtGui import QColor, QPainter, QPainterPath, QPen
from PyQt5.QtNetwork import QNetworkRequest
from PyQt5.QtWebSockets import QWebSocket
from PyQt5.QtWidgets import QWidget
from signal_finder import START_MHZ, END_MHZ, detect_signals


# Tuned slot: fill and trace colours for searching, locked and lost.
SLOT_COLORS = {"searching": ((58, 150, 255), "#8cc4ff"), "locked": ((37, 194, 110), "#50d7a0"),
               "lost": ((230, 70, 70), "#ff8a8a")}


class Spectrum(QWidget):
    clicked = pyqtSignal(float)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.frame = ()
        self.last_frame = 0
        self.signals = []
        self.last_detection = 0
        self.online = False
        self.selected = None
        self.slot_state = "searching"
        self.message = ""
        self.setMinimumHeight(108)
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

    def connect_feed(self):
        request = QNetworkRequest(QUrl("wss://eshail.batc.org.uk/wb/fft"))
        request.setRawHeader(b"Sec-WebSocket-Protocol", b"fft")
        self.socket.open(request)

    def on_connected(self):
        self.online = True
        self.update()

    def on_disconnected(self):
        self.online = False
        self.update()
        self.retry.start(2000)

    def on_frame(self, data):
        data = bytes(data)
        if not 32 <= len(data) <= 65536 or len(data) % 2:
            return
        self.frame = struct.unpack("<%dH" % (len(data) // 2), data)
        self.last_frame = time.monotonic()
        if self.last_frame - self.last_detection >= 1:
            self.signals = detect_signals(self.frame)
            self.last_detection = self.last_frame
        self.update()

    @property
    def fresh(self):
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

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton and self.width():
            if self.fresh:
                self.clicked.emit(START_MHZ + event.x() / self.width() * (END_MHZ - START_MHZ))
            else:
                self.show_message("Waiting for fresh BATC spectrum data…")
        super().mousePressEvent(event)

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor("#05141d"))
        painter.setRenderHint(QPainter.Antialiasing)
        width = self.width()
        bottom = self.height() - 24
        top = 10
        plot = max(1, bottom - top)
        x = lambda mhz: (mhz - START_MHZ) / (END_MHZ - START_MHZ) * width
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
                sample = self.frame[left] + (index - left) * (self.frame[min(left + 1, len(self.frame) - 1)] - self.frame[left])
                normalized = max(0, min(1, (sample / 65535 - 0.12) / (0.58 - 0.12)))
                point = QPointF(pixel, bottom - normalized * plot)
                if pixel == 0:
                    line.moveTo(point)
                else:
                    line.lineTo(point)
            fill = QPainterPath(line)
            fill.lineTo(width, bottom)
            fill.lineTo(0, bottom)
            fill.closeSubpath()
            painter.fillPath(fill, QColor(255, 176, 0, 45))
            painter.setPen(QPen(QColor("#ffb000"), 1))
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
        for index in range(10):
            frequency = START_MHZ + index
            text = "%.1f" % frequency
            size = painter.fontMetrics().horizontalAdvance(text)
            left = max(0, min(width - size, x(frequency) - size / 2))
            painter.drawText(QPointF(left, self.height() - 5), text)
        text = self.message
        if not self.fresh and not text:
            text = "BATC spectrum reconnecting…" if not self.online else "BATC spectrum stale…"
        if text:
            rect = painter.fontMetrics().boundingRect(text).adjusted(-5, -4, 5, 4)
            rect.moveTopLeft(self.rect().topLeft())
            painter.fillRect(rect, QColor("#0c1620"))
            painter.setPen(QColor("#dde8f1"))
            painter.drawText(6, painter.fontMetrics().ascent() + 4, text)

    def stop(self):
        try:
            self.socket.disconnected.disconnect(self.on_disconnected)
        except TypeError:
            pass   # already stopped: closing can run twice
        self.retry.stop()
        self.clock.stop()
        self.socket.abort()
