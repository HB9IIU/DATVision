// DVB-S and DVB-S2 transmitter chains, see dvbs_tx.h.
#include "dect2/dvbs/dvbs_tx.h"
#include "dvbs_sf.h"
#include "dect2/dvbt/dvbt.h"
#include "dvbs_vlsnr.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <deque>
#include "dect2/common/mathconst.h"

namespace dect2 {
namespace dvbs {

namespace {

void nullPacket(uint8_t* p) { memset(p, 0xFF, 188); p[0] = 0x47; p[1] = 0x1F; p[2] = 0xFF; p[3] = 0x10; }
bool isNull(const uint8_t* p) { return p[0] == 0x47 && ((p[1] & 0x1F) << 8 | p[2]) == 0x1FFF; }

// ============================================================================ DVB-S
class TxS1 : public DvbsTxSource {
public:
    TxS1(const DvbsTxConfig& c, std::function<void(uint8_t*)> ts) : rate_(c.rate), ts_(std::move(ts)), enc_(c.rate), inter_(false) {}
    void generate(cf32* out, size_t n) override {
        for (size_t i = 0; i < n; i++) {
            while (pos_ >= syms_.size()) refill();
            out[i] = syms_[pos_++];
        }
    }
    uint64_t packetsSent() const override { return packets_; }

private:
    // one group of eight packets: energy dispersal (the PRBS restarts every eight packets), Reed-Solomon, convolutional interleaver, inner code
    void refill() {
        uint8_t ts[8 * 188], sc[8 * 188], rs[8 * 204], il[8 * 204];
        for (int p = 0; p < 8; p++) { if (ts_) ts_(ts + p * 188); else nullPacket(ts + p * 188); ts[p * 188] = 0x47; packets_++; }
        dvbt::scramble(ts, 8, sc);
        for (int p = 0; p < 8; p++) dvbt::rsEncode(sc + p * 188, rs + p * 204);
        inter_.process(rs, il, sizeof rs);
        std::vector<uint8_t> bits(sizeof il * 8), coded;
        for (size_t i = 0; i < sizeof il; i++) for (int b = 0; b < 8; b++) bits[i * 8 + b] = (il[i] >> (7 - b)) & 1;
        enc_.encode(bits, coded);
        // the serial stream alternates I and Q (table 2): a bit left over from the previous group completes the first pair
        std::vector<uint8_t> all;
        if (haveSpare_) all.push_back(spare_);
        all.insert(all.end(), coded.begin(), coded.end());
        haveSpare_ = all.size() & 1;
        if (haveSpare_) { spare_ = all.back(); all.pop_back(); }
        syms_.resize(all.size() / 2);
        pos_ = 0;
        for (size_t i = 0; i < syms_.size(); i++) syms_[i] = cf32((1 - 2 * all[2 * i]) * dect2::math::k1OverSqrt2f, (1 - 2 * all[2 * i + 1]) * dect2::math::k1OverSqrt2f);
    }
    int rate_;
    std::function<void(uint8_t*)> ts_;
    dvbt::InnerEncoder enc_;
    dvbt::ConvInterleaver inter_;
    std::vector<cf32> syms_;
    size_t pos_ = 0;
    bool haveSpare_ = false;
    uint8_t spare_ = 0;
    uint64_t packets_ = 0;
};

// ============================================================================ DVB-S2
const std::vector<VcmStep> kVcm = {
    {kQpsk, 3, false, false}, {k8psk, 5, false, true}, {kQpsk, 6, true, false}, {k16apsk, 6, false, false}, {kQpsk, 5, false, true},
    {k32apsk, 8, true, true}, {k8psk, 4, false, false},
};

class TxS2 : public DvbsTxSource {
public:
    TxS2(const DvbsTxConfig& c, std::function<void(uint8_t*)> ts) : c_(c), ts_(std::move(ts)) {}
    void generate(cf32* out, size_t n) override {
        for (size_t i = 0; i < n; i++) {
            if (c_.superframe >= 0) {   // the super-frame's own symbols (SOSF, SFFI, super-frame pilots) between those of the PLFRAMEs
                if (sfSyms_.empty()) buildSf();
                const SfPart part = sfPart((size_t)sfPos_, c_.sfPilots);
                if (part != SfPart::Payload) { out[i] = sfSyms_[(size_t)sfPos_]; sfPos_ = (sfPos_ + 1) % kSfl; continue; }
                sfPos_ = (sfPos_ + 1) % kSfl;
            }
            while (pos_ >= frame_.size()) nextFrame();
            out[i] = frame_[pos_++];
        }
    }
    void buildSf() {
        SfScrambler ref(0), pay(0);
        sfSyms_.assign((size_t)kSfl, cf32(0, 0));
        const auto sosf = sfSosfSymbols(0, ref);
        const auto sffi = sfSffiSymbols(c_.superframe, pay);
        std::copy(sosf.begin(), sosf.end(), sfSyms_.begin());
        std::copy(sffi.begin(), sffi.end(), sfSyms_.begin() + kSosfLen);
        for (int k = kSfPilotAfter; k < kSfl; k += kSfPilotPeriod) {
            const auto pl = sfPilotSymbols(0, ref, (size_t)k);
            std::copy(pl.begin(), pl.end(), sfSyms_.begin() + k);
        }
    }
    uint64_t packetsSent() const override { return packets_; }

private:
    // the next user packet as it is transmitted: the sync byte is replaced by the CRC-8 of the previous packet (clause 5.1.4)
    void nextUp(std::vector<uint8_t>& up) {
        uint8_t p[188];
        for (;;) {
            if (ts_) ts_(p); else nullPacket(p);
            p[0] = 0x47;
            packets_++;
            if (c_.npd && isNull(p) && dnp_ < 255) { dnp_++; continue; }
            break;
        }
        up.assign(p, p + 188);
        if (c_.npd) { up.push_back((uint8_t)dnp_); dnp_ = 0; }
        up[0] = prevCrc_;
        prevCrc_ = s2Crc8(up.data() + 1, (int)up.size() - 1);
    }

