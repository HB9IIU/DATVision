// DVB-S2 frame receiver, see dvbs_s2rx.h.
#include "dect2/common/thread_name.h"
#include "dvbs_s2rx.h"
#include "dvbs_vlsnr.h"
#include "dvbs_s2refine.h"
#include "dvbs_symthread.h"
#include "dvbs_compute.h"
#include "dect2/dsp/ldpc.h"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <map>
#include <mutex>
#include <thread>
#include "dect2/common/mathconst.h"

namespace dect2 {
namespace dvbs {

#if defined(__GNUC__)
#define DVBS_INLINE inline __attribute__((always_inline))
#elif defined(_MSC_VER)
#define DVBS_INLINE __forceinline
#else
#define DVBS_INLINE inline
#endif

namespace {
using dect2::math::kPi;
const cf32 kPilot(dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f);

// complex product written out (the library one tests for NaN results on every call)
inline cf32 cmul(cf32 a, cf32 b) {
    return cf32(a.real() * b.real() - a.imag() * b.imag(), a.real() * b.imag() + a.imag() * b.real());
}

// e^{-j d} for small d: the loop never turns by more than a fraction of a radian per symbol
inline cf32 smallRot(float d) {
    const float d2 = d * d;
    const float c = 1.f - d2 * 0.5f + d2 * d2 * (1.f / 24.f);
    const float s = d * (1.f - d2 * (1.f / 6.f) + d2 * d2 * (1.f / 120.f));
    return cf32(c, -s);
}

// fast e^x for x <= 0 (weights of the soft detector: a relative error of 1e-4 is plenty)
inline float fexp(float x) {
    x = std::max(x, -60.f);
    const float t = x * 1.44269504f;
    const float fi = std::floor(t), f = t - fi;
    const float p = 1.f + f * (0.69314718f + f * (0.24022651f + f * (0.05550411f + f * 0.00961813f)));
    union { float f; int32_t i; } u;
    u.i = ((int32_t)fi + 127) << 23;
    return p * u.f;
}
inline float ftanh(float x) {
    const float ax = std::min(std::fabs(x), 12.f);
    const float e = fexp(-2.f * ax);
    const float t = (1.f - e) / (1.f + e);
    return x < 0 ? -t : t;
}

// tanh by linear interpolation in a table (2048 steps over 0 .. 12, error below 4e-6): a few cycles of latency where ftanh needs a polynomial and
// a division, which matters in the carrier loop where every symbol waits for the one before
struct TanhTable {
    static constexpr int kN = 2048;
    static constexpr float kMax = 12.f, kScale = (float)kN / kMax;
    struct E { float v, d; };
    E t[kN + 1];
    TanhTable() {
        for (int i = 0; i <= kN; i++) t[i].v = (float)std::tanh((double)i / kScale);
        for (int i = 0; i < kN; i++) t[i].d = t[i + 1].v - t[i].v;
        t[kN].d = 0.f;
    }
};
const TanhTable kTanh;
inline float ftanhTab(float x) {
    const float u = std::min(std::fabs(x), TanhTable::kMax) * TanhTable::kScale;
    const int i = (int)u;
    const TanhTable::E e = kTanh.t[i];
    const float t = e.v + (u - (float)i) * e.d;
    return std::copysign(t, x);
}

struct Job {
    uint64_t seq = 0;
    int modcod = 0, mod = 0, rate = 0;
    bool sh = false;
    std::vector<cf32> syms;
    float sigma2 = 0.1f;
    uint32_t frameSyms = 0;
    int vlIdx = -1;                // >= 0: a VL-SNR frame of this mode (dvbs_vlsnr.h), syms are its data symbols
};

struct Result {
    uint64_t seq = 0;
    uint32_t frameSyms = 0;
    bool dummy = false, dropped = false, ok = false;
    int vlIdx = -1, kbch = 0;
    int iters = 0, bchFixed = 0, mod = 0, rate = 0;
    bool sh = false, gpu = false;
    double berPre = -1;
    std::vector<uint8_t> bb;      // descrambled BBFRAME bits when ok
};

} // namespace

struct S2Rx::Impl {
    S2Rx& o;
    explicit Impl(S2Rx& owner) : o(owner) {}

    std::mutex cbMu;
    std::function<void(const uint8_t*, size_t, double)> pktCb;
    std::function<void(const uint8_t*, size_t, int, int, bool, int)> dataCb;
    std::function<void(const std::string&)> logCb;
    void log(const std::string& s) { std::function<void(const std::string&)> cb; { std::lock_guard<std::mutex> lk(cbMu); cb = logCb; } if (cb) cb(s); }

    // ------------------------------------------------------------------ carrier loop and frame walker (receiver thread)
    double theta = 0, omega = 0;
    float kpD = 0, kiD = 0, kpiD = 0;        // loop gains (kpiD: the phase step per unit of error, proportional and integral part together)
    bool dry = false;                        // trial run for the frequency search: nothing is submitted or counted
    double dryMetric = 0; int dryHeaders = 0; bool dryAbort = false;
    cf32 ph{1.f, 0.f};
    float gain = 1.f;
    float tanhScale = 0.f;                   // 0.70710678 * invSg2 * the table scale: arguments of tanh in table steps
    float sg2 = 0.1f, invSg2 = 10.f;      // noise variance per real dimension and its inverse: what the soft decision detectors work with
    float kdEma = 1.f; int kdN = 0;       // mean of Re(x conj(soft decision)): the detector gain, for the loop gain correction
    float kdGain = 1.f;
    int phCount = 0;
    int pos = 0, L = 0;
    bool haveParams = false, dummy = false, soft = true;
    bool skip = false;                       // a frame of known length that is not decoded here (S2X VL-SNR, reserved PLS codes): the loop runs free
    // VL-SNR frames (EN 302 307-2 clause 5.5.2.5): after the 90 symbol PLHEADER (PLS code 129 / 131) come the 900 symbols of the VL-SNR header, which
    // gives the mode (Walsh index, table 18b), then pilots and data by the layout of figures 17 and 18
    bool vl = false;
    int vlSet = 0, vlIdx = -1;               // vlIdx: mode (table 18a), -2 a VL-SNR dummy frame, -3 a header that was not recognised (the frame is followed only)
    int vlShift = 0;                         // where the scrambling sequence starts (vlScrIndex)
    const std::vector<uint8_t>* vlLay = nullptr;
    cf32 vlHdr[kVlHeaderSymbols];
    cf32 vlAcc[2]{cf32(0.f, 0.f), cf32(0.f, 0.f)};   // pilot correlation under the two scrambling hypotheses (shift 0 and -900)
    bool hdrS2x = false;                     // the last header was an S2X one (b0 = 1): the next is expected to be one too
    float dmin = 1.f;
    int modcod = -1, mod = 0, rate = 0;
    bool sh = false, pil = false;
    S2Dims dims;
    const cf32* constel = nullptr;
    const NearestPoint* nearest = nullptr;
    const std::vector<uint8_t>* rn = nullptr;
    std::vector<cf32> frameSyms;
    cf32 hdr[90];
    double daErr = 0; int daN = 0;           // error energy and count of known symbols in this frame
    double phErr2 = 0; uint64_t phN = 0;
    double merEma = 0;
    int hdrFail = 0;
    int lastHeaderL = 0;                     // frame length since the last header that updated the loop (0: none yet)
    int lastModcod = -1;
    bool vcm = false;
    std::vector<cf32> cellRing;
    size_t cellPos = 0, cellCount = 0;
    SymbolThread th;                         // the frame work runs here
    std::vector<cf32> cellSnap;              // for the interface thread, updated with every frame under statMu
    double phaseSnap = 0; bool lockSnap = false;
    mutable std::mutex statMu;
    S2Stats st;
    uint64_t statSyms = 0;
    uint64_t seqNext = 0;
    int plN = 0;

    // Loop noise bandwidths (times the symbol period). The data loop is narrow: with decisions on noisy symbols a wide loop slips cycles, which costs
    // more than the phase noise of an LNB. The frequency is found before the loop starts (acquire), so it only has to follow drift. Known symbols
    // (SOF, pilots) give a clean phase and may use a wider loop, as long as they are not too noisy.
    double dataBn() const {
        if (vl) return 0.0006;      // the symbols are far below 0 dB: the loop has to average over many of them
        // Narrow near the threshold of the MODCOD (a wide loop slips on noisy symbols), wider with margin (phase noise of the LNB is then the
        // larger part of the error and the decisions are reliable)
        const double esdb = 10 * std::log10(1.0 / (2.0 * (double)sg2));
        const double margin = dummy || mod < 0 ? 0.0 : esdb - s2QefEsN0(mod, rate, sh);
        const double base = mod == kQpsk ? 0.0015 : 0.001, cap = mod == kQpsk ? 0.008 : mod == k8psk ? 0.006 : 0.005;
        return std::min(cap, base * (1.0 + 0.7 * std::max(0.0, margin - 2.0)));
    }
    void setNoise(double v) {
        sg2 = (float)std::max(1e-3, std::min(vl ? 8.0 : 2.0, v));
        invSg2 = 1.f / sg2;
        tanhScale = dect2::math::k1OverSqrt2f * invSg2 * TanhTable::kScale;
    }
    static void gains(double bnT, double zeta, float& kp, float& ki) {
        const double th = bnT / (zeta + 1.0 / (4.0 * zeta));
        const double d = 1.0 + 2.0 * zeta * th + th * th;
        kp = (float)(4.0 * zeta * th / d);
        ki = (float)(4.0 * th * th / d);
    }
    void setLoops() {
        // overdamped: the frequency is found from the headers (see decodeHeader), the loop only has to follow the phase
        gains(dataBn(), 2.0, kpD, kiD);
        kpiD = kpD + kiD;
    }

