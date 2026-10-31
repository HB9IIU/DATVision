"""Pure tuning checks; no desktop, network or SDR needed."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ui/pi_player"))
from signal_finder import START_MHZ, END_MHZ, find_signal


def carrier(center, rate):
    return tuple(30000 if abs(START_MHZ + i * (END_MHZ - START_MHZ) / 8191 - center) < rate / 2000 else 9000
                 for i in range(8192))


class SignalFinderChecks(unittest.TestCase):
    def test_carrier_estimates(self):
        for center, rate in ((10491.5, 1500), (10492.75, 500), (10498.25, 333), (10499.25, 125)):
            with self.subTest(rate=rate):
                signal = find_signal(carrier(center, rate), center + 0.02)
                self.assertEqual(signal.symbol_rate_ks, rate)
                self.assertAlmostEqual(signal.center_mhz, center, delta=0.01)

    def test_empty_or_blank(self):
        self.assertIsNone(find_signal((), 10494))
        self.assertIsNone(find_signal((0,) * 8192, 10494))

    def test_invalid_clicks(self):
        frame = carrier(10492.75, 500)
        for frequency in (float("nan"), float("inf"), 10489, 10500):
            self.assertIsNone(find_signal(frame, frequency))

    def test_wide_signal_is_identified_for_rejection(self):
        self.assertEqual(find_signal(carrier(10495.0, 2000), 10495.0).symbol_rate_ks, 2000)


if __name__ == "__main__":
    unittest.main()
