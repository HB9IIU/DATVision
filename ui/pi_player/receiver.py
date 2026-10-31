"""Local Pi capture/decoder lifecycle. The transport stream stays on this Pi."""
import socket
import subprocess
import threading
from PyQt5.QtCore import QObject, pyqtSignal


class Receiver(QObject):
    status = pyqtSignal(int, str)
    failed = pyqtSignal(int, str)

    def __init__(self, root):
        super().__init__()
        self.root = root
        self.session = None
        self.generation = 0

    def tune(self, signal, lnb_mhz, port):
        self.stop()
        frequency = round((signal.center_mhz - lnb_mhz) * 1e6)
        if not 24000000 <= frequency <= 1766000000 or not 25 <= signal.symbol_rate_ks <= 1500:
            raise ValueError("This RTL-SDR setup supports 25–1500 kS/s and a valid LNB frequency")
        rate = 2560000 if signal.symbol_rate_ks >= 1250 else 2400000
        self.generation += 1
        session = {"id": self.generation, "stop": threading.Event(), "processes": []}
        self.session = session
        try:
            source = subprocess.Popen([str(self.root / "build/linux/rtl_iq"), str(frequency), str(rate), "40.2"],
                                      stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.root)
            session["processes"].append(source)
            decoder = subprocess.Popen([str(self.root / "build/linux/datvrx"), "-", "--rate", str(rate / 1e6),
                                        "--format", "cu8", "--rs", str(signal.symbol_rate_ks / 1000),
                                        "--std", "0", "--roll", "0.25", "--no-realtime"],
                                       stdin=source.stdout, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.root)
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
                udp.sendto(data, ("127.0.0.1", port))
        except OSError:
            pass
        finally:
            udp.close()
            if not session["stop"].is_set():
                self.failed.emit(session["id"], "Reception ended · tap a signal to retry")

    def stop(self):
        session, self.session = self.session, None
        if session is None:
            return
        session["stop"].set()
        for process in session["processes"]:
            if process.poll() is None:
                process.terminate()
        for process in session["processes"]:
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)
