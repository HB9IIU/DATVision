"""Service and stream names from the decoded transport stream (PAT, PMT, SDT). GPL-3.0-or-later.

Only single-packet sections are read; DATV tables normally fit in one packet.
"""
import re

VIDEO_TYPES = {0x01: "MPEG-1", 0x02: "MPEG-2", 0x10: "MPEG-4", 0x1B: "H.264", 0x24: "H.265", 0x33: "H.266"}
AUDIO_TYPES = {0x03: "MPEG audio", 0x04: "MPEG audio", 0x0F: "AAC", 0x11: "AAC", 0x81: "AC-3", 0x87: "E-AC-3"}


class TsInfo:
    def __init__(self):
        self.pmt_pid = None
        self.service = ""
        self.provider = ""
        self.video = ""
        self.audio = ""

    def feed(self, data):
        for offset in range(0, len(data) - 187, 188):
            if data[offset] != 0x47 or not data[offset + 1] & 0x40:
                continue
            pid = (data[offset + 1] & 0x1F) << 8 | data[offset + 2]
            if pid not in (0, 0x11, self.pmt_pid):
                continue
            control = data[offset + 3] >> 4 & 3
            start = offset + 4 + (1 + data[offset + 4] if control & 2 else 0)
            if not control & 1 or start >= offset + 188:
                continue
            payload = data[start:offset + 188]
            section = payload[1 + payload[0]:]
            if len(section) < 3:
                continue
            length = (section[1] & 0x0F) << 8 | section[2]
            if length < 9 or length + 3 > len(section):
                continue
            section = section[:length + 3]
            if pid == 0 and section[0] == 0x00:
                self._pat(section)
            elif pid == 0x11 and section[0] == 0x42:
                self._sdt(section)
            elif pid == self.pmt_pid and section[0] == 0x02:
                self._pmt(section)

    def _pat(self, section):
        for i in range(8, len(section) - 7, 4):
            if section[i] << 8 | section[i + 1]:
                self.pmt_pid = (section[i + 2] & 0x1F) << 8 | section[i + 3]
                return

    def _pmt(self, section):
        video = audio = ""
        i = 12 + ((section[10] & 0x0F) << 8 | section[11])
        while i + 5 <= len(section) - 4:
            kind = section[i]
            size = (section[i + 3] & 0x0F) << 8 | section[i + 4]
            video = video or VIDEO_TYPES.get(kind, "")
            audio = audio or AUDIO_TYPES.get(kind, "") or (private_audio(section[i + 5:i + 5 + size]) if kind == 0x06 else "")
            i += 5 + size
        self.video, self.audio = video, audio

    def _sdt(self, section):
        i = 11
        while i + 5 <= len(section) - 4:
            size = (section[i + 3] & 0x0F) << 8 | section[i + 4]
            j, end = i + 5, min(i + 5 + size, len(section) - 4)
            while j + 2 <= end:
                tag, body = section[j], section[j + 2:j + 2 + section[j + 1]]
                if tag == 0x48 and len(body) >= 3:
                    provider = body[2:2 + body[1]]
                    rest = body[2 + body[1]:]
                    self.provider = text(provider)
                    self.service = text(rest[1:1 + rest[0]]) if rest else ""
                    return
                j += 2 + section[j + 1]
            i += 5 + size


CALLSIGN = re.compile(r"(?<![A-Z0-9])[A-Z0-9]{1,3}[0-9][A-Z]{1,4}(?![A-Z0-9])")


def callsign(service_name):
    """The callsign in a DVB service name, as the Windows version finds it; '' if none."""
    found = CALLSIGN.search(service_name.upper())
    return found.group(0) if found else ""


def private_audio(descriptors):
    i = 0
    while i + 2 <= len(descriptors):
        tag, body = descriptors[i], descriptors[i + 2:i + 2 + descriptors[i + 1]]
        if tag == 0x6A:
            return "AC-3"
        if tag == 0x7A:
            return "E-AC-3"
        if tag == 0x05 and body[:4] == b"Opus":
            return "Opus"
        i += 2 + descriptors[i + 1]
    return ""


def text(raw):
    # DVB strings may start with a character-table selector; callsigns are plain ASCII.
    if raw and raw[0] < 0x20:
        raw = raw[3:] if raw[0] == 0x10 else raw[1:]
    return "".join(c for c in bytes(raw).decode("latin-1") if c.isprintable()).strip()
