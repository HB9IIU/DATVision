"""Pure tuning and stream-info checks; no desktop, network or SDR needed."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ui/pi_player"))
from signal_finder import START_MHZ, END_MHZ, find_signal, detect_signals, nearest_signal
from ts_info import TsInfo, callsign
import calibration
from sweeper import to_view_scale, NOISE_LEVEL, PER_DB, MIN_RISE


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

    def test_detects_every_carrier_and_snaps_taps(self):
        frame = [max(a, b) for a, b in zip(carrier(10491.5, 1500), carrier(10497.25, 333))]
        signals = detect_signals(frame)
        self.assertEqual([s.symbol_rate_ks for s in signals], [1500, 333])
        self.assertEqual(nearest_signal(signals, 10497.55).symbol_rate_ks, 333)
        self.assertIsNone(nearest_signal(signals, 10495.0))
        self.assertEqual(detect_signals(()), [])


def packet(pid, section):
    data = bytes((0x47, 0x40 | pid >> 8, pid & 0xFF, 0x10, 0)) + section
    return data + b"\xff" * (188 - len(data))


def table(table_id, body):
    return bytes((table_id, 0xB0 | (len(body) + 4) >> 8, (len(body) + 4) & 0xFF)) + body + b"\0\0\0\0"


class TsInfoChecks(unittest.TestCase):
    def test_service_and_streams(self):
        name, provider = b"\x15HB9IIU", b"QO-100"
        service = bytes((0x48, 3 + len(provider) + len(name), 1, len(provider))) + provider + bytes((len(name),)) + name
        sdt = table(0x42, bytes((0, 1, 0xC1, 0, 0, 0, 1, 0xFF, 0, 1, 0xFC, 0x80 | len(service) >> 8, len(service) & 0xFF)) + service)
        pat = table(0x00, bytes((0, 1, 0xC1, 0, 0, 0, 1, 0xE0, 0x20)))
        pmt = table(0x02, bytes((0, 1, 0xC1, 0, 0, 0xE1, 0, 0xF0, 0, 0x24, 0xE1, 0, 0xF0, 0, 0x0F, 0xE1, 1, 0xF0, 0)))
        info = TsInfo()
        info.feed(packet(0x11, sdt) + packet(0, pat) + packet(0x20, pmt) + b"\x00" * 188)
        self.assertEqual((info.service, info.provider), ("HB9IIU", "QO-100"))
        self.assertEqual((info.video, info.audio), ("H.265", "AAC"))


class CalibrationChecks(unittest.TestCase):
    def test_detuned_steps_agree_on_the_true_oscillator(self):
        true_lnb, assumed = 9750.054, 9750.0
        estimates = []
        for detune in calibration.STEPS_KHZ:
            offset = (assumed - true_lnb) * 1e6 - detune * 1000   # what the receiver would track
            estimates.append(calibration.estimate(assumed, [offset - 8, offset, offset + 8], detune))
        value, spread = calibration.summarize(estimates)
        self.assertAlmostEqual(value, true_lnb, places=4)
        self.assertLess(spread, 0.1)

    def test_too_few_good_steps(self):
        self.assertIsNone(calibration.summarize([9750.05, None, None, 9750.05, None]))


class LocalSpectrumChecks(unittest.TestCase):
    def test_local_sweep_is_found_like_batc_data(self):
        # 3841 bins over the QO-100 range: noise at 15 dB, a 333 kS/s carrier 8 dB above.
        bins = 3841
        tenths = [150] * bins
        for i in range(bins):
            if abs(START_MHZ + i * (END_MHZ - START_MHZ) / (bins - 1) - 10497.25) < 0.17:
                tenths[i] = 230
        frame = to_view_scale(tenths)
        self.assertEqual(frame[0], NOISE_LEVEL)
        self.assertEqual(max(frame), NOISE_LEVEL + 8 * PER_DB)
        signal = find_signal(frame, 10497.3)
        self.assertEqual(signal.symbol_rate_ks, 333)
        self.assertAlmostEqual(signal.center_mhz, 10497.25, delta=0.01)

    def test_band_edge_bump_is_not_a_signal(self):
        bins = 3841
        tenths = [150 + (20 if i > bins * 0.95 else 0) for i in range(bins)]   # a 2 dB rise at the top edge
        self.assertIsNone(find_signal(to_view_scale(tenths), 10499.1, MIN_RISE))


class BandChecks(unittest.TestCase):
    def test_terrestrial_band_has_no_beacon_snap(self):
        import bands
        band = bands.BY_KEY["70cm"]
        bins = 1707   # 4 MHz at the sweep's bin width
        tenths = [150] * bins
        for i in range(bins):
            if abs(band.start_mhz + i * (band.end_mhz - band.start_mhz) / (bins - 1) - 437.0) < 0.17:
                tenths[i] = 230
        signal = find_signal(to_view_scale(tenths), 437.05, MIN_RISE, band.start_mhz, band.end_mhz, band.beacon_mhz)
        self.assertEqual(signal.symbol_rate_ks, 333)
        self.assertAlmostEqual(signal.center_mhz, 437.0, delta=0.01)
        self.assertTrue(all(band.gain_db in __import__("panels").GAINS for band in bands.BANDS))

    def test_6cm_converter_if_ranges(self):
        import bands
        band = bands.BY_KEY["6cm"]
        self.assertEqual(5705 - band.converter_los[0], 505)   # ON1AVO's example: RX5808, LO 5200
        for lo in band.converter_los:   # every choice stays receivable by the stick
            self.assertTrue(24 <= band.start_mhz - lo and band.end_mhz - lo <= 1766)
        # Within the IF outputs Freddy gives: RX5808 450-600 MHz, KON-DWN-5560 at 5000 MHz 650-800 MHz.
        self.assertTrue(450 <= band.start_mhz - 5200 and band.end_mhz - 5200 <= 600)
        self.assertTrue(650 <= band.start_mhz - 5000 and band.end_mhz - 5000 <= 800)



class CallsignChecks(unittest.TestCase):
    def test_callsign_from_service_names(self):
        self.assertEqual(callsign("HB9IIU"), "HB9IIU")
        self.assertEqual(callsign("a71a"), "A71A")
        self.assertEqual(callsign("DK0TE PlutoDVB"), "DK0TE")
        self.assertEqual(callsign("QO-100 beacon"), "")



class NetworkStreamChecks(unittest.TestCase):
    def test_destinations_as_in_the_windows_version(self):
        from receiver import udp_endpoint, vlc_address, DEFAULT_STREAM
        self.assertEqual(vlc_address(udp_endpoint(DEFAULT_STREAM)), "udp://@239.255.42.42:1234")
        self.assertEqual(vlc_address(udp_endpoint("udp://@192.168.0.20:5000")), "udp://@:5000")
        for bad in ("udp://0.0.0.0:1234", "udp://255.255.255.255:1234", "udp://1.2.3.4:0", "http://1.2.3.4:80", ""):
            with self.assertRaises(ValueError):
                udp_endpoint(bad)


if __name__ == "__main__":
    unittest.main()
