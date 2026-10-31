// Stand-in for OnAir's core/src/tv/demo_ts.cpp, which encodes a test programme with FFmpeg for the transmit side.
// The receiver never calls it; dvbstool gen uses it only without --ts, and then sends null packets.
#include "dect2/tv/demo_ts.h"
#include <cstring>

namespace dect2 {

std::function<void(uint8_t*)> demoTsSource(double) {
    return [cc = 0](uint8_t* p) mutable {
        std::memset(p, 0xFF, 188);
        p[0] = 0x47; p[1] = 0x1F; p[2] = 0xFF; p[3] = (uint8_t)(0x10 | (cc++ & 15));
    };
}

std::function<void(uint8_t*)> demoTsSource(double netBitrate, const DemoTsIdentity&) { return demoTsSource(netBitrate); }

}  // namespace dect2