    void begin(const S2HuntResult& h) {
        theta = h.theta; omega = h.phi;
        ph = std::polar(1.0f, (float)(-theta));
        gain = h.sofScore > 0.2f && h.sofScore < 5.f ? h.sofScore : 1.f;
        { const bool vlh = h.modcod == 64 || h.modcod == 65; vl = vlh; setNoise(h.snrDb > (vlh ? -14.f : -5.f) && h.snrDb < 40.f ? 0.5 * std::pow(10.0, -h.snrDb / 10.0) : 0.1); vl = false; }
        kdGain = 1.f; kdEma = 1.f; kdN = 0;
        // the modulation is not known before the first header is read: the loops are set again then
        setLoops();
        pos = 0; haveParams = false; dummy = false; skip = false;
        hdrS2x = s2ModcodIsS2x(h.modcod);
        frameSyms.clear();
        daErr = 0; daN = 0; hdrFail = 0; lastModcod = -1; vcm = false;
        phErr2 = 0; phN = 0; merEma = 0;
        dryMetric = 0; dryHeaders = 0; dryAbort = false;
        tail.clear(); tailing = false;
        vl = false; vlIdx = -1; vlShift = vlShiftHint; vlAcc[0] = vlAcc[1] = cf32(0.f, 0.f);
        lastHeaderL = 0;
        plN = o.plCode_.load() < 0 ? 0 : o.plCode_.load();
        rn = &s2ScramblingRn(plN);
        cellRing.assign(2048, cf32()); cellPos = 0; cellCount = 0;
        { std::lock_guard<std::mutex> lk(statMu); cellSnap.clear(); phaseSnap = 0; lockSnap = false; }
        { std::lock_guard<std::mutex> lk(statMu); st = S2Stats(); }
        // the first header is at the start: set its parameters from the hunt (the PLS is decoded again as the symbols come in)
        L = 90;
        pilotAcc = cf32(0.f, 0.f);
    }

    // The phase error of a data symbol: Im(x conj(E)), E the expected symbol given x (the mean of the constellation weighted by how likely each point
    // is, which is what an ideal detector at this noise level would use). At high signal to noise ratio E is the nearest point, at low ratio it
    // shrinks toward zero and the error stays small, which is what keeps the loop from chasing noise.
    // tanh(a magnitude scaled by the detector gain) from the table; the argument scale is folded into one constant
    inline float tanhMag(float ax) const {
        const float u = std::min(ax * tanhScale, (float)TanhTable::kN);
        const int i = (int)u;
        const TanhTable::E e = kTanh.t[i];
        return e.v + (u - (float)i) * e.d;
    }
    DVBS_INLINE float ddError(const cf32& x) {
        const float xr = x.real(), xi = x.imag();
        if (!soft) {
            switch (mod) {
            case kQpsk: {
                const float a = dect2::math::k1OverSqrt2f;
                const float sr = xr > 0 ? a : -a, si = xi > 0 ? a : -a;
                return xi * sr - xr * si;
            }
            default: {
                const cf32 sp = constel[nearest->index(xr, xi)];
                return (xi * sp.real() - xr * sp.imag()) / std::max(1e-3f, std::norm(sp));
            }
            }
        }
        float er, ei;
        if (mod == kQpsk) {
            // The error is a * kdGain * (xi sgn(xr) t(xr) - xr sgn(xi) t(xi)), t = tanh of the scaled magnitude: the signs are put on the factors
            // beside the table lookups and the constant factors outside, which keeps the chain from the symbol to the loop correction short.
            const float a = dect2::math::k1OverSqrt2f;
            const float tr = tanhMag(std::fabs(xr)), ti = tanhMag(std::fabs(xi));
            const float xis = std::bit_cast<float>(std::bit_cast<uint32_t>(xi) ^ (std::bit_cast<uint32_t>(xr) & 0x80000000u));
            const float xrs = std::bit_cast<float>(std::bit_cast<uint32_t>(xr) ^ (std::bit_cast<uint32_t>(xi) & 0x80000000u));
            const float err = (a * kdGain) * (xis * tr - xrs * ti);
            er = std::copysign(a * tr, xr); ei = std::copysign(a * ti, xi);
            kdEma += (xr * er + xi * ei - kdEma) * (1.f / 512.f);
            if ((++kdN & 15) == 0) kdGain = 1.f / std::max(0.3f, std::min(1.f, kdEma));
            return err;
        } else {
            // the two nearest points carry the expectation (the third is more than e^-9 weaker at the noise levels where this detector is used)
            int i1, i2;
            nearest->index2(xr, xi, i1, i2);
            const cf32 c1 = constel[i1], c2 = constel[i2];
            const float d1 = (xr - c1.real()) * (xr - c1.real()) + (xi - c1.imag()) * (xi - c1.imag());
            const float d2 = (xr - c2.real()) * (xr - c2.real()) + (xi - c2.imag()) * (xi - c2.imag());
            const float e = fexp(-(d2 - d1) * 0.5f * invSg2);       // weight of the second point against the first
            const float iw = 1.f / (1.f + e);
            er = (c1.real() + e * c2.real()) * iw; ei = (c1.imag() + e * c2.imag()) * iw;
        }
        kdEma += (xr * er + xi * ei - kdEma) * (1.f / 512.f);
        if ((++kdN & 15) == 0) kdGain = 1.f / std::max(0.3f, std::min(1.f, kdEma));
        return (xi * er - xr * ei) * kdGain;
    }

    cf32 pilotAcc{0.f, 0.f};
    // A pilot block is known: its mean phase is the phase error of the loop, with no 90 degree ambiguity. A loop that slipped to the wrong
    // quadrant (the data aided detector holds it there) is put back
    void pilotBlockDone() {
        const float a = std::abs(pilotAcc);
        if (a > 36 * 0.3f) {
            const float psi = std::arg(pilotAcc);
            if (std::fabs(psi) > 0.6f) turnLoop(psi);
            else { const float c = 0.5f * psi; ph *= std::polar(1.0f, -c); theta += c; }      // half of the measured phase error: the pilots are few
        }
        pilotAcc = cf32(0.f, 0.f);
    }
    void turnLoop(float psi) {       // the symbols leaving the loop are turned by psi: take that out
        ph *= std::polar(1.0f, -psi);
        theta += psi;
        { std::lock_guard<std::mutex> lk(statMu); st.phaseJumps++; }
    }

    // The new phase is the old one turned by omega + (kp + ki) * error. The turn by omega alone is known before the error is, it comes in as P (the
    // old phase already turned): what is left to do after the error is a rotation by a small angle (below 0.02 rad: cos and sin to the fourth order
    // are exact to a few 1e-10), so the carrier loop closes faster than with one rotation by the whole step.
    void loopUpdate(float e, const cf32 P) {
        const float de = std::max(-0.5f, std::min(0.5f, e));
        const float kap = kpiD * de, k2 = kap * kap;
        ph = cmul(P, cf32(1.f - k2 * 0.5f, -(kap * (1.f - k2 * (1.f / 6.f)))));
        theta += (float)omega + kap;
        omega += kiD * de;
        if ((++phCount & 31) == 0) ph /= std::abs(ph);
    }

    void step(const cf32 z) {
        // the scaling and the descrambling turn do not depend on the carrier loop and are done on the symbol first
        const float ig = 1.f / gain;
        cf32 zs(z.real() * ig, z.imag() * ig);
        if (vl && pos >= 90) { stepVl(zs); return; }
        if (pos >= 90) zs = s2RotateByR(zs, (4 - (int)(*rn)[pos - 90]) & 3);
        const cf32 P = cmul(ph, smallRot((float)omega));
        const cf32 u = cmul(zs, ph);
        float err = 0;
        bool known = false;
        cf32 ref(0, 0);
        if (pos < 26) {
            ref = s2SofSymbols()[pos]; known = true;
            err = u.imag() * ref.real() - u.real() * ref.imag();
            hdr[pos] = u;
        } else if (pos < 90) {
            hdr[pos] = u;
            // pi/2 BPSK: the point lies on the diagonal (even index) or the anti-diagonal (odd index), a bit is its sign. Soft decision: the mean of the
            // two points weighted by how likely each is at this noise level. An S2X header is turned by 90 degrees after the SOF: the detector for the
            // other orientation would push the loop away from the right phase, so it works on the symbol turned back, the orientation of the header
            // before (the next one is the same in all but a mixed stream)
            const cf32 w = hdrS2x ? cf32(u.imag(), -u.real()) : u;
            const float v = dect2::math::k1OverSqrt2f * ((pos & 1) ? (w.imag() - w.real()) : (w.real() + w.imag()));
            const float t = ftanh(v * invSg2);
            const cf32 s = t * ((pos & 1) ? cf32(-dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f) : cf32(dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f));
            err = w.imag() * s.real() - w.real() * s.imag();
        } else {
            const int i = pos - 90;
            const cf32 x = u;      // (the descrambling turn was applied to the symbol before the loop phase)
            bool isPilot = false;
            if (dummy) isPilot = true;
            else if (pil) { const int per = i % 1476; isPilot = per >= 1440; }
            if (skip) {
                // nothing is known of these symbols: no error for the loop
            } else if (isPilot) {
                known = true; ref = kPilot;
                err = x.imag() * ref.real() - x.real() * ref.imag();
                daErr += std::norm(x - ref); daN++;
                if (!dummy) {
                    pilotAcc += x * std::conj(ref);
                    if (i % 1476 == 1475) pilotBlockDone();
                }
            } else {
                err = ddError(x);
                frameSyms.push_back(x);
                if ((frameSyms.size() & 7) == 0) { cellRing[cellPos] = x; cellPos = (cellPos + 1) & 2047; cellCount++; }
            }
        }
        if (known) { phErr2 += (double)err * err; phN++; }
        loopUpdate(err, P);
        pos++;
        if (pos == 90) decodeHeader();
        else if (pos == L && pos > 90) finishFrame();
    }

