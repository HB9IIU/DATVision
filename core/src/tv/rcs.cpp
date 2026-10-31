// DVB-RCS / RCS2 forward-link signalling (see rcs.h).
// The reading of the tables follows DecSAT's crates/decsat-ts/src/rcs.rs (GPL-3.0-or-later, made by CasualArclamp), rewritten in C++; credits in THIRD_PARTY.md.
#include "dect2/tv/rcs.h"
#include "dect2/tv/ts.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

namespace dect2 {

namespace {
uint32_t u24(const uint8_t* b) { return (uint32_t)b[0] << 16 | (uint32_t)b[1] << 8 | b[2]; }
uint32_t u32(const uint8_t* b) { return (uint32_t)b[0] << 24 | (uint32_t)b[1] << 16 | (uint32_t)b[2] << 8 | b[3]; }
int u16(const uint8_t* b) { return b[0] << 8 | b[1]; }
double pcrS(uint32_t t) { return t / 27e6; }
double bcdDegrees(int v, bool east) {
    auto d = [&](int s) { return (double)((v >> s) & 0xF); };
    const double deg = d(12) * 100 + d(8) * 10 + d(4) + d(0) / 10.0;
    return east ? deg : -deg;
}
double offset100Hz(const uint8_t* b) {     // signed 24 bit, units of 100 Hz
    int32_t v = (int32_t)((uint32_t)b[0] << 16 | (uint32_t)b[1] << 8 | b[2]);
    if (v & 0x800000) v -= 0x1000000;
    return v * 100.0;
}
struct Desc { int tag; const uint8_t* d; int n; };
std::vector<Desc> descriptors(const uint8_t* b, int n) {
    std::vector<Desc> v;
    while (n >= 2) {
        const int tag = b[0], len = b[1];
        if (2 + len > n) break;
        v.push_back({tag, b + 2, len});
        b += 2 + len; n -= 2 + len;
    }
    return v;
}
template <class T, class K> void mergeBy(std::vector<T>& have, const std::vector<T>& nw, K key) {
    for (const T& x : nw) {
        auto it = std::find_if(have.begin(), have.end(), [&](const T& h) { return key(h) == key(x); });
        if (it == have.end()) have.push_back(x); else *it = x;
    }
}
bool forwardLink(const uint8_t* d, int n, RcsForwardLink& f) {
    if (n < 15) return false;
    const int standard = (d[11] >> 3) & 3;
    f.satelliteId = d[0]; f.beamId = u16(d + 1); f.nccId = d[3]; f.usage = d[4] >> 5; f.localLinkId = d[4] & 0x1F;
    f.frequencyHz = u32(d + 5) * 100.0;
    f.orbitalDeg = bcdDegrees(u16(d + 9), (d[11] & 0x80) != 0);
    f.polarization = (d[11] >> 5) & 3;
    f.standard = standard;
    f.symbolRate = u24(d + 12) * 100.0;
    if (standard == 1 || standard == 2) {
        switch (d[11] & 3) { case 1: f.rollOff = 0.20; break; case 2: f.rollOff = 0.25; break; case 3: f.rollOff = 0.35; break; default: break; }
        if (n > 15) f.isi = d[15];
    } else if (standard == 3) {
        const int mode = d[11] & 7;
        f.s2xMode = mode;
        if (n > 15) f.isi = d[15];
        if ((mode == 1 || mode == 2) && n > 16) {
            switch (d[16] & 7) { case 0: f.rollOff = 0.35; break; case 1: f.rollOff = 0.25; break; case 2: f.rollOff = 0.20; break; case 4: f.rollOff = 0.15; break; case 5: f.rollOff = 0.10; break; case 6: f.rollOff = 0.05; break; default: break; }
        }
    }
    return true;
}
bool returnLink(const uint8_t* d, int n, RcsReturnLink& r) {
    if (n < 12) return false;
    r.satelliteId = d[0]; r.beamId = u16(d + 1); r.gatewayId = d[3];
    r.orbitalDeg = bcdDegrees(u16(d + 5), (d[7] & 1) != 0);
    r.superframeId = d[8]; r.txOffsetHz = offset100Hz(d + 9);
    return true;
}
bool linkage(const uint8_t* d, int n, RcsLinkage& l) {
    if (n < 7) return false;
    l.linkageType = d[6]; l.serviceId = u16(d + 4);
    if (l.linkageType == 0x81 && n >= 10) {
        l.interactiveNetworkId = u16(d + 7);
        const int cnt = d[9] + 1;
        for (int k = 0; k < cnt; k++) {
            const int at = 10 + 4 * k;
            if (at + 4 > n) break;
            l.populations.push_back({u16(d + at), u16(d + at + 2)});
        }
    }
    return true;
}
} // namespace

const char* rcsTableName(int id) {
    switch (id) {
    case 0x3E: return "MPE (DSM-CC)"; case 0x40: return "NIT"; case 0x41: return "RMT (RCS map)"; case 0x70: return "TDT";
    case 0xA0: return "SCT (superframes)"; case 0xA1: return "FCT (frame types)"; case 0xA2: return "TCT (timeslot types)"; case 0xA3: return "SPT (satellite positions)";
    case 0xA4: return "CMT (corrections)"; case 0xA5: return "TBTP (burst time plan)"; case 0xA6: return "PCR packet payload"; case 0xAA: return "TMST (transmission modes)";
    case 0xAB: return "FCT2 (RCS2 frame types)"; case 0xAC: return "BCT (RCS2 burst types)"; case 0xAD: return "TBTP2 (RCS2 burst time plan)"; case 0xAE: return "TMST2";
    case 0xAF: return "FAT"; case 0xB0: return "TIM (terminal information)"; case 0xB1: return "LL-FEC parity"; case 0xB2: return "MMT2";
    default: return id >= 0xC0 && id <= 0xFE ? "user defined" : "reserved";
    }
}
std::string rcsTableLabel(int id) {
    const std::string n = rcsTableName(id);
    char b[24];
    if (n == "user defined") { snprintf(b, sizeof b, "user %02Xh", id); return b; }
    if (n == "reserved") { snprintf(b, sizeof b, "%02Xh", id); return b; }
    const size_t p = n.find(" (");
    return p == std::string::npos ? n : n.substr(0, p);
}
const char* rcsDescriptorName(int tag) {
    switch (tag) {
    case 0x40: return "network name"; case 0x4A: return "linkage"; case 0xA0: return "network layer info"; case 0xA1: return "correction message"; case 0xA2: return "logon initialize";
    case 0xA3: return "ACQ assign"; case 0xA4: return "SYNC assign"; case 0xA5: return "encrypted logon ID"; case 0xA6: return "echo value"; case 0xA7: return "RCS content";
    case 0xA8: return "satellite forward link"; case 0xA9: return "satellite return link"; case 0xAA: return "table update"; case 0xAB: return "contention control";
    case 0xAC: return "correction control"; case 0xAD: return "forward interaction path"; case 0xAE: return "return interaction path"; case 0xAF: return "connection control";
    case 0xB0: return "mobility control"; case 0xB1: return "correction message extension"; case 0xB2: return "return transmission modes"; case 0xB3: return "mesh logon initialize";
    case 0xB5: return "implementation type"; case 0xB6: return "LL-FEC identifier";
    default: return tag >= 0xE0 && tag <= 0xFE ? "user defined" : "other";
    }
}
const char* rcsRateName(int c) {
    static const char* n[] = {"1/2", "2/3", "3/4", "5/6", "7/8", "1/3", "2/5", "4/5", "6/7", "1/4", "3/5", "8/9", "9/10"};
    if (c >= 0 && c <= 12) return n[c];
    return c == 15 ? "uncoded" : "reserved";
}
const char* rcsModulationName(int m) {
    switch (m) {
    case 0: return "not defined"; case 1: return "QPSK"; case 2: return "8PSK"; case 3: return "16APSK"; case 4: return "32APSK"; case 5: return "pi/2-BPSK";
    default: return m >= 0x10 && m <= 0x17 ? "pi/2-BPSK, spread" : m >= 0x18 && m <= 0x1F ? "QPSK, spread" : "reserved";
    }
}
const char* rcsPayloadName(int p) {
    switch (p) {
    case 1: return "TRF, 1 ATM cell"; case 2: return "TRF, 2 ATM cells"; case 4: return "TRF, 4 ATM cells"; case 5: return "TRF, MPEG-TS packets"; case 6: return "CSC (logon)";
    case 7: return "ACQ"; case 8: return "SYNC"; case 9: return "DVB-S2 normal frame, pilots"; case 10: return "DVB-S2 normal frame"; case 11: return "DVB-S2 short frame, pilots";
    case 12: return "DVB-S2 short frame"; case 13: return "DVB-S2 very short frame, pilots";
    default: return p >= 0x80 ? "user defined" : "reserved";
    }
}
std::vector<std::string> rcsTimFlags(int s) {
    std::vector<std::string> v;
    if (s < 0) return v;
    static const struct { int b; const char* n; } f[] = {{0x80, "logon IDs encrypted"}, {0x10, "CSC link failure recovery"}, {0x08, "link failure recovery"}, {0x04, "return link failure"}, {0x02, "NCC receive failure"}, {0x01, "scheduler failure"}};
    for (const auto& e : f) if (s & e.b) v.push_back(e.n);
    return v;
}

RcsView RcsRx::view() const {
    RcsView v = view_;
    v.active = active();
    for (const auto& kv : declared_)
        for (int t : kv.second) if (!v.tables.count(t)) v.declaredMissing.push_back({kv.first, t});
    v.tbtpLogons = tbtpLogons_.size(); v.cmtLogons = cmtLogons_.size(); v.timMacs = timMacs_.size();
    return v;
}

void RcsRx::packet(const uint8_t* p, double clockS) {
    clock_ = clockS;
    if (p[1] & 0x80) return;
    const int pid = (p[1] & 0x1F) << 8 | p[2];
    const int afc = (p[3] >> 4) & 3;
    if (pid == ncrPid_ && (afc & 2) && p[4] >= 7 && (p[5] & 0x10)) {
        const uint8_t* b = p + 6;
        const uint64_t base = (uint64_t)b[0] << 25 | (uint64_t)b[1] << 17 | (uint64_t)b[2] << 9 | (uint64_t)b[3] << 1 | (b[4] >> 7);
        const uint64_t ext = (uint64_t)(b[4] & 1) << 8 | b[5];
        ncr(base * 300 + ext);
    }
    if ((p[3] >> 6) != 0) return;                      // scrambled: not read
    const bool reads = pid == 0 || pmts_.count(pid) || declared_.count(pid);
    if (!reads || !(afc & 1)) return;
    const size_t at = 4 + ((afc & 2) ? 1 + (size_t)p[4] : 0);
    if (at >= 188) return;
    const uint8_t* payload = p + at;
    const size_t n = 188 - at;
    Asm& st = asm_[pid];
    auto secLen = [](const uint8_t* s) { return 3 + (size_t)(((s[1] & 0x0F) << 8) | s[2]); };
    auto emit = [&](const uint8_t* s, size_t len) { section(pid, s, (int)len); };
    if (p[1] & 0x40) {
        const size_t ptr = payload[0];
        if (1 + ptr > n) { st.partial.clear(); st.active = false; return; }
        if (st.active && ptr > 0) {
            st.partial.insert(st.partial.end(), payload + 1, payload + 1 + ptr);
            if (st.partial.size() >= 3 && st.partial.size() >= secLen(st.partial.data())) emit(st.partial.data(), secLen(st.partial.data()));
        }
        st.partial.clear(); st.active = false;
        const uint8_t* rest = payload + 1 + ptr;
        size_t rn = n - 1 - ptr;
        while (rn >= 3 && rest[0] != 0xFF) {
            const size_t len = secLen(rest);
            if (rn >= len) { emit(rest, len); rest += len; rn -= len; }
            else { st.partial.assign(rest, rest + rn); st.active = true; break; }
        }
    } else if (st.active) {
        st.partial.insert(st.partial.end(), payload, payload + n);
        if (st.partial.size() >= 3 && st.partial.size() >= secLen(st.partial.data())) { emit(st.partial.data(), secLen(st.partial.data())); st.partial.clear(); st.active = false; }
        else if (st.partial.size() > 4200) { st.partial.clear(); st.active = false; }
    }
}

void RcsRx::ncr(uint64_t ticks) {
    ncrPackets_++;
    if (!ncrHave_ || ticks < ncrLastT_) { ncrFirstT_ = ticks; ncrFirstS_ = clock_; ncrHave_ = true; }   // a wrap (26.5 hours) starts the measurement again
    ncrLastT_ = ticks;
    const double span = clock_ - ncrFirstS_;
    view_.haveNcr = true;
    view_.ncr.pid = ncrPid_; view_.ncr.packets = ncrPackets_; view_.ncr.latest = ticks;
    if (ncrPackets_ >= 3 && span > 0) view_.ncr.intervalS = span / (double)(ncrPackets_ - 1);
    if (span > 2.0) {
        const double rate = (double)(ticks - ncrFirstT_) / 27e6 / span;
        view_.ncr.havePpm = true; view_.ncr.ppm = (rate - 1.0) * 1e6;
    }
}

void RcsRx::section(int pid, const uint8_t* s, int len) {
    if (len < 12) return;
    const int id = s[0];
    const bool ok = mpegCrc32(s, len) == 0;
    const bool psi = pid == 0 || pmts_.count(pid);
    if (!psi) {
        RcsTableTally& t = view_.tables[id];
        if (t.sections == 0) t.firstS = clock_;
        t.pid = pid; t.sections++; t.bytes += (uint64_t)len; t.lastS = clock_;
        if (!ok) { t.crcBad++; return; }
    } else if (!ok) return;
    const uint8_t* body = s + 3;
    const int bn = len - 7;
    switch (id) {
    case 0x00: if (pid == 0 && bn >= 5) pat(body + 5, bn - 5); break;
    case 0x02: if (bn >= 5) pmt(body + 5, bn - 5); break;
    case 0x41: rmt(body, bn); break;
    case 0xA0: if (bn >= 5) sct(body + 5, bn - 5); break;
    case 0xA1: if (bn >= 5) fct(body + 5, bn - 5); break;
    case 0xA2: if (bn >= 5) tct(body + 5, bn - 5); break;
    case 0xA4: if (bn >= 5) cmt(body + 5, bn - 5); break;
    case 0xA5: if (bn >= 5) tbtp(body + 5, bn - 5); break;
    case 0xB0: tim(s, len); break;
    default: break;
    }
}

void RcsRx::pat(const uint8_t* b, int n) {
    for (int i = 0; i + 4 <= n; i += 4)
        if (u16(b + i) != 0) pmts_.insert((b[i + 2] & 0x1F) << 8 | b[i + 3]);
}

void RcsRx::pmt(const uint8_t* b, int n) {
    if (n < 4) return;
    const int pcrPid = (b[0] & 0x1F) << 8 | b[1];
    const int info = std::min(((b[2] & 0x0F) << 8 | b[3]), n - 4);
    const uint8_t* es = b + 4 + info;
    int rem = n - 4 - info;
    bool rcs = false;
    while (rem >= 5) {
        const int pid = (es[1] & 0x1F) << 8 | es[2];
        const int dn = std::min(((es[3] & 0x0F) << 8 | es[4]), rem - 5);
        for (const Desc& d : descriptors(es + 5, dn)) {
            if (d.tag != 0xA7) continue;
            rcs = true;
            std::vector<int>& ids = declared_[pid];
            for (int k = 0; k < d.n; k++) if (std::find(ids.begin(), ids.end(), (int)d.d[k]) == ids.end()) ids.push_back(d.d[k]);
        }
        es += 5 + dn; rem -= 5 + dn;
    }
    if (rcs && pcrPid != 0x1FFF) ncrPid_ = pcrPid;
}

void RcsRx::rmt(const uint8_t* body, int bn) {
    if (bn < 7) return;
    view_.networkId = u16(body);
    const uint8_t* b = body + 5;
    int rem = bn - 5;
    const int n = std::min(((b[0] & 0x0F) << 8 | b[1]), rem - 2);
    std::vector<RcsLinkage> links;
    for (const Desc& d : descriptors(b + 2, n)) {
        if (d.tag == 0x40) view_.networkName.assign((const char*)d.d, (size_t)d.n);
        else if (d.tag == 0x4A) { RcsLinkage l; if (linkage(d.d, d.n, l)) links.push_back(l); }
    }
    view_.linkages = links;
    const uint8_t* rest = b + 2 + n;
    rem -= 2 + n;
    if (rem < 2) return;
    const uint8_t* ts = rest + 2;
    rem -= 2;
    std::vector<RcsForwardLink> fw;
    std::vector<RcsReturnLink> rt;
    while (rem >= 6) {
        const int dn = std::min(((ts[4] & 0x0F) << 8 | ts[5]), rem - 6);
        for (const Desc& d : descriptors(ts + 6, dn)) {
            if (d.tag == 0xA8) { RcsForwardLink f; if (forwardLink(d.d, d.n, f)) fw.push_back(f); }
            else if (d.tag == 0xA9) { RcsReturnLink r; if (returnLink(d.d, d.n, r)) rt.push_back(r); }
        }
        ts += 6 + dn; rem -= 6 + dn;
    }
    view_.forward = fw; view_.returns = rt;
}

void RcsRx::sct(const uint8_t* b, int n) {
    if (n < 1) return;
    const int count = b[0];
    int at = 1;
    std::vector<RcsSuperframe> out;
    for (int i = 0; i <= count; i++) {
        if (at + 19 > n) break;
        const uint8_t* e = b + at;
        RcsSuperframe sf;
        sf.id = e[0]; sf.durationS = pcrS(u32(e + 8)); sf.centreHz = u32(e + 12) * 100.0; sf.counter = u16(e + 16);
        const int frames = (e[18] & 0x1F) + 1;
        at += 19;
        for (int k = 0; k < frames; k++) {
            if (at + 8 > n) break;
            const uint8_t* f = b + at;
            sf.frames.push_back({f[0], pcrS(u32(f + 1)), offset100Hz(f + 5)});
            at += 8;
        }
        out.push_back(sf);
    }
    mergeBy(view_.superframes, out, [](const RcsSuperframe& s) { return s.id; });
}

void RcsRx::fct(const uint8_t* b, int n) {
    if (n < 1) return;
    const int count = b[0];
    int at = 1;
    std::vector<RcsFrameType> out;
    for (int i = 0; i <= count; i++) {
        if (at + 10 > n) break;
        const uint8_t* e = b + at;
        RcsFrameType f;
        f.id = e[0]; f.durationS = pcrS(u32(e + 1)); f.timeslots = (e[5] & 0x07) << 8 | e[6];
        const int groups = e[9] + 1;
        at += 10;
        for (int k = 0; k < groups; k++) {
            if (at + 9 > n) break;
            const uint8_t* g = b + at;
            f.groups.push_back({offset100Hz(g), pcrS(u32(g + 3)), g[7], g[8] + 1});
            at += 9;
        }
        out.push_back(f);
    }
    mergeBy(view_.frameTypes, out, [](const RcsFrameType& s) { return s.id; });
}

void RcsRx::tct(const uint8_t* b, int n) {
    if (n < 1) return;
    const int count = b[0];
    int at = 1;
    std::vector<RcsTimeslot> out;
    for (int i = 0; i <= count; i++) {
        if (at + 15 > n) break;
        const uint8_t* e = b + at;
        const bool turbo = (e[9] & 0x80) != 0, newPerm = (e[13] & 1) != 0;
        int len = 14;
        if (turbo && newPerm) len += 7;
        const int preamble = at + len < n ? b[at + len] : 0;
        RcsTimeslot t;
        t.id = e[0]; t.symbolRate = u24(e + 1); t.durationS = pcrS(u24(e + 4)); t.turbo = turbo; t.codeRate = e[9] & 0x0F; t.modulation = e[10] >> 3; t.payload = e[11]; t.preambleSymbols = preamble;
        out.push_back(t);
        at += len + 1 + (preamble + 3) / 4;
    }
    mergeBy(view_.timeslots, out, [](const RcsTimeslot& s) { return s.id; });
}

void RcsRx::tbtp(const uint8_t* b, int n) {
    if (n < 4) return;
    const int frames = (b[3] & 0x1F) + 1;
    int at = 4;
    for (int i = 0; i < frames; i++) {
        if (at + 3 > n) return;
        const int entries = ((b[at + 1] & 0x07) << 8 | b[at + 2]) + 1;
        at += 3;
        for (int k = 0; k < entries; k++) {
            if (at + 5 > n) return;
            tbtpLogons_.insert(u16(b + at));
            view_.tbtpAssignments++;
            at += 5 + ((b[at + 2] & 0x80) ? 1 : 0);
        }
    }
}

void RcsRx::cmt(const uint8_t* b, int n) {
    if (n < 1) return;
    const int count = b[0];
    int at = 1;
    for (int i = 0; i <= count; i++) {
        if (at + 4 > n) return;
        const int flags = b[at + 3];
        cmtLogons_.insert(u16(b + at + 1));
        view_.cmtEntries++;
        at += 4 + ((flags & 0x80) ? 1 : 0) + ((flags & 0x40) ? 1 : 0) + ((flags & 0x20) ? 2 : 0);
    }
}

void RcsRx::tim(const uint8_t* s, int len) {
    if (len < 18) return;
    const uint8_t mac[6] = {s[11], s[10], s[9], s[8], s[4], s[3]};
    const uint8_t* body = s + 12;
    const int bn = len - 16;
    bool all = true;
    for (int i = 0; i < 6; i++) if (mac[i] != 0xFF) all = false;
    if (all) {
        if (bn < 2) return;
        view_.timStatus = body[0];
        view_.timDescriptors.clear();
        for (const Desc& d : descriptors(body + 2, bn - 2)) view_.timDescriptors.push_back(d.tag);
    } else {
        view_.timUnicast++;
        uint64_t m = 0;
        for (int i = 0; i < 6; i++) m = m << 8 | mac[i];
        timMacs_.insert(m);
    }
}

// ---------------------------------------------------------------- test signal
namespace {
std::vector<uint8_t> sect(int id, int ext, const std::vector<uint8_t>& body) {
    std::vector<uint8_t> s{(uint8_t)id, 0, 0, (uint8_t)(ext >> 8), (uint8_t)ext, 0xC1, 0, 0};
    s.insert(s.end(), body.begin(), body.end());
    const int l = (int)s.size() + 4 - 3;
    s[1] = (uint8_t)(0xB0 | (l >> 8)); s[2] = (uint8_t)l;
    // appending the CRC of the section so far makes the whole section check to zero
    const uint32_t crc = mpegCrc32(s.data(), (int)s.size());
    s.push_back((uint8_t)(crc >> 24)); s.push_back((uint8_t)(crc >> 16)); s.push_back((uint8_t)(crc >> 8)); s.push_back((uint8_t)crc);
    return s;
}
void be32(std::vector<uint8_t>& v, uint32_t x) { v.push_back((uint8_t)(x >> 24)); v.push_back((uint8_t)(x >> 16)); v.push_back((uint8_t)(x >> 8)); v.push_back((uint8_t)x); }
void be24(std::vector<uint8_t>& v, uint32_t x) { v.push_back((uint8_t)(x >> 16)); v.push_back((uint8_t)(x >> 8)); v.push_back((uint8_t)x); }
}

RcsTestNetwork buildRcsTestNetwork() {
    RcsTestNetwork n;
    auto add = [&](int pid, std::vector<uint8_t> s) { n.pids.push_back(pid); n.sections.push_back(std::move(s)); };
    add(0, sect(0x00, 0x3B07, {0x00, 0x01, 0xE0, 0x24}));
    std::vector<uint8_t> pmt{0xF6, 0x00, 0xF0, 0x00};
    const struct { int pid; std::vector<uint8_t> ids; } pm[] = {{0x26, {0x41}}, {0x27, {0xA0, 0xA1, 0xA2}}, {0x29, {0xA5}}, {0x934, {0xB0}}};
    for (const auto& e : pm) {
        pmt.push_back(0x05);
        pmt.push_back((uint8_t)(0xE0 | (e.pid >> 8))); pmt.push_back((uint8_t)e.pid);
        const int dl = 2 + (int)e.ids.size();
        pmt.push_back((uint8_t)(0xF0 | (dl >> 8))); pmt.push_back((uint8_t)dl);
        pmt.push_back(0xA7); pmt.push_back((uint8_t)e.ids.size());
        pmt.insert(pmt.end(), e.ids.begin(), e.ids.end());
    }
    add(0x24, sect(0x02, 0x0001, pmt));
    // RMT: network name, an RCS linkage, one TS with a forward and a return link
    const char* name = "TEST NET";
    std::vector<uint8_t> nd{0x40, 8};
    nd.insert(nd.end(), name, name + 8);
    const uint8_t lk[] = {0x4A, 14, 0x3B, 0x07, 0x00, 0x00, 0x00, 0x01, 0x81, 0x00, 0x6E, 0x00, 0x00, 0x01, 0x00, 0x00};
    nd.insert(nd.end(), lk, lk + sizeof lk);
    const uint8_t fwd[] = {0x22, 0x05, 0xE7, 0x0A, 0x0F, 0x06, 0xF9, 0x47, 0x40, 0x01, 0x92, (uint8_t)(0x80 | (2 << 3) | 0x05), 0x00, 0x4E, 0x20, 0x00};
    const uint8_t ret[] = {0x22, 0x00, 0x01, 0x01, 0x00, 0x01, 0x92, 0x01, 0x03, 0xFF, 0xFF, 0xF6};
    std::vector<uint8_t> td{0xA8, (uint8_t)sizeof fwd};
    td.insert(td.end(), fwd, fwd + sizeof fwd);
    td.push_back(0xA9); td.push_back((uint8_t)sizeof ret);
    td.insert(td.end(), ret, ret + sizeof ret);
    std::vector<uint8_t> rmt{(uint8_t)(0xF0 | (nd.size() >> 8)), (uint8_t)nd.size()};
    rmt.insert(rmt.end(), nd.begin(), nd.end());
    const int tl = 6 + (int)td.size();
    rmt.push_back((uint8_t)(0xF0 | (tl >> 8))); rmt.push_back((uint8_t)tl);
    const uint8_t th[] = {0x3B, 0x07, 0x00, 0x00};
    rmt.insert(rmt.end(), th, th + 4);
    rmt.push_back((uint8_t)(0xF0 | (td.size() >> 8))); rmt.push_back((uint8_t)td.size());
    rmt.insert(rmt.end(), td.begin(), td.end());
    add(0x26, sect(0x41, 0x0001, rmt));
    // SCT: superframe 3 at 14.25 GHz, 26.67 ms, one frame
    std::vector<uint8_t> sct{0x00, 0x03, 0x00, 0, 0, 0, 0, 0, 0};
    be32(sct, 720000); be32(sct, 142500000);
    const uint8_t sc[] = {0x00, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0};
    sct.insert(sct.end(), sc, sc + sizeof sc);
    add(0x27, sect(0xA0, 0x0001, sct));
    // FCT: frame 1, 26.67 ms, 24 slots in one group of type 5
    std::vector<uint8_t> fct{0x00, 0x01};
    be32(fct, 720000);
    const uint8_t fc[] = {0x00, 24, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 5, 23};
    fct.insert(fct.end(), fc, fc + sizeof fc);
    add(0x27, sect(0xA1, 0x0001, fct));
    // TCT: type 5, 256 kS/s, turbo 2/3, QPSK, TRF MPEG, no preamble
    std::vector<uint8_t> tct{0x00, 0x05};
    be24(tct, 256000); be24(tct, 30000);
    const uint8_t tc[] = {0, 0, 0x81, (uint8_t)(0x01 << 3), 0x05, 0, 0, 0x00, 0x00};
    tct.insert(tct.end(), tc, tc + sizeof tc);
    add(0x27, sect(0xA2, 0x0001, tct));
    // broadcast TIM: status 0, one correction control descriptor (laid out as the long form of the section: no extended header)
    std::vector<uint8_t> tim{0xB0, 0x00, 0x00, 0xFF, 0xFF, 0xC1, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0xAC, 0x01, 0x00};
    const int l = (int)tim.size() + 4 - 3;
    tim[1] = (uint8_t)(0xB0 | (l >> 8)); tim[2] = (uint8_t)l;
    const uint32_t crc = mpegCrc32(tim.data(), (int)tim.size());
    tim.push_back((uint8_t)(crc >> 24)); tim.push_back((uint8_t)(crc >> 16)); tim.push_back((uint8_t)(crc >> 8)); tim.push_back((uint8_t)crc);
    add(0x934, tim);
    return n;
}

} // namespace dect2
