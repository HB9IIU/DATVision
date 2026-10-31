// Minimal binary-safe RTL-SDR pipe source. GPL-3.0-or-later, like this project.
#include <rtl-sdr.h>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

struct Capture {
    rtlsdr_dev_t* device;
    uint64_t remaining;
    bool limited;
};

// Stop requests cancel the transfer so the stick is closed cleanly; an abrupt
// exit mid-transfer can leave the RTL-SDR Blog V4 unresponsive on USB.
static rtlsdr_dev_t* volatile active = nullptr;
static volatile std::sig_atomic_t stopping = 0;

static void requestStop(int) {
    stopping = 1;
    if (active) rtlsdr_cancel_async(active);
}

static void deliver(unsigned char* data, uint32_t length, void* opaque) {
    auto& state = *static_cast<Capture*>(opaque);
    size_t count = length;
    if (state.limited && state.remaining < count) count = static_cast<size_t>(state.remaining);
    if (std::fwrite(data, 1, count, stdout) != count || std::fflush(stdout) != 0) {
        rtlsdr_cancel_async(state.device);
        return;
    }
    if (state.limited) {
        state.remaining -= count;
        if (!state.remaining) rtlsdr_cancel_async(state.device);
    }
}

int main(int argc, char** argv) {
    if (argc < 4 || argc > 5) {
        std::fprintf(stderr, "usage: rtl_iq frequency_Hz sample_rate_Hz gain_dB [sample_count]\n");
        return 1;
    }
    const uint32_t frequency = std::strtoul(argv[1], nullptr, 10);
    const uint32_t rate = std::strtoul(argv[2], nullptr, 10);
    const int gain = static_cast<int>(std::atof(argv[3]) * 10 + 0.5);
    uint64_t remaining = argc == 5 ? std::strtoull(argv[4], nullptr, 10) * 2 : 0;
    const bool limited = argc == 5;
    if (!frequency || !rate || (limited && !remaining)) return 1;
#ifdef _WIN32
    _setmode(_fileno(stdout), _O_BINARY);
#endif
    std::signal(SIGINT, requestStop);
    std::signal(SIGTERM, requestStop);
#ifdef SIGPIPE
    std::signal(SIGPIPE, SIG_IGN);  // a closed reader is seen by fwrite instead
#endif
    rtlsdr_dev_t* device = nullptr;
    if (rtlsdr_open(&device, 0) < 0) return 1;
    if (rtlsdr_set_sample_rate(device, rate) < 0 ||
        rtlsdr_set_center_freq(device, frequency) < 0 ||
        rtlsdr_set_tuner_gain_mode(device, 1) < 0 ||
        rtlsdr_set_tuner_gain(device, gain) < 0 ||
        rtlsdr_reset_buffer(device) < 0) {
        std::fprintf(stderr, "RTL-SDR configuration failed\n");
        rtlsdr_close(device);
        return 1;
    }
    std::fprintf(stderr, "rtl_iq: %u Hz, %u samples/s, gain %.1f dB; binary stdout\n", frequency, rate, gain / 10.0);
    Capture capture{device, remaining, limited};
    active = device;
    const int result = stopping ? 0 : rtlsdr_read_async(device, deliver, &capture, 0, 262144);
    active = nullptr;
    rtlsdr_close(device);
    return result < 0 ? 1 : 0;
}