    // ---- VL-SNR frames
    // The error of the pi/2 BPSK data aided detector: the symbol j of the data lies on the diagonal (even j) or the anti-diagonal (odd j); the mean of the two
    // points weighted by how likely each is at this noise level is the expected symbol. The detector gain (kdEma) scales the error, which at -9 dB is
    // otherwise a fraction of what the loop design assumes.
    float vlBpskError(const cf32& w, int j) {
        const float v = dect2::math::k1OverSqrt2f * ((j & 1) ? (w.imag() - w.real()) : (w.real() + w.imag()));
        const float t = ftanh(v * invSg2);
        const cf32 s = t * ((j & 1) ? cf32(-dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f) : cf32(dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f));
        kdEma += (v * t - kdEma) * (1.f / 1024.f);
        if ((++kdN & 15) == 0) kdGain = 1.f / std::max(0.08f, std::min(1.f, kdEma));
        return (w.imag() * s.real() - w.real() * s.imag()) * kdGain;
    }

    void stepVl(const cf32 zs) {
        const cf32 P = cmul(ph, smallRot((float)omega));
        const cf32 u = cmul(zs, ph);
        float err = 0;
        bool known = false;
        if (pos < 90 + kVlHeaderSymbols) {
            vlHdr[pos - 90] = u;
        } else if (vlIdx != -3) {
            const int k = pos - (90 + kVlHeaderSymbols);
            const int r = (int)(*rn)[(size_t)vlScrIndex(pos, vlShift)];
            if ((*vlLay)[(size_t)k] || vlIdx == -2) {
                const cf32 x = s2RotateByR(u, (4 - r) & 3);
                known = true;
                err = x.imag() * kPilot.real() - x.real() * kPilot.imag();
                daErr += std::norm(x - kPilot); daN++;
                if (vlIdx >= 0 && !dry) {
                    // which scrambling start the pilots follow, in case the first guess was wrong
                    for (int h = 0; h < 2; h++) {
                        const int rr = (int)(*rn)[(size_t)vlScrIndex(pos, h ? -kVlHeaderSymbols : 0)];
                        vlAcc[h] += s2RotateByR(u, (4 - rr) & 3) * std::conj(kPilot);
                    }
                }
            } else if (vlIdx >= 0) {
                const bool bpsk = vlMode(vlIdx).bpsk;
                const cf32 x = bpsk ? ((r & 1) ? -u : u) : s2RotateByR(u, (4 - r) & 3);
                err = bpsk ? vlBpskError(x, (int)frameSyms.size()) : ddError(x);
                frameSyms.push_back(x);
                if ((frameSyms.size() & 7) == 0) { cellRing[cellPos] = x; cellPos = (cellPos + 1) & 2047; cellCount++; }
            }
        }
        if (known) { phErr2 += (double)err * err; phN++; }
        loopUpdate(err, P);
        pos++;
        if (pos == 90 + kVlHeaderSymbols) vlHeaderDone();
        else if (pos == L) finishFrame();
    }

    // The VL-SNR header is known to be one of 16 sequences: the best correlation gives the mode (and the phase of the loop, as the SOF does)
    void vlHeaderDone() {
        cf32 best(0.f, 0.f); int bw = -1; float bm = 0.f;
        cf32 ref[kVlHeaderSymbols];
        for (int w = 0; w < 16; w++) {
            vlHeaderSymbols(w, ref);
            cf32 acc(0.f, 0.f);
            for (int k = 0; k < kVlHeaderSymbols; k++) acc += vlHdr[k] * std::conj(ref[k]);
            if (std::abs(acc) > bm) { bm = std::abs(acc); bw = w; best = acc; }
        }
        const float score = bm / (float)kVlHeaderSymbols;
        const int want = vlSet;
        int mode = -3;
        if (score > 0.3f * std::max(0.2f, std::min(3.f, 1.f))) {
            if (bw == kVlDummyWalsh) mode = want == 2 ? -2 : -3;
            else { const int mi = vlModeByWalsh(bw); if (mi >= 0 && vlMode(mi).set == want) mode = mi; }
        }
        vlIdx = mode;
        if (dry) { if (mode == -3) dryAbort = true; return; }
        if (mode == -3) {
            std::lock_guard<std::mutex> lk(statMu);
            st.headerBad++;
            return;
        }
        // the phase of the loop over the header: put back (the header is the first long known run since the PLHEADER)
        const float psi = std::arg(best);
        if (std::fabs(psi) < 1.0f) { ph *= std::polar(1.0f, -psi); theta += psi; }
        const float g = score;
        if (g > 0.2f && g < 5.f) gain *= std::pow(g, 0.7f);
        dummy = mode == -2;
        if (mode >= 0) {
            const VlMode& m = vlMode(mode);
            this->mod = kQpsk; this->rate = 0; soft = true;
            setLoops();
            frameSyms.clear(); frameSyms.reserve((size_t)m.symbols);
            std::lock_guard<std::mutex> lk(statMu);
            st.vlMode = mode; st.modcod = modcod; st.mod = m.bpsk ? 0 : kQpsk; st.rate = -1; st.shortFrame = m.nldpc == 16200; st.pilots = true;
        }
    }

    // after a frame: if the pilots followed the other scrambling start better than the one in use, take that one from the next frame on
    void vlFrameEnd() {
        const float a0 = std::abs(vlAcc[0]), a1 = std::abs(vlAcc[1]);
        const int cur = vlShift == 0 ? 0 : 1;
        const float ac = cur ? a1 : a0, ao = cur ? a0 : a1;
        if (ao > 3.f * ac + 100.f) { vlShift = cur ? 0 : -kVlHeaderSymbols; std::lock_guard<std::mutex> lk(statMu); st.vlShiftSwitches++; }
        vlAcc[0] = vlAcc[1] = cf32(0.f, 0.f);
    }

    // ---- header position tracking. The symbol clock of the front end can slip by a symbol (noise in the timing loop, a clock offset): the next header then
    // comes one symbol early or late. The last symbols of a frame and the 98 after them are held back, the header is looked for at the expected place
    // and four symbols either side with the code word of the frame before, and the frame walk goes on from the best place.
    static constexpr int kTailBefore = 4, kTailAfter = 4, kTailLen = kTailBefore + 90 + kTailAfter;
    std::vector<cf32> tail;
    bool tailing = false;

    void feedSymbol(const cf32 v) {
        if (tailing) {
            tail.push_back(v);
            if ((int)tail.size() == kTailLen) resolveTail();
            return;
        }
        if (!dry && L > 400 && L < (1 << 20) && pos == L - kTailBefore) { tailing = true; tail.clear(); tail.push_back(v); return; }
        step(v);
    }

    void resolveTail() {
        tailing = false;
        // the loop state at the first held symbol: phase rotation and frequency carry on over the held symbols
        const cf32* ref90 = nullptr;
        cf32 hh[90];
        if (haveParams && !dummy) { s2PlHeader(modcod, sh, pil, hh); ref90 = hh; }
        const cf32* sof = s2SofSymbols();
        cf32 u[kTailLen];
        {
            cf32 r = ph;
            const cf32 st = smallRot((float)omega);
            for (int k = 0; k < kTailLen; k++) { u[k] = tail[(size_t)k] * r * (1.f / gain); r *= st; }
        }
        double bestM = -1, m0 = 0; int bd = 0;
        for (int d = -kTailBefore; d <= kTailAfter; d++) {
            const int a = kTailBefore + d;
            cf32 acc(0, 0);
            const int cnt = ref90 ? 90 : 26;
            const cf32* rf = ref90 ? ref90 : sof;
            for (int k = 0; k < cnt; k++) acc += u[a + k] * std::conj(rf[k]);
            const double m = std::abs(acc);
            if (d == 0) m0 = m;
            if (m > bestM) { bestM = m; bd = d; }
        }
        // the expected place wins unless another is clearly better
        if (bd != 0 && !(bestM > 1.5 * m0 && bestM > (ref90 ? 90 : 26) * 0.25)) bd = 0;
        // symbols of the frame that is ending
        const int dataSyms = kTailBefore + std::min(bd, 0);
        for (int k = 0; k < dataSyms; k++) step(tail[(size_t)k]);
        if (pos < L && haveParams) { /* a symbol was lost: the frame is short, it ends here */ if (pos > 90) finishFrame(); }
        if (bd != 0) {
            std::lock_guard<std::mutex> lk(statMu);
            st.timingSlips++;
        }
        if (pos != 0) { pos = 0; L = 90; frameSyms.clear(); daErr = 0; daN = 0; }
        for (int k = kTailBefore + bd; k < kTailLen; k++) {
            step(tail[(size_t)k]);
            if (!o.tracking_.load()) break;
        }
        tail.clear();
    }

