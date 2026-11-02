// Stitched RTL-SDR spectrum sweep for the touch player's local spectrum. GPL-3.0-or-later, like this project.
//
//   rtl_sweep start_Hz end_Hz gain_dB
//
// Keeps the stick open and hops across start..end in steps of the flat middle of
// the band the stick sees, averages windowed FFTs at each step and prints one
// line per complete sweep on standard output:
//
//   sweep <first bin Hz> <bin width Hz> <bins> <dB x 10>,<dB x 10>,...
//
// The spike at the centre of each step is replaced by its neighbours. Stop it
// with SIGTERM or SIGINT; the stick is then closed cleanly.
#include <rtl-sdr.h>
#include <cmath>
#include <complex>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
#include <atomic>
#include <cstring>
#include <thread>

namespace {

constexpr uint32_t kRate = 2400000;
constexpr int kFft = 1024;
constexpr int kKeep = kFft * 2 / 5;   // bins kept each side of the centre: the flat 80 %
constexpr int kNotch = 2;             // bins each side of the centre spike to replace
constexpr int kBlock = 16384;         // bytes per synchronous read (8192 samples)
constexpr int kSettleBlocks = 4;      // read and dropped after each retune (about 14 ms)
constexpr int kCaptureBlocks = 16;    // averaged per step (about 55 ms)

std::atomic<bool> stopping{false};
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

// Average power per bin, centre frequency in the middle (index kFft / 2).
bool measure(rtlsdr_dev_t* device, std::vector<double>& power, const std::vector<float>& window,
             const std::vector<cf>& twiddle) {
    std::vector<unsigned char> raw(kBlock);
    std::vector<cf> x(kFft);
    power.assign(kFft, 0);
    int frames = 0, read = 0;
    for (int block = 0; block < kSettleBlocks + kCaptureBlocks; block++) {
        if (stopping || rtlsdr_read_sync(device, raw.data(), kBlock, &read) < 0 || read != kBlock) return false;
        if (block < kSettleBlocks) continue;
        for (int start = 0; start + 2 * kFft <= kBlock; start += 2 * kFft, frames++) {
            for (int i = 0; i < kFft; i++)
                x[i] = cf((raw[start + 2 * i] - 127.5f) / 127.5f, (raw[start + 2 * i + 1] - 127.5f) / 127.5f) * window[i];
            fft(x, twiddle);
            for (int i = 0; i < kFft; i++) power[(i + kFft / 2) % kFft] += std::norm(x[i]);
        }
    }
    for (double& p : power) p /= frames;
    const double left = power[kFft / 2 - kNotch - 1], right = power[kFft / 2 + kNotch + 1];
    for (int k = -kNotch; k <= kNotch; k++)
        power[kFft / 2 + k] = left + (right - left) * (k + kNotch + 1) / (2.0 * kNotch + 2);
    return true;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 4 && !(argc == 5 && std::strcmp(argv[4], "--watch-stdin") == 0)) {
        std::fprintf(stderr, "usage: rtl_sweep start_Hz end_Hz gain_dB\n");
        return 1;
    }
    const double start = std::atof(argv[1]), end = std::atof(argv[2]);
    const int gain = static_cast<int>(std::atof(argv[3]) * 10 + 0.5);
    if (!(start > 0) || !(end > start)) return 1;
    std::signal(SIGINT, requestStop);
    std::signal(SIGTERM, requestStop);
    if (argc == 5) std::thread([] { while (std::getchar() != EOF) {} stopping = true; }).detach();
#ifdef SIGPIPE
    std::signal(SIGPIPE, SIG_IGN);  // a closed reader is seen by fflush instead
#endif

    const double binHz = (double)kRate / kFft, usableHz = 2 * kKeep * binHz;
    const int bins = (int)((end - start) / binHz) + 1;
    const int steps = (int)std::ceil((end - start) / usableHz);
    std::vector<uint32_t> centres(steps);
    for (int s = 0; s < steps; s++) centres[s] = (uint32_t)std::lround(start + usableHz * (s + 0.5));

    std::vector<float> window(kFft);
    std::vector<cf> twiddle(kFft / 2);
    for (int i = 0; i < kFft; i++) window[i] = 0.5f - 0.5f * std::cos(2 * M_PI * i / (kFft - 1));
    for (int k = 0; k < kFft / 2; k++) twiddle[k] = std::polar(1.0f, (float)(-2 * M_PI * k / kFft));

    rtlsdr_dev_t* device = nullptr;
    if (rtlsdr_open(&device, 0) < 0) return 1;
    if (rtlsdr_set_sample_rate(device, kRate) < 0 || rtlsdr_set_tuner_gain_mode(device, 1) < 0 ||
        rtlsdr_set_tuner_gain(device, gain) < 0 || rtlsdr_reset_buffer(device) < 0) {
        std::fprintf(stderr, "RTL-SDR configuration failed\n");
        rtlsdr_close(device);
        return 1;
    }
    std::fprintf(stderr, "rtl_sweep: %.3f-%.3f MHz, %d steps of %.3f MHz, %d bins of %.0f Hz, gain %.1f dB\n",
                 start / 1e6, end / 1e6, steps, usableHz / 1e6, bins, binHz, gain / 10.0);

    std::vector<std::vector<double>> step(steps);
    std::string line;
    int result = 0;
    while (!stopping) {
        bool complete = true;
        for (int s = 0; s < steps && complete; s++)
            complete = rtlsdr_set_center_freq(device, centres[s]) >= 0 &&
                       measure(device, step[s], window, twiddle);
        if (!complete) {
            if (!stopping) { std::fprintf(stderr, "rtl_sweep: reading the RTL-SDR failed\n"); result = 1; }
            break;
        }
        char head[96];
        std::snprintf(head, sizeof head, "sweep %.0f %.4f %d ", start, binHz, bins);
        line = head;
        for (int b = 0; b < bins; b++) {
            const double f = start + b * binHz;
            const int s = std::min(steps - 1, (int)((f - start) / usableHz));
            const int k = std::max(-kKeep, std::min(kKeep, (int)std::lround((f - centres[s]) / binHz)));
            char value[16];
            std::snprintf(value, sizeof value, b ? ",%d" : "%d", (int)std::lround(100 * std::log10(step[s][kFft / 2 + k] + 1e-12)));
            line += value;
        }
        line += '\n';
        if (std::fputs(line.c_str(), stdout) < 0 || std::fflush(stdout) != 0) break;
    }
    rtlsdr_close(device);
    return result;
}
