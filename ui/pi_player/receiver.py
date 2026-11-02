"""Local Pi capture/decoder lifecycle. The transport stream stays on this Pi."""
import ctypes
import ipaddress
import re
import signal
import socket
import subprocess
import threading


DEFAULT_STREAM = "udp://239.255.42.42:1234"   # as the Windows version
DEFAULT_PLUTO = "usb:"   # the first Pluto on USB; the network link (ip:192.168.2.1) lost samples under load


def udp_endpoint(text):
    """(address, port) of udp://IPv4:port (udp://@… accepted); ValueError otherwise."""
    found = re.fullmatch(r"udp://@?(\d+\.\d+\.\d+\.\d+):(\d+)", text.strip())
    if not found:
        raise ValueError("udp://IPv4:port expected, for example " + DEFAULT_STREAM)
    address, port = ipaddress.IPv4Address(found.group(1)), int(found.group(2))
    if address.is_unspecified or address == ipaddress.IPv4Address("255.255.255.255") or not 1 <= port <= 65535:
        raise ValueError("not a usable destination: " + text)
    return str(address), port


def vlc_address(endpoint):
    # What to open in VLC on the receiving PC: the group for multicast, else the port.
    address, port = endpoint
    return "udp://@%s:%d" % (address, port) if ipaddress.IPv4Address(address).is_multicast else "udp://@:%d" % port


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
        self.stream_to = None   # (address, port) on the home network, or None
        self.stuck = []

    @property
    def info(self):
        return self.session["info"] if self.session else None

    def tune(self, signal, lnb_mhz, port, gain_db, device="rtl", pluto_uri=DEFAULT_PLUTO):
        self.stop()
        self.stuck = [process for process in self.stuck if process.poll() is None]
        pluto = device == "pluto"
        if self.stuck:
            raise RuntimeError("%s not responding\n\nUnplug it and plug it back in." % ("Pluto" if pluto else "RTL-SDR stick"))
        frequency = round((signal.center_mhz - lnb_mhz) * 1e6)
        low, high = (47000000, 6000000000) if pluto else (24000000, 1766000000)
        if not low <= frequency <= high or not 25 <= signal.symbol_rate_ks <= 1500:
            raise ValueError("This setup supports 25–1500 kS/s and %d–%d MHz after the LNB or converter"
                             % (low // 1000000, high // 1000000))
        # The Pluto's AD936x starts at about 2.08 MS/s; its gain is automatic.
        rate = 2560000 if pluto or signal.symbol_rate_ks >= 1250 else 2400000
        command = ([str(self.root / "build/linux/pluto_iq"), pluto_uri, str(frequency), str(rate), "auto"] if pluto else
                   [str(self.root / "build/linux/rtl_iq"), str(frequency), str(rate), "%.1f" % gain_db])
        self.generation += 1
        session = {"id": self.generation, "stop": threading.Event(), "processes": [], "info": TsInfo()}
        self.session = session
        try:
            source = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.root, preexec_fn=end_with_player)
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
        network = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        # One hop only: the stream stays on the home network.
        network.setsockopt(socket.IPPROTO_IP, socket.IP_TTL, 1)
        network.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)
        network_failed = False
        try:
            while not session["stop"].is_set():
                data = decoder.stdout.read(188 * 7)
                if not data:
                    break
                session["info"].feed(data)
                udp.sendto(data, ("127.0.0.1", port))
                destination = self.stream_to
                if destination and not network_failed:
                    try:
                        network.sendto(data, destination)   # seven whole TS packets, 1316 bytes
                    except OSError as error:
                        network_failed = True
                        self.status.emit(session["id"], "Network stream error: %s" % error.strerror)
                if self.record_file is not None:
                    with self.record_lock:
                        if self.record_file is not None:
                            self.record_file.write(data)
                            self.recorded += len(data)
        except OSError:
            pass
        finally:
            udp.close()
            network.close()
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
