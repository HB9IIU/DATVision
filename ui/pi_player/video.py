"""Small ctypes binding to the system libVLC. Video is embedded in an X11 Qt widget."""
import ctypes as C
import os
from ctypes.util import find_library
import threading


class Stats(C.Structure):
    # libvlc_media_stats_t (VLC 3)
    _fields_ = [("read_bytes", C.c_int), ("input_bitrate", C.c_float), ("demux_read_bytes", C.c_int),
                ("demux_bitrate", C.c_float), ("demux_corrupted", C.c_int), ("demux_discontinuity", C.c_int),
                ("decoded_video", C.c_int), ("decoded_audio", C.c_int), ("displayed_pictures", C.c_int),
                ("lost_pictures", C.c_int), ("played_abuffers", C.c_int), ("lost_abuffers", C.c_int),
                ("sent_packets", C.c_int), ("sent_bytes", C.c_int), ("send_bitrate", C.c_float)]


class Video:
    def __init__(self, window_id):
        self.lib = C.CDLL(find_library("vlc") or "libvlc.so.5")
        signatures = {
            "libvlc_new": ([C.c_int, C.POINTER(C.c_char_p)], C.c_void_p),
            "libvlc_release": ([C.c_void_p], None),
            "libvlc_media_new_location": ([C.c_void_p, C.c_char_p], C.c_void_p),
            "libvlc_media_add_option": ([C.c_void_p, C.c_char_p], None),
            "libvlc_media_release": ([C.c_void_p], None),
            "libvlc_media_player_new": ([C.c_void_p], C.c_void_p),
            "libvlc_media_player_set_media": ([C.c_void_p, C.c_void_p], None),
            "libvlc_media_player_set_xwindow": ([C.c_void_p, C.c_uint32], None),
            "libvlc_media_player_play": ([C.c_void_p], C.c_int),
            "libvlc_media_player_stop": ([C.c_void_p], None),
            "libvlc_media_player_release": ([C.c_void_p], None),
            "libvlc_audio_set_volume": ([C.c_void_p, C.c_int], C.c_int),
            "libvlc_video_set_mouse_input": ([C.c_void_p, C.c_uint], None),
            "libvlc_video_set_key_input": ([C.c_void_p, C.c_uint], None),
            "libvlc_video_get_size": ([C.c_void_p, C.c_uint, C.POINTER(C.c_uint), C.POINTER(C.c_uint)], C.c_int),
            "libvlc_media_player_get_media": ([C.c_void_p], C.c_void_p),
            "libvlc_media_get_stats": ([C.c_void_p, C.POINTER(Stats)], C.c_int),
        }
        for name, (args, result) in signatures.items():
            function = getattr(self.lib, name)
            function.argtypes = args
            function.restype = result
        options = [b"--no-video-title-show", b"--quiet", b"--avcodec-hw=none"]
        # Diagnosis: extra libVLC options, e.g. DATVRX_VLC_OPTIONS="--no-audio".
        options += [option.encode() for option in os.environ.get("DATVRX_VLC_OPTIONS", "").split()]
        self.instance = self.lib.libvlc_new(len(options), (C.c_char_p * len(options))(*options))
        if not self.instance:
            raise RuntimeError("VLC could not initialize")
        self.window_id = int(window_id)
        self.retiring = []
        self.player = self.new_player()

    def new_player(self):
        player = self.lib.libvlc_media_player_new(self.instance)
        self.lib.libvlc_media_player_set_xwindow(player, self.window_id)
        self.lib.libvlc_video_set_mouse_input(player, 0)
        self.lib.libvlc_video_set_key_input(player, 0)
        return player

    def play(self, port, volume):
        media = self.lib.libvlc_media_new_location(self.instance, ("udp://@127.0.0.1:%d" % port).encode())
        for option in (b":demux=ts", b":network-caching=500"):
            self.lib.libvlc_media_add_option(media, option)
        self.lib.libvlc_media_player_set_media(self.player, media)
        self.lib.libvlc_media_release(media)
        if self.lib.libvlc_media_player_play(self.player) < 0:
            raise RuntimeError("VLC could not start playback")
        self.volume(volume)

    def volume(self, volume):
        self.lib.libvlc_audio_set_volume(self.player, max(0, min(100, volume)))

    def size(self):
        width, height = C.c_uint(), C.c_uint()
        self.lib.libvlc_video_get_size(self.player, 0, C.byref(width), C.byref(height))
        return width.value, height.value

    def stats(self):
        """(decoded, displayed, lost) video pictures of the current stream, or None."""
        media = self.lib.libvlc_media_player_get_media(self.player)
        stats = Stats()
        if not media or not self.lib.libvlc_media_get_stats(media, C.byref(stats)):
            if media:
                self.lib.libvlc_media_release(media)
            return None
        self.lib.libvlc_media_release(media)
        return stats.decoded_video, stats.displayed_pictures, stats.lost_pictures

    def has_picture(self):
        return all(self.size())

    def stop(self):
        # Stopping VLC takes about 0.6 s on the Pi: retire the old player in the
        # background and continue at once with a fresh one.
        old, self.player = self.player, self.new_player()
        self.retiring = [thread for thread in self.retiring if thread.is_alive()]
        self.retiring.append(threading.Thread(target=self.retire, args=(old,), daemon=True))
        self.retiring[-1].start()

    def retire(self, player):
        self.lib.libvlc_media_player_stop(player)
        self.lib.libvlc_media_player_release(player)

    def close(self):
        for thread in self.retiring:
            thread.join(timeout=3)
        self.retire(self.player)
        self.lib.libvlc_release(self.instance)
