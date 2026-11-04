// Binary-safe ADALM-Pluto pipe source (libiio): interleaved 8-bit unsigned I/Q on
// standard output, as from an RTL-SDR, for datvrx --format cu8. datvrx decodes cu8
// about four times faster than cs16; the AGC keeps the 12-bit samples in range, so
// the conversion costs nothing measurable (same Es/N0 on the beacon).
// GPL-3.0-or-later, like this project.
#include <iio.h>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <thread>
#include <vector>
#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

static volatile std::sig_atomic_t stopping = 0;
static void requestStop(int) { stopping = 1; }

static bool setText(iio_channel* channel, const char* attribute, const char* value) {
    return iio_channel_attr_write(channel, attribute, value) >= 0;
}

static bool setNumber(iio_channel* channel, const char* attribute, long long value) {
    return iio_channel_attr_write_longlong(channel, attribute, value) >= 0;
}

int main(int argc, char** argv) {
    // --watch-stdin: stop when standard input closes (Windows has no SIGTERM for this).
    const bool watchStdin = argc == 6 && std::strcmp(argv[5], "--watch-stdin") == 0;
    if (argc != 5 && !watchStdin) {
        std::fprintf(stderr, "usage: pluto_iq uri frequency_Hz sample_rate_Hz gain_dB|auto [--watch-stdin]\n"
                             "  e.g. pluto_iq usb: 741500000 2560000 auto   (usb: is faster than ip:192.168.2.1)\n");
        return 1;
    }
    const char* uri = argv[1];
    const long long frequency = std::strtoll(argv[2], nullptr, 10);
    const long long rate = std::strtoll(argv[3], nullptr, 10);
    const bool automatic = std::strcmp(argv[4], "auto") == 0;
    if (frequency <= 0 || rate <= 0) return 1;
    std::signal(SIGINT, requestStop);
    std::signal(SIGTERM, requestStop);
#ifdef SIGPIPE
    std::signal(SIGPIPE, SIG_IGN);  // a closed reader is seen by fwrite instead
#endif
#ifdef _WIN32
    _setmode(_fileno(stdout), _O_BINARY);
#endif
    if (watchStdin) std::thread([] { while (std::getchar() != EOF) {} stopping = 1; }).detach();

    iio_context* context = iio_create_context_from_uri(uri);
    if (!context) {
        std::fprintf(stderr, "pluto_iq: no Pluto found at %s\n", uri);
        return 2;
    }
    const char* model = iio_context_get_attr_value(context, "hw_model");
    const char* firmware = iio_context_get_attr_value(context, "fw_version");
    std::fprintf(stderr, "Pluto: %s, firmware %s\n", model ? model : "?", firmware ? firmware : "?");
    iio_device* phy = iio_context_find_device(context, "ad9361-phy");
    iio_device* rx = iio_context_find_device(context, "cf-ad9361-lpc");
    iio_channel* lo = phy ? iio_device_find_channel(phy, "altvoltage0", true) : nullptr;
    iio_channel* control = phy ? iio_device_find_channel(phy, "voltage0", false) : nullptr;
    iio_channel* i = rx ? iio_device_find_channel(rx, "voltage0", false) : nullptr;
    iio_channel* q = rx ? iio_device_find_channel(rx, "voltage1", false) : nullptr;
    if (!lo || !control || !i || !q) {
        std::fprintf(stderr, "pluto_iq: %s is not an AD936x Pluto\n", uri);
        iio_context_destroy(context);
        return 2;
    }

    // The AD936x runs from about 2.08 MS/s without its decimation filters.
    bool ok = setNumber(control, "sampling_frequency", rate) && setNumber(control, "rf_bandwidth", rate * 4 / 5) &&
              setNumber(lo, "frequency", frequency) && setText(control, "rf_port_select", "A_BALANCED");
    if (automatic) {
        ok = ok && setText(control, "gain_control_mode", "slow_attack");
    } else {
        char gain[32];
        std::snprintf(gain, sizeof gain, "%.0f", std::atof(argv[4]));
        ok = ok && setText(control, "gain_control_mode", "manual") && setText(control, "hardwaregain", gain);
    }
    if (!ok) {
        std::fprintf(stderr, "pluto_iq: the Pluto refused %lld Hz at %lld S/s\n", frequency, rate);
        iio_context_destroy(context);
        return 3;
    }
    std::fprintf(stderr, "Tuned to %lld Hz, %lld S/s, gain %s\n", frequency, rate, automatic ? "automatic" : argv[4]);

    iio_channel_enable(i);
    iio_channel_enable(q);
    // A deep reserve on the Pluto (16 x 0.1 s at 2.56 MS/s): with only the default
    // 4 small buffers, a moment of video or spectrum load on the Pi lost samples
    // and with them the DVB-S2 frame synchronisation.
    iio_device_set_kernel_buffers_count(rx, 16);
    iio_buffer* buffer = iio_device_create_buffer(rx, 262144, false);
    if (!buffer) {
        std::fprintf(stderr, "pluto_iq: cannot start streaming\n");
        iio_context_destroy(context);
        return 3;
    }
    int result = 0;
    std::vector<uint8_t> output;
    while (!stopping) {
        const ssize_t filled = iio_buffer_refill(buffer);
        if (filled < 0) {
            std::fprintf(stderr, "pluto_iq: streaming stopped (%zd)\n", filled);
            result = 4;
            break;
        }
        const int16_t* start = static_cast<const int16_t*>(iio_buffer_start(buffer));
        const size_t count = static_cast<const int16_t*>(iio_buffer_end(buffer)) - start;
        output.resize(count);
        for (size_t n = 0; n < count; ++n) {
            // 12-bit signed (sign-extended) to 8-bit offset binary, saturating.
            const int value = (start[n] >> 4) + 128;
            output[n] = static_cast<uint8_t>(value < 0 ? 0 : value > 255 ? 255 : value);
        }
        if (std::fwrite(output.data(), 1, count, stdout) != count || std::fflush(stdout) != 0) break;
    }
    iio_buffer_destroy(buffer);
    iio_context_destroy(context);
    return result;
}
