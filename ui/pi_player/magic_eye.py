"""Magic eye tuning indicator, an end-on view of an EM34/6E5-style tube. GPL-3.0-or-later.

Idea from the Magic Eye of HB9IIU SDR-Tiouner, itself inspired by the Lynx DATV
Receiver by G8YTZ. Driven by DVB lock and the Es/N0 margin over the approximate
threshold of the mode; RF strength is not used.
"""
import math
import random
import time
from PyQt5.QtCore import Qt, QPointF, QRectF, QTimer, pyqtSignal
from PyQt5.QtGui import QColor, QConicalGradient, QPainter, QPainterPath, QPen, QPixmap, QRadialGradient
from PyQt5.QtWidgets import QWidget

GOOD_MARGIN_DB = 5   # margin at which the shadow is (almost) closed


def radial(cx, cy, radius, stops, fx=None, fy=None):
    gradient = QRadialGradient(QPointF(cx, cy), radius, QPointF(cx if fx is None else fx, cy if fy is None else fy))
    for position, color in stops:
        gradient.setColorAt(position, color)
    return gradient


def ring(cx, cy, inner, outer):
    path = QPainterPath()
    path.addEllipse(QPointF(cx, cy), outer, outer)
    path.addEllipse(QPointF(cx, cy), inner, inner)
    path.setFillRule(Qt.OddEvenFill)
    return path