    // The BBFRAME of kbch bits: BBHEADER, the data field cut from the user packet stream, then BB scrambled. SYNCD points at the first packet that starts
    // inside the field. RO (clause 5.1.6, table 2): 0.35, 0.25 and 0.20 are sent as 00, 01, 10; the low range 0.15, 0.10 and 0.05 alternates 11 and 00, 01, 10.
    int nextRo() {
        const double r = c_.rollOff;
        if (r > 0.3) return 0;
        if (r > 0.22) return 1;
        if (r > 0.17) return 2;
        const int lo = r > 0.125 ? 0 : r > 0.075 ? 1 : 2;
        return (roToggle_++ & 1) ? lo : 3;
    }
    void fillBbFrame(int kbch, std::vector<uint8_t>& bb) {
        // the data field is a whole number of bytes; what is left of the BBFRAME is padding of zero bits (clause 5.2.1): the medium and short VL-SNR frames have
        // a Kbch whose data field is not
        const int dfl = (kbch - 80) / 8 * 8, upBits = c_.npd ? 1512 : 1504;
        bb.assign((size_t)kbch, 0);
        S2BbHeader h;
        h.ro = nextRo();
        h.ccm = !c_.vcm; h.npd = c_.npd; h.upl = upBits; h.dfl = dfl;
        h.syncd = upBitPos_ == 0 ? 0 : upBits - upBitPos_;
        if (h.syncd >= dfl) h.syncd = 65535;
        const bool gseFrame = c_.gseField && (!c_.mis || (misToggle_++ & 1));
        h.sis = !c_.mis; h.isi = c_.mis ? (gseFrame ? 1 : 0) : 0;
        if (gseFrame) {
            h.tsGs = c_.hem ? 2 : 1; h.upl = 0; h.syncd = 0; h.npd = false; h.hem = c_.hem;
            std::vector<uint8_t> f;
            if (c_.hem) {
                const size_t need = (size_t)dfl / 8;
                while (hemBuf_.size() < need + 16) { auto more = c_.gseField(4096); if (more.empty()) break; hemBuf_.insert(hemBuf_.end(), more.begin(), more.end()); }
                // the first packet that starts in this field (hemNext_ counts from the start of hemBuf_)
                size_t at = hemNext_;
                h.syncd = 65535;
                if (at < need) h.syncd = (int)(at * 8);
                f.assign(hemBuf_.begin(), hemBuf_.begin() + (long)std::min(need, hemBuf_.size()));
                // advance the packet start past this field
                size_t nx = hemNext_;
                while (nx < need && nx + 2 <= hemBuf_.size()) {
                    const size_t len = 2 + (size_t)(((hemBuf_[nx] << 8) | hemBuf_[nx + 1]) & 0x0FFF);
                    if (len == 2 && (hemBuf_[nx] & 0xF0) == 0) { nx = need; break; }   // padding
                    nx += len;
                }
                hemBuf_.erase(hemBuf_.begin(), hemBuf_.begin() + (long)std::min(need, hemBuf_.size()));
                hemNext_ = nx >= need ? nx - need : 0;
            } else f = c_.gseField((size_t)dfl / 8);
            f.resize((size_t)dfl / 8, 0);
            s2BuildBbHeader(h, bb.data());
            for (int i = 0; i < dfl; i++) bb[80 + (size_t)i] = (f[(size_t)i >> 3] >> (7 - (i & 7))) & 1;
            s2BbScramble(bb.data(), kbch);
            return;
        }
        s2BuildBbHeader(h, bb.data());
        for (int i = 0; i < dfl; i++) {
            if (cur_.empty()) { nextUp(cur_); upBitPos_ = 0; }
            bb[80 + (size_t)i] = (cur_[(size_t)(upBitPos_ >> 3)] >> (7 - (upBitPos_ & 7))) & 1;
            if (++upBitPos_ == (int)cur_.size() * 8) { upBitPos_ = 0; cur_.clear(); }
        }
        s2BbScramble(bb.data(), kbch);
    }