    void decodeHeader() {
        // header symbols after the carrier loop: SOF check and PLS decode
        cf32 acc(0, 0);
        const cf32* sof = s2SofSymbols();
        for (int k = 0; k < 26; k++) acc += hdr[k] * std::conj(sof[k]);
        // the SOF is known: its phase is the error of the loop, with no ambiguity. Whatever it is, the header is read with it taken out
        const float psi = std::abs(acc) > 1e-3f ? std::arg(acc) : 0.f;
        const cf32 derot = std::polar(1.0f, -psi);
        for (int k = 0; k < 90; k++) hdr[k] *= derot;
        const float sofScore = std::abs(acc) / 26.f;
        if (dry && dryHeaders++ >= 1) dryMetric += (double)(acc.real() / 26.f);    // how well the loop kept the phase since the last header
        const PlsResult pr = s2PlsDecode(&hdr[26]);
        bool good = sofScore > 0.25f && pr.score > 0.30f && pr.score - pr.second > 0.12f && s2PlsKind(pr.code) >= 0;
        PlsResult use = pr;
        if (!good && haveParams && sofScore > 0.25f) {
            // a noisy header: the code word of the frame before is by far the most likely one (constant coding and modulation is the usual case)
            const float sp = s2PlsScore(&hdr[26], modcod, sh, pilHdr);
            if (sp > 0.30f && sp >= pr.score - 0.10f) {
                use.modcod = modcod; use.shortFrame = sh; use.pilots = pilHdr; use.score = sp; use.code = s2PlsValue(modcod, sh, pilHdr); good = true;
            }
        }
        int mc = use.modcod, md = 0, rt = 0;
        bool bs = use.shortFrame, bp = use.pilots;
        // data frames need a MODCOD this receiver has; dummy frames and the S2X frames it does not decode only need their length
        const int kind = good ? s2PlsKind(use.code) : -1;
        bool useful = kind >= 0;
        if (kind == 0 && (!s2ModcodSplit(mc, md, rt) || !s2Dims(md, rt, bs).ok)) useful = false;
        if (!useful) {
            if (dry) { dryAbort = true; return; }
            hdrFail++;
            { std::lock_guard<std::mutex> lk(statMu); st.headerBad++; st.consecutiveHeaderBad = hdrFail; }
            if (haveParams && hdrFail <= 3) { L = frameLen(modcod, mod, rate, sh, pil); setupFrame(modcod, mod, rate, sh, pil, true); lastHeaderL = 0; return; }
            o.lost_ = true; o.tracking_ = false;
            log("DVB-S2: frame synchronisation lost");
            L = 1 << 30;      // stop interpreting symbols: the owner will hunt again
            return;
        }
        hdrFail = 0;
        { std::lock_guard<std::mutex> lk(statMu); st.consecutiveHeaderBad = 0; }
        // the header is now known: the data aided phase error over its 90 symbols, and the amplitude
        cf32 known[90];
        for (int k = 0; k < 26; k++) known[k] = sof[k];
        cf32 hh[90]; s2PlHeader(mc, bs, bp, hh);
        for (int k = 26; k < 90; k++) known[k] = hh[k];
        cf32 a2(0, 0); double e2 = 0;
        for (int k = 0; k < 90; k++) a2 += hdr[k] * std::conj(known[k]);
        const float g = std::abs(a2) / 90.f;
        const cf32 rot = a2 / std::max(1e-6f, std::abs(a2));      // residual phase of the header
        for (int k = 0; k < 90; k++) e2 += std::norm(hdr[k] * std::conj(rot) / std::max(g, 0.2f) - known[k]);
        daErr = e2; daN = 90;
        // Frame rate update of the carrier loop from the 90 known symbols: the phase error of the loop over the header is the phase and, from one
        // header to the next, the frequency error times the frame length (a few millionths of a radian per symbol precision, far better than the data
        // loop could find in noise). A large error is a slip of the loop: the phase is put back and the frequency left alone.
        {
            float psiFull = psi + std::arg(a2);
            while (psiFull > 3.14159265f) psiFull -= 6.2831853f;
            while (psiFull < -3.14159265f) psiFull += 6.2831853f;
            if (std::fabs(psiFull) > 0.35f) { if (!dry) turnLoop(psiFull); else { ph *= std::polar(1.0f, -psiFull); theta += psiFull; } }
            else {
                ph *= std::polar(1.0f, -psiFull); theta += psiFull;
                if (lastHeaderL > 0) omega += 0.6 * (double)psiFull / (double)lastHeaderL;
            }
        }
        // a correction of the amplitude from what the header says (damped: the symbols are noisy)
        (void)rot;
        if (g > 0.2f && g < 5.f) gain *= std::pow(g, 0.7f);
        // the rest of the frame
        if (lastModcod >= 0 && mc != lastModcod) vcm = true;
        lastModcod = mc;
        pilHdr = bp;
        const bool sfp = o.sfPil_.load();   // super-frame pilots: the PLHEADER's pilot bit says they are on, the frame has none of its own
        setupFrame(mc, md, rt, bs, bp, false);
        if (sfp) pil = false;
        L = frameLen(mc, md, rt, bs, sfp ? false : bp);
        lastHeaderL = L;
        haveParams = true;    }

    // The soft detector where the noise is large against the spacing of the points. The DVB-S2 APSK constellations do without it (their MODCODs
    // run at a high Es/N0, where it costs time and gains nothing); the S2X ones run down to a few dB above the Es/N0 of QPSK 3/4, where hard
    // decisions slip the loop (16APSK 7/15 short at 9 dB lost a frame in a thousand without it)
    bool softWanted() const { return (mod <= k8psk || s2IsS2x(rate)) && sg2 > (dmin * 0.125f) * (dmin * 0.125f); }

    bool pilHdr = false;     // the pilot bit of the last PLHEADER (the frame's own pilots may be none when the super-frame's stand in for them)
    int frameLen(int mc, int md, int rt, bool bs, bool bp) {
        (void)md; (void)rt;
        return s2PlsFrameSymbols(s2PlsValue(mc, bs, bp));
    }

    void setupFrame(int mc, int md, int rt, bool bs, bool bp, bool flywheel) {
        {
            const int code = s2PlsValue(mc, bs, bp);
            vl = (code == 129 || code == 131);
            if (vl) { vlSet = code == 129 ? 1 : 2; vlIdx = -3; vlLay = &vlLayout(vlSet); frameSyms.clear(); L = vlFrameSymbols(vlSet); dummy = false; skip = false; hdrS2x = true; modcod = mc; sh = bs; pil = bp; return; }
        }
        skip = s2PlsKind(s2PlsValue(mc, bs, bp)) == 2;
        hdrS2x = s2PlsValue(mc, bs, bp) >= 128;
        modcod = mc; sh = bs; pil = bp;
        if (!flywheel) pilHdr = bp;
        if (!skip) { mod = md; rate = rt; }      // a frame that is not decoded keeps the loop settings of the one before
        dummy = mc == 0;
        if (!dummy && !skip) setLoops();
        if (!dummy && !skip) {
            dims = s2Dims(md, rt, bs); constel = s2Constellation(md, rt); nearest = &nearestPoint(md, rt);
            const int P = s2ConstellationSize(md);
            dmin = 1e9f;
            for (int i = 0; i < P; i++) for (int j = i + 1; j < P; j++) dmin = std::min(dmin, std::abs(constel[i] - constel[j]));
            soft = softWanted();
        }
        frameSyms.clear();
        if (!dummy && !skip) frameSyms.reserve(dims.xfecSymbols);
        if (!flywheel) { /* the DA statistics of the header were set by the caller */ }
        else { daErr = 0; daN = 0; }
        std::lock_guard<std::mutex> lk(statMu);
        if (skip) { st.unsupportedCode = s2PlsValue(mc, bs, bp); return; }    // the reported MODCOD stays the one decoded
        st.modcod = mc; st.mod = dummy ? st.mod : md; st.rate = dummy ? st.rate : rt; st.shortFrame = bs; st.pilots = bp; st.vcm = vcm;
    }

    void finishFrame() {
        if (dry) { frameSyms.clear(); pos = 0; L = 90; daErr = 0; daN = 0; return; }
        const uint32_t fs = (uint32_t)L;
        const double sigma2 = daN > 0 ? std::max(1e-4, daErr / daN / 2.0) : 0.1;    // per real dimension
        if (daN > 0) {
            const double mer = 10 * std::log10(std::max(1e-9, (double)daN / std::max(daErr, 1e-9)));
            merEma = merEma == 0 ? mer : 0.7 * merEma + 0.3 * mer;
        }
        if (daN >= 90) { setNoise(0.5 * sg2 + 0.5 * sigma2); if (!dummy && !skip && !vl) soft = softWanted(); }
        {
            std::lock_guard<std::mutex> lk(statMu);
            st.framesSeen++;
            if (vl && vlIdx >= 0) st.framesVl++;
            st.merDb = merEma; st.snrDb = merEma;
            st.carrierRadPerSym = omega;
            const size_t have = std::min<size_t>(cellCount, 2048);
            cellSnap.resize(have);
            for (size_t i = 0; i < have; i++) cellSnap[i] = cellRing[(cellPos + 2048 - have + i) & 2047];
            phaseSnap = phN ? std::sqrt(phErr2 / (double)phN) : 0.0;
            lockSnap = phN > 200 && phaseSnap < 0.6;
        }
        if (vl) vlFrameEnd();
        if (dummy || skip || (vl && vlIdx < 0)) {
            { std::lock_guard<std::mutex> lk(statMu); if (dummy) st.framesDummy++; else { st.framesUnsupported++; st.unsupportedCode = vlPlsCode(vlSet); } }
            Result r; r.seq = seqNext++; r.frameSyms = fs; r.dummy = true;
            deposit(std::move(r));
        } else {
            Job j;
            j.seq = seqNext++; j.modcod = modcod; j.mod = mod; j.rate = rate; j.sh = sh;
            j.vlIdx = vl ? vlIdx : -1;
            j.syms = std::move(frameSyms); j.sigma2 = (float)sigma2; j.frameSyms = fs;
            frameSyms.clear();
            submit(std::move(j));
        }
        pos = 0; L = 90;
        daErr = 0; daN = 0;
    }

    // ------------------------------------------------------------------ frequency search
    // The frame search gives the carrier frequency to a few thousandths of a radian per symbol. A frame (up to 33 000 symbols) needs it to a few
    // millionths. The two verified headers do that: the known 90 symbols of each give a phase, and the phase difference over one frame length L
    // fixes the frequency up to a multiple of 2 pi / L (about 2e-4 rad/symbol), to within a few millionths. The right multiple is found from the data:
    // the first frame is derotated with each candidate frequency, hard decisions are made, and the one whose bits satisfy the most LDPC parity
    // checks is the right one (a wrong one turns the phase by whole cycles over the frame, which leaves a random word). If that does not give a
    // clear answer, the carrier loop is tried at a grid of frequencies instead (acquireByLoop).
    static void descramblePoint(cf32& x, int r) { x = s2RotateByR(x, (4 - r) & 3); }