class MagicEye(QWidget):
    tapped = pyqtSignal()

    def __init__(self, size=120):
        super().__init__()
        self.setFixedSize(size, size)
        self.c = size / 2
        self.outer = size * 0.40     # edge of the fluorescent target
        self.cap = size * 0.155      # black cap over the cathode
        self.value = self.target = 0.0   # 0 widest shadow, 1 closed
        self.light = self.bright = 0.0   # 0 cold tube, 1 lit
        self.last = time.monotonic()
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.step)
        self.base = self.layer(self.paint_base)
        self.phosphor = self.layer(self.paint_phosphor)
        self.cap_layer = self.layer(self.paint_cap)
        self.glass = self.layer(self.paint_glass)

    def show_quality(self, running, locked, esn0, required):
        self.bright = 1.0 if running else 0.0
        if running and locked and esn0 is not None:
            fraction = (esn0 - required) / GOOD_MARGIN_DB if required is not None else esn0 / 15
            self.target = 0.12 + 0.88 * max(0.0, min(1.0, fraction))
        else:
            self.target = 0.0
        if not self.timer.isActive():
            self.last = time.monotonic()
            self.timer.start(33)

    def step(self):
        now = time.monotonic()
        dt = min(0.1, now - self.last)
        self.last = now
        # The shadow opens quickly when the signal drops and closes more slowly.
        for name, goal, tau in (("value", self.target, 0.10 if self.target < self.value else 0.22),
                                ("light", self.bright, 0.35)):
            current = getattr(self, name)
            current += (goal - current) * (1 - math.exp(-dt / tau))
            setattr(self, name, goal if abs(goal - current) < 0.002 else current)
        if self.value == self.target and self.light == self.bright:
            self.timer.stop()
        self.update()

    def mousePressEvent(self, event):
        self.tapped.emit()

    # ---- static layers, rendered once
    def layer(self, paint):
        pixmap = QPixmap(self.size())
        pixmap.fill(Qt.transparent)
        painter = QPainter(pixmap)
        painter.setRenderHint(QPainter.Antialiasing)
        painter.setPen(Qt.NoPen)
        paint(painter)
        painter.end()
        return pixmap

    def paint_base(self, p):
        c, s = self.c, self.width()
        # Mounting bezel: dark brushed metal.
        metal = QConicalGradient(QPointF(c, c), 125)
        for position, grey in ((0, 0x5c), (0.18, 0x22), (0.42, 0x4a), (0.6, 0x18), (0.82, 0x3e), (1, 0x5c)):
            metal.setColorAt(position, QColor(grey, grey + 3, grey + 1))
        p.setBrush(metal)
        p.drawEllipse(QPointF(c, c), s * 0.495, s * 0.495)
        p.setBrush(QColor("#030504"))
        p.drawEllipse(QPointF(c, c), s * 0.462, s * 0.462)
        # Glass interior with the silvery getter deposit towards the rim.
        p.setBrush(radial(c, c, s * 0.455, ((0, QColor("#0c1510")), (0.8, QColor("#050a07")),
                                             (0.93, QColor(58, 56, 48)), (1, QColor(22, 22, 20)))))
        p.drawEllipse(QPointF(c, c), s * 0.455, s * 0.455)

    def paint_phosphor(self, p):
        c, outer, cap = self.c, self.outer, self.cap
        p.setClipPath(ring(c, c, cap * 0.9, outer))
        k = cap / outer
        p.setBrush(radial(c, c, outer, ((0, QColor("#d6ffb0")), (k, QColor("#c2ff9e")), (k + 0.14, QColor("#7cff98")),
                                        (0.72, QColor("#2ee874")), (0.9, QColor("#0d9e48")), (1, QColor("#043d1c")))))
        p.drawEllipse(QPointF(c, c), outer, outer)
        # Fine radial ribs of the target anode.
        rng = random.Random(6)
        for i in range(240):
            angle = 2 * math.pi * i / 240 + rng.uniform(-0.01, 0.01)
            light = rng.random() < 0.45
            p.setPen(QPen(QColor(235, 255, 230, rng.randint(14, 34)) if light else QColor(0, 45, 18, rng.randint(22, 55)), 0.7))
            start = cap + rng.uniform(0, 3)
            p.drawLine(QPointF(c + start * math.cos(angle), c + start * math.sin(angle)),
                       QPointF(c + outer * math.cos(angle), c + outer * math.sin(angle)))

    def paint_cap(self, p):
        c, cap = self.c, self.cap
        p.setBrush(radial(c, c, cap, ((0, QColor("#2c312e")), (0.7, QColor("#0d100e")), (1, QColor("#020302"))),
                          c - cap * 0.35, c - cap * 0.4))
        p.drawEllipse(QPointF(c, c), cap, cap)
        sheen = QConicalGradient(QPointF(c, c), 135)
        for position, alpha in ((0, 50), (0.12, 0), (0.5, 22), (0.62, 0), (1, 50)):
            sheen.setColorAt(position, QColor(190, 200, 195, alpha))
        p.setBrush(sheen)
        p.drawEllipse(QPointF(c, c), cap * 0.92, cap * 0.92)

    def paint_glass(self, p):
        c, s = self.c, self.width()
        # Curvature: the glass darkens towards its rim.
        p.setBrush(radial(c, c, s * 0.455, ((0, QColor(0, 0, 0, 0)), (0.82, QColor(0, 0, 0, 0)), (1, QColor(0, 0, 0, 120)))))
        p.drawEllipse(QPointF(c, c), s * 0.455, s * 0.455)
        # Broad window reflection, top left.
        p.setBrush(radial(c - s * 0.15, c - s * 0.19, s * 0.24, ((0, QColor(255, 255, 255, 62)), (1, QColor(255, 255, 255, 0)))))
        p.drawEllipse(QPointF(c - s * 0.15, c - s * 0.19), s * 0.24, s * 0.19)
        # Small sharp highlight.
        p.save()
        p.translate(c - s * 0.2, c - s * 0.25)
        p.rotate(-38)
        p.setBrush(radial(0, 0, s * 0.06, ((0, QColor(255, 255, 255, 190)), (1, QColor(255, 255, 255, 0)))))
        p.drawEllipse(QPointF(0, 0), s * 0.065, s * 0.028)
        p.restore()
        # Faint crescent reflection along the lower right edge.
        p.setPen(QPen(QColor(255, 255, 255, 34), s * 0.012, Qt.SolidLine, Qt.RoundCap))
        p.setBrush(Qt.NoBrush)
        p.drawArc(QRectF(c - s * 0.42, c - s * 0.42, s * 0.84, s * 0.84), -70 * 16, 55 * 16)

    # ---- moving parts
    def shadow(self):
        # Two wedges, top and bottom, with soft edges that fluoresce slightly brighter.
        half = 2 + (1 - self.value) * 48
        soft = 5
        gradient = QConicalGradient(QPointF(self.c, self.c), 0)
        for degree in range(0, 361, 1):
            distance = min(abs(degree - 90), abs(degree - 270))
            if distance < half - soft / 2:
                color = QColor(3, 24, 11, 226)
            elif distance < half + soft / 2:
                t = (distance - (half - soft / 2)) / soft
                color = QColor(3, 24, 11, round(226 * (1 - t * t * (3 - 2 * t))))
            elif distance < half + soft / 2 + 4:
                color = QColor(215, 255, 205, round(60 * (1 - (distance - half - soft / 2) / 4)))
            else:
                color = QColor(0, 0, 0, 0)
            gradient.setColorAt(degree / 360, color)
        return gradient

    def paintEvent(self, event):
        c, outer, cap = self.c, self.outer, self.cap
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.drawPixmap(0, 0, self.base)
        if self.light > 0.01:
            p.setPen(Qt.NoPen)
            p.setOpacity(self.light)
            # Glow scattered in the glass around the target.
            p.setBrush(radial(c, c, outer * 1.16, ((0.8, QColor(40, 255, 120, 90)), (1, QColor(40, 255, 120, 0)))))
            p.drawEllipse(QPointF(c, c), outer * 1.16, outer * 1.16)
            p.drawPixmap(0, 0, self.phosphor)
            p.setOpacity(1)
            p.setClipPath(ring(c, c, cap * 0.9, outer + 1))
            p.setBrush(self.shadow())
            p.drawEllipse(QPointF(c, c), outer + 1, outer + 1)
            p.setClipping(False)
        p.drawPixmap(0, 0, self.cap_layer)
        if self.light > 0.01:
            # Green light reflected on the cap's edge.
            p.setPen(QPen(QColor(110, 255, 150, round(110 * self.light)), 1.2))
            p.setBrush(Qt.NoBrush)
            p.drawEllipse(QPointF(c, c), cap - 0.6, cap - 0.6)
        p.drawPixmap(0, 0, self.glass)
