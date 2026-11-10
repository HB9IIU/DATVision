"""Receiving bands: QO-100 through the LNB, terrestrial bands direct from the antenna. GPL-3.0-or-later.

The terrestrial segments start from BATC's published DATV activity (4 m around
71 MHz, the narrowband segment at 146 MHz, 437 MHz on 70 cm) and are meant to
be adjusted while testing. 23 cm follows the IARU Region 1 ATV segments
(1243.25-1260 and 1272-1291 MHz), which hold BATC's 1255 MHz simplex and
1248-1249 MHz repeater inputs; 23 cm A runs on to 1275 MHz to take in 1265 MHz.
10 m, 6 m, 2 m from 144 MHz and 13 cm around 2344 MHz follow ranges suggested by Benno.
"""
from dataclasses import dataclass

# What each receiver tunes itself, after the LNB or a downconverter: the stick 24 MHz to 1766 MHz,
# an ADALM-Pluto or FISHBall 47 MHz to 6 GHz. So the HF bands need the stick, 13 cm and 6 cm direct need the Pluto.
PLUTO_MIN_MHZ, PLUTO_MAX_MHZ = 47.0, 6000.0
RTL_MIN_MHZ, RTL_MAX_MHZ = 24.0, 1766.0
QO100_LNB_MHZ = 9750.0


@dataclass(frozen=True)
class Band:
    key: str
    name: str
    start_mhz: float
    end_mhz: float
    via_lnb: bool = False     # QO-100: shifted by the calibrated LNB oscillator
    converter_los: tuple = () # downconverter LO choices in MHz, first the default
    converter_names: tuple = ()
    beacon_mhz: float = 0     # a known reference signal, 0 for none
    gain_db: float = 40.2     # starting RF gain; one of the tuner's steps
    note: str = ""            # the third line of the band tile; empty = how it is received (LNB, converter, antenna)

    def receiver_range(self):
        """(lowest, highest) MHz the receiver itself has to tune for this band."""
        shift = QO100_LNB_MHZ if self.via_lnb else self.converter_los[0] if self.converter_los else 0.0
        return abs(self.start_mhz - shift), abs(self.end_mhz - shift)

    def usable(self, pluto):
        """Whether the chosen receiver (the Pluto, else the RTL-SDR stick) can tune this band."""
        low, high = sorted(self.receiver_range())
        return low >= (PLUTO_MIN_MHZ if pluto else RTL_MIN_MHZ) and high <= (PLUTO_MAX_MHZ if pluto else RTL_MAX_MHZ)


BANDS = (
    Band("qo100", "QO-100", 10490.5, 10499.5, via_lnb=True, beacon_mhz=10491.5),
    Band("10m", "10 m", 28.0, 30.0, gain_db=32.8),
    Band("6m", "6 m", 50.0, 52.0, gain_db=32.8),
    Band("4m", "4 m", 70.0, 72.0, gain_db=32.8),
    Band("2m", "2 m", 143.0, 147.0, gain_db=32.8),
    Band("70cm", "70 cm", 430.0, 440.0, gain_db=32.8),
    Band("23cm-a", "23 cm A", 1243.0, 1275.0, gain_db=36.4),
    Band("23cm-b", "23 cm B", 1272.0, 1291.0, gain_db=36.4),
    Band("13cm", "13 cm", 2300.0, 2450.0, gain_db=36.4),   # the whole 13 cm amateur band
    # 5.7 GHz through a downconverter (ON1AVO): the stick receives RF - LO. Activity
    # centres on 5705 MHz; IF 490-520 MHz with an RX5808 (LO 5200), 690-720 MHz with
    # a DG0VE KON-DWN-5560 at its standard 5000 MHz LO (4700 and 4600 selectable).
    Band("6cm-direct", "6 cm", 5690.0, 5720.0, gain_db=36.4, note="Pluto, no converter"),   # a Pluto tunes 5.7 GHz itself
    Band("6cm", "6 cm + converter", 5690.0, 5720.0, gain_db=36.4, converter_los=(5200.0, 5000.0, 4700.0, 4600.0),
         converter_names=("RX5808", "KON-DWN-5560", "KON-DWN-5560", "KON-DWN-5560")),
)
BY_KEY = {band.key: band for band in BANDS}

# Bands wider than this have the pan / zoom controls: a tap recentres the view on the finger
# (the first tap from the whole band zooms to FIRST_ZOOM_MHZ, or a third of a narrower band),
# a tap on the middle tunes; the buttons - + FULL zoom by a factor of three. The view is never
# narrower than MIN_VIEW_MHZ.
ZOOM_ABOVE_MHZ = 10
FIRST_ZOOM_MHZ = 15
MIN_VIEW_MHZ = 3