    // Parity check failures of the hard decisions of one frame, derotated with theta0 + omega * k (k counted from the first symbol of the header)
    double frameSyndrome(const cf32* z, int L, double theta0, double omega, float gain, int md, int rt, bool bs, bool bp) {
        const S2Dims dm = s2Dims(md, rt, bs);
        const NearestPoint& np = nearestPoint(md, rt);
        const int m = md + 2;
        const std::vector<uint8_t>& R = s2ScramblingRn(plN);
        std::vector<float> sgn((size_t)dm.xfecSymbols * (size_t)m), de((size_t)dm.nldpc);
        const cf32 stp = std::polar(1.0f, (float)(-omega));
        cf32 rot = std::polar(1.0f, (float)(-theta0 - omega * 90.0));
        const float ig = 1.f / gain;
        int si = 0;
        for (int i = 0; i < L - 90; i++) {
            const cf32 x = z[90 + i] * rot * ig;
            rot *= stp;
            if ((i & 255) == 255) rot /= std::abs(rot);
            if (bp && (i % 1476) >= 1440) continue;
            cf32 y = x;
            descramblePoint(y, R[(size_t)i]);
            const int l = np.index(y.real(), y.imag());
            if (si >= dm.xfecSymbols) break;
            for (int b = 0; b < m; b++) sgn[(size_t)si * m + b] = ((l >> (m - 1 - b)) & 1) ? -1.f : 1.f;
            si++;
        }
        if (si != dm.xfecSymbols) return 1.0;
        s2BitDeinterleaveLlr(sgn.data(), dm.nldpc, md, rt, de.data());
        const LdpcCode& code = s2Ldpc(rt, bs);
        const std::vector<int>& cs = code.chkStart();
        const std::vector<int>& cv = code.chkVar();
        const int nChecks = (int)cs.size() - 1;
        int bad = 0;
        for (int c = 0; c < nChecks; c++) {
            int x = 0;
            for (int j = cs[(size_t)c]; j < cs[(size_t)c + 1]; j++) x ^= de[(size_t)cv[(size_t)j]] < 0 ? 1 : 0;
            bad += x;
        }
        return (double)bad / (double)nChecks;
    }

    bool acquireByHeaders(const S2HuntResult& h, const cf32* win, size_t n, S2HuntResult& out) {
        int md = 0, rt = 0;
        if (h.modcod < 1 || !s2ModcodSplit(h.modcod, md, rt)) return false;
        const S2Dims dm = s2Dims(md, rt, h.shortFrame);
        if (!dm.ok) return false;
        const int L = s2FrameSymbols(dm, h.pilots && !o.sfPil_.load());
        const bool inv = h.inverted;
        auto zAt = [&](size_t i) { return inv ? std::conj(win[i]) : win[i]; };        if (h.firstPos + (size_t)L + 95 > n) return false;
        cf32 ref[90];
        s2PlHeader(h.modcod, h.shortFrame, h.pilots, ref);
        const double w0 = h.phi;
        auto headerSum = [&](size_t q, double w) {
            cf32 acc(0, 0);
            for (int k = 0; k < 90; k++) acc += zAt(q + (size_t)k) * std::polar(1.0f, (float)(-w * k)) * std::conj(ref[k]);
            return acc;
        };
        // header positions: the best coherent correlation of the 90 known symbols near each. The first is where the frame search put it (it may be
        // a symbol or two off); the second is looked for up to 8 symbols either side of one frame later, because the symbol clock of the front end
        // is still settling over the first frame and may have slipped a few symbols (QPSK 1/4 at the Es/N0 of table 13 lost every frame of a
        // 5 s recording when only 2 were searched: the second header was not found and the frequency was never fixed)
        size_t pq[2];
        for (int t = 0; t < 2; t++) {
            const long c = t == 0 ? (long)h.firstPos : (long)pq[0] + (long)L;
            const long r = t == 0 ? 2 : 8;
            double bm = -1; long bq = c;
            for (long d = -r; d <= r; d++) {
                const long q = c + d;
                if (q < 0 || (size_t)q + 95 > n) continue;
                const double a = std::abs(headerSum((size_t)q, w0));
                if (a > bm) { bm = a; bq = q; }
            }
            pq[t] = (size_t)bq;
        }
        // the symbols between the two headers: one frame plus what the clock slipped
        const int D = (int)(pq[1] - pq[0]);
        const cf32 s1 = headerSum(pq[0], w0), s2 = headerSum(pq[1], w0);
        if (std::abs(s1) < 90 * 0.15f || std::abs(s2) < 90 * 0.15f) return false;
        const double psi = std::arg(s2 * std::conj(s1));
        const double span = h.snrDb < 4.f ? 0.006 : 0.0045;
        const double stepW = 2.0 * dect2::math::kPi / (double)D;
        // the phase of the second header minus that of the first is the true frequency times D (the same local derotation is applied to both)
        const int mmin = (int)std::ceil(((w0 - span) - psi / D) / stepW), mmax = (int)std::floor(((w0 + span) - psi / D) / stepW);
        std::vector<cf32> conjFrame;
        const cf32* frame = win + pq[0];
        if (inv) { conjFrame.resize((size_t)L); for (int i = 0; i < L; i++) conjFrame[(size_t)i] = std::conj(win[pq[0] + (size_t)i]); frame = conjFrame.data(); }
        double best = 2.0, second = 2.0, bestW = w0, bestTh = h.theta;
        std::vector<double> all;
        for (int mm = mmin; mm <= mmax; mm++) {
            const double w = psi / D + mm * stepW;
            const cf32 s = headerSum(pq[0], w);
            const double th = std::arg(s);
            const float gain = std::max(0.2f, std::min(3.f, std::abs(s) / 90.f));
            const double f = frameSyndrome(frame, L, th, w, gain, md, rt, h.shortFrame, h.pilots && !o.sfPil_.load());
            all.push_back(f);
            if (f < best) { second = best; best = f; bestW = w; bestTh = th; }
            else if (f < second) second = f;
        }
        if (all.empty()) return false;
        // clear answer: far below the random level (0.5 for a bad frequency, with a spread of 0.5 / sqrt(checks)) and well below the runner-up. The
        // neighbours of the right frequency are not quite random (part of the frame is still right), so the runner-up is measured against the
        // depth of the best: 8PSK 2/3 0.2 dB below table 13 gave 0.456 against 0.489 (sd 0.0034), a clear answer that a fixed 10 sd gap refused
        const double sd = 0.5 / std::sqrt((double)(dm.nldpc - dm.kldpc));
        if (best > 0.5 - 10 * sd || second - best < std::max(6 * sd, 0.3 * (0.5 - best))) return false;
        out = h;
        out.pos = pq[0];
        out.phi = bestW;
        out.theta = bestTh;
        out.sofScore = std::max(0.2f, std::min(3.f, std::abs(headerSum(pq[0], bestW)) / 90.f));
        return true;
    }

    // VL-SNR: the frame is found by its PLHEADER; the frequency, the symbol position, the mode of the header and where the scrambling starts are then
    // searched together over the one frame: the sum of the known symbols (PLHEADER, VL-SNR header of each candidate, the pilots of each hypothesis of
    // the scrambling start) at each trial frequency is largest, coherently, at the right combination. The frame is 16 000 to 33 000 symbols long, so
    // the grid is 2 pi / L / 4 apart.
    int vlShiftHint = 0;
    bool acquireVl(const S2HuntResult& h, const cf32* win, size_t n, S2HuntResult& out) {
        if (h.modcod != 64 && h.modcod != 65) return false;
        const int set = h.modcod == 64 ? 1 : 2;
        const int Lf = vlFrameSymbols(set);
        if (h.firstPos + (size_t)Lf + 4 > n || h.firstPos < 2) return false;
        const bool inv = h.inverted;
        cf32 plh[90];
        s2PlHeader(h.modcod, false, true, plh);
        std::vector<int> cands; std::vector<std::vector<cf32>> refs;
        for (int w = 0; w < 16; w++) {
            const int mi = vlModeByWalsh(w);
            if ((mi >= 0 && vlMode(mi).set == set) || (w == kVlDummyWalsh && set == 2)) {
                cands.push_back(w); refs.emplace_back((size_t)kVlHeaderSymbols);
                vlHeaderSymbols(w, refs.back().data());
            }
        }
        const std::vector<uint8_t>& lay = vlLayout(set);
        const std::vector<uint8_t>& R = s2ScramblingRn(plN);
        const double stepW = 2.0 * dect2::math::kPi / (double)Lf / 4.0;
        const double span = 0.008;
        const int K = (int)std::ceil(span / stepW);
        double bestM = -1, secondM = -1;
        S2HuntResult best = h;
        int bestShift = 0;
        std::vector<cf32> frame((size_t)Lf);
        for (long d = -2; d <= 2; d++) {
            const size_t q = (size_t)((long)h.firstPos + d);
            if (q + (size_t)Lf > n) continue;
            for (int i = 0; i < Lf; i++) frame[(size_t)i] = inv ? std::conj(win[q + (size_t)i]) : win[q + (size_t)i];
            for (int j = -K; j <= K; j++) {
                const double w0 = h.phi + j * stepW;
                const cf32 stp = std::polar(1.0f, (float)(-w0));
                cf32 rot(1.f, 0.f);
                cf32 aPlh(0, 0), aP[2]{cf32(0, 0), cf32(0, 0)};
                std::vector<cf32> aH(cands.size(), cf32(0, 0));
                for (int i = 0; i < Lf; i++) {
                    const cf32 x = frame[(size_t)i] * rot;
                    rot *= stp;
                    if ((i & 255) == 255) rot /= std::abs(rot);
                    if (i < 90) aPlh += x * std::conj(plh[i]);
                    else if (i < 990) { for (size_t c = 0; c < cands.size(); c++) aH[c] += x * std::conj(refs[c][(size_t)(i - 90)]); }
                    else if (lay[(size_t)(i - 990)]) {
                        aP[0] += s2RotateByR(x, (4 - (int)R[(size_t)vlScrIndex(i, 0)]) & 3) * std::conj(kPilot);
                        aP[1] += s2RotateByR(x, (4 - (int)R[(size_t)vlScrIndex(i, -kVlHeaderSymbols)]) & 3) * std::conj(kPilot);
                    }
                }
                size_t bc = 0; float bh = 0.f;
                for (size_t c = 0; c < cands.size(); c++) if (std::abs(aH[c]) > bh) { bh = std::abs(aH[c]); bc = c; }
                for (int s = 0; s < 2; s++) {
                    const cf32 tot = aPlh + aH[bc] + aP[s];
                    const double m = std::abs(tot);
                    if (m > bestM) {
                        secondM = bestM; bestM = m; bestShift = s ? -kVlHeaderSymbols : 0;
                        best.pos = q; best.phi = w0; best.theta = std::arg(tot);
                        best.sofScore = std::max(0.2f, std::min(3.f, (float)(m / (90.0 + kVlHeaderSymbols + 1512.0))));
                    } else if (m > secondM) secondM = m;
                }
            }
        }
        (void)secondM;
        if (bestM < 0.15 * (90.0 + kVlHeaderSymbols)) return false;
        vlShiftHint = bestShift;
        out = best;
        return true;
    }

