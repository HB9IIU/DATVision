"""Adapted from HB9IIU Experimental H.266 Player's GPL-v3 SignalFinder.

https://github.com/HB9IIU/HB9IIU-Experimental-H266-Player
The BATC spectrum is a remote tuning reference, not a local quality measurement.
"""
from dataclasses import dataclass
import math
import statistics

START_MHZ = 10490.5
END_MHZ = 10499.5
BEACON_MHZ = 10491.5
SYMBOL_RATES = (25, 33, 50, 66, 125, 250, 333, 500, 1000, 1500, 2000)


@dataclass(frozen=True)
class Signal:
    center_mhz: float
    symbol_rate_ks: int


def find_signal(frame, clicked_mhz):
    if len(frame) < 16 or not math.isfinite(clicked_mhz) or not START_MHZ <= clicked_mhz <= END_MHZ:
        return None
    step = (END_MHZ - START_MHZ) / (len(frame) - 1)
    values = []
    for i in range(len(frame)):
        samples = frame[max(0, i - 3):min(len(frame), i + 4)]
        values.append(sum(samples) / len(samples))
    noise = statistics.median(values)
    clicked_bin = round((clicked_mhz - START_MHZ) / step)
    radius = max(1, round(0.1 / step))
    peak = max(range(max(0, clicked_bin - radius), min(len(frame), clicked_bin + radius + 1)), key=values.__getitem__)
    if values[peak] - noise < 2000:
        return None
    threshold = noise + (values[peak] - noise) / 2
    left = right = peak
    while left > 0 and values[left - 1] > threshold:
        left -= 1
    while right < len(frame) - 1 and values[right + 1] > threshold:
        right += 1
    center = START_MHZ + (left + right) / 2 * step
    width_ks = (right - left + 1) * step * 1000
    rate = min(SYMBOL_RATES, key=lambda value: abs(math.log(value / width_ks)))
    if abs(center - BEACON_MHZ) < 0.3:
        center, rate = BEACON_MHZ, 1500
    return Signal(center, rate)
