"""Receiving bands: QO-100 through the LNB, terrestrial bands direct from the antenna. GPL-3.0-or-later.

The terrestrial segments start from BATC's published DATV activity (4 m around
71 MHz, the narrowband segment at 146 MHz, 437 MHz on 70 cm) and are meant to
be adjusted while testing. 23 cm follows the IARU Region 1 ATV segments
(1243.25-1260 and 1272-1291 MHz), which hold BATC's 1255 MHz simplex and
1248-1249 MHz repeater inputs.
"""
from dataclasses import dataclass


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


BANDS = (
    Band("qo100", "QO-100", 10490.5, 10499.5, via_lnb=True, beacon_mhz=10491.5),
    Band("4m", "4 m", 70.5, 71.5, gain_db=32.8),
    Band("2m", "2 m", 146.0, 147.0, gain_db=32.8),
    Band("70cm", "70 cm", 435.0, 439.0, gain_db=32.8),
    Band("23cm-a", "23 cm A", 1243.0, 1260.0, gain_db=36.4),
    Band("23cm-b", "23 cm B", 1272.0, 1291.0, gain_db=36.4),
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
