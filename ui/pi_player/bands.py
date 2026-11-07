"""Receiving bands: QO-100 through the LNB, terrestrial bands direct from the antenna. GPL-3.0-or-later.

The terrestrial segments start from BATC's published DATV activity (4 m around
71 MHz, the narrowband segment at 146 MHz, 437 MHz on 70 cm) and are meant to
be adjusted while testing. 23 cm follows the IARU Region 1 ATV segments
(1243.25-1260 and 1272-1291 MHz), which hold BATC's 1255 MHz simplex and
1248-1249 MHz repeater inputs; 23 cm A runs on to 1275 MHz to take in 1265 MHz.
10 m, 6 m, 2 m from 144 MHz and 13 cm around 2344 MHz follow ranges suggested by Benno.
"""
from dataclasses import dataclass

PLUTO_MIN_MHZ = 47.0   # the lowest frequency an ADALM-Pluto tunes here


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

    def pluto_ok(self):
        """The Pluto tunes from 47 MHz: the HF bands need the RTL-SDR stick."""
        return self.via_lnb or bool(self.converter_los) or self.start_mhz >= PLUTO_MIN_MHZ


BANDS = (
    Band("qo100", "QO-100", 10490.5, 10499.5, via_lnb=True, beacon_mhz=10491.5),
    Band("10m", "10 m", 28.0, 30.0, gain_db=32.8),
    Band("6m", "6 m", 50.0, 52.0, gain_db=32.8),
    Band("4m", "4 m", 70.5, 71.5, gain_db=32.8),
    Band("2m", "2 m", 144.0, 147.0, gain_db=32.8),
    Band("70cm", "70 cm", 435.0, 439.0, gain_db=32.8),
    Band("23cm-a", "23 cm A", 1243.0, 1275.0, gain_db=36.4),
    Band("23cm-b", "23 cm B", 1272.0, 1291.0, gain_db=36.4),
    Band("13cm", "13 cm", 2330.0, 2360.0, gain_db=36.4),
    # 5.7 GHz through a downconverter (ON1AVO): the stick receives RF - LO. Activity
    # centres on 5705 MHz; IF 490-520 MHz with an RX5808 (LO 5200), 690-720 MHz with
    # a DG0VE KON-DWN-5560 at its standard 5000 MHz LO (4700 and 4600 selectable).
    Band("6cm", "6 cm", 5690.0, 5720.0, gain_db=36.4, converter_los=(5200.0, 5000.0, 4700.0, 4600.0),
         converter_names=("RX5808", "KON-DWN-5560", "KON-DWN-5560", "KON-DWN-5560")),
)
BY_KEY = {band.key: band for band in BANDS}

# On bands wider than this, the first tap zooms into a window around the finger
# and the second tap tunes.
ZOOM_ABOVE_MHZ = 10
ZOOM_MHZ = 3
