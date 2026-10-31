"""Small ctypes binding to the system libVLC. Video is embedded in an X11 Qt widget."""
import ctypes as C
from ctypes.util import find_library


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
        }
        for name, (args, result) in signatures.items():
            function = getattr(self.lib, name)
            function.argtypes = args
            function.restype = result
        options = [b"--no-video-title-show", b"--quiet", b"--avcodec-hw=none"]
        self.instance = self.lib.libvlc_new(len(options), (C.c_char_p * len(options))(*options))
        if not self.instance:
            raise RuntimeError("VLC could not initialize")
        self.player = self.lib.libvlc_media_player_new(self.instance)
        self.lib.libvlc_media_player_set_xwindow(self.player, int(window_id))
        self.lib.libvlc_video_set_mouse_input(self.player, 0)
        self.lib.libvlc_video_set_key_input(self.player, 0)

    def play(self, port, volume):
        self.stop()
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

    def has_picture(self):
        width, height = C.c_uint(), C.c_uint()
        self.lib.libvlc_video_get_size(self.player, 0, C.byref(width), C.byref(height))
        return width.value > 0 and height.value > 0

    def stop(self):
        self.lib.libvlc_media_player_stop(self.player)

    def close(self):
        self.stop()
        self.lib.libvlc_media_player_release(self.player)
        self.lib.libvlc_release(self.instance)