    S2HuntResult acquire(const S2HuntResult& h, const cf32* win, size_t n) {
        if (h.headers < 2 || h.firstPos + 90 >= n) return h;
        plN = o.plCode_.load() < 0 ? 0 : o.plCode_.load();
        S2HuntResult r;
        if (acquireVl(h, win, n, r)) return r;
        if (acquireByHeaders(h, win, n, r)) return r;
        return acquireByLoop(h, win, n);
    }

    S2HuntResult acquireByLoop(const S2HuntResult& h, const cf32* win, size_t n) {
        if (h.headers < 2 || h.firstPos + 90 >= n) return h;
        const bool inv = h.inverted;
        auto zAt = [&](size_t i) { return inv ? std::conj(win[i]) : win[i]; };
        const cf32* sof = s2SofSymbols();
        const size_t end = std::min(n, h.lastPos + 90);
        // The data loop pulls in a frequency error of about a third of the range in which its detector is linear (the spacing of the constellation
        // points: 90 degrees for QPSK, 45 for 8PSK, 15 to 30 for APSK), so the trial frequencies are that close together
        int mod0 = 0, rate0 = 0;
        if (!s2ModcodSplit(h.modcod, mod0, rate0)) mod0 = 0;
        static const double kStep[kS2Mods] = {0.0010, 0.0005, 0.0004, 0.0003, 0.0002, 0.0002, 0.0002};
        const double dw = kStep[mod0 >= 0 && mod0 < kS2Mods ? mod0 : 0];
        const double spanW = h.snrDb < 4.f ? 0.006 : 0.004;
        const int K = (int)std::ceil(spanW / dw);
        S2HuntResult best = h;
        double bestM = -1e30;
        for (int j = -K; j <= K; j++) {
            const double w = h.phi + j * dw;
            // refine the header position with the coherent SOF correlation at this frequency
            size_t bf = h.firstPos;
            double bm = -1;
            cf32 bacc(0, 0);
            for (long d = -2; d <= 2; d++) {
                const long q = (long)h.firstPos + d;
                if (q < 0 || (size_t)q + 90 >= n) continue;
                cf32 acc(0, 0);
                for (int k = 0; k < 26; k++) acc += zAt((size_t)q + (size_t)k) * std::polar(1.0f, (float)(-w * k)) * std::conj(sof[k]);
                if (std::abs(acc) > bm) { bm = std::abs(acc); bf = (size_t)q; bacc = acc; }
            }
            if (bm <= 0) continue;
            S2HuntResult hj = h;
            hj.phi = w; hj.theta = std::arg(bacc); hj.pos = bf;
            begin(hj);
            dry = true;
            for (size_t i = bf; i < end && !dryAbort; i++) step(zAt(i));
            dry = false;
            const double m = dryAbort || dryHeaders < 2 ? -1e9 : dryMetric / (double)(dryHeaders - 1);
            if (m > bestM) { bestM = m; best = hj; }
        }
        if (bestM < 0.3) return h;      // nothing stayed locked: the frame search's own estimate at its latest header
        return best;
    }

    // The data and pilot symbols of a frame, the common case, taken in one run with the state of the carrier loop in registers (the same work as
    // step() does for these symbols). Returns the number of symbols taken: it stops before the symbol that ends a pilot block, before the held back
    // symbols at the end of the frame and before the end of the frame, which the general path handles.
    size_t fastRun(const cf32* z, size_t n, bool inverted) {
        if (tailing || dummy || skip || vl || !rn || pos < 90 || L <= 90) return 0;
        const bool tailLogic = !dry && L > 400 && L < (1 << 20);
        const int stop = tailLogic ? L - kTailBefore : L - 1;
        if (pos >= stop) return 0;
        const size_t cnt = std::min<size_t>(n, (size_t)(stop - pos));
        const bool pilots = pil;
        const uint8_t* rnv = rn->data();
        const float ig = 1.f / gain;
        int per = pilots ? (pos - 90) % 1476 : 0;
        cf32 phl = ph, pacc = pilotAcc;
        double th = theta, om = omega, dErr = daErr, pe2 = phErr2;
        int dN = daN;
        uint64_t pN = phN;
        int pc = phCount, p = pos;
        size_t k = 0;
        for (; k < cnt; k++) {
            if (pilots && per == 1475) break;
            cf32 zin = z[k];
            if (inverted) zin = cf32(zin.real(), -zin.imag());
            const cf32 zs = s2RotateByR(cf32(zin.real() * ig, zin.imag() * ig), (4 - (int)rnv[p - 90]) & 3);
            const cf32 P = cmul(phl, smallRot((float)om));
            const cf32 x = cmul(zs, phl);
            float err;
            if (pilots && per >= 1440) {
                err = x.imag() * kPilot.real() - x.real() * kPilot.imag();
                const float dr = x.real() - kPilot.real(), di = x.imag() - kPilot.imag();
                dErr += (double)(dr * dr + di * di); dN++;
                pacc = cf32(pacc.real() + (x.real() * kPilot.real() + x.imag() * kPilot.imag()), pacc.imag() + (x.imag() * kPilot.real() - x.real() * kPilot.imag()));
                pe2 += (double)err * err; pN++;
            } else {
                err = ddError(x);
                frameSyms.push_back(x);
                if ((frameSyms.size() & 7) == 0) { cellRing[cellPos] = x; cellPos = (cellPos + 1) & 2047; cellCount++; }
            }
            const float de = std::max(-0.5f, std::min(0.5f, err));
            const float kap = kpiD * de, k2 = kap * kap;
            phl = cmul(P, cf32(1.f - k2 * 0.5f, -(kap * (1.f - k2 * (1.f / 6.f)))));
            th += (float)om + kap;
            om += kiD * de;
            if ((++pc & 31) == 0) phl /= std::abs(phl);
            p++;
            if (pilots) per++;
        }
        ph = phl; pilotAcc = pacc; theta = th; omega = om; daErr = dErr; phErr2 = pe2;
        daN = dN; phN = pN; phCount = pc; pos = p;
        return k;
    }

    void feedBlock(const cf32* z, size_t n) {
        const bool inv = o.inverted_.load();
        size_t i = 0;
        while (i < n && o.tracking_.load()) {
            const size_t used = fastRun(z + i, n - i, inv);
            if (used) { i += used; continue; }
            feedSymbol(inv ? std::conj(z[i]) : z[i]);
            i++;
        }
    }

    // ------------------------------------------------------------------ decoder threads
    std::vector<std::thread> workers;
    std::mutex qMu;
    std::condition_variable qCv, spaceCv;
    std::deque<Job> queue;
    bool stopFlag = false;
    size_t maxQueue = 6;
    std::mutex resMu;
    std::map<uint64_t, Result> results;
    uint64_t nextOut = 0;

    void startWorkers() {
        stopWorkers();
        stopFlag = false;
        const unsigned hw = std::max(1u, std::thread::hardware_concurrency());
        const unsigned n = std::max(1u, std::min(6u, hw > 2 ? hw - 2 : 1u));
        for (unsigned i = 0; i < n; i++) workers.emplace_back(dect2::namedThread("onair-dvbs-w", [this] { workerLoop(); }));
        maxQueue = 2 * n + 2;
    }
    void stopWorkers() {
        { std::lock_guard<std::mutex> lk(qMu); stopFlag = true; }
        qCv.notify_all(); spaceCv.notify_all();
        for (auto& t : workers) if (t.joinable()) t.join();
        workers.clear();
        queue.clear();
    }

    void submit(Job&& j) {
        std::unique_lock<std::mutex> lk(qMu);
        if (queue.size() >= maxQueue) {
            if (o.blocking_.load()) spaceCv.wait(lk, [&] { return queue.size() < maxQueue || stopFlag; });
            if (queue.size() >= maxQueue) {                 // behind: this frame is skipped
                lk.unlock();
                Result r; r.seq = j.seq; r.frameSyms = j.frameSyms; r.dropped = true;
                deposit(std::move(r));
                return;
            }
        }
        queue.push_back(std::move(j));
        lk.unlock();
        qCv.notify_one();
    }

    ComputeChoice compute;

