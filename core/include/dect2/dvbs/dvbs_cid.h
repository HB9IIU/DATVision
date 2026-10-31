// DVB-CID, the carrier identification signal of ETSI TS 103 129 V1.1.1: a low-level spread-spectrum BPSK signal an uplink adds under its own carrier, carrying the
// modulator's 64-bit unique identifier and, optionally, its position, a telephone number and a short text.
//
// Chain (clause 5): a 244-bit frame (unique word, then two halves of 69 data bits: 32 identifier bits, a 5-bit content ID, 24 information bits, a CRC-8; each half with
// 42 BCH parity bits), scrambled (not the unique word), sent four times, differentially encoded, every bit spread by one 4096-chip sequence, BPSK, root raised cosine
// (alpha 0.35) at 112 kchip/s (host carrier below 512 kBd) or 224 kchip/s, 220 Hz above the host carrier's centre and 27.5 dB or more below its spectral density.
//
// Adapted from DecSAT by Arclamp (CasualArclamp), https://github.com/CasualArclamp/DecSAT, licence GPL-3.0-or-later: crates/decsat-modem/src/cid.rs (made by
// CasualArclamp), whose framing, BCH code, scrambler and content fields are rewritten here in C++. The receiver (acquisition by FFT correlation, early/late tracking,
// half-bit frequency measurement, unique word search) is this project's own design for the same signal. See THIRD_PARTY.md.
#pragma once
#include "dect2/common/ring.h"
#include <cstdint>
#include <future>
#include <memory>
#include <string>
#include <vector>

namespace dect2 {
namespace dvbcid {

constexpr int kFrameBits = 244, kUwBits = 22, kHalf = 111, kData = 69, kParity = 42, kRepeat = 4, kChips = 4096, kSps = 8;
constexpr double kOffsetHz = 220.0;

double chipRate(double hostSymbolRate);       // 112 or 224 kchip/s
double psdBelowHostDb(double hostSymbolRate); // the CID's spectral density below the host carrier's (table 6)
const std::vector<uint8_t>& chipSequence();   // the 4096 chips; the first 32 are 5091E364h

// the identifier as shown on equipment, "CC:XX:...:XX" with the CRC-8 check digits first; the MAC address it encapsulates ("" if none)
uint8_t checkDigits(uint64_t guid);
std::string guidText(uint64_t guid);
std::string guidMac(uint64_t guid);

struct Field { int contentId = 0; uint32_t info = 0; };
struct Frame {
    uint64_t guid = 0;
    Field fields[2];
    bool second = false;        // the complemented unique word
    int corrected = 0;          // bit errors the BCH code fixed
    int scrambler = 0;          // 0 as the specification states it, 1 the reversed reading
};

std::vector<uint8_t> buildFrame(uint64_t guid, const Field fields[2], bool second);   // 244 bits (0/1)
bool parseFrame(const uint8_t* bits244, Frame& out);                                    // hard bits, the four copies already combined
std::vector<uint8_t> spreadFrames(const std::vector<std::vector<uint8_t>>& frames, uint8_t& diff);   // each frame four times, differential, 4096 chips per bit

// What the fields told so far (clause 4.2, table 1), by content ID: 0 format, 1 latitude, 2 longitude, 3..5 telephone, 6..12 user text
struct Report {
    bool haveGuid = false;
    uint64_t guid = 0;
    bool have[13] = {false};
    uint32_t fields[13] = {0};
    void add(const Frame& f);
    void setPosition(double latitude, double longitude);
    void setTelephone(const std::string& number);
    void setUserText(const std::string& text);
    std::vector<std::vector<Field>> frameFields() const;   // the fields set, two to a frame, as pairs
    bool latitude(double& deg) const;                      // north positive
    bool longitude(double& deg) const;                     // east positive
    std::string telephone() const;                         // "" until all three fields came
    std::string userText() const;                          // "" until all seven fields came
    bool hasText() const;
};

// test signal: BPSK of the chips at 8 samples per chip with a root raised cosine, offset by freqHz, with the sample clock off by clockPpm
std::vector<cf32> modulate(const std::vector<uint8_t>& chips, double freqHz, double chipRateHz, double clockPpm = 0, double phase = 0);

// From the input of the receiver to the receiver's samples: mixes the host carrier's centre to zero, low-pass filters, and resamples to 8 samples per chip
class Tap {
public:
    Tap(double inputRateHz, double chipRateHz);
    bool usable() const { return ok_; }
    void process(const cf32* x, size_t n, double hostCenterHz, std::vector<cf32>& out);   // appends
private:
    double fs_, fsOut_;
    bool ok_ = false;
    int dec_ = 1;
    std::vector<float> taps_;
    std::vector<cf32> hist_;        // the last inputs not yet used
    double phase_ = 0;
    double frac_ = 0;               // position of the next output between decimated samples
    cf32 last_{0, 0};
    std::vector<cf32> dsBuf_;
    size_t decCount_ = 0;
};

struct Stats {
    bool acquired = false;
    uint64_t searches = 0;
    double offsetHz = 0;        // against the centre of the input
    float snrDb = 0;            // despread, per bit
    uint64_t bits = 0, frames = 0, badFrames = 0;
    double clockPpm = 0;
    Report report;
};

// The receiver: samples at 8 per chip centred on the host carrier. Acquisition (FFT correlation over code phase and frequency) runs on its own thread when async.
class Rx {
public:
    explicit Rx(double chipRateHz, bool async = false);
    ~Rx();
    void push(const cf32* x, size_t n, std::vector<Frame>& out);
    Stats stats() const;
    void reset();
private:
    struct Impl;
    std::unique_ptr<Impl> p_;
};

} // namespace dvbcid
} // namespace dect2
