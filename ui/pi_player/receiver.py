"""Local Pi capture/decoder lifecycle. The transport stream stays on this Pi."""
import ctypes
import signal
import socket
import subprocess
import threading


def end_with_player():
    # Runs in each child before it starts: the kernel sends it SIGTERM when the
    # player ends in any way, so no receiver is left holding the stick.
    ctypes.CDLL(None, use_errno=True).prctl(1, signal.SIGTERM)   # PR_SET_PDEATHSIG
from PyQt5.QtCore import QObject, pyqtSignal
from ts_info import TsInfo


class Receiver(QObject):
    status = pyqtSignal(int, str)
    failed = pyqtSignal(int, str)

    def __init__(self, root, realtime=False):
        super().__init__()
        self.root = root
        self.realtime = realtime   # slow Pi: drop a frame when behind rather than lose stick samples
        self.session = None
        self.generation = 0
        # TS recording: the delivery thread writes what it passes to the video too.
        self.record_lock = threading.Lock()
        self.record_file = None
        self.record_path = None
        self.recorded = 0
        self.stuck = []

    @property
    def info(self):
        return self.session["info"] if self.session else None

    def tune(self, signal, lnb_mhz, port, gain_db):
        self.stop()
        self.stuck = [process for process in self.stuck if process.poll() is None]
        if self.stuck:
            raise RuntimeError("RTL-SDR stick not responding\n\nUnplug it and plug it back in.")
        frequency = round((signal.center_mhz - lnb_mhz) * 1e6)
        if not 24000000 <= frequency <= 1766000000 or not 25 <= signal.symbol_rate_ks <= 1500:
            raise ValueError("This RTL-SDR setup supports 25–1500 kS/s and a valid LNB frequency")
        rate = 2560000 if signal.symbol_rate_ks >= 1250 else 2400000
        self.generation += 1
        session = {"id": self.generation, "stop": threading.Event(), "processes": [], "info": TsInfo()}
        self.session = session
        try:
            source = subprocess.Popen([str(self.root / "build/linux/rtl_iq"), str(frequency), str(rate), "%.1f" % gain_db],
                                      stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.root, preexec_fn=end_with_player)
            session["processes"].append(source)
            decoder = subprocess.Popen([str(self.root / "build/linux/datvrx"), "-", "--rate", str(rate / 1e6),
                                        "--format", "cu8", "--rs", str(signal.symbol_rate_ks / 1000),
                                        "--std", "0", "--roll", "0.25", "--realtime" if self.realtime else "--no-realtime", "--cells", "256"],
                                       stdin=source.stdout, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.root,
                                       preexec_fn=end_with_player)
            session["processes"].append(decoder)
            source.stdout.close()
        except Exception:
            self.stop()
            raise
        for process, name in ((source, "radio"), (decoder, "receiver")):
            threading.Thread(target=self.log, args=(session, process, name), daemon=True).start()
        threading.Thread(target=self.deliver, args=(session, decoder, port), daemon=True).start()
        return session["id"]

    def log(self, session, process, name):
        with (self.root / "test-output" / ("pi-player-" + name + ".log")).open("w") as log:
            for raw in iter(process.stderr.readline, b""):
                line = raw.decode("utf-8", errors="replace").strip()
                if not line.startswith("datvrx cells "):   # four a second; only for the display
                    log.write(line + "\n")
                    log.flush()
                if not session["stop"].is_set():
                    self.status.emit(session["id"], line)

    def deliver(self, session, decoder, port):
        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            while not session["stop"].is_set():
                data = decoder.stdout.read(188 * 7)
                if not data:
                    break
                session["info"].feed(data)
                udp.sendto(data, ("127.0.0.1", port))
                if self.record_file is not None:
                    with self.record_lock:
                        if self.record_file is not None:
                            self.record_file.write(data)
                            self.recorded += len(data)
        except OSError:
            pass
        finally:
            udp.close()
            if not session["stop"].is_set():
                self.failed.emit(session["id"], "Reception ended · tap a signal to retry")

    @property
    def recording(self):
        return self.record_file is not None

    def start_recording(self, path):
        with self.record_lock:
            if self.session is None or self.session["stop"].is_set():
                raise RuntimeError("Tune a live station before recording.")
            if self.record_file is not None:
                raise RuntimeError("Already recording.")
            self.record_file = open(path, "wb")
            self.record_path, self.recorded = path, 0

    def stop_recording(self):
        """(path, bytes) of the finished recording, or None."""
        with self.record_lock:
            record_file, self.record_file = self.record_file, None
            if record_file is None:
                return None
            record_file.close()
            return self.record_path, self.recorded

    def stop(self):
        session, self.session = self.session, None
        if session is None:
            return
        session["stop"].set()
        for process in session["processes"]:
            if process.poll() is None:
                process.terminate()
        for process in session["processes"]:
            if not exited(process, 3):
                process.kill()
                if not exited(process, 2):
                    # Stuck in the USB driver: never block or crash the interface on it.
                    self.stuck.append(process)


def exited(process, timeout):
    try:
        process.wait(timeout=timeout)
        return True
    except subprocess.TimeoutExpired:
        return False
