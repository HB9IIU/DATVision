// datvrx: a stand-alone DVB-S / DVB-S2 receiver built from the OnAir receiver (https://github.com/adswill/OnAir, GPL-3.0-or-later).
//
// Reads complex baseband samples from a file or from standard input (a pipe from rtl_sdr, hackrf_transfer, rx_sdr, ...),
// finds the carrier, symbol rate, modulation and code rate by itself, and writes the decoded MPEG transport stream to a file
// or to standard output, where any player (ffplay, VLC, mpv) shows the picture. Status goes to standard error.
//
//   datvrx [input|-] --rate Msps [--format cu8|cs8|cs16|cf32] [-o out.ts|-] [--rs Msym/s] [--std 0|1|2] [--roll 0.35]
//          [--carrier kHz] [--isi n] [--pls n] [--secs s] [--realtime | --no-realtime] [--quiet]
//
// Example (Linux, RTL-SDR on the QO-100 wide band transponder with a 9750 MHz LNB, the 1 Msym/s area):
//   rtl_sdr -f 745250000 -s 2400000 -g 40 - | datvrx - --rate 2.4 --format cu8 | ffplay -
#include "dect2/dvbs/dvbs_rx.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>
#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#define DATVRX_ISATTY(f) _isatty(_fileno(f))
#else
#include <unistd.h>
#define DATVRX_ISATTY(f) isatty(fileno(f))
#endif

using namespace dect2;

namespace {

void usage() {
    fprintf(stderr,
            "datvrx: DVB-S / DVB-S2 receiver (from OnAir)\n"
            "usage: datvrx [input|-] --rate Msps [options]\n"
            "  input            a recording, or - (default) for standard input\n"
            "  --rate Msps      sample rate of the input (required), at least 2 Msps\n"
            "  --format F       cu8 (rtl_sdr), cs8 (hackrf_transfer), cs16, cf32; default from the file name, else cu8\n"
            "  -o out.ts | -    where the transport stream goes; default standard output\n"
            "  --rs Msym/s      symbol rate (0 = find it; needed below about %.0f kS/s at 2.4 Msps)\n"
            "  --std 0|1|2      0 automatic, 1 DVB-S only, 2 DVB-S2 only\n"
            "  --roll R         roll-off (0 = automatic)\n"
            "  --carrier kHz    decode the carrier nearest this offset from the centre (wide input with several carriers)\n"
            "  --isi n          DVB-S2 input stream, --pls n physical layer scrambling code\n"
            "  --secs s         stop after this much signal\n"
            "  --realtime       live input: drop frames when the computer is behind (default for standard input)\n"
            "  --no-realtime    wait for the decoder instead (default for files)\n"
            "  --cells n        four times a second, the last n symbols (at most 2048) on a \"datvrx cells\" status line\n"
            "                   in hex: I then Q per symbol, each a byte 128 + 64 x value (for constellation displays);\n"
            "                   while locked also a \"datvrx carrier\" line with the tracked carrier offset in Hz\n"
            "  --quiet          no status lines\n",
            dvbsMinAutoSymbolRate(2.4e6) / 1e3);
}

std::atomic<bool> gStop{false};
void onSignal(int) { gStop = true; }

}  // namespace

