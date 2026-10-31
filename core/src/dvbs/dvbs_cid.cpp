// DVB-CID: see dvbs_cid.h. The framing follows DecSAT's crates/decsat-modem/src/cid.rs (GPL-3.0-or-later, made by CasualArclamp); the receiver is this project's own.
#include "dect2/dvbs/dvbs_cid.h"
#include "dect2/dsp/dsp_compat.h"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <map>

namespace dect2 {
namespace dvbcid {

namespace {
constexpr uint32_t kUw = 0x147147, kUwMask = (1u << kUwBits) - 1;

// CRC-8 of clause 5.1.2: x^8 + x^7 + x^6 + x^4 + x^2 + 1 (D5h), register from FFh, bits MSB first
uint8_t crc8(const uint8_t* bits, int n) {
    uint8_t r = 0xFF;
    for (int i = 0; i < n; i++) {
        const uint8_t fb = (uint8_t)((r >> 7) ^ bits[i]);
        r = (uint8_t)(r << 1);
        if (fb & 1) r ^= 0xD5;
    }
    return r;
}

// GF(2^7), x^7 + x^3 + 1, and beta = alpha^23 whose powers 1..12 are the generator's roots
struct Gf {
    uint8_t exp[254], log[128];
    Gf() {
        unsigned v = 1;
        for (int i = 0; i < 127; i++) {
            exp[i] = (uint8_t)v; log[v] = (uint8_t)i;
            v <<= 1;
            if (v & 0x80) v ^= 0x89;
        }
        for (int i = 127; i < 254; i++) exp[i] = exp[i - 127];
        log[0] = 0;
    }
    uint8_t mul(uint8_t a, uint8_t b) const { return (a == 0 || b == 0) ? 0 : exp[log[a] + log[b]]; }
    uint8_t inv(uint8_t a) const { return exp[(127 - log[a]) % 127]; }
    uint8_t beta(int e) const { return exp[(23 * e) % 127]; }
};
const Gf& gf() { static const Gf g; return g; }

uint64_t polyMul(uint64_t a, uint64_t b) { uint64_t r = 0; while (b) { if (b & 1) r ^= a; a <<= 1; b >>= 1; } return r; }
uint64_t bchGenerator() {
    static const uint64_t polys[6] = {0x91, 0x9D, 0xBF, 0xC1, 0xD5, 0xF1};
    uint64_t g = 1;
    for (uint64_t p : polys) g = polyMul(g, p);
    return g;
}
void bchParity(const uint8_t* data, uint8_t* out) {
    const uint64_t g = bchGenerator(), mask = (1ull << kParity) - 1;
    uint64_t r = 0;
    for (int i = 0; i < kData; i++) {
        const uint64_t fb = ((r >> (kParity - 1)) & 1) ^ data[i];
        r = (r << 1) & mask;
        if (fb) r ^= g & mask;
    }
    for (int i = 0; i < kParity; i++) out[i] = (uint8_t)((r >> (kParity - 1 - i)) & 1);
}
// corrects up to 6 errors of a 111-bit word in place; returns the number corrected, -1 when it cannot
int bchCorrect(uint8_t* word) {
    const Gf& g = gf();
    uint8_t s[13] = {0};
    bool any = false;
    for (int j = 1; j <= 12; j++) {
        uint8_t acc = 0;
        for (int i = 0; i < kHalf; i++) if (word[i]) acc ^= g.beta(j * (kHalf - 1 - i));
        s[j] = acc; any |= acc != 0;
    }
    if (!any) return 0;
    std::vector<uint8_t> c(14, 0), b(14, 0);
    c[0] = 1; b[0] = 1;
    int l = 0, m = 1;
    uint8_t bb = 1;
    for (int n = 0; n < 12; n++) {
        uint8_t d = s[n + 1];
        for (int i = 1; i <= l; i++) d ^= g.mul(c[(size_t)i], s[n + 1 - i]);
        if (d == 0) { m++; continue; }
        const uint8_t coef = g.mul(d, g.inv(bb));
        if (2 * l <= n) {
            const std::vector<uint8_t> t = c;
            for (size_t i = 0; i + (size_t)m < c.size(); i++) c[i + (size_t)m] ^= g.mul(coef, b[i]);
            l = n + 1 - l; b = t; bb = d; m = 1;
        } else {
            for (size_t i = 0; i + (size_t)m < c.size(); i++) c[i + (size_t)m] ^= g.mul(coef, b[i]);
            m++;
        }
    }
    if (l > 6) return -1;
    std::vector<int> found;
    for (int i = 0; i < kHalf; i++) {
        const int e = kHalf - 1 - i;
        const uint8_t x = g.beta((127 - e % 127) % 127);
        uint8_t v = 0, xp = 1;
        for (int k = 0; k <= l; k++) { v ^= g.mul(c[(size_t)k], xp); xp = g.mul(xp, x); }
        if (v == 0) found.push_back(i);
    }
    if ((int)found.size() != l) return -1;
    for (int i : found) word[i] ^= 1;
    return l;
}

// scrambling sequence of the 222 non-UW bits: x^9 + x^5 + 1 from 041h, the feedback both output and shifted in, restarted every frame (order 1: the reversed reading)
std::vector<uint8_t> scramblerSeq(int order) {
    uint8_t r[9];
    for (int k = 0; k < 9; k++) r[k] = (uint8_t)((0x41 >> (order == 0 ? k : 8 - k)) & 1);
    std::vector<uint8_t> out(kFrameBits - kUwBits);
    for (auto& o : out) {
        o = r[8] ^ r[4];
        for (int k = 8; k > 0; k--) r[k] = r[k - 1];
        r[0] = o;
    }
    return out;
}
} // namespace

double chipRate(double rs) { return rs >= 512e3 ? 224e3 : 112e3; }
double psdBelowHostDb(double rs) {
    if (rs < 2048e3) return -27.5;
    if (rs < 4096e3) return -24.5;
    if (rs < 8192e3) return -21.5;
    if (rs < 16384e3) return -18.5;
    return -17.5;
}
const std::vector<uint8_t>& chipSequence() {
    static const std::vector<uint8_t> seq = [] {
        std::vector<uint8_t> c{0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0};
        while ((int)c.size() < kChips) { const size_t n = c.size(); c.push_back(c[n - 15] ^ c[n - 14]); }
        return c;
    }();
    return seq;
}
uint8_t checkDigits(uint64_t guid) {
    uint8_t bits[64];
    for (int i = 0; i < 64; i++) bits[i] = (uint8_t)((guid >> (63 - i)) & 1);
    return crc8(bits, 64);
}
std::string guidText(uint64_t guid) {
    char b[40];
    snprintf(b, sizeof b, "%02X", checkDigits(guid));
    std::string s = b;
    for (int i = 7; i >= 0; i--) { snprintf(b, sizeof b, ":%02X", (unsigned)((guid >> (8 * i)) & 0xFF)); s += b; }
    return s;
}
std::string guidMac(uint64_t guid) {
    uint8_t o[8];
    for (int i = 0; i < 8; i++) o[i] = (uint8_t)(guid >> (8 * (7 - i)));
    if (!(o[3] == 0xFF && (o[4] == 0xFE || o[4] == 0xFF))) return std::string();
    char b[24];
    snprintf(b, sizeof b, "%02X:%02X:%02X:%02X:%02X:%02X", o[0], o[1], o[2], o[5], o[6], o[7]);
    return b;
}

std::vector<uint8_t> buildFrame(uint64_t guid, const Field fields[2], bool second) {
    std::vector<uint8_t> f(kFrameBits, 0);
    const uint32_t uw = second ? (~kUw & kUwMask) : kUw;
    for (int i = 0; i < kUwBits; i++) f[(size_t)i] = (uint8_t)((uw >> (kUwBits - 1 - i)) & 1);
    for (int h = 0; h < 2; h++) {
        const uint32_t id = h == 0 ? (uint32_t)(guid >> 32) : (uint32_t)guid;
        uint8_t d[kData];
        int k = 0;
        for (int i = 0; i < 32; i++) d[k++] = (uint8_t)((id >> (31 - i)) & 1);
        for (int i = 0; i < 5; i++) d[k++] = (uint8_t)((fields[h].contentId >> (4 - i)) & 1);
        for (int i = 0; i < 24; i++) d[k++] = (uint8_t)((fields[h].info >> (23 - i)) & 1);
        const uint8_t crc = crc8(d, 61);
        for (int i = 0; i < 8; i++) d[k++] = (uint8_t)((crc >> (7 - i)) & 1);
        uint8_t p[kParity];
        bchParity(d, p);
        const int at = kUwBits + h * kHalf;
        memcpy(&f[(size_t)at], d, kData);
        memcpy(&f[(size_t)(at + kData)], p, kParity);
    }
    const std::vector<uint8_t> sc = scramblerSeq(0);
    for (size_t i = 0; i < sc.size(); i++) f[kUwBits + i] ^= sc[i];
    return f;
}

bool parseFrame(const uint8_t* bits, Frame& out) {
    uint32_t uw = 0;
    for (int i = 0; i < kUwBits; i++) uw = (uw << 1) | bits[i];
    const int a = __builtin_popcount(uw ^ kUw), b = __builtin_popcount(uw ^ (~kUw & kUwMask));
    bool second;
    if (a <= 3) second = false; else if (b <= 3) second = true; else return false;
    for (int order = 0; order < 2; order++) {
        const std::vector<uint8_t> sc = scramblerSeq(order);
        uint8_t body[kFrameBits - kUwBits];
        for (size_t i = 0; i < sizeof body; i++) body[i] = bits[kUwBits + i] ^ sc[i];
        Frame fr;
        fr.second = second; fr.scrambler = order;
        uint32_t ids[2] = {0, 0};
        bool ok = true;
        for (int h = 0; h < 2 && ok; h++) {
            uint8_t* half = body + h * kHalf;
            const int n = bchCorrect(half);
            if (n < 0) { ok = false; break; }
            uint8_t want = 0;
            for (int i = 0; i < 8; i++) want = (uint8_t)((want << 1) | half[61 + i]);
            if (crc8(half, 61) != want) { ok = false; break; }
            fr.corrected += n;
            auto num = [&](int from, int to) { uint32_t v = 0; for (int i = from; i < to; i++) v = (v << 1) | half[i]; return v; };
            ids[h] = num(0, 32);
            fr.fields[h].contentId = (int)num(32, 37);
            fr.fields[h].info = num(37, 61);
        }
        if (ok) { fr.guid = (uint64_t)ids[0] << 32 | ids[1]; out = fr; return true; }
    }
    return false;
}

std::vector<uint8_t> spreadFrames(const std::vector<std::vector<uint8_t>>& frames, uint8_t& diff) {
    const std::vector<uint8_t>& seq = chipSequence();
    std::vector<uint8_t> out;
    for (const auto& f : frames)
        for (int r = 0; r < kRepeat; r++)
            for (uint8_t b : f) {
                diff ^= b;
                for (uint8_t c : seq) out.push_back(c ^ diff);
            }
    return out;
}

// ---------------------------------------------------------------- report
void Report::add(const Frame& f) {
    haveGuid = true; guid = f.guid;
    for (const Field& fl : f.fields)
        if (fl.contentId >= 0 && fl.contentId < 13) { have[fl.contentId] = true; fields[fl.contentId] = fl.info; }
}
void Report::setPosition(double lat, double lon) {
    auto nmea = [](double deg) { const uint32_t h = (uint32_t)std::lround(std::fabs(deg) * 6000.0); return (h / 6000) * 10000 + h % 6000; };
    lat = std::max(-90.0, std::min(90.0, lat)); lon = std::max(-180.0, std::min(180.0, lon));
    have[1] = true; fields[1] = (nmea(lat) << 4) | (lat < 0 ? 1u : 0u);
    have[2] = true; fields[2] = (nmea(lon) << 3) | (lon < 0 ? 1u : 0u);
}
void Report::setTelephone(const std::string& number) {
    std::string lower;
    for (char c : number) lower += (char)((c >= 'A' && c <= 'Z') ? c + 32 : c);
    const size_t e = lower.find("ext");
    std::vector<uint32_t> nib;
    for (size_t i = 0; i < (e == std::string::npos ? lower.size() : e); i++) if (lower[i] >= '0' && lower[i] <= '9') nib.push_back((uint32_t)(lower[i] - '0'));
    if (e != std::string::npos) {
        nib.push_back(0xD);
        for (size_t i = e + 3; i < lower.size(); i++) if (lower[i] >= '0' && lower[i] <= '9') nib.push_back((uint32_t)(lower[i] - '0'));
    }
    nib.resize(18, 0xF);
    for (int k = 0; k < 3; k++) {
        uint32_t v = 0;
        for (int i = 0; i < 6; i++) v = (v << 4) | nib[(size_t)(k * 6 + i)];
        have[3 + k] = true; fields[3 + k] = v;
    }
}
void Report::setUserText(const std::string& text) {
    std::vector<uint8_t> bits;
    int n = 0;
    for (char ch : text) {
        if (n++ >= 24) break;
        const unsigned c = (ch >= ' ' && ch <= '~') ? (unsigned)ch : (unsigned)'?';
        for (int i = 6; i >= 0; i--) bits.push_back((uint8_t)((c >> i) & 1));
    }
    bits.resize(168, 0);
    for (int k = 0; k < 7; k++) {
        uint32_t v = 0;
        for (int i = 0; i < 24; i++) v = (v << 1) | bits[(size_t)(k * 24 + i)];
        have[6 + k] = true; fields[6 + k] = v;
    }
}
std::vector<std::vector<Field>> Report::frameFields() const {
    std::vector<Field> set;
    for (int i = 0; i < 13; i++) if (have[i]) set.push_back({i, fields[i]});
    std::vector<std::vector<Field>> out;
    for (size_t i = 0; i < set.size(); i += 2) out.push_back({set[i], i + 1 < set.size() ? set[i + 1] : set[0]});
    return out;
}
bool Report::latitude(double& deg) const {
    if (!have[1]) return false;
    const double x = (fields[1] >> 4) / 100.0;
    deg = std::floor(x / 100.0) + std::fmod(x, 100.0) / 60.0;
    if (fields[1] & 1) deg = -deg;
    return true;
}
bool Report::longitude(double& deg) const {
    if (!have[2]) return false;
    const double x = (fields[2] >> 3) / 100.0;
    deg = std::floor(x / 100.0) + std::fmod(x, 100.0) / 60.0;
    if (fields[2] & 1) deg = -deg;
    return true;
}
std::string Report::telephone() const {
    if (!(have[3] && have[4] && have[5])) return std::string();
    std::string s = "+";
    for (int f = 3; f <= 5; f++)
        for (int k = 5; k >= 0; k--) {
            const uint32_t d = (fields[f] >> (4 * k)) & 0xF;
            if (d <= 9) s += (char)('0' + d); else if (d == 0xD) s += " ext. ";
        }
    return s;
}
bool Report::hasText() const { for (int i = 6; i < 13; i++) if (!have[i]) return false; return true; }
std::string Report::userText() const {
    if (!hasText()) return std::string();
    std::vector<uint8_t> bits;
    for (int k = 6; k < 13; k++) for (int i = 0; i < 24; i++) bits.push_back((uint8_t)((fields[k] >> (23 - i)) & 1));
    std::string s;
    for (size_t i = 0; i + 7 <= bits.size(); i += 7) {
        unsigned c = 0;
        for (int j = 0; j < 7; j++) c = (c << 1) | bits[i + (size_t)j];
        if (c == 0) break;
        s += (c >= 0x20 && c < 0x7F) ? (char)c : '.';
    }
    return s;
}

// ---------------------------------------------------------------- waveform
namespace {
constexpr double kAlpha = 0.35;
double rrc(double t) {   // root raised cosine, t in chip periods
    const double a = kAlpha, pi = 3.14159265358979323846;
    if (std::fabs(t) < 1e-9) return 1 - a + 4 * a / pi;
    if (std::fabs(std::fabs(t) - 1 / (4 * a)) < 1e-9) return a / std::sqrt(2.0) * ((1 + 2 / pi) * std::sin(pi / (4 * a)) + (1 - 2 / pi) * std::cos(pi / (4 * a)));
    return (std::sin(pi * t * (1 - a)) + 4 * a * t * std::cos(pi * t * (1 + a))) / (pi * t * (1 - (4 * a * t) * (4 * a * t)));
}
constexpr int kSpan = 6;   // chips each side

// the reference of one bit: its chips shaped by the pulse, circular (a bit's neighbours are taken to start the same way), unit energy per sample
const std::vector<float>& refWave() {
    static const std::vector<float> ref = [] {
        const int L = kChips * kSps;
        std::vector<float> r((size_t)L, 0.f);
        const std::vector<uint8_t>& seq = chipSequence();
        for (int i = 0; i < kChips; i++) {
            const float a = seq[(size_t)i] ? -1.f : 1.f;
            for (int n = -kSpan * kSps; n <= kSpan * kSps; n++) {
                const long idx = (long)i * kSps + n;
                r[(size_t)((idx % L + L) % L)] += a * (float)rrc(n / (double)kSps);
            }
        }
        double e = 0;
        for (float v : r) e += (double)v * v;
        const float k = (float)std::sqrt((double)L / e);
        for (float& v : r) v *= k;
        return r;
    }();
    return ref;
}
} // namespace

std::vector<cf32> modulate(const std::vector<uint8_t>& chips, double freqHz, double chipRateHz, double clockPpm, double phase) {
    const double sps = kSps / (1.0 + clockPpm * 1e-6);   // output samples per chip
    const size_t nOut = (size_t)((double)chips.size() * sps);
    std::vector<cf32> out(nOut);
    for (size_t n = 0; n < nOut; n++) {
        const double tc = (double)n / sps;   // chip units
        const long c0 = (long)std::floor(tc);
        double v = 0;
        for (long c = c0 - kSpan; c <= c0 + kSpan; c++) {
            if (c < 0 || c >= (long)chips.size()) continue;
            v += (chips[(size_t)c] ? -1.0 : 1.0) * rrc(tc - (double)c);
        }
        out[n] = cf32((float)v, 0.f);
    }
    double e = 0;
    for (const auto& s : out) e += std::norm(s);
    const float k = e > 0 ? (float)std::sqrt((double)nOut / e) : 1.f;
    const double w = 2.0 * 3.14159265358979323846 * freqHz / (chipRateHz * kSps);
    for (size_t n = 0; n < nOut; n++) {
        const double ph = phase + w * (double)n;
        out[n] *= k * cf32((float)std::cos(ph), (float)std::sin(ph));
    }
    return out;
}

// ---------------------------------------------------------------- receiver
namespace {
constexpr int L = kChips * kSps;
const int kLevels[4] = {8, 16, 32, 64};
constexpr double kSpanHz = 820.0;

struct AcqResult { bool found = false; long lag = 0; double freqHz = 0; double stat = 0, thr = 0; };

double gammaTailLog(double x, int n) {   // ln P(Gamma(n,1) > x)
    double mx = -1e300;
    std::vector<double> t((size_t)n);
    for (int i = 0; i < n; i++) { t[(size_t)i] = i * std::log(x) - std::lgamma(i + 1.0); mx = std::max(mx, t[(size_t)i]); }
    double sum = 0;
    for (double v : t) sum += std::exp(v - mx);
    return -x + mx + std::log(sum);
}

AcqResult acquire(const std::vector<cf32>& win, int slices, double chipRateHz) {
    AcqResult res;
    const int log2n = 15;
    const double bin = chipRateHz * kSps / L;
    const int M = (int)std::ceil(kSpanHz / bin);
    const std::vector<float>& ref = refWave();
    std::vector<float> rr(ref), ri((size_t)L, 0.f);
    fftSplit(rr.data(), ri.data(), log2n, false);
    for (auto& v : ri) v = -v;   // conjugate
    std::vector<std::vector<float>> xr((size_t)slices, std::vector<float>((size_t)L)), xi((size_t)slices, std::vector<float>((size_t)L));
    for (int s = 0; s < slices; s++) {
        for (int n = 0; n < L; n++) { const cf32 v = win[(size_t)s * L + (size_t)n]; xr[(size_t)s][(size_t)n] = v.real(); xi[(size_t)s][(size_t)n] = v.imag(); }
        fftSplit(xr[(size_t)s].data(), xi[(size_t)s].data(), log2n, false);
    }
    std::vector<float> acc((size_t)L), yr((size_t)L), yi((size_t)L);
    double best = -1, sumAll = 0;
    long bestLag = 0; int bestM = 0;
    for (int m = -M; m <= M; m++) {
        std::fill(acc.begin(), acc.end(), 0.f);
        for (int s = 0; s < slices; s++) {
            const float* ar = xr[(size_t)s].data(); const float* ai = xi[(size_t)s].data();
            for (int k = 0; k < L; k++) {
                const int j = ((k + m) % L + L) % L;
                const float a = ar[j], b = ai[j], c = rr[(size_t)k] , d = ri[(size_t)k];
                yr[(size_t)k] = a * c - b * d;
                yi[(size_t)k] = a * d + b * c;
            }
            fftSplit(yr.data(), yi.data(), log2n, true);
            for (int k = 0; k < L; k++) acc[(size_t)k] += yr[(size_t)k] * yr[(size_t)k] + yi[(size_t)k] * yi[(size_t)k];
        }
        for (int k = 0; k < L; k++) {
            sumAll += acc[(size_t)k];
            if (acc[(size_t)k] > best) { best = acc[(size_t)k]; bestLag = k; bestM = m; }
        }
    }
    const double cells = (double)(2 * M + 1) * L;
    const double mean = sumAll / cells;
    res.stat = mean > 0 ? best * slices / mean : 0;
    // threshold: the false alarm rate over all cells below 1e-4
    double lo = slices, hi = slices + 200;
    const double target = std::log(1e-4 / cells);
    for (int it = 0; it < 60; it++) { const double mid = 0.5 * (lo + hi); if (gammaTailLog(mid, slices) > target) lo = mid; else hi = mid; }
    res.thr = hi;
    res.found = res.stat > res.thr;
    res.lag = bestLag;
    res.freqHz = bestM * bin;
    return res;
}

struct TrackState {
    double f = 0;          // Hz, our carrier estimate
    double psi = 0;        // NCO phase, rad
    long long pos = 0;     // absolute sample index of the start of the next bit
    double tauAcc = 0, rate = 0;   // timing: fractional shift, clock rate in samples per bit
    cf32 prev{0, 0};
    bool havePrev = false;
    double phiSm = 0;
    int bit = 0;
    double snrAvg = 0, sigAvg = 0, noiseAvg = 0;
    int badRun = 0;
};
} // namespace

struct Rx::Impl {
    double chipRateHz;
    bool async;
    double fs;
    std::vector<cf32> buf;
    long long base = 0, total = 0;
    int level = 0;
    std::future<AcqResult> job;
    bool jobRunning = false;
    long long jobStart = 0, lastJobTotal = -(1LL << 60);
    int jobSlices = 0;
    bool tracking = false;
    TrackState tr;
    std::vector<float> soft;
    long long softBase = 0;
    Stats st;
    std::map<uint64_t, long long> lastSeen;
    double snrMean = 0;
    int searchesBeforeEscalate = 0;

