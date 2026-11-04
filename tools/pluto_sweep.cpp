// Stitched ADALM-Pluto spectrum sweep for the touch player's local spectrum, the
// counterpart of rtl_sweep with exactly its output. GPL-3.0-or-later, like this project.
//
//   pluto_sweep uri start_Hz end_Hz gain_dB
//
// The Pluto sees 20 MHz at once (rtl_sweep: 2.4 MHz), but its analog filter falls
// off towards the edges: measured on LNB noise, -1.3 dB at 6 MHz from the centre,
// -3.8 dB at 8 MHz. Only the flat +/-5.5 MHz is used, so QO-100 takes one step,
// 23 cm two, 6 cm three; the steps overlap evenly. A band narrower than about
// 5 MHz is received just above the centre, so the centre (DC) spike lies outside
// it. At each step it averages windowed FFTs and prints one line per complete
// sweep on standard output:
//
//   sweep <first bin Hz> <bin width Hz> <bins> <dB x 10>,<dB x 10>,...
//
// The gain is fixed: an automatic gain would change the level from step to step.
// The spike at the centre of each step is replaced by its neighbours.
#include <iio.h>
#include <algorithm>
#include <cmath>
#include <complex>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr long long kRate = 20000000;
constexpr int kFft = 8192;            // 2441 Hz bins, as fine as rtl_sweep's 2344 Hz
constexpr double kFlatHz = 5.5e6;     // used each side of the centre: within about 1 dB
constexpr double kBesideHz = 0.3e6;   // a narrow band starts this far above the centre spike
constexpr int kNotch = 6;             // bins each side of the centre (DC) spike to replace
constexpr int kFrames = 64;           // FFTs averaged per step (26 ms of signal; rtl_sweep averages 128 shorter ones)

volatile std::sig_atomic_t stopping = 0;
void requestStop(int) { stopping = 1; }

using cf = std::complex<float>;

void fft(std::vector<cf>& x, const std::vector<cf>& twiddle) {
    const int n = (int)x.size();
    for (int i = 1, j = 0; i < n; i++) {
        int bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) std::swap(x[i], x[j]);
    }
    for (int len = 2; len <= n; len <<= 1)
        for (int i = 0; i < n; i += len)
            for (int k = 0; k < len / 2; k++) {
                const cf t = x[i + k + len / 2] * twiddle[k * (n / len)];
                x[i + k + len / 2] = x[i + k] - t;
                x[i + k] += t;
            }
}

// Average power per bin, centre frequency in the middle (index kFft / 2). The first
// buffer after a retune can still hold samples from before it, so it is dropped.
bool measure(iio_buffer* buffer, std::vector<double>& power, const std::vector<float>& window,
             const std::vector<cf>& twiddle) {
    std::vector<cf> x(kFft);
    power.assign(kFft, 0);
    for (int pass = 0; pass < 2; pass++)
        if (stopping || iio_buffer_refill(buffer) < 0) return false;
    const int16_t* samples = static_cast<const int16_t*>(iio_buffer_start(buffer));
    for (int frame = 0; frame < kFrames; frame++) {
        const int16_t* s = samples + 2 * kFft * frame;
        for (int i = 0; i < kFft; i++) x[i] = cf(s[2 * i] / 2048.0f, s[2 * i + 1] / 2048.0f) * window[i];
        fft(x, twiddle);
        for (int i = 0; i < kFft; i++) power[(i + kFft / 2) % kFft] += std::norm(x[i]);
    }
    for (double& p : power) p /= kFrames;
    const double left = power[kFft / 2 - kNotch - 1], right = power[kFft / 2 + kNotch + 1];
    for (int k = -kNotch; k <= kNotch; k++)
        power[kFft / 2 + k] = left + (right - left) * (k + kNotch + 1) / (2.0 * kNotch + 2);
    return true;
}

}  // namespace