    // The front half of a frame's work: phase refinement, demapper, bit deinterleaver. dl receives the LDPC input (nldpc values).
    static bool prepareFrame(Job& j, const S2Dims& d, std::vector<float>& llr, float* dl) {
        if ((int)j.syms.size() != d.xfecSymbols) return false;
        llr.resize((size_t)d.xfecSymbols * (size_t)d.bitsPerSym);
        {
            // the phase error the carrier loop left, measured in blocks against decisions (block length from the noise level)
            const float es = 1.f / (2.f * std::max(j.sigma2, 1e-3f));
            const int W = std::max(8, std::min(512, (int)(83.f / es)));
            s2RefinePhase(j.syms.data(), d.xfecSymbols, j.mod, j.rate, j.sigma2, W, std::vector<PhaseMark>());
        }
        s2Demap(j.syms.data(), d.xfecSymbols, j.mod, j.rate, j.sigma2, llr.data());
        s2BitDeinterleaveLlr(llr.data(), d.nldpc, j.mod, j.rate, dl);
        return true;
    }

    // The back half: BCH, BBHEADER descrambling and the bit error estimate from a decoded code word
    static void finishFrame(const Job& j, const S2Dims& d, const float* dl, const uint8_t* hard, bool ldpcOk, int iters, bool gpu, std::vector<uint8_t>& bb, Result& r) {
        r.iters = iters; r.gpu = gpu;
        if (!ldpcOk) return;
        const int fixed = s2BchDecode(hard, j.rate, j.sh, bb);
        if (fixed < 0) return;
        s2BbScramble(bb.data(), d.kbch);
        r.ok = true; r.bchFixed = fixed; r.bb = bb;
        // bit errors before the LDPC decoder: hard decisions on the input against the decoded code word
        size_t diff = 0;
        for (int i = 0; i < d.nldpc; i++) diff += (dl[(size_t)i] < 0) != (hard[(size_t)i] != 0);
        r.berPre = (double)diff / d.nldpc;
    }

    // a VL-SNR frame: soft demapper, the code of its mode (shortened, punctured), BCH, BB descrambling
    void processVl(const Job& j, std::vector<float>& llr, std::vector<uint8_t>& bb) {
        const VlMode& m = vlMode(j.vlIdx);
        Result r;
        r.seq = j.seq; r.frameSyms = j.frameSyms; r.vlIdx = j.vlIdx; r.kbch = m.kbch;
        if ((int)j.syms.size() == m.symbols) {
            llr.resize((size_t)m.txBits);
            vlDemap(j.syms.data(), j.vlIdx, std::max(j.sigma2, 1e-3f), llr.data());
            int it = 0;
            const int fixed = vlDecode(llr.data(), j.vlIdx, 100, bb, &it);
            r.iters = it;
            if (fixed >= 0) {
                s2BbScramble(bb.data(), m.kbch);
                r.ok = true; r.bchFixed = fixed; r.bb = bb;
            }
        }
        deposit(std::move(r));
    }