    Impl(double cr, bool as) : chipRateHz(cr), async(as), fs(cr * kSps) {}

    void startJob() {
        const int slices = kLevels[level];
        const size_t need = (size_t)(slices + 1) * L;
        if (buf.size() < need || total - lastJobTotal < (long long)need) return;
        lastJobTotal = total;
        // the newest data
        jobStart = base + (long long)(buf.size() - need);
        std::vector<cf32> w(buf.end() - (long)need, buf.end() - (long)L);
        // the window is slices*L long; the extra bit of margin stays in the buffer
        const double cr = chipRateHz;
        jobSlices = slices;
        st.searches++;
        if (async) {
            jobRunning = true;
            job = std::async(std::launch::async, [w = std::move(w), slices, cr]() { return acquire(w, slices, cr); });
        } else {
            finishJob(acquire(w, slices, cr));
        }
    }

    void finishJob(const AcqResult& r) {
        jobRunning = false;
        if (!r.found) {
            searchesBeforeEscalate++;
            if (searchesBeforeEscalate >= 2 && level < 3) { level++; searchesBeforeEscalate = 0; }
            // forget the old data: start again from the newest
            const long long keep = (long long)(kLevels[level] + 1) * L;
            if ((long long)buf.size() > keep) { const long long drop = (long long)buf.size() - keep; buf.erase(buf.begin(), buf.begin() + drop); base += drop; }
            return;
        }
        // a bit boundary at jobStart + lag + k*L, the first one that leaves a few samples of margin
        long long p = jobStart + r.lag;
        while (p - 8 < base) p += L;
        tr = TrackState();
        tr.f = r.freqHz;
        tr.pos = p;
        tracking = true;
        soft.clear(); softBase = 0;
        st.acquired = true;
        level = 0; searchesBeforeEscalate = 0;
    }

