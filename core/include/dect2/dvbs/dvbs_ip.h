// IP over DVB-S/S2: GSE (ETSI TS 102 606-1) de-encapsulation and reassembly, IP packet validation, fragment reassembly, flow statistics and PCAP export.
//
// Adapted from DecSAT by Arclamp (CasualArclamp), https://github.com/CasualArclamp/DecSAT, licence GPL-3.0-or-later: the crates decsat-gse (decode.rs, auto.rs,
// encap.rs) and decsat-ip (packet.rs, frag.rs, stats.rs, pcap.rs), written in Rust and rewritten here in C++ for the receiver's architecture. The original
// files begin "made by CasualArclamp"; their notes on what was seen on real links (the GSE variants, vendor CRCs, ciphertext) are kept where they apply.
// DecSAT in turn credits dontlookup's parsers (MIT) for the two GSE variants. See THIRD_PARTY.md.
//
// Nothing encrypted is decoded: payloads are only counted; a flow whose UDP checksums fail while its headers are good, and non-IP PDUs that read as random bytes,
// are marked as encrypted. The statistics keep addresses, ports, protocols and counts, never payload.
#pragma once
#include <array>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace dect2 {
namespace dvbsip {

// ---------------------------------------------------------------- IP packets

constexpr uint8_t kProtoIcmp = 1, kProtoTcp = 6, kProtoUdp = 17, kProtoIcmpV6 = 58;

struct IpInfo {
    bool v6 = false;
    std::array<uint8_t, 16> src{}, dst{};   // IPv4 in the first four bytes
    uint8_t protocol = 0;
    size_t len = 0;                         // the whole packet, from its header
    bool hasPorts = false;
    uint16_t sport = 0, dport = 0;
};

uint16_t inetChecksum(const uint8_t* p, size_t n);                  // RFC 1071: 0 over a header that carries a correct one
bool parseIp(const uint8_t* data, size_t n, IpInfo& out);           // strict: version, lengths and (IPv4) the header checksum must agree
// RFC 768 check of an IPv4 UDP datagram: 1 holds, 0 fails, -1 when there is none to check (no checksum sent, a fragment, odd lengths, not UDP over IPv4)
int udpChecksumOk(const uint8_t* ip, const IpInfo& info);
// every IPv4 packet that validates at some byte offset: the fallback when the encapsulation is unknown. (offset, info)
std::vector<std::pair<size_t, IpInfo>> blindIpv4Search(const uint8_t* data, size_t n);
std::string addrString(const std::array<uint8_t, 16>& a, bool v6);
const char* protocolName(uint8_t p);
std::vector<uint8_t> buildUdpV4(const uint8_t src[4], const uint8_t dst[4], uint16_t sport, uint16_t dport, const std::vector<uint8_t>& payload, bool withUdpChecksum = false);
std::vector<std::vector<uint8_t>> fragmentIpv4(const std::vector<uint8_t>& d, size_t mtu);

uint32_t crc32Msb(const uint8_t* p, size_t n, uint32_t init);       // polynomial 04C11DB7, MSB first, no final XOR
inline uint32_t crc32Mpeg2(const uint8_t* p, size_t n) { return crc32Msb(p, n, 0xFFFFFFFFu); }

// IPv4 fragments back to whole datagrams (any order, bounded memory)
class Reassembler {
public:
    // fills `out` with the packet to use (the same bytes, or a reassembled datagram) and returns true; false while a datagram is incomplete
    bool push(const uint8_t* ip, const IpInfo& info, std::vector<uint8_t>& out);
    uint64_t fragments = 0, reassembled = 0, dropped = 0;
private:
    struct Pending {
        uint32_t src, dst; uint8_t proto; uint16_t id;
        std::vector<uint8_t> header;
        std::vector<std::pair<size_t, std::vector<uint8_t>>> pieces;
        long end = -1;
        uint64_t touched = 0;
    };
    bool complete(Pending& p, std::vector<uint8_t>& payload);
    std::vector<Pending> pending_;
    uint64_t packets_ = 0;
};

// ---------------------------------------------------------------- GSE

enum class LengthMode { Standard, HeaderIncluded };   // what GSE_LENGTH counts: the bytes after the 2-byte header (the standard), or the header too
enum class FragMode { Plain, Split };                  // the fragment id: one 8-bit id (the standard), or a 6-bit id and a 2-bit counter
struct Variant {
    LengthMode length = LengthMode::Standard;
    FragMode frag = FragMode::Plain;
    const char* label() const;
    size_t gseLength(size_t bodyLen) const { return length == LengthMode::Standard ? bodyLen : bodyLen + 2; }
    bool operator==(const Variant& o) const { return length == o.length && frag == o.frag; }
};
extern const Variant kVariants[4];                    // the standard first

struct Label {
    int type = 2;                                      // 0: six bytes, 1: three bytes, 2: none (broadcast)
    std::array<uint8_t, 6> b{};
    size_t size() const { return type == 0 ? 6 : (type == 1 ? 3 : 0); }
    bool operator<(const Label& o) const { return type != o.type ? type < o.type : b < o.b; }
    bool operator==(const Label& o) const { return type == o.type && b == o.b; }
    std::string text() const;                          // "00:01:02:03:04:05", "01:02:03" or "broadcast"
};

struct Pdu {
    uint16_t protocol = 0;                             // an EtherType: 0x0800 IPv4, 0x86DD IPv6, 0x0001 bridged Ethernet, ...
    Label label;
    std::vector<uint8_t> data;
    uint32_t fragments = 1;
    int crcOk = -1;                                    // fragmented PDUs: 1 the CRC-32 matched, 0 not, -1 none
};

struct GseStats {
    uint64_t packets = 0, whole = 0, start = 0, middle = 0, end = 0, padding = 0;
    uint64_t errors = 0;                               // packets that do not fit their data field
    uint64_t reassembled = 0, orphans = 0, collisions = 0, crcOk = 0, crcBad = 0;
    uint64_t vouched = 0;                              // passed on the strength of the frames' own CRC (see GseDecoder::dataField)
};

// One GSE decoder for one variant: BBFRAME data fields in, reassembled PDUs out. A data field the caller has checked by other means (a vendor's CRC over the
// whole BBFRAME) is "verified": a PDU reassembled only from verified fields, its split counters running on, is passed on even when its own total length and
// CRC-32 follow a rule of the vendor's that this decoder does not know.
class GseDecoder {
public:
    explicit GseDecoder(Variant v = Variant()) : variant(v) {}
    void dataField(const uint8_t* field, size_t n, bool verified, std::vector<Pdu>& out);
    Variant variant;
    GseStats stats;
private:
    struct Partial {
        size_t totalLength = 0;
        uint16_t protocol = 0;
        Label label;
        std::vector<uint8_t> data, crcInput;
        uint32_t fragments = 1;
        bool verified = false;
        uint8_t counter = 0;
        void follow(uint8_t id, bool v, bool split) { const uint8_t c = id & 3; const bool next = !split || c == ((counter + 1) & 3); verified = verified && v && next; counter = c; }
    };
    bool whole(const uint8_t* b, size_t n, int lt, Label& last, std::vector<Pdu>& out);
    bool start(const uint8_t* b, size_t n, int lt, Label& last);
    bool middle(const uint8_t* b, size_t n);
    bool end(const uint8_t* b, size_t n, std::vector<Pdu>& out);
    uint8_t fragKey(uint8_t b) const { return variant.frag == FragMode::Plain ? b : (uint8_t)(b >> 2); }
    std::map<uint8_t, Partial> partial_;
    bool verified_ = false;
};

// GSE to IP with the variant chosen by the data: every variant decodes every data field, each is scored by the valid IP packets it yields, and the packets of
// the best one are passed on. When no variant has produced IP for a while, a blind IPv4 search takes over.
struct IpPacket { std::vector<uint8_t> data; IpInfo info; };
struct LabelStats {
    uint64_t pdus = 0, bytes = 0, ip = 0;
    std::map<int, uint64_t> modcods;                   // PDU bytes by the MODCOD of the frame each PDU completed in
    std::vector<std::array<uint8_t, 16>> addrs;        // destinations of its IP packets, the first few
    std::vector<bool> addrV6;
};
struct VariantReport { Variant variant; GseStats stats; uint64_t ipPackets = 0; };

class GseIp {
public:
    GseIp();
    // a BBFRAME as its BBHEADER and data field: a vendor CRC at the end of the field is checked and taken off
    void bbframe(const uint8_t* bb, size_t n, std::vector<IpPacket>& out);
    void dataField(const uint8_t* field, size_t n, std::vector<IpPacket>& out) { field_(field, n, false, out); }
    void setModcod(int m) { modcod_ = m; }
    int chosen() const;                                // index into kVariants, -1 before any IP has been seen
    int forced = -1;                                   // use this variant whatever the scores
    // 0 none yet, 1 GSE, 2 blind search
    int source() const { return chosen() >= 0 ? 1 : (blindPackets_ > 0 ? 2 : 0); }
    std::vector<VariantReport> reports() const;
    uint64_t blindPackets() const { return blindPackets_; }
    uint64_t trailers() const { return trailers_; }
    const std::map<uint16_t, uint64_t>& otherProtocols() const { return otherProtocols_; }
    // bits per byte of the non-IP PDUs once enough were seen (negative before)
    double otherEntropy() const;
    bool otherEncrypted() const { const double h = otherEntropy(); return h > 7.95; }
    const std::map<Label, LabelStats>& labels() const { return labels_; }
    void keepOthers(bool on) { keepOthers_ = on; if (!on) others_.clear(); }
    std::vector<Pdu> takeOthers() { std::vector<Pdu> o; o.swap(others_); return o; }
    static bool frameTrailer(const uint8_t* bb, size_t n);
    static bool ipOf(const Pdu& p, const uint8_t*& ip, IpInfo& info);   // the IP packet a PDU carries (also inside bridged Ethernet)
private:
    void field_(const uint8_t* field, size_t n, bool verified, std::vector<IpPacket>& out);
    int bestFit() const;
    void countLabel(const Pdu& p);
    void other(const Pdu& p);
    std::vector<GseDecoder> dec_;
    std::vector<std::vector<Pdu>> pdus_;
    std::vector<double> scores_;
    std::vector<uint64_t> ipCounts_;
    uint64_t fields_ = 0, fieldsSinceIp_ = 0, blindPackets_ = 0, trailers_ = 0;
    std::map<uint16_t, uint64_t> otherProtocols_;
    uint64_t otherBytes_[256] = {};
    std::map<Label, LabelStats> labels_;
    int modcod_ = 0;
    bool keepOthers_ = false;
    std::vector<Pdu> others_;
};

// a GSE encapsulator for test signals: PDUs in, data fields of a given length out
class GseEncapsulator {
public:
    explicit GseEncapsulator(Variant v = Variant()) : variant(v) {}
    void push(uint16_t protocol, const Label& label, std::vector<uint8_t> pdu) { queue_.push_back({protocol, label, std::move(pdu), 0, false, 0, 0, 0}); }
    size_t queued() const { return queue_.size(); }
    std::vector<uint8_t> fill(size_t len);             // zero padded to len
    std::vector<uint8_t> packets(size_t len);
    Variant variant;
private:
    struct Pending { uint16_t protocol; Label label; std::vector<uint8_t> pdu; size_t sent; bool frag; uint8_t id, counter; uint32_t crc; };
    std::vector<uint8_t> header(bool s, bool e, int lt, size_t bodyLen) const;
    uint8_t fragByte(uint8_t id, uint8_t counter) const { return variant.frag == FragMode::Plain ? id : (uint8_t)((id << 2) | (counter & 3)); }
    size_t maxBody() const { return variant.length == LengthMode::Standard ? 4095 : 4093; }
    std::deque<Pending> queue_;
    uint8_t nextId_ = 0;
};



// GSE in high-efficiency mode (EN 302 307-2 clause 5.1.7): GSE packets sliced across BBFRAMEs, SYNCD pointing at the first packet that starts in the data field.
// The deframer joins the slices and hands whole GSE packets (as a byte string, ready for GseDecoder / GseIp::dataField) on.
struct HemStats { uint64_t fields = 0, packets = 0, joined = 0, resyncs = 0, unsynced = 0, mismatches = 0; };
class HemDeframer {
public:
    void discontinuity() { partial_.clear(); synced_ = false; }
    void dataField(const uint8_t* field, size_t n, uint16_t syncdBits, std::vector<uint8_t>& out);
    HemStats stats;
private:
    void resyncAt(long start, size_t& p);
    std::vector<uint8_t> partial_;
    bool synced_ = false;
};

// ---------------------------------------------------------------- IP in the transport stream: MPE and ULE

// PSI/private sections back together from TS payloads, per PID
class SectionAssembler {
public:
    void feed(uint16_t pid, bool pusi, const uint8_t* payload, size_t n, std::vector<std::vector<uint8_t>>& out);
private:
    std::map<uint16_t, std::vector<uint8_t>> partial_;
};

struct MpeStats { std::map<uint16_t, uint64_t> pids; uint64_t datagrams = 0, badSections = 0, skipped = 0; };

// Multiprotocol Encapsulation (EN 301 192): sections with table_id 0x3E, found on any PID that carries one (a PES start rules a PID out), the datagram of each
// (also behind LLC/SNAP) to `out`. Sections whose payload is scrambled are skipped, never decoded.
class MpeExtractor {
public:
    void packet(const uint8_t* p188, std::vector<std::vector<uint8_t>>& out);
    MpeStats stats;
private:
    void section(uint16_t pid, const std::vector<uint8_t>& s, std::vector<std::vector<uint8_t>>& out);
    SectionAssembler sections_;
    std::map<uint16_t, bool> notMpe_;
};

struct UleStats { std::map<uint16_t, uint64_t> pids; uint64_t sndus = 0, badCrc = 0, other = 0, ipDatagrams = 0; };

// Unidirectional Lightweight Encapsulation (RFC 4326, RFC 5163): SNDUs in the payload of TS packets (payload unit pointer), CRC-32 checked. A PID is taken up after
// three SNDUs with a good CRC that carry IP (or a bridged Ethernet frame holding IP); extension headers that are mandatory are skipped over, never interpreted.
class UleExtractor {
public:
    void packet(const uint8_t* p188, std::vector<std::vector<uint8_t>>& out);
    UleStats stats;
private:
    struct Pid { std::vector<uint8_t> buf; bool synced = false; int good = 0; bool accepted = false; int miss = 0; uint8_t cc = 255; bool blocked = false; int tries = 0; };
    void parse(uint16_t pid, Pid& st, bool pusi, const uint8_t* payload, size_t n, std::vector<std::vector<uint8_t>>& out);
    std::map<uint16_t, Pid> pids_;
};

std::vector<uint8_t> buildMpeSection(const uint8_t mac[6], const std::vector<uint8_t>& datagram);
std::vector<std::vector<uint8_t>> packetizeSection(uint16_t pid, uint8_t& cc, const std::vector<uint8_t>& section);   // 188-byte TS packets
std::vector<uint8_t> buildUleSndu(uint16_t type, const std::vector<uint8_t>& pdu, const uint8_t* npa6 = nullptr);   // npa6 null: D = 1, no destination address
std::vector<std::vector<uint8_t>> packetizeUle(uint16_t pid, uint8_t& cc, const std::vector<std::vector<uint8_t>>& sndus);   // SNDUs packed back to back, the payload pointer at the first start


// ---------------------------------------------------------------- multicast media: SAP/SDP announcements, RTP streams, MPEG-TS in UDP

struct SdpMedia { std::string type, proto; int port = 0; std::vector<int> payloadTypes; std::map<int, std::string> rtpmap; std::string address, control; };
struct SdpSession { std::string name, info, origin, address; std::vector<SdpMedia> media; uint64_t seen = 0; std::string source; };
bool parseSdp(const std::string& text, SdpSession& out);
const char* rtpPayloadName(int pt);                   // static payload types: "PCMU", "MPA", "MP2T" ...

struct MediaFlow {
    std::string dst, src;
    int port = 0;
    std::string kind;            // "RTP", "MPEG-TS", "RTP MPEG-TS", "SAP"
    int payloadType = -1;
    std::string codec;           // from the payload type or the SDP rtpmap
    uint32_t ssrc = 0;
    uint64_t packets = 0, bytes = 0, lost = 0;
    double kbps = 0;
};

class IpMedia {
public:
    // one UDP datagram (payload only); returns true when it is an MPEG-TS flow and fills tsOffset (0 or 12 plus extensions): the transport packets start there
    bool udp(const IpInfo& info, const uint8_t* payload, size_t n, double now, size_t& tsOffset);
    std::vector<MediaFlow> flows() const;
    std::vector<SdpSession> sessions() const;
    void clear() { *this = IpMedia(); }
private:
    struct F { MediaFlow f; uint16_t lastSeq = 0; bool haveSeq = false; int goodSeq = 0; double t0 = 0, tLast = 0; uint64_t bytesWin = 0; double winT = 0; };
    std::map<std::string, F> flows_;
    std::map<std::string, SdpSession> sdp_;
};

// ---------------------------------------------------------------- statistics and export

struct FlowKey {
    bool v6 = false;
    std::array<uint8_t, 16> src{}, dst{};
    uint8_t protocol = 0;
    uint16_t sport = 0, dport = 0;
    bool operator<(const FlowKey& o) const;
};
struct Flow {
    FlowKey key;
    uint64_t packets = 0, bytes = 0, udpChecked = 0, udpBad = 0;
    bool encrypted() const { return udpChecked >= 8 && (double)udpBad >= 0.95 * (double)udpChecked; }   // headers good but the UDP checksum fails: changed in transit
};

class IpStats {
public:
    void add(const IpInfo& p, int udpOk);              // udpOk: udpChecksumOk()
    uint64_t packets = 0, bytes = 0, ipv4 = 0, ipv6 = 0;
    std::map<uint8_t, uint64_t> protocols;             // by IP protocol number
    std::map<uint16_t, uint64_t> udpPorts, tcpPorts;   // by destination port: packets
    std::vector<Flow> topFlows(size_t n) const;        // by bytes
    size_t flowCount() const { return flows_.size(); }
    size_t encryptedFlows() const;
    void clear() { *this = IpStats(); }
private:
    std::map<FlowKey, Flow> flows_;
};

// libpcap file, link type RAW (101): IPv4 and IPv6 packets as they are
class PcapWriter {
public:
    ~PcapWriter() { close(); }
    bool open(const std::string& path);
    void close();
    bool isOpen() const { return f_ != nullptr; }
    void write(double unixSeconds, const uint8_t* p, size_t n);
    uint64_t packets = 0, bytes = 0;
    std::string path;
private:
    FILE* f_ = nullptr;
};

// the whole path for one source of IP packets: reassembly, statistics, the PCAP file
class IpSink {
public:
    void push(const uint8_t* ip, size_t n, double now);   // an IP packet found by GSE, MPE or ULE
    std::function<void(const IpInfo&, const uint8_t* ip, const uint8_t* payload, size_t n, double now)> onUdp;   // every whole UDP datagram, for the media analysis
    IpStats stats;
    Reassembler frag;
    PcapWriter pcap;
};

} // namespace dvbsip
} // namespace dect2