    void workerLoop() {
        std::vector<float> llr, dlAll;
        std::vector<uint8_t> hard, bb, hardAll, okAll;
        std::vector<int> itAll;
        constexpr size_t kBatch = 8;
        for (;;) {
            std::vector<Job> jobs;
            bool gpu = false, probe = false;
            {
                std::unique_lock<std::mutex> lk(qMu);
                qCv.wait(lk, [&] { return !queue.empty() || stopFlag; });
                if (stopFlag && queue.empty()) return;
                jobs.push_back(std::move(queue.front()));
                queue.pop_front();
                gpu = compute.pickGpu(&probe);
                // frames of the same MODCOD that are already waiting go to the GPU together: one call decodes them all
                while (gpu && jobs[0].vlIdx < 0 && jobs.size() < kBatch && !queue.empty() && queue.front().vlIdx < 0 && queue.front().mod == jobs[0].mod && queue.front().rate == jobs[0].rate && queue.front().sh == jobs[0].sh) {
                    jobs.push_back(std::move(queue.front()));
                    queue.pop_front();
                }
            }
            spaceCv.notify_all();
            if (jobs[0].vlIdx >= 0) { processVl(jobs[0], llr, bb); continue; }
            const Job& j0 = jobs[0];
            const S2Dims d = s2Dims(j0.mod, j0.rate, j0.sh);
            const size_t nb = jobs.size();
            std::vector<Result> res(nb);
            std::vector<char> usable(nb, 0);
            dlAll.resize(nb * (size_t)d.nldpc);
            for (size_t i = 0; i < nb; i++) {
                res[i].seq = jobs[i].seq; res[i].frameSyms = jobs[i].frameSyms; res[i].mod = jobs[i].mod; res[i].rate = jobs[i].rate; res[i].sh = jobs[i].sh;
                usable[i] = prepareFrame(jobs[i], d, llr, dlAll.data() + i * (size_t)d.nldpc) ? 1 : 0;
            }
            const LdpcCode& code = s2Ldpc(j0.rate, j0.sh);
            const float alpha = s2LdpcAlpha(j0.mod, j0.rate, j0.sh);
            bool gpuDone = false;
            if (gpu) {
                // unusable frames (wrong symbol count) are decoded as zeros and ignored
                hardAll.resize(nb * (size_t)d.nldpc); okAll.assign(nb, 0); itAll.assign(nb, 0);
                const auto t0 = std::chrono::steady_clock::now();
                gpuDone = GpuLdpc::instance().decode(code, dlAll.data(), (int)nb, 50, hardAll.data(), okAll.data(), itAll.data(), alpha, true);
                if (gpuDone) compute.report(true, std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / (double)nb);
            }
            for (size_t i = 0; i < nb; i++) {
                if (!usable[i]) { deposit(std::move(res[i])); continue; }
                const float* dl = dlAll.data() + i * (size_t)d.nldpc;
                std::vector<float> dlv;
                int iters = 0;
                bool ok = false, onGpu = false;
                const uint8_t* hp = nullptr;
                if (gpuDone && okAll[i]) { ok = true; onGpu = true; iters = itAll[i]; hp = hardAll.data() + i * (size_t)d.nldpc; }
                else {
                    // the CPU decoder: the chosen path, or the answer for a frame the GPU could not decode (it uses the same code with the right scaling)
                    dlv.assign(dl, dl + d.nldpc);
                    const auto t0 = std::chrono::steady_clock::now();
                    // the high code rates converge in a few iterations, where the 8-bit decoder is about twice as fast; the low rates need as many iterations
                    // again with it, so they keep the float one. Both with the two-minimum correction (the LLRs are in their true scale: s2Demap with
                    // the noise measured on the known symbols). 32APSK and up keep the float decoder: their bits differ so much in reliability
                    // that the 8-bit range (scaled to the mean) clips the weak ones, which cost 0.1 dB at 32APSK 4/5
                    ok = s2RateValue(j0.rate) >= 0.7 && j0.mod <= k16apsk ? code.decodeFast8(dlv, 50, hard, &iters, alpha, true) : code.decodeFast(dlv, 50, hard, &iters, alpha, true);
                    if (!gpuDone) compute.report(false, std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
                    hp = hard.data();
                }
                finishFrame(jobs[i], d, dl, hp, ok, iters, onGpu, bb, res[i]);
                deposit(std::move(res[i]));
            }
        }
    }

    // ------------------------------------------------------------------ results in order, packets
    // transport stream assembly
    std::vector<uint8_t> carry;
    bool haveCarry = false;
    uint8_t prevCrc = 0;
    bool havePrevCrc = false;
    double pendingSyms = 0;
    int isiFirst = -1;
    std::vector<uint8_t> outPk;
    bool roLow = false;
    static constexpr int kRelocks = 3;
    int relockLeft = kRelocks;               // searches again after locks that decode nothing (kept over begin(); a decoded frame refills it)

    void deposit(Result&& r) {
        std::lock_guard<std::mutex> lk(resMu);
        const uint64_t s = r.seq;
        results.emplace(s, std::move(r));
        while (true) {
            auto it = results.find(nextOut);
            if (it == results.end()) break;
            Result res = std::move(it->second);
            results.erase(it);
            nextOut++;
            processResult(res);
        }
    }

    void emitPackets(double secs) {
        if (outPk.empty()) return;
        std::function<void(const uint8_t*, size_t, double)> cb;
        { std::lock_guard<std::mutex> lk(cbMu); cb = pktCb; }
        const size_t n = outPk.size() / 188;
        if (cb) cb(outPk.data(), n, secs);
        outPk.clear();
    }

    void processResult(const Result& r) {
        pendingSyms += r.frameSyms;
        statSyms += r.frameSyms;
        if (statSyms >= 2.0 * std::max(1.0, o.symbolRate_)) {   // the useful bit rate of every stream over the last two seconds
            std::lock_guard<std::mutex> lk(statMu);
            for (auto& kv : st.streams) { kv.second.bps = kv.second.winBits / ((double)statSyms / std::max(1.0, o.symbolRate_)); kv.second.winBits = 0; }
            statSyms = 0;
        }
        if (r.dummy) return;
        bool relock = false;
        {
            std::lock_guard<std::mutex> lk(statMu);
            if (r.dropped) { st.fecBad++; st.dropped++; st.consecutiveBad++; }
            else if (r.ok) {
                st.fecOk++; st.consecutiveBad = 0;
                relockLeft = kRelocks;
                st.ldpcIterAvg = st.ldpcIterAvg == 0 ? r.iters : 0.9 * st.ldpcIterAvg + 0.1 * r.iters;
                st.ldpcOnGpu = r.gpu; if (r.gpu) st.ldpcGpuFrames++;
                if (r.berPre >= 0) st.berPre = st.berPre < 0 ? r.berPre : 0.9 * st.berPre + 0.1 * r.berPre;
            } else {
                st.fecBad++; st.consecutiveBad++; st.bchBad++;
                // The headers are read but not one frame has decoded since the lock: the carrier frequency the acquisition chose may be a whole
                // number of cycles per frame off (the headers cannot tell), which no later frame would correct. Search again, a few times
                // (a signal below the threshold of its MODCOD keeps its MER and constellation display between the tries).
                // 16 frames: near the threshold the first few frames after a good lock fail while the loops settle, and a search then costs more
                if (st.fecOk == 0 && st.consecutiveBad >= 16 && relockLeft > 0) { relockLeft--; relock = true; }
            }
        }
        if (relock && o.tracking_.load()) {
            log("DVB-S2: headers locked but no frame decoded: searching for the carrier again");
            o.lost_ = true; o.tracking_ = false;
        }
        if (!r.ok) { haveCarry = false; carry.clear(); havePrevCrc = false; return; }
        S2BbHeader h;
        if (!s2ParseBbHeader(r.bb.data(), h)) {
            std::lock_guard<std::mutex> lk(statMu);
            st.bbHeaderBad++;
            haveCarry = false; carry.clear(); havePrevCrc = false;
            return;
        }
        {
            std::lock_guard<std::mutex> lk(statMu);
            // RO 11 switches to the low range: 00, 01, 10 then mean 0.15, 0.10, 0.05 (EN 302 307-2 table 2)
            if (h.ro == 3) roLow = true;
            if (h.ro != 3) st.roSignalled = roLow ? 3 + h.ro : h.ro;
            st.sis = h.sis; st.tsGs = h.tsGs;
            S2StreamStat& ss = st.streams[h.sis ? 0 : h.isi];
            ss.isi = h.sis ? 0 : h.isi; ss.single = h.sis; ss.ccm = h.ccm; ss.issyi = h.issyi; ss.npd = h.npd; ss.hem = h.hem;
            ss.tsGs = h.tsGs; ss.upl = h.upl; ss.sync = h.sync;
            ss.frames++; ss.modcods[r.mod * 1000 + r.rate * 2 + (r.sh ? 1 : 0)]++; ss.winBits += h.dfl;
        }
        // a generic stream (GSE, GSE-HEM, other packets): every stream of the carrier is handed on, whichever ISI is being decoded as the transport stream.
        // A generic stream of 188-byte packets is read as transport packets (a hub's signalling), like any other.
        if (h.tsGs != 3 && !(h.upl == 1504 && h.tsGs != 2)) {
            { std::lock_guard<std::mutex> lk(statMu); st.gseFrames++; }
            haveCarry = false; carry.clear();
            std::function<void(const uint8_t*, size_t, int, int, bool, int)> dcb;
            { std::lock_guard<std::mutex> lk(cbMu); dcb = dataCb; }
            const int dd = r.kbch ? r.kbch : s2Dims(r.mod, r.rate, r.sh).kbch;
            if (dcb && h.dfl >= 0 && h.dfl <= dd - 80) {
                const size_t nb = (size_t)h.dfl / 8;
                std::vector<uint8_t> bf(10 + nb);
                for (size_t i = 0; i < 10 + nb; i++) { unsigned c = 0; for (int b = 0; b < 8; b++) c = (c << 1) | r.bb[i * 8 + (size_t)b]; bf[i] = (uint8_t)c; }
                dcb(bf.data(), bf.size(), r.mod, r.rate, r.sh, h.tsGs);
            }
            return;
        }
        if (!h.sis) {
            const int want = o.isiSel_.load() >= 0 ? o.isiSel_.load() : isiFirst;
            if (isiFirst < 0 && o.isiSel_.load() < 0) isiFirst = h.isi;
            const int sel = o.isiSel_.load() >= 0 ? o.isiSel_.load() : isiFirst;
            (void)want;
            { std::lock_guard<std::mutex> lk(statMu); st.isi = sel; }
            if (h.isi != sel) { std::lock_guard<std::mutex> lk(statMu); st.otherIsi++; return; }
        } else { std::lock_guard<std::mutex> lk(statMu); st.isi = -1; }
        const int d = r.kbch ? r.kbch : s2Dims(r.mod, r.rate, r.sh).kbch;
        if (h.dfl > d - 80 || h.dfl < 0) { std::lock_guard<std::mutex> lk(statMu); st.bbHeaderBad++; return; }
        // the data field as bytes
        const int nbytes = h.dfl / 8;
        std::vector<uint8_t> by((size_t)nbytes);
        for (int i = 0; i < nbytes; i++) { unsigned c = 0; for (int b = 0; b < 8; b++) c = (c << 1) | r.bb[80 + (size_t)i * 8 + b]; by[(size_t)i] = (uint8_t)c; }
        const int upl = h.upl / 8;
        if (upl < 2 || upl > 400) return;
        size_t start = 0;
        if (h.syncd == 65535) { carry.insert(carry.end(), by.begin(), by.end()); haveCarry = true; return; }
        const size_t sbytes = (size_t)h.syncd / 8;
        if (sbytes > by.size()) { std::lock_guard<std::mutex> lk(statMu); st.bbHeaderBad++; return; }
        if (haveCarry && !carry.empty() && carry.size() + sbytes == (size_t)upl) {
            carry.insert(carry.end(), by.begin(), by.begin() + (std::ptrdiff_t)sbytes);
            emitUp(carry.data(), upl, h);
        } else if (haveCarry && !carry.empty()) { havePrevCrc = false; }
        carry.clear();
        start = sbytes;
        while (start + (size_t)upl <= by.size()) { emitUp(&by[start], upl, h); start += (size_t)upl; }
        carry.assign(by.begin() + (std::ptrdiff_t)start, by.end());
        haveCarry = true;
        emitPackets(pendingSyms / std::max(1.0, o.symbolRate_));
        pendingSyms = 0;
    }

    // one user packet: [CRC-8 of the previous packet][187 bytes][optional ISSY][optional DNP]
    void emitUp(const uint8_t* up, int upl, const S2BbHeader& h) {
        {
            const uint8_t crcOfThis = s2Crc8(up + 1, upl - 1);
            if (havePrevCrc && up[0] != prevCrc) { std::lock_guard<std::mutex> lk(statMu); st.crcErrors++; }
            prevCrc = crcOfThis; havePrevCrc = true;
        }
        if (h.npd) {
            const int dnp = up[upl - 1];
            for (int i = 0; i < dnp; i++) {
                uint8_t n[188]; memset(n, 0xFF, 188); n[0] = 0x47; n[1] = 0x1F; n[2] = 0xFF; n[3] = 0x10;
                outPk.insert(outPk.end(), n, n + 188);
            }
        }
        uint8_t pkt[188];
        pkt[0] = (uint8_t)h.sync;
        memcpy(pkt + 1, up + 1, 187);
        outPk.insert(outPk.end(), pkt, pkt + 188);
        std::lock_guard<std::mutex> lk(statMu);
        st.packets++;
    }
};

// ============================================================================ the public class
S2Rx::S2Rx() : p_(std::make_unique<Impl>(*this)) {}
S2Rx::~S2Rx() { p_->th.stop(); p_->stopWorkers(); }

void S2Rx::setDataFieldCallback(std::function<void(const uint8_t*, size_t, int, int, bool, int)> cb) { std::lock_guard<std::mutex> lk(p_->cbMu); p_->dataCb = std::move(cb); }
void S2Rx::setPacketCallback(std::function<void(const uint8_t*, size_t, double)> cb) { std::lock_guard<std::mutex> lk(p_->cbMu); p_->pktCb = std::move(cb); }
void S2Rx::setLogCallback(std::function<void(const std::string&)> cb) { std::lock_guard<std::mutex> lk(p_->cbMu); p_->logCb = std::move(cb); }

void S2Rx::start(const S2HuntResult& h, const cf32* win, size_t n) {
    Impl& I = *p_;
    stop();
    I.startWorkers();
    I.roLow = false;
    I.carry.clear(); I.haveCarry = false; I.havePrevCrc = false; I.pendingSyms = 0; I.isiFirst = -1;
    I.seqNext = 0; I.nextOut = 0; I.results.clear();
    inverted_ = h.inverted;
    lost_ = false;
    tracking_ = true;
    // up to four seconds of symbols may wait for the thread at 1 Msym/s, a few frames at the highest rates; a recording waits instead of dropping
    // blocking (a file): a short queue, so the front end cannot run seconds ahead; the symbols queued when sync is lost are thrown away,
    // and how many that were depended on the thread scheduling
    I.th.start([&I](const cf32* z, size_t m) { I.feedBlock(z, m); }, blocking_.load() ? 1u << 16 : 1u << 22, blocking_.load());
    std::vector<cf32> copy(win, win + n);
    I.th.post([this, &I, h, copy = std::move(copy)]() {
        const size_t m = copy.size();
        const S2HuntResult h2 = I.acquire(h, copy.data(), m);
        I.begin(h2);
        I.feedBlock(copy.data() + h2.pos, m - h2.pos);
    });
}

void S2Rx::push(const cf32* z, size_t n) {
    if (!tracking_.load()) return;
    p_->th.push(z, n);
}

void S2Rx::drain() {
    Impl& I = *p_;
    I.th.sync();
    for (int i = 0; i < 2500; i++) {
        { std::lock_guard<std::mutex> lk(I.resMu); if (I.nextOut >= I.seqNext) break; }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
}

void S2Rx::stop() {
    Impl& I = *p_;
    tracking_ = false;                            // symbols still queued are skipped
    I.th.stop();
    if (!I.workers.empty()) {
        // let the frames in the queue finish
        for (int i = 0; i < 200; i++) {
            { std::lock_guard<std::mutex> lk(I.qMu); if (I.queue.empty()) break; }
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
        I.stopWorkers();
    }
    tracking_ = false;
}

void S2Rx::setComputeMode(int m) { p_->compute.mode = m; }

S2Stats S2Rx::stats() const { std::lock_guard<std::mutex> lk(p_->statMu); return p_->st; }
void S2Rx::cells(std::vector<cf32>& out) const { std::lock_guard<std::mutex> lk(p_->statMu); out = p_->cellSnap; }
double S2Rx::phaseRms() const { std::lock_guard<std::mutex> lk(p_->statMu); return p_->phaseSnap; }
bool S2Rx::carrierLocked() const { std::lock_guard<std::mutex> lk(p_->statMu); return p_->lockSnap; }

} // namespace dvbs
} // namespace dect2