    void trim() {
        if (tracking) {
            const long long keep = tr.pos - 16;
            if (keep > base + 4 * L) { const long long drop = keep - base - 2 * L; buf.erase(buf.begin(), buf.begin() + drop); base += drop; }
        } else if (!jobRunning) {
            const long long keep = (long long)(kLevels[3] + 2) * L;
            if ((long long)buf.size() > keep) { const long long drop = (long long)buf.size() - keep; buf.erase(buf.begin(), buf.begin() + drop); base += drop; }
        } else if ((long long)buf.size() > 6LL * 1024 * 1024) {   // a job that takes too long: give up on its data
            jobRunning = false;
            buf.erase(buf.begin(), buf.end() - (long)(kLevels[level] + 1) * L);
            base = total - (long long)buf.size();
        }
    }

    // one bit
    bool trackBit(std::vector<Frame>& out) {
        const long long need = tr.pos + L + 16;
        if (need > base + (long long)buf.size()) return false;
        const std::vector<float>& ref = refWave();
        const double w = 2.0 * 3.14159265358979323846 * tr.f / fs;
        const long long off = tr.pos - base;
        // mixed samples for n in [-2, L+2)
        static thread_local std::vector<cf32> y;
        y.resize((size_t)L + 4);
        double ph = tr.psi - w * 2.0;
        double pr = std::cos(-ph), pi_ = std::sin(-ph);
        const double rr = std::cos(-w), ri = std::sin(-w);
        for (int n = 0; n < L + 4; n++) {
            y[(size_t)n] = buf[(size_t)(off - 2 + n)] * cf32((float)pr, (float)pi_);
            const double nr = pr * rr - pi_ * ri, ni = pr * ri + pi_ * rr;
            pr = nr; pi_ = ni;
            if ((n & 1023) == 1023) { const double m = std::sqrt(pr * pr + pi_ * pi_); pr /= m; pi_ /= m; }
        }
        auto corr = [&](int d, int from, int to) {
            double a = 0, b = 0;
            for (int n = from; n < to; n++) { const cf32 v = y[(size_t)(n + 2 + d)]; a += (double)v.real() * ref[(size_t)n]; b += (double)v.imag() * ref[(size_t)n]; }
            return cf32((float)a, (float)b);
        };
        const cf32 p = corr(0, 0, L), pa = corr(0, 0, L / 2), pb = corr(0, L / 2, L), pe = corr(2, 0, L), pl = corr(-2, 0, L);
        // noise: a code phase far from the signal's
        const int nOff = 9000;
        double nA = 0, nB = 0;
        for (int n = 0; n < L - nOff - 4; n++) { const cf32 v = y[(size_t)(n + 2 + nOff)]; nA += (double)v.real() * ref[(size_t)n]; nB += (double)v.imag() * ref[(size_t)n]; }
        // the noise correlation's power, scaled to a full bit
        const double nPow = (nA * nA + nB * nB) * (double)L / (double)(L - nOff - 4);
        const double sig = (double)std::norm(p);
        tr.sigAvg = tr.bit == 0 ? sig : 0.95 * tr.sigAvg + 0.05 * sig;
        tr.noiseAvg = tr.bit == 0 ? nPow : 0.95 * tr.noiseAvg + 0.05 * nPow;
        tr.snrAvg = std::max(0.0, (tr.sigAvg - tr.noiseAvg) / std::max(tr.noiseAvg, 1e-30));
        // frequency: the phase between the two halves of the bit
        const double dMeas = std::arg(pb * std::conj(pa)) * fs / (3.14159265358979323846 * L);
        tr.psi += w * (double)L;
        tr.f += (tr.bit < 24 ? 0.4 : 0.12) * dMeas;
        // timing
        const double ep = std::abs(pe), lp = std::abs(pl);
        const double err = (ep - lp) / (ep + lp + 1e-30);
        long long shift = 0;
        if (tr.bit >= 4) {
            tr.rate += 0.05 * err;
            tr.tauAcc += 3.0 * err + tr.rate;
            while (tr.tauAcc >= 1.0) { shift++; tr.tauAcc -= 1.0; }
            while (tr.tauAcc <= -1.0) { shift--; tr.tauAcc += 1.0; }
        }
        // differential detection
        if (tr.havePrev) {
            const cf32 z = p * std::conj(tr.prev);
            const double phi = 0.5 * std::arg(z * z);
            if (tr.bit < 24) tr.phiSm = phi; else tr.phiSm += 0.15 * std::remainder(phi - tr.phiSm, 3.14159265358979323846);
            const cf32 zr = z * cf32((float)std::cos(-tr.phiSm), (float)std::sin(-tr.phiSm));
            if (tr.bit >= 24) {
                soft.push_back(zr.real() / (float)std::max(1.0, std::norm(p) * 0.5 + std::norm(tr.prev) * 0.5));
                st.bits++;
            }
        }
        tr.prev = p; tr.havePrev = true;
        tr.pos += L + shift;
        tr.bit++;
        st.offsetHz = tr.f;
        st.snrDb = (float)(10 * std::log10(std::max(tr.snrAvg, 1e-3)));
        st.clockPpm = -tr.rate / L * 1e6;   // positive: its chips come faster than our clock
        // lost?
        if (tr.bit > 40) {
            if (tr.snrAvg < 0.4) tr.badRun++; else tr.badRun = 0;
            if (tr.badRun > 60) { tracking = false; st.acquired = false; soft.clear(); }
        }
        if (!soft.empty() && soft.size() % kFrameBits == 0) trySync(out);
        return true;
    }