int main(int argc, char** argv) {
    // --watch-stdin: stop when standard input closes (Windows has no SIGTERM for this).
    const bool watchStdin = argc == 6 && std::strcmp(argv[5], "--watch-stdin") == 0;
    if (argc != 5 && !watchStdin) {
        std::fprintf(stderr, "usage: pluto_sweep uri start_Hz end_Hz gain_dB [--watch-stdin]\n"
                             "  e.g. pluto_sweep usb: 741000000 750000000 50\n");
        return 1;
    }
    const char* uri = argv[1];
    const double start = std::atof(argv[2]), end = std::atof(argv[3]);
    const long long gain = std::llround(std::atof(argv[4]));
    if (!(start > 0) || !(end > start)) return 1;
    std::signal(SIGINT, requestStop);
    std::signal(SIGTERM, requestStop);
#ifdef SIGPIPE
    std::signal(SIGPIPE, SIG_IGN);  // a closed reader is seen by fflush instead
#endif
    if (watchStdin) std::thread([] { while (std::getchar() != EOF) {} stopping = 1; }).detach();

    const double binHz = (double)kRate / kFft, usableHz = 2 * kFlatHz, span = end - start;
    const int keep = (int)(kFlatHz / binHz);   // bins used each side of a step's centre
    const int bins = (int)(span / binHz) + 1;
    std::vector<long long> centres;
    if (span + kBesideHz <= kFlatHz) {
        centres.push_back(std::llround(start - kBesideHz));   // the whole band beside the spike
    } else {
        const int steps = (int)std::ceil(span / usableHz);
        const double first = start + kFlatHz, last = end - kFlatHz;
        for (int s = 0; s < steps; s++)
            centres.push_back(std::llround(steps == 1 ? (start + end) / 2 : first + (last - first) * s / (steps - 1)));
    }
    const int steps = (int)centres.size();

    std::vector<float> window(kFft);
    std::vector<cf> twiddle(kFft / 2);
    for (int i = 0; i < kFft; i++) window[i] = 0.5f - 0.5f * std::cos(2 * M_PI * i / (kFft - 1));
    for (int k = 0; k < kFft / 2; k++) twiddle[k] = std::polar(1.0f, (float)(-2 * M_PI * k / kFft));

    iio_context* context = iio_create_context_from_uri(uri);
    if (!context) {
        std::fprintf(stderr, "pluto_sweep: no Pluto found at %s\n", uri);
        return 2;
    }
    iio_device* phy = iio_context_find_device(context, "ad9361-phy");
    iio_device* rx = iio_context_find_device(context, "cf-ad9361-lpc");
    iio_channel* lo = phy ? iio_device_find_channel(phy, "altvoltage0", true) : nullptr;
    iio_channel* control = phy ? iio_device_find_channel(phy, "voltage0", false) : nullptr;
    iio_channel* i = rx ? iio_device_find_channel(rx, "voltage0", false) : nullptr;
    iio_channel* q = rx ? iio_device_find_channel(rx, "voltage1", false) : nullptr;
    if (!lo || !control || !i || !q ||
        iio_channel_attr_write_longlong(control, "sampling_frequency", kRate) < 0 ||
        iio_channel_attr_write_longlong(control, "rf_bandwidth", kRate * 9 / 10) < 0 ||
        iio_channel_attr_write(control, "rf_port_select", "A_BALANCED") < 0 ||
        iio_channel_attr_write(control, "gain_control_mode", "manual") < 0 ||
        iio_channel_attr_write_longlong(control, "hardwaregain", gain) < 0) {
        std::fprintf(stderr, "pluto_sweep: Pluto configuration failed\n");
        iio_context_destroy(context);
        return 1;
    }
    iio_channel_enable(i);
    iio_channel_enable(q);
    // Few kernel buffers, so that little is queued from before a retune.
    iio_device_set_kernel_buffers_count(rx, 2);
    iio_buffer* buffer = iio_device_create_buffer(rx, kFft * kFrames, false);
    if (!buffer) {
        std::fprintf(stderr, "pluto_sweep: cannot start streaming\n");
        iio_context_destroy(context);
        return 1;
    }
    std::fprintf(stderr, "pluto_sweep: %.3f-%.3f MHz, %d steps of %.3f MHz, %d bins of %.0f Hz, gain %lld dB\n",
                 start / 1e6, end / 1e6, steps, usableHz / 1e6, bins, binHz, gain);

    std::vector<std::vector<double>> step(steps);
    std::string line;
    int result = 0;
    while (!stopping) {
        bool complete = true;
        for (int s = 0; s < steps && complete; s++)
            complete = iio_channel_attr_write_longlong(lo, "frequency", centres[s]) >= 0 &&
                       measure(buffer, step[s], window, twiddle);
        if (!complete) {
            if (!stopping) { std::fprintf(stderr, "pluto_sweep: reading the Pluto failed\n"); result = 1; }
            break;
        }
        char head[96];
        std::snprintf(head, sizeof head, "sweep %.0f %.4f %d ", start, binHz, bins);
        line = head;
        for (int b = 0; b < bins; b++) {
            const double f = start + b * binHz;
            int s = 0;   // the step whose centre is nearest: it sees this bin in its flattest part
            for (int t = 1; t < steps; t++)
                if (std::fabs(f - centres[t]) < std::fabs(f - centres[s])) s = t;
            const int k = std::max(-keep, std::min(keep, (int)std::lround((f - centres[s]) / binHz)));
            char value[16];
            std::snprintf(value, sizeof value, b ? ",%d" : "%d", (int)std::lround(100 * std::log10(step[s][kFft / 2 + k] + 1e-12)));
            line += value;
        }
        line += '\n';
        if (std::fputs(line.c_str(), stdout) < 0 || std::fflush(stdout) != 0) break;
    }
    iio_buffer_destroy(buffer);
    iio_context_destroy(context);
    return result;
}