int main(int argc, char** argv) {
    std::string in = "-", out = "-", format;
    double rate = 0, rs = 0, roll = 0, secsMax = 0, carrierKhz = 0;
    bool haveCarrier = false, quiet = false;
    int stdHint = 0, isi = -1, pls = -1, realtime = -1, cellsOut = 0;
    bool haveInput = false;
    for (int i = 1; i < argc; i++) {
        const std::string a = argv[i];
        auto next = [&]() -> std::string {
            if (i + 1 >= argc) { fprintf(stderr, "%s needs a value\n", a.c_str()); exit(1); }
            return argv[++i];
        };
        if (a == "--rate") rate = atof(next().c_str());
        else if (a == "--format") format = next();
        else if (a == "-o") out = next();
        else if (a == "--rs") rs = atof(next().c_str());
        else if (a == "--std") stdHint = atoi(next().c_str());
        else if (a == "--roll") roll = atof(next().c_str());
        else if (a == "--carrier") { carrierKhz = atof(next().c_str()); haveCarrier = true; }
        else if (a == "--isi") isi = atoi(next().c_str());
        else if (a == "--pls") pls = atoi(next().c_str());
        else if (a == "--secs") secsMax = atof(next().c_str());
        else if (a == "--realtime") realtime = 1;
        else if (a == "--no-realtime") realtime = 0;
        else if (a == "--cells") cellsOut = std::max(0, std::min(2048, atoi(next().c_str())));
        else if (a == "--quiet") quiet = true;
        else if (a == "-h" || a == "--help") { usage(); return 0; }
        else if (!haveInput && (a == "-" || a[0] != '-')) { in = a; haveInput = true; }
        else { fprintf(stderr, "unknown option %s\n", a.c_str()); usage(); return 1; }
    }
    if (rate <= 0) { usage(); return 1; }
    const bool fromStdin = in == "-";
    if (format.empty()) {
        const size_t d = in.rfind('.');
        const std::string ext = d == std::string::npos ? "" : in.substr(d + 1);
        format = (ext == "cf32" || ext == "cs8" || ext == "cs16" || ext == "cu8") ? ext : "cu8";
    }
    size_t bps;
    if (format == "cu8" || format == "cs8") bps = 2;
    else if (format == "cs16") bps = 4;
    else if (format == "cf32") bps = 8;
    else { fprintf(stderr, "unknown format %s (cu8, cs8, cs16, cf32)\n", format.c_str()); return 1; }
    if (realtime < 0) realtime = fromStdin ? 1 : 0;

#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#else
    std::signal(SIGPIPE, SIG_IGN);   // the player went away: noticed as a write error
#endif
    std::signal(SIGINT, onSignal);
    std::signal(SIGTERM, onSignal);

    FILE* fin = fromStdin ? stdin : fopen(in.c_str(), "rb");
    if (!fin) { fprintf(stderr, "cannot open %s\n", in.c_str()); return 1; }
    FILE* fout = out == "-" ? stdout : fopen(out.c_str(), "wb");
    if (!fout) { fprintf(stderr, "cannot write %s\n", out.c_str()); return 1; }
    if (out == "-" && DATVRX_ISATTY(stdout)) {
        fprintf(stderr, "the transport stream would go to the terminal: pipe it into a player (| ffplay -) or use -o file.ts\n");
        return 1;
    }

    DvbsReceiver rx;
    rx.configure(rate * 1e6);
    if (!rx.ready()) { fprintf(stderr, "%.2f Msps is below the %.1f Msps this receiver needs\n", rate, dvbsTuning().minSampleRate / 1e6); return 1; }
    rx.setBlocking(!realtime);
    if (rs > 0) rx.setSymbolRate(rs * 1e6);
    if (stdHint) rx.setStandardHint(stdHint);
    if (roll > 0) rx.setRollOff(roll);
    if (haveCarrier) rx.selectCarrier(true, carrierKhz * 1e3);
    if (isi >= 0) rx.setIsi(isi);
    if (pls >= 0) rx.setPlScrambling(pls);

    std::mutex outMu;
    std::atomic<uint64_t> packets{0};
    std::atomic<bool> outBroken{false};
    rx.setPacketCallback([&](const uint8_t* p, size_t n, double) {
        std::lock_guard<std::mutex> lk(outMu);
        if (outBroken) return;
        if (fwrite(p, 188, n, fout) != n || fflush(fout) != 0) { outBroken = true; gStop = true; return; }
        packets += n;
    });
    if (!quiet) rx.setLogCallback([](const std::string& s) { fprintf(stderr, "datvrx: %s\n", s.c_str()); });

    const size_t block = 1 << 15;
    std::vector<unsigned char> raw(block * bps);
    std::vector<cf32> x(block);
    size_t carry = 0;   // bytes of an incomplete sample left from the previous read (pipes deliver any length)
    uint64_t total = 0, seq = 0, nextReport = (uint64_t)(rate * 1e6), nextCells = (uint64_t)(rate * 1e6 / 4);
    std::string cellsLine;
    DvbsTelemetry tel;
    if (!quiet)
        fprintf(stderr, "datvrx: %s, %.3f Msps %s, %s; searching (symbol rate %s)\n", fromStdin ? "standard input" : in.c_str(), rate, format.c_str(),
                realtime ? "live" : "recording", rs > 0 ? (std::to_string(rs) + " Msym/s").c_str() : "automatic");
    while (!gStop) {
        const size_t got = fread(raw.data() + carry, 1, raw.size() - carry, fin);
        if (got == 0) break;
        const size_t bytes = carry + got, n = bytes / bps;
        for (size_t i = 0; i < n; i++) {
            const unsigned char* s = &raw[i * bps];
            if (format == "cu8") x[i] = cf32(((int)s[0] - 127.5f) / 127.5f, ((int)s[1] - 127.5f) / 127.5f);
            else if (format == "cs8") x[i] = cf32((int8_t)s[0] / 128.f, (int8_t)s[1] / 128.f);
            else if (format == "cs16") { int16_t re, im; memcpy(&re, s, 2); memcpy(&im, s + 2, 2); x[i] = cf32(re / 32768.f, im / 32768.f); }
            else { float re, im; memcpy(&re, s, 4); memcpy(&im, s + 4, 4); x[i] = cf32(re, im); }
        }
        carry = bytes - n * bps;
        if (carry) memmove(raw.data(), raw.data() + n * bps, carry);
        rx.feed(x.data(), n);
        total += n;
        DvbsTelemetry t;
        if (rx.telemetry(t, seq)) { seq = t.seq; tel = t; }
        if (total >= nextReport) {
            nextReport += (uint64_t)(rate * 1e6);
            if (!quiet) fprintf(stderr, "datvrx [%6.1f s] %s, TS packets %llu\n", (double)total / (rate * 1e6), dvbsSummary(tel).c_str(), (unsigned long long)packets.load());
        }
        if (cellsOut && total >= nextCells) {
            nextCells += (uint64_t)(rate * 1e6 / 4);
            if (!quiet && tel.state > 0 && !tel.cells.empty()) {
                static const char kHex[] = "0123456789abcdef";
                const size_t count = std::min(tel.cells.size(), (size_t)cellsOut);
                cellsLine = "datvrx cells ";
                for (size_t i = tel.cells.size() - count; i < tel.cells.size(); i++)
                    for (float v : {tel.cells[i].real(), tel.cells[i].imag()}) {
                        const int b = std::max(0, std::min(255, (int)std::lround(128 + 64 * v)));
                        cellsLine += kHex[b >> 4];
                        cellsLine += kHex[b & 15];
                    }
                fprintf(stderr, "%s\n", cellsLine.c_str());
            }
            if (!quiet && tel.state == 2) fprintf(stderr, "datvrx carrier %+.1f\n", tel.cfoHz);
        }
        if (secsMax > 0 && (double)total / (rate * 1e6) >= secsMax) break;
    }
    if (!outBroken) rx.flush();
    {
        DvbsTelemetry t;
        if (rx.telemetry(t, 0)) tel = t;
    }
    if (!quiet) {
        fprintf(stderr, "datvrx: %.1f s of signal, %s\n", (double)total / (rate * 1e6), dvbsSummary(tel).c_str());
        if (tel.standard)
            fprintf(stderr, "datvrx: symbol rate %.4f Msym/s, roll-off %.2f, carrier %+.1f kHz, Es/N0 %.1f dB, frames ok %llu bad %llu, TS packets %llu\n",
                    tel.symbolRate / 1e6, tel.rollOff, tel.cfoHz / 1e3, tel.merDb, (unsigned long long)tel.blocksOk, (unsigned long long)tel.blocksBad,
                    (unsigned long long)packets.load());
        if (outBroken) fprintf(stderr, "datvrx: the output was closed (player stopped)\n");
    }
    if (fout != stdout) fclose(fout);
    if (fin != stdin) fclose(fin);
    return packets == 0 ? 2 : 0;
}
