"""Layout scale: the interface is designed for 1024 x 600 and scaled to other screens. GPL-3.0-or-later.

Set once at start, before any widget is built: 1280 x 720 gives 1.2, 800 x 480 gives 0.78.
"""
import re

DESIGN_WIDTH, DESIGN_HEIGHT = 1024, 600
scale = 1.0


def set_for(width, height):
    global scale
    scale = min(width / DESIGN_WIDTH, height / DESIGN_HEIGHT)


def px(value):
    return round(value * scale)


def style(sheet):
    return re.sub(r"(\d+)px", lambda match: "%dpx" % px(int(match.group(1))), sheet)
