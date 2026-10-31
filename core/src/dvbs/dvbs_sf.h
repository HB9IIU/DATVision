// DVB-S2X super-frames (EN 302 307-2 Annex E): the sequences and layout of formats 0 and 1, and the stage that finds super-frames in the symbol stream, reads their
// format and takes their own symbols (SOSF, SFFI and the super-frame pilots) out so that the PL demodulator sees plain PLFRAMEs.
//
// Adapted from DecSAT by Arclamp (CasualArclamp), https://github.com/CasualArclamp/DecSAT, GPL-3.0-or-later: decsat-frame/src/superframe.rs and
// decsat-engine/src/superframe.rs, rewritten in C++. The original files begin "made by CasualArclamp".
//
// A super-frame is SFL = 612 540 symbols (formats 0 to 4). It opens with a 270-symbol Start-Of-Super-Frame (SOSF) and a 450-symbol Super-Frame Format Indicator (SFFI),
// 720 symbols, and the rest is the format's content. Formats 0 (S2X) and 1 (S2 legacy) carry ordinary PLFRAMEs, interrupted by the SOSF/SFFI of each super-frame and, when
// switched on, by type-A super-frame pilots: 36 symbols after every 16 capacity units of 90 symbols counted from the start, which replace the PLFRAMEs' own pilot blocks.
// The other formats are named only.
#pragma once
#include "dect2/common/ring.h"
#include "dect2/dsp/fftutil.h"
#include <cstdint>
#include <memory>
#include <vector>

namespace dect2 {
namespace dvbs {

constexpr int kSfl = 612540, kSosfLen = 270, kSffiLen = 450, kSfHead = kSosfLen + kSffiLen, kCuLen = 90, kSfPilotLen = 36, kSfPilotAfter = 16 * kCuLen, kSfPilotPeriod = kSfPilotAfter + kSfPilotLen;

const char* sfFormatName(int format);                       // Table E.1
std::vector<float> sfSosfSequence(int i);                   // 270 values +-1
std::vector<float> sfPilotSequence(int i);                  // 36 values +-1
void sfCodeword(int format, bool out[15]);                  // the simplex code of the format indicator
int sfDecodeSffi(const float soft[15], float& match);       // the format and how well it fits (1 clean)

// the scramblers of clause E.2.4: reference data (SOSF, pilots) and payload data (SFFI); quarter-turn sequences
class SfScrambler {
public:
    explicit SfScrambler(uint32_t n = 0);
    cf32 factor(size_t i) const;
    std::vector<uint8_t> quarter;
};

enum class SfPart { Sosf, Sffi, Pilot, Payload };
SfPart sfPart(size_t k, bool pilots);                       // what symbol k of a super-frame is

std::vector<cf32> sfSosfSymbols(int seq, const SfScrambler& reference);
std::vector<cf32> sfSffiSymbols(int format, const SfScrambler& payload);
std::vector<cf32> sfPilotSymbols(int seq, const SfScrambler& reference, size_t at);

struct SfView {
    bool searching = true;      // looking for a super-frame start
    bool noneFound = false;     // searched long enough and found none: the carrier has no super-frames
    bool locked = false;        // locked on the super-frame grid
    int format = -1;            // the format the latest SFFI said (Table E.1), -1 not read
    bool pilots = false;        // SF-pilots on in the latest super-frame
    uint64_t superframes = 0;
    float sosfMatch = 0, sffiMatch = 0;
    uint64_t losses = 0, slips = 0;   // slips: super-frames that started a symbol or two away from where the last one put them
    bool carriesPlframes() const { return locked && format >= 0 && format <= 1; }
};

class SfStripper {
public:
    SfStripper();
    void reset();
    // symbols in, symbols out (shorter by the super-frames' own symbols, delayed by up to 32768 symbols)
    void push(const cf32* in, size_t n, std::vector<cf32>& out);
    SfView view() const { return view_; }
private:
    cf32 at(uint64_t n) const { return buf_[(size_t)(n - base_)]; }
    size_t heldFrom(uint64_t n) const { const uint64_t e = base_ + buf_.size(); return e > n ? (size_t)(e - n) : 0; }
    void release(uint64_t upto, std::vector<cf32>& out);
    bool releaseLocked(uint64_t limit, std::vector<cf32>& out);
    bool nextSuperframe(uint64_t due, uint64_t& at);
    float sosfMatch(uint64_t p) const;
    void readSuperframe(uint64_t p);
    bool search();
    SfScrambler reference_, payload_;
    std::vector<cf32> sosf_, sosfDiff_, kernel_, work_;
    std::vector<float> pilot_;
    std::vector<cf32> buf_;
    uint64_t base_ = 0, scan_ = 0, budget_ = 0, start_ = 0;
    bool haveStart_ = false;
    uint32_t misses_ = 0;
    double w_ = 0, corr_ = 0;   // the carrier offset read from the SOSF (rad per symbol) and the phase the removed symbols carried away: the symbols after a gap are turned back by it
    std::unique_ptr<Fft> fft_;
    SfView view_;
};

} // namespace dvbs
} // namespace dect2
