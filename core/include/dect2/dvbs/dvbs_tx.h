// DVB-S and DVB-S2 transmitter chains (test signal): transport stream packets in, a continuous stream of modulation symbols (one sample per
// symbol, before pulse shaping) out. Nothing is transmitted anywhere: the symbols only exist in memory or in a file.
#pragma once
#include "dvbs_s2.h"
#include "dect2/common/ring.h"
#include <cstdint>
#include <functional>
#include <memory>
#include <vector>

namespace dect2 {
namespace dvbs {

struct DvbsTxConfig {
    int standard = 2;               // 1 DVB-S (EN 300 421), 2 DVB-S2 (EN 302 307-1); 3 DVB-S2X (EN 302 307-2) is the same chain with an S2X rate
    int mod = kQpsk;                // S2: S2Mod. DVB-S is always QPSK
    int rate = 5;                   // DVB-S: 0..4 = 1/2 2/3 3/4 5/6 7/8. DVB-S2: index of s2RateName (0 = 1/4 ... 10 = 9/10, S2X MODCODs from
                                    // kS2Rates on: s2xRate()); default 2/3
    bool shortFrame = false;        // S2 only (an S2X rate has its own frame size: this must match it)
    bool pilots = false;            // S2 only
    double rollOff = 0.35;          // used by the shaper and for the RO field of the BBHEADER (0.15, 0.10, 0.05: the low range, signalled by alternating 11)
    double symbolRate = 5e6;        // only needed for netBitrate() and the shaper
    int plScramble = 0;             // S2: Gold code number n of the physical layer scrambling
    bool vcm = false;               // S2: cycle through several MODCODs (frame by frame) to exercise the receiver's header decoding
    int dummyEvery = 0;             // S2: insert a dummy PLFRAME after this many data frames (0 = never)
    int vlMode = -1;                // S2X: >= 0 sends VL-SNR frames of this mode (index of table 18a: 0 QPSK 2/9, 1..3 BPSK 1/5 11/45 1/3 medium, 4 and 5 BPSK 1/5
                                    // and 11/45 spreading 2, 6..8 BPSK 1/5 4/15 1/3 short; see dvbs_vlsnr.h); mod, rate and pilots are not used then
    int vlDummyEvery = 0;           // VL-SNR: a dummy PLFRAME after this many data frames (0 = never)
    int vlScrShift = 0;             // VL-SNR: where the scrambling sequence starts, 0 = it counts from the end of the PLS header, -900 = from the end of the VL-SNR header
    // S2: the BBFRAMEs carry a generic continuous stream (GSE) instead of a transport stream: the data field comes from `gseField(bytes)` (zero padded, which
    // reads as GSE padding), TS/GS = 01, no user packets
    std::function<std::vector<uint8_t>(size_t bytes)> gseField;
    bool hem = false;               // S2X: GSE in high-efficiency mode: `gseField` then returns whole GSE packets (no padding), which are sliced across BBFRAMEs, SYNCD pointing at the
                                    // first packet that starts in a data field, TS/GS = 10 and the CRC-8 of the BBHEADER XOR 1
    int superframe = -1;            // S2X: lay the PLFRAMEs into super-frames of this format (0 S2X, 1 S2 legacy; -1 none), see dvbs_sf.h
    bool sfPilots = false;          // with a super-frame: the type-A super-frame pilots are on (the PLFRAMEs then carry none of their own, their PLHEADER pilot bit says so)
    bool mis = false;               // S2: a multistream carrier: the frames alternate between ISI 0 (the transport stream) and ISI 1 (the generic stream of `gseField`)
    bool npd = false;               // S2: delete null packets from the transport stream and send the count in the DNP byte (clause 5.1.3, annex D.3)
};

class DvbsTxSource {
public:
    virtual ~DvbsTxSource() = default;
    virtual void generate(cf32* out, size_t n) = 0;       // the next n symbols
    virtual uint64_t packetsSent() const = 0;             // transport stream packets taken from the packet source so far
};

// `ts` fills one 188-byte packet (starting with 0x47). It is called whenever the chain needs the next packet. With no `ts`, null packets are sent.
std::unique_ptr<DvbsTxSource> makeDvbsTx(const DvbsTxConfig& cfg, std::function<void(uint8_t*)> ts);

// Transport stream bit rate of the configuration when the chain is always full (what to hand to demoTsSource()). With `vcm` the mean over the cycle.
double dvbsNetBitrate(const DvbsTxConfig& cfg);
// The MODCODs that `vcm` cycles through: (modulation, rate, short, pilots)
struct VcmStep { int mod, rate; bool shortFrame, pilots; };
const std::vector<VcmStep>& dvbsVcmCycle();

} // namespace dvbs
} // namespace dect2
