"""LNB oscillator calibration on the QO-100 beacon. GPL-3.0-or-later.

The beacon is tuned several times, deliberately detuned by known amounts. After
each lock the receiver's tracked carrier offset gives the true oscillator:

    carrier offset = assumed LNB - true LNB - detune
    true LNB       = assumed LNB - carrier offset - detune

The detuned steps must all agree; their spread shows how far to trust the result.
"""
import statistics

STEPS_KHZ = (-100, -50, 0, 50, 100)
SAMPLES = 8           # offset readings per step, four a second
SETTLE_S = 1.0        # ignore readings this soon after the lock
STEP_TIMEOUT_S = 12
MAX_SPREAD_KHZ = 5    # disagreement beyond this means the result is not trusted
MIN_GOOD_STEPS = 3


def estimate(assumed_lnb_mhz, offsets_hz, detune_khz):
    return assumed_lnb_mhz - statistics.median(offsets_hz) / 1e6 - detune_khz / 1000


def summarize(estimates):
    """(LNB MHz, spread kHz) from the good steps, or None when there are too few."""
    good = [value for value in estimates if value is not None]
    if len(good) < MIN_GOOD_STEPS:
        return None
    return round(statistics.mean(good), 4), (max(good) - min(good)) * 1000