    void nextFrame() {
        if (c_.vlMode >= 0) { nextVlFrame(); return; }
        int mod = c_.mod, rate = c_.rate;
        bool sh = c_.shortFrame, pil = c_.pilots;
        if (c_.vcm) { const VcmStep& v = kVcm[vcmPos_++ % kVcm.size()]; mod = v.mod; rate = v.rate; sh = v.shortFrame; pil = v.pilots; }
        bool pilHdr = pil;
        if (c_.superframe >= 0 && c_.sfPilots) { pilHdr = true; pil = false; }   // the super-frame pilots stand in for the PLFRAME's own
        frame_.clear();
        pos_ = 0;
        if (c_.dummyEvery > 0 && ++sinceDummy_ > c_.dummyEvery) {
            sinceDummy_ = 0;
            frame_.resize(90 + 36 * 90);
            s2PlHeader(0, false, false, frame_.data());
            const auto& rn = s2ScramblingRn(c_.plScramble);
            for (int i = 0; i < 36 * 90; i++) frame_[90 + i] = s2RotateByR(cf32(dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f), rn[i]);
            return;
        }
        const S2Dims d = s2Dims(mod, rate, sh);
        std::vector<uint8_t> bb;
        fillBbFrame(d.kbch, bb);
        std::vector<uint8_t> fec, il;
        s2EncodeFec(bb, rate, sh, fec);
        s2BitInterleave(fec, mod, rate, il);
        std::vector<cf32> sym(d.xfecSymbols);
        s2MapBits(il.data(), (int)il.size(), mod, rate, sym.data());
        // PLFRAME: header, then the slots with a pilot block after every 16 slots (but not at the end), all scrambled except the header
        const int total = s2FrameSymbols(d, pil);
        frame_.resize(total);
        s2PlHeader(s2Modcod(mod, rate), sh, pilHdr, frame_.data());
        const auto& rn = s2ScramblingRn(c_.plScramble);
        int o = 90, si = 0, i = 0;
        for (int slot = 0; slot < d.slots; slot++) {
            for (int k = 0; k < 90; k++, si++) { frame_[o] = s2RotateByR(sym[si], rn[i]); o++; i++; }
            if (pil && (slot + 1) % 16 == 0 && slot + 1 < d.slots)
                for (int k = 0; k < 36; k++) { frame_[o] = s2RotateByR(cf32(dect2::math::k1OverSqrt2f, dect2::math::k1OverSqrt2f), rn[i]); o++; i++; }
        }
    }