    void trySync(std::vector<Frame>& out) {
        const int W = (int)std::min<size_t>(soft.size(), kFrameBits * 6);
        if (W < kFrameBits * 2) return;
        const float* s = soft.data() + (soft.size() - (size_t)W);
        const long long wAbs = softBase + (long long)(soft.size() - (size_t)W);
        uint32_t uw = kUw;
        float u[kUwBits];
        for (int i = 0; i < kUwBits; i++) u[i] = ((uw >> (kUwBits - 1 - i)) & 1) ? -1.f : 1.f;
        double bestM = -1, sumM = 0;
        int bestO = 0;
        for (int o = 0; o < kFrameBits; o++) {
            double M = 0;
            for (int pos = o; pos + kUwBits <= W; pos += kFrameBits) {
                double c = 0;
                for (int i = 0; i < kUwBits; i++) c += s[pos + i] * u[i];
                M += std::fabs(c);
            }
            sumM += M;
            if (M > bestM) { bestM = M; bestO = o; }
        }
        const double meanM = sumM / kFrameBits;
        if (!(bestM > 1.6 * meanM)) return;
        // frames at bestO + k*244
        std::vector<int> starts;
        for (int pos = bestO; pos + kFrameBits <= W; pos += kFrameBits) starts.push_back(pos);
        bool decodedAny = false;
        for (size_t j = 0; j < starts.size(); j++) {
            for (int g = std::min<int>(kRepeat, (int)(starts.size() - j)); g >= 1; g--) {
                uint8_t bits[kFrameBits];
                for (int i = 0; i < kFrameBits; i++) {
                    float acc = 0;
                    for (int c = 0; c < g; c++) acc += s[starts[j + (size_t)c] + i];
                    bits[i] = acc < 0 ? 1 : 0;
                }
                Frame fr;
                if (!parseFrame(bits, fr)) continue;
                decodedAny = true;
                const long long abs = wAbs + starts[j];
                const uint64_t key = (fr.guid * 1000003ull) ^ ((uint64_t)fr.fields[0].info << 8) ^ ((uint64_t)fr.fields[1].info << 20) ^ ((uint64_t)fr.fields[0].contentId << 3) ^ ((uint64_t)fr.fields[1].contentId << 40) ^ (fr.second ? 1 : 0);
                auto it = lastSeen.find(key);
                if (it == lastSeen.end() || abs - it->second >= kFrameBits * kRepeat) {
                    lastSeen[key] = abs;
                    st.frames++;
                    st.report.add(fr);
                    out.push_back(fr);
                } else it->second = std::max(it->second, abs);
                break;
            }
        }
        if (!decodedAny) st.badFrames++;
        if (soft.size() > (size_t)kFrameBits * 12) {
            const size_t drop = soft.size() - (size_t)kFrameBits * 8;
            soft.erase(soft.begin(), soft.begin() + (long)drop);
            softBase += (long long)drop;
        }
    }
};

// ---------------------------------------------------------------- tap
Tap::Tap(double fs, double chipRateHz) : fs_(fs), fsOut_(chipRateHz * kSps) {
    if (fs < 1.1 * fsOut_) return;
    dec_ = std::max(1, (int)std::floor(fs / fsOut_ / 1.0));
    const double fsDec = fs / dec_;
    // low pass at 0.4 of the output rate, Blackman window, about 8 decimated samples long on each side
    const int half = std::max(15, 4 * dec_);
    const double fc = 0.4 * fsOut_ / fs;   // cycles per input sample
    taps_.resize((size_t)(2 * half + 1));
    double sum = 0;
    for (int i = -half; i <= half; i++) {
        const double x = (double)i;
        const double sinc = i == 0 ? 2 * fc : std::sin(2 * 3.14159265358979323846 * fc * x) / (3.14159265358979323846 * x);
        const double wdw = 0.42 + 0.5 * std::cos(3.14159265358979323846 * x / (half + 1)) + 0.08 * std::cos(2 * 3.14159265358979323846 * x / (half + 1));
        taps_[(size_t)(i + half)] = (float)(sinc * wdw);
        sum += sinc * wdw;
    }
    for (float& t : taps_) t = (float)(t / sum);
    (void)fsDec;
    ok_ = true;
}

void Tap::process(const cf32* x, size_t n, double hostCenterHz, std::vector<cf32>& out) {
    if (!ok_) return;
    const double w = -2.0 * 3.14159265358979323846 * hostCenterHz / fs_;
    const size_t nt = taps_.size();
    const double step = fs_ / dec_ / fsOut_;   // decimated samples per output sample, >= 1
    for (size_t i = 0; i < n; i++) {
        hist_.push_back(x[i] * cf32((float)std::cos(phase_), (float)std::sin(phase_)));
        phase_ += w;
        if (phase_ > 3.14159265358979323846) phase_ -= 2 * 3.14159265358979323846; else if (phase_ < -3.14159265358979323846) phase_ += 2 * 3.14159265358979323846;
        if (hist_.size() >= nt) {
            if (++decCount_ % (size_t)dec_ == 0) {
                cf32 acc(0, 0);
                for (size_t k = 0; k < nt; k++) acc += hist_[hist_.size() - nt + k] * taps_[k];
                dsBuf_.push_back(acc);
            }
            if (hist_.size() > nt * 4) hist_.erase(hist_.begin(), hist_.end() - (long)nt);
        }
    }
    // linear interpolation to the output rate
    while (frac_ + 1.0 < (double)dsBuf_.size()) {
        const size_t k = (size_t)frac_;
        const float a = (float)(frac_ - (double)k);
        out.push_back(dsBuf_[k] * (1.f - a) + dsBuf_[k + 1] * a);
        frac_ += step;
    }
    const size_t drop = std::min((size_t)frac_, dsBuf_.size());
    if (drop > 0) { dsBuf_.erase(dsBuf_.begin(), dsBuf_.begin() + (long)drop); frac_ -= (double)drop; }
}

Rx::Rx(double chipRateHz, bool async) : p_(std::make_unique<Impl>(chipRateHz, async)) {}
Rx::~Rx() { if (p_->jobRunning && p_->job.valid()) p_->job.wait(); }
Stats Rx::stats() const { return p_->st; }
void Rx::reset() {
    if (p_->jobRunning && p_->job.valid()) p_->job.wait();
    const double cr = p_->chipRateHz; const bool as = p_->async;
    p_ = std::make_unique<Impl>(cr, as);
}

void Rx::push(const cf32* x, size_t n, std::vector<Frame>& out) {
    Impl& I = *p_;
    I.buf.insert(I.buf.end(), x, x + n);
    I.total += (long long)n;
    if (I.jobRunning && I.job.valid() && I.job.wait_for(std::chrono::seconds(0)) == std::future_status::ready) I.finishJob(I.job.get());
    if (I.tracking) {
        while (I.trackBit(out)) {}
        I.trim();
    } else if (!I.jobRunning) {
        I.startJob();
        if (I.tracking) { while (I.trackBit(out)) {} }
        I.trim();
    } else I.trim();
}

} // namespace dvbcid
} // namespace dect2
