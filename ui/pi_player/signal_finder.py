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
MIN_RISE = 2000   # how far a carrier must stand above the noise floor, in frame units


@dataclass(frozen=True)
class Signal:
    center_mhz: float
    symbol_rate_ks: int


def smooth(frame):
    # Seven-bin moving average, as in the original finder.
    sums = [0]
    for value in frame:
        sums.append(sums[-1] + value)
    n = len(frame)
    return [(sums[min(n, i + 4)] - sums[max(0, i - 3)]) / (min(n, i + 4) - max(0, i - 3)) for i in range(n)]


def measure(values, noise, step, peak, start=START_MHZ, beacon=BEACON_MHZ):
    threshold = noise + (values[peak] - noise) / 2
    left = right = peak
    while left > 0 and values[left - 1] > threshold:
        left -= 1
    while right < len(values) - 1 and values[right + 1] > threshold:
        right += 1
    center = start + (left + right) / 2 * step
    width_ks = (right - left + 1) * step * 1000
    rate = min(SYMBOL_RATES, key=lambda value: abs(math.log(value / width_ks)))
    if beacon and abs(center - beacon) < 0.3:
        center, rate = beacon, 1500
    return Signal(center, rate)


def find_signal(frame, clicked_mhz, min_rise=MIN_RISE, start=START_MHZ, end=END_MHZ, beacon=BEACON_MHZ, radius_mhz=0.1):
    if len(frame) < 16 or not math.isfinite(clicked_mhz) or not start <= clicked_mhz <= end:
        return None
    step = (end - start) / (len(frame) - 1)
    values = smooth(frame)
    noise = statistics.median(values)
    clicked_bin = round((clicked_mhz - start) / step)
    radius = max(1, round(radius_mhz / step))
    peak = max(range(max(0, clicked_bin - radius), min(len(frame), clicked_bin + radius + 1)), key=values.__getitem__)
    if values[peak] - noise < min_rise:
        return None
    return measure(values, noise, step, peak, start, beacon)


def detect_signals(frame, min_rise=MIN_RISE, start=START_MHZ, end=END_MHZ, beacon=BEACON_MHZ):
    """All carriers in a frame, for the spectrum markers and finger-tolerant tapping."""
    if len(frame) < 16:
        return []
    step = (end - start) / (len(frame) - 1)
    values = smooth(frame)
    noise = statistics.median(values)
    signals = []
    i = 0
    while i < len(values):
        if values[i] - noise < min_rise:
            i += 1
            continue
        last = i
        while last + 1 < len(values) and values[last + 1] - noise >= min_rise:
            last += 1
        # A rise touching either end of the frame is the transponder edge, not a carrier.
        if 0 < i and last < len(values) - 1:
            signal = measure(values, noise, step, max(range(i, last + 1), key=values.__getitem__), start, beacon)
            if not signals or abs(signals[-1].center_mhz - signal.center_mhz) > 0.05:
                signals.append(signal)
        i = last + 1
    return signals


def nearest_signal(signals, tapped_mhz, reach_mhz=0.25):
    """The marked signal whose band is closest to a fingertip, if within reach."""
    distance = lambda signal: max(0, abs(signal.center_mhz - tapped_mhz) - signal.symbol_rate_ks * 1.35 / 2000)
    best = min(signals, key=distance, default=None)
    return best if best is not None and distance(best) <= reach_mhz else None