    // a VL-SNR PLFRAME (EN 302 307-2 clause 5.5.2.5): the mode of the configuration, with a VL-SNR dummy frame after every vlDummyEvery data frames
    void nextVlFrame() {
        pos_ = 0;
        if (c_.vlDummyEvery > 0 && ++sinceDummy_ > c_.vlDummyEvery) {
            sinceDummy_ = 0;
            vlBuildFrame(-1, std::vector<uint8_t>(), c_.plScramble, c_.vlScrShift, frame_);
            return;
        }
        std::vector<uint8_t> bb;
        fillBbFrame(vlMode(c_.vlMode).kbch, bb);
        vlBuildFrame(c_.vlMode, bb, c_.plScramble, c_.vlScrShift, frame_);
    }

    DvbsTxConfig c_;
    int misToggle_ = 0;
    int sfPos_ = 0;
    std::vector<cf32> sfSyms_;
    std::vector<uint8_t> hemBuf_;
    size_t hemNext_ = 0;
    std::function<void(uint8_t*)> ts_;
    std::vector<cf32> frame_;
    size_t pos_ = 0;
    std::vector<uint8_t> cur_;       // the packet being sent
    int upBitPos_ = 0;
    uint8_t prevCrc_ = 0;
    int dnp_ = 0, vcmPos_ = 0, sinceDummy_ = 0, roToggle_ = 0;
    uint64_t packets_ = 0;
};

} // namespace

const std::vector<VcmStep>& dvbsVcmCycle() { return kVcm; }

std::unique_ptr<DvbsTxSource> makeDvbsTx(const DvbsTxConfig& cfg, std::function<void(uint8_t*)> ts) {
    if (cfg.standard == 1) return std::make_unique<TxS1>(cfg, std::move(ts));
    return std::make_unique<TxS2>(cfg, std::move(ts));
}

double dvbsNetBitrate(const DvbsTxConfig& c) {
    if (c.standard == 1) {
        static const double r[5] = {1.0 / 2, 2.0 / 3, 3.0 / 4, 5.0 / 6, 7.0 / 8};
        return c.symbolRate * 2 * r[std::max(0, std::min(4, c.rate))] * 188.0 / 204.0;
    }
    if (c.vlMode >= 0) {
        const VlMode& m = vlMode(c.vlMode);
        return c.symbolRate * (double)((m.kbch - 80) / 8 * 8) / (double)vlFrameSymbols(m.set);
    }
    auto one = [&](int mod, int rate, bool sh, bool pil) {
        const S2Dims d = s2Dims(mod, rate, sh);
        if (!d.ok) return 0.0;
        // user packets are 1504 bits (1512 with NPD); the data field holds kbch - 80 bits of them
        return c.symbolRate * (double)(d.kbch - 80) / (double)s2FrameSymbols(d, pil);
    };
    if (!c.vcm) return one(c.mod, c.rate, c.shortFrame, c.pilots);
    double bits = 0, syms = 0;
    for (const VcmStep& v : kVcm) {
        const S2Dims d = s2Dims(v.mod, v.rate, v.shortFrame);
        bits += d.kbch - 80;
        syms += s2FrameSymbols(d, v.pilots);
    }
    return c.symbolRate * bits / syms;
}

} // namespace dvbs
} // namespace dect2
