// DVB-S2X super-frames (see dvbs_sf.h). Adapted from DecSAT by Arclamp (CasualArclamp), GPL-3.0-or-later (decsat-frame and decsat-engine, superframe.rs).
#include "dvbs_sf.h"
#include <algorithm>
#include <cmath>

namespace dect2 {
namespace dvbs {

namespace {
const float kDiag = 0.70710678f;
const cf32 DIAG(kDiag, kDiag);
const float FIND = 0.35f, CONFIRM = 0.12f, SFFI_FITS = 0.7f;
const uint32_t MISSES = 2;
const size_t DELAY = 32768;
const uint64_t SEARCH_SUPERFRAMES = 3;
const int FFT_LEN = 8192;
const int PILOT_FIELDS = 8;
const float PILOTS_ON = 0.4f;
const uint64_t SLACK = 2;
const int kSffiSpread = 30;

float walsh(int row, int col) { return __builtin_popcount((unsigned)(row & col)) % 2 == 0 ? 1.f : -1.f; }

// frequency of a tone in noise, cycles per symbol: a lag-1 differential for an unambiguous coarse value, refined at each longer lag in turn
double toneFreq(const std::vector<cf32>& b, const int* lags, int nl) {
    double f = 0;
    for (int li = 0; li < nl; li++) {
        const size_t lag = (size_t)lags[li];
        std::complex<double> acc(0, 0);
        for (size_t n = 0; n + lag < b.size(); n++) acc += std::complex<double>(b[n + lag]) * std::conj(std::complex<double>(b[n]));
        const double w = -6.283185307179586 * f * (double)lag;
        const double resid = std::arg(acc * std::complex<double>(std::cos(w), std::sin(w)));
        f += resid / (6.283185307179586 * (double)lag);
    }
    return f;
}

const std::vector<uint8_t>& mSeq(int which) {
    // Built once under the static-initialisation lock, so concurrent first calls from several threads are safe.
    struct Seqs {
        std::vector<uint8_t> x, y;
        Seqs() {
            const size_t N = (1u << 20) - 1;
            x.assign(N, 0); y.assign(N, 0);
            x[0] = 1;
            for (int i = 0; i < 20; i++) y[(size_t)i] = 1;
            for (size_t i = 0; i + 20 < N; i++) {   // 1 + x^3 + x^20 and 1 + y^2 + y^11 + y^17 + y^20
                x[i + 20] = x[i + 3] ^ x[i];
                y[i + 20] = y[i + 17] ^ y[i + 11] ^ y[i + 2] ^ y[i];
            }
        }
    };
    static const Seqs s;
    return which ? s.y : s.x;
}
} // namespace

const char* sfFormatName(int format) {
    switch (format) {
    case 0: return "DVB-S2X";
    case 1: return "DVB-S2 legacy";
    case 2: return "bundled PLFRAMEs (64 800 payload) with SF-pilots";
    case 3: return "bundled PLFRAMEs (16 200 payload) with SF-pilots";
    case 4: return "flexible format with VL-SNR PLH tracking";
    case 5: return "periodic beam hopping";
    case 6: return "traffic-driven beam hopping";
    case 7: return "simplified traffic-driven beam hopping";
    default: return "reserved";
    }
}

std::vector<float> sfSosfSequence(int i) {
    std::vector<float> h(kSosfLen);
    for (int c = 0; c < kSosfLen; c++) h[(size_t)c] = c < 256 ? walsh(i, c) : walsh(i % 16, c - 256 + 1);
    return h;
}

std::vector<float> sfPilotSequence(int i) {
    std::vector<float> h(kSfPilotLen);
    for (int c = 0; c < kSfPilotLen; c++) h[(size_t)c] = c < 32 ? walsh(i, c) : walsh(i % 4, c - 32);
    return h;
}

void sfCodeword(int format, bool out[15]) {
    for (int j = 0; j < 15; j++) out[j] = (__builtin_popcount((unsigned)((format & 0x0F) & (j + 1))) % 2) == 1;
}

int sfDecodeSffi(const float soft[15], float& match) {
    float total = 1e-12f;
    for (int j = 0; j < 15; j++) total += std::fabs(soft[j]);
    int best = 0;
    float bm = -1e9f;
    for (int f = 0; f < 16; f++) {
        bool c[15];
        sfCodeword(f, c);
        float corr = 0;
        for (int j = 0; j < 15; j++) corr += c[j] ? -soft[j] : soft[j];
        corr /= total;
        if (corr > bm) { bm = corr; best = f; }
    }
    match = bm;
    return best;
}

SfScrambler::SfScrambler(uint32_t n) {
    const size_t N = (1u << 20) - 1;
    const auto& x = mSeq(0);
    const auto& y = mSeq(1);
    const size_t nn = (size_t)n % N;
    auto z = [&](size_t i) { return (uint8_t)(x[(i + nn) % N] ^ y[i]); };
    quarter.resize((size_t)kSfl);
    for (size_t i = 0; i < (size_t)kSfl; i++) quarter[i] = (uint8_t)(2 * z((i + 524288) % N) + z(i));
}

cf32 SfScrambler::factor(size_t i) const {
    switch (quarter[i]) { case 0: return cf32(1, 0); case 1: return cf32(0, 1); case 2: return cf32(-1, 0); default: return cf32(0, -1); }
}

SfPart sfPart(size_t k, bool pilots) {
    if (k < (size_t)kSosfLen) return SfPart::Sosf;
    if (k < (size_t)kSfHead) return SfPart::Sffi;
    if (pilots && k % (size_t)kSfPilotPeriod >= (size_t)kSfPilotAfter) return SfPart::Pilot;
    return SfPart::Payload;
}

std::vector<cf32> sfSosfSymbols(int seq, const SfScrambler& ref) {
    const auto h = sfSosfSequence(seq);
    std::vector<cf32> v(h.size());
    for (size_t i = 0; i < h.size(); i++) v[i] = DIAG * h[i] * ref.factor(i);
    return v;
}

std::vector<cf32> sfSffiSymbols(int format, const SfScrambler& payload) {
    bool c[15];
    sfCodeword(format, c);
    std::vector<cf32> v;
    for (int b = 0; b < 15; b++) for (int k = 0; k < kSffiSpread; k++) { const size_t idx = (size_t)(kSosfLen + b * kSffiSpread + k); v.push_back(DIAG * (c[b] ? -1.f : 1.f) * payload.factor(idx)); }
    return v;
}

std::vector<cf32> sfPilotSymbols(int seq, const SfScrambler& ref, size_t at) {
    const auto h = sfPilotSequence(seq);
    std::vector<cf32> v((size_t)kSfPilotLen);
    for (size_t k = 0; k < v.size(); k++) v[k] = DIAG * h[k] * ref.factor(at + k);
    return v;
}

// ---------------------------------------------------------------- the stripper

SfStripper::SfStripper() : reference_(0), payload_(0) {
    sosf_ = sfSosfSymbols(0, reference_);
    sosfDiff_.resize((size_t)kSosfLen - 1);
    for (size_t m = 1; m < (size_t)kSosfLen; m++) sosfDiff_[m - 1] = sosf_[m] * std::conj(sosf_[m - 1]);
    fft_ = std::make_unique<Fft>(FFT_LEN);
    kernel_.assign((size_t)FFT_LEN, cf32(0, 0));
    std::copy(sosfDiff_.begin(), sosfDiff_.end(), kernel_.begin());
    fft_->forward(kernel_.data());
    for (auto& k : kernel_) k = std::conj(k);
    work_.assign((size_t)FFT_LEN, cf32(0, 0));
    pilot_ = sfPilotSequence(0);
    reset();
}

void SfStripper::reset() {
    buf_.clear(); base_ = scan_ = 0; haveStart_ = false; misses_ = 0;
    budget_ = SEARCH_SUPERFRAMES * (uint64_t)kSfl;
    w_ = corr_ = 0;
    view_ = SfView();
}

void SfStripper::push(const cf32* in, size_t n, std::vector<cf32>& out) {
    buf_.insert(buf_.end(), in, in + n);
    for (;;) {
        if (!haveStart_) {
            if (view_.noneFound) { release(base_ + buf_.size(), out); return; }
            if (!search()) { release(scan_, out); return; }   // everything before the next place a super-frame might start goes on as it is
        }
        // locked: pass symbols on, stripped, up to DELAY short of the newest, reading each super-frame as its start comes up
        const uint64_t end = base_ + buf_.size();
        const uint64_t limit = end > DELAY ? end - DELAY : 0;
        if (!releaseLocked(limit, out)) return;
    }
}

void SfStripper::release(uint64_t upto, std::vector<cf32>& out) {
    const size_t n = std::min<size_t>(upto > base_ ? (size_t)(upto - base_) : 0, buf_.size());
    out.insert(out.end(), buf_.begin(), buf_.begin() + (long)n);
    buf_.erase(buf_.begin(), buf_.begin() + (long)n);
    base_ += n;
}

bool SfStripper::releaseLocked(uint64_t limit, std::vector<cf32>& out) {
    uint64_t n = base_;
    std::vector<cf32> kept;
    kept.reserve(buf_.size());
    bool lost = false;
    while (n < limit) {
        if (n == start_ + (uint64_t)kSfl) {   // the next super-frame is due: read it first
            if (heldFrom(n) < (size_t)(kSfHead + kSfPilotAfter + PILOT_FIELDS * kSfPilotPeriod)) break;
            uint64_t s;
            if (nextSuperframe(n, s)) { while (n < s) { kept.push_back(at(n)); n++; } continue; }   // a slip of a symbol or two: pass the ones skipped
            lost = true;
            break;
        }
        const uint64_t q = n - start_;
        const bool own = view_.carriesPlframes() && sfPart((size_t)q, view_.pilots) != SfPart::Payload;
        // the carrier turns on through the symbols that are taken out: the ones after the gap are turned back by what the gap carried, so the PL demodulator does not see
        // a phase jump (a frame with one in its middle would not decode)
        if (own) { corr_ += w_; if (corr_ > 3.141592653589793) corr_ -= 6.283185307179586; else if (corr_ < -3.141592653589793) corr_ += 6.283185307179586; }
        else kept.push_back(corr_ == 0.0 ? at(n) : at(n) * std::polar(1.0f, (float)-corr_));
        n++;
    }
    out.insert(out.end(), kept.begin(), kept.end());
    buf_.erase(buf_.begin(), buf_.begin() + (long)(n - base_));
    base_ = n;
    if (lost) {
        haveStart_ = false; view_.locked = false; view_.searching = true; view_.losses++; corr_ = 0;
        scan_ = base_; budget_ = SEARCH_SUPERFRAMES * (uint64_t)kSfl;
        return true;
    }
    return false;
}

bool SfStripper::nextSuperframe(uint64_t due, uint64_t& at_) {
    uint64_t best = due, lo = due > SLACK ? due - SLACK : 0;
    if (lo < base_) lo = base_;
    float bm = -1;
    for (uint64_t p = lo; p <= due + SLACK; p++) { const float m = sosfMatch(p); if (m > bm) { bm = m; best = p; } }
    uint64_t chosen;
    if (bm > CONFIRM) { misses_ = 0; chosen = best; }
    else { misses_++; if (misses_ >= MISSES) return false; chosen = due; }
    if (chosen != due) view_.slips++;
    readSuperframe(chosen);
    start_ = chosen;
    at_ = chosen;
    return true;
}

float SfStripper::sosfMatch(uint64_t p) const {
    std::complex<double> acc(0, 0);
    double mag = 0;
    for (size_t m = 1; m < (size_t)kSosfLen; m++) {
        const cf32 d = at(p + m) * std::conj(at(p + m - 1));
        acc += std::complex<double>(d * std::conj(sosfDiff_[m - 1]));
        mag += std::abs(d);
    }
    return (float)(std::abs(acc) / std::max(mag, 1e-12));
}

void SfStripper::readSuperframe(uint64_t p) {
    const float sm = sosfMatch(p);
    // the SOSF with its modulation taken off is a tone at the carrier offset: its frequency over growing lags, then its phase
    std::vector<cf32> tone((size_t)kSosfLen);
    for (size_t m = 0; m < (size_t)kSosfLen; m++) tone[m] = at(p + m) * std::conj(sosf_[m]);
    static const int lags[5] = {1, 4, 16, 64, 160};
    const float w = (float)(6.283185307179586 * toneFreq(tone, lags, 5));
    w_ = (double)w;
    std::complex<float> z(0, 0);
    for (size_t m = 0; m < tone.size(); m++) z += tone[m] * std::polar(1.0f, -w * (float)m);
    const float phase = std::arg(z);
    float soft[15] = {};
    for (int b = 0; b < 15; b++)
        for (int k = 0; k < 30; k++) {
            const size_t i = (size_t)(kSosfLen + 30 * b + k);
            const cf32 rot = std::polar(1.0f, -(phase + w * (float)i));
            const cf32 y = at(p + i) * std::conj(DIAG * payload_.factor(i)) * rot;
            soft[b] += y.real();
        }
    float sffiMatch;
    const int format = sfDecodeSffi(soft, sffiMatch);
    float pilots = 0;
    for (int f = 0; f < PILOT_FIELDS; f++) {
        const size_t atp = (size_t)(kSfPilotAfter + f * kSfPilotPeriod);
        std::complex<double> acc(0, 0);
        double mag = 0;
        for (size_t k = 1; k < (size_t)kSfPilotLen; k++) {
            auto r = [&](size_t kk) { return DIAG * pilot_[kk] * reference_.factor(atp + kk); };
            const cf32 d = at(p + atp + k) * std::conj(at(p + atp + k - 1));
            acc += std::complex<double>(d * std::conj(r(k) * std::conj(r(k - 1))));
            mag += std::abs(d);
        }
        pilots += (float)(std::abs(acc) / std::max(mag, 1e-12));
    }
    view_.sosfMatch = sm; view_.sffiMatch = sffiMatch; view_.format = format;
    view_.pilots = pilots / PILOT_FIELDS > PILOTS_ON;
    view_.superframes++;
}

bool SfStripper::search() {
    const size_t need = (size_t)(kSfHead + kSfPilotAfter + PILOT_FIELDS * kSfPilotPeriod);
    while (heldFrom(scan_) > (size_t)FFT_LEN + 1) {
        // differentials of the block: d[j] = y[a+j+1] y*[a+j]
        const uint64_t a = scan_;
        std::vector<float> mags((size_t)FFT_LEN);
        for (int j = 0; j < FFT_LEN; j++) { work_[(size_t)j] = at(a + (uint64_t)j + 1) * std::conj(at(a + (uint64_t)j)); mags[(size_t)j] = std::abs(work_[(size_t)j]); }
        fft_->forward(work_.data());
        for (size_t i = 0; i < work_.size(); i++) work_[i] *= kernel_[i];
        fft_->inverse(work_.data());
        const size_t taps = (size_t)kSosfLen - 1, valid = (size_t)FFT_LEN - taps;
        float norm = 0;
        for (size_t i = 0; i < taps; i++) norm += mags[i];
        const float scale = 1.0f / (float)FFT_LEN;
        long found = -1;
        float fm = 0;
        for (size_t t = 0; t < valid; t++) {
            if (t > 0) norm += mags[t + taps - 1] - mags[t - 1];
            const float m = std::abs(work_[t]) * scale / std::max(norm, 1e-12f);
            if (m > FIND && (found < 0 || m > fm)) { found = (long)t; fm = m; }
        }
        if (found >= 0) {
            const uint64_t p = a + (uint64_t)found;   // a start: read it once the super-frame's head and first pilot fields are in
            if (heldFrom(p) < need) { scan_ = p; return false; }
            readSuperframe(p);
            if (view_.sffiMatch > SFFI_FITS) {
                start_ = p; haveStart_ = true; misses_ = 0;
                view_.locked = true; view_.searching = false;
                return true;   // the symbols before it go on as they are
            }
            view_.superframes--;   // a chance peak: carry on past it
            scan_ = p + 1;
        } else scan_ = a + valid;
        budget_ = budget_ > valid ? budget_ - valid : 0;
        if (budget_ == 0) { view_.searching = false; view_.noneFound = true; return false; }
    }
    return false;
}

} // namespace dvbs
} // namespace dect2
