"""Video through mpv, embedded in the player's X11 video area. GPL-3.0-or-later.

Raspberry Pi's VLC cannot display the QO-100 beacon (H.264 1280x720 at 50 fps)
on a Pi 4 ("failed to create video converter"); mpv with the same software
decoder plays it smoothly there. One mpv process per stream, driven through its
JSON IPC socket; it has the same interface as video.Video.
"""
import ctypes
import json
import os
import shutil
import signal
import socket
import subprocess
import threading
from receiver import exited

AVAILABLE = shutil.which("mpv") is not None


def killed_with_player():
    # mpv can hang in its own shutdown; a leftover one keeps drawing into the reused
    # window id and blacks out the next player's video. Make the kernel kill it.
    ctypes.CDLL(None, use_errno=True).prctl(1, signal.SIGKILL)   # PR_SET_PDEATHSIG


class MpvVideo:
    def __init__(self, window_id, log_path, max_fps=None):
        self.window_id = int(window_id)
        self.output = ["--vo=gpu", "--gpu-context=x11egl"]
        if max_fps:
            # A Pi 4 stays black with the beacon's 50 pictures/s; shown at 25 it plays.
            # Other DATV streams already run at 25 or less.
            self.output.append("--vf=fps=fps=%d" % max_fps)
        self.log_path = log_path
        self.socket_path = "/tmp/pi-datv-rx-mpv-%d.sock" % os.getpid()
        self.process = None
        self.wanted_volume = 50

    def play(self, port, volume):
        self.start("udp://127.0.0.1:%d?overrun_nonfatal=1&fifo_size=1000000" % port, volume)

    def play_file(self, path, volume):
        self.start(str(path), volume)

    @property
    def ended(self):
        # A recording played to its end: mpv exits by itself.
        return self.process is not None and self.process.poll() is not None

    def start(self, source, volume):
        self.stop()
        self.wanted_volume = volume
        options = os.environ.get("DATVRX_MPV_OPTIONS", "").split()   # diagnosis, e.g. --vo=x11
        with open(self.log_path, "w") as log:
            self.process = subprocess.Popen(
                ["mpv", "--no-config", "--wid=%d" % self.window_id, "--input-ipc-server=" + self.socket_path,
                 # Taps on the picture must reach the player, not mpv.
                 "--no-input-default-bindings", "--input-cursor=no", "--input-vo-keyboard=no",
                 "--osc=no", "--osd-level=0", "--hwdec=no"] + self.output + [
                 "--volume=%d" % max(0, min(100, volume)), "--volume-max=100",
                 "--demuxer-lavf-format=mpegts", "--msg-level=all=warn"] + options + [source],
                stdin=subprocess.DEVNULL, stdout=log, stderr=log, preexec_fn=killed_with_player)

    def request(self, *command):
        # Short timeout: the interface asks every second and must never wait on mpv.
        try:
            with socket.socket(socket.AF_UNIX) as connection:
                connection.settimeout(0.15)
                connection.connect(self.socket_path)
                connection.sendall((json.dumps({"command": list(command)}) + "\n").encode())
                data = b""
                while not data.endswith(b"\n"):
                    chunk = connection.recv(4096)
                    if not chunk:
                        break
                    data += chunk
            for line in data.splitlines():
                reply = json.loads(line)
                if "error" in reply:
                    return reply.get("data") if reply["error"] == "success" else None
        except (OSError, ValueError):
            return None
        return None

    def volume(self, volume):
        self.wanted_volume = volume
        if self.process:
            self.request("set_property", "volume", max(0, min(100, volume)))

    def size(self):
        if not self.process:
            return 0, 0
        return self.request("get_property", "width") or 0, self.request("get_property", "height") or 0

    def has_picture(self):
        return all(self.size())

    def stats(self):
        """(decoded, displayed, lost) video pictures of the current stream, or None."""
        if not self.process:
            return None
        frames = self.request("get_property", "estimated-frame-number")
        if frames is None:
            return None
        dropped = (self.request("get_property", "frame-drop-count") or 0) + \
                  (self.request("get_property", "decoder-frame-drop-count") or 0)
        return frames, max(0, frames - dropped), dropped

    def stop(self):
        # Ending mpv is quick, but reap it in the background so tuning never waits.
        process, self.process = self.process, None
        if process is None:
            return
        process.terminate()
        threading.Thread(target=self.reap, args=(process,), daemon=True).start()

    def reap(self, process):
        if not exited(process, 2):
            process.kill()
            exited(process, 2)

    def close(self):
        process, self.process = self.process, None
        if process is not None:
            process.kill()
