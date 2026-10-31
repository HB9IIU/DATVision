// DVB-S2X very low SNR (VL-SNR) frames, EN 302 307-2 V1.2.1 clauses 5.3, 5.4.0a, 5.5.2.5, 5.5.2.6 and 5.5.4.1: the nine modes of table 18a (set 1:
// QPSK 2/9 normal, pi/2 BPSK 1/5, 11/45 and 1/3 medium, pi/2 BPSK 1/5 and 11/45 with spreading factor 2; set 2: pi/2 BPSK 1/5, 4/15 and 1/3 short), their
// codes (shortened and punctured), the VL-SNR header of 900 symbols, the PLFRAME layout with the additional pilots (figures 17 and 18) and the
// scrambling. Shared by the transmitter (test signal), the receiver and the tests. Internal.
#pragma once
#include "dect2/dvbs/dvbs_s2.h"
#include <vector>
#include "dect2/common/mathconst.h"

namespace dect2 {
namespace dvbs {

constexpr int kVlModes = 9;
constexpr int kVlDummyWalsh = 12;          // table 18b: the VL-SNR dummy PLFRAME (set 2)
constexpr int kVlHeaderSymbols = 900;
constexpr int kVlSet1Symbols = 33282, kVlSet2Symbols = 16686;

struct VlMode {
    int walsh;               // index of the VL-SNR header (table 18b), "Annex-I index"
    int set;                 // 1: PLS code 129, 33 282 symbols; 2: PLS code 131, 16 686 symbols
    const char* name;        // "QPSK" or "BPSK" (pi/2 BPSK)
    const char* rate;        // code rate identifier of table 18a
    bool bpsk;
    int spread;              // 1, or 2 for the "Spreading Factor 2" modes: every bit is sent twice
    const char* frame;       // "normal", "medium" or "short" FECFRAME
    int nldpc, kldpc, kbch, nbch;   // nbch = BCH coded block = kldpc - xs
    int xs, period, xp;      // shortening, puncturing period and number of punctured parity bits (table 19a)
    const char* table;       // address table of the LDPC code, "B.1", "C.1", "C.2", "C.8".."C.10", or null: the DVB-S2 code of rate index s2rate (short 1/4, 1/3)
    int s2rate;
    double esn0;             // ideal Es/N0 for quasi error free reception (tables 20a to 20c)
    int txBits;              // bits of the XFECFRAME that are sent: nldpc - xs - xp
    int symbols;             // modulation symbols of the XFECFRAME: 30 780 (set 1) or 14 976 (set 2)
};

const VlMode& vlMode(int index);           // 0 .. kVlModes - 1, in the order of table 18a
int vlModeByWalsh(int walsh);              // -1 for the dummy, the unassigned patterns and everything else
inline int vlFrameSymbols(int set) { return set == 1 ? kVlSet1Symbols : kVlSet2Symbols; }
inline int vlPlsCode(int set) { return set == 1 ? 129 : 131; }
const char* vlModeName(int index);         // "QPSK 2/9", "BPSK 1/5 SF2", ...

// ---- codes. BBFRAME (kbch bits, already BB scrambled) <-> the transmitted bits (txBits)
void vlEncode(const std::vector<uint8_t>& bb, int mode, std::vector<uint8_t>& tx);
// LLRs of the transmitted bits (> 0: bit 0) -> BBFRAME: shortened bits are known zeros, punctured ones erasures. Returns the number of bits the BCH decoder
// corrected, or -1 (LDPC or BCH failed). iters: LDPC iterations used.
int vlDecode(const float* llr, int mode, int maxIter, std::vector<uint8_t>& bb, int* iters = nullptr);
// generator polynomial of the BCH code of the medium FECFRAME (g(x) = g1(x) ... g12(x) of table 7), coefficient i of x^i, degree 180
const std::vector<uint8_t>& vlMediumBchGenerator();

// ---- modulation of the XFECFRAME (clause 5.4.0a): bits -> symbols (pi/2 BPSK, spread by two or not; QPSK as DVB-S2), and the soft demapper (noise
// variance sigma2 per real dimension) back to LLRs of the transmitted bits
void vlMap(const uint8_t* tx, int mode, cf32* sym);
void vlDemap(const cf32* sym, int mode, float sigma2, float* llr);
// pi/2 BPSK reference of the symbol at position j of a run: +-(1 + j)/sqrt 2 (j even), +-(-1 + j)/sqrt 2 (odd), for bit 0 / bit 1
inline cf32 vlBpskRef(int j) { return (j & 1) ? cf32(-dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f) : cf32(dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f); }

// ---- VL-SNR header: the 900 symbols for a table 18b index (0 .. 15). walshHeaderIndex of a received header: the best correlation
void vlHeaderSymbols(int walsh, cf32* out900);
// Correlates 900 received symbols (carrier phase and amplitude already taken out) against the 16 headers. Returns the index with the highest real
// correlation (1 = perfect), `second` the best of the others
int vlHeaderDetect(const cf32* sym900, float* score = nullptr, float* second = nullptr);

// ---- PLFRAME layout after the headers (set 1 or 2): for each of the 32 292 / 15 696 symbols after the VL-SNR header: 0 data, 1 regular pilot, 2 additional pilot
const std::vector<uint8_t>& vlLayout(int set);

// ---- scrambling (clause 5.5.4.1). The symbol at frame position p (0 = the first symbol of the PLHEADER) is scrambled with Rn(p - 90 + shift) where
// shift = 0 counts the sequence from the end of the PLS header (the VL-SNR header's 900 symbols use up its first values) and shift = -900 starts it at
// the first symbol after the VL-SNR header; pilots, dummy symbols and QPSK data are turned by Rn * 90 degrees, pi/2 BPSK data are multiplied by
// (-1)^Rn (exp(j Rn pi), clause 5.5.4.1.1).
inline int vlScrIndex(int pos, int shift) { return pos - 90 + shift; }

// ---- whole frames (test signal). mode: table index; mode = -1: a dummy frame. bb: kbch bits (BB scrambled). plScramble: Gold code n of the pilots and
// QPSK data. shift: see vlScrIndex.
void vlBuildFrame(int mode, const std::vector<uint8_t>& bb, int plScramble, int shift, std::vector<cf32>& frame);

} // namespace dvbs
} // namespace dect2
