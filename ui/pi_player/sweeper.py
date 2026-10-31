"""Local spectrum from the stick: runs rtl_sweep and converts its sweeps. GPL-3.0-or-later."""
import statistics
import subprocess
import threading
from PyQt5.QtCore import QObject, pyqtSignal
from receiver import exited, end_with_player

# The spectrum view and the signal finder work on the BATC scale (16-bit values).
# Put the local noise floor where BATC's sits and give each dB a fixed height.
NOISE_LEVEL = 13000
PER_DB = 1100
MIN_RISE = 3 * PER_DB   # the flat local floor needs a clearer rise than BATC's


def to_view_scale(tenths_db):
    db = [value / 10 for value in tenths_db]
    noise = statistics.median(db)
    return tuple(max(0, min(65535, round(NOISE_LEVEL + (value - noise) * PER_DB))) for value in db)


class Sweeper(QObject):
    frame = pyqtSignal(int, object)
    failed = pyqtSignal(int, str)

    def __init__(self, root):
        super().__init__()
        self.root = root
        self.process = None
        self.generation = 0

    @property
    def running(self):
        return self.process is not None

    def start(self, start_hz, end_hz, gain_db):
        self.stop()
        self.generation += 1
        process = subprocess.Popen([str(self.root / "build/linux/rtl_sweep"), "%d" % start_hz, "%d" % end_hz, "%.1f" % gain_db],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.root, preexec_fn=end_with_player)
        self.process = process
        threading.Thread(target=self.read, args=(process, self.generation), daemon=True).start()
        threading.Thread(target=self.log, args=(process,), daemon=True).start()
        return self.generation

    def read(self, process, generation):
        for raw in iter(process.stdout.readline, b""):
            parts = raw.split(b" ", 4)
            if len(parts) == 5 and parts[0] == b"sweep":
                try:
                    values = [int(value) for value in parts[4].split(b",")]
                except ValueError:
                    continue
                self.frame.emit(generation, to_view_scale(values))
        if process is self.process:   # ended without being stopped
            self.failed.emit(generation, "Spectrum scan stopped")

    def log(self, process):
        with (self.root / "test-output/pi-player-sweep.log").open("w") as log:
            for raw in iter(process.stderr.readline, b""):
                log.write(raw.decode("utf-8", errors="replace"))
                log.flush()

    def stop(self):
        process, self.process = self.process, None
        if process is None:
            return
        if process.poll() is None:
            process.terminate()
        if not exited(process, 3):
            process.kill()
            exited(process, 2)
