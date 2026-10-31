// DVB-RCS / RCS2 forward-link signalling in a transport stream (EN 301 790 section 8.5; EN 301 545-2 section 6.4): what a VSAT hub's outbound tells its
// terminals about the return link. The PMT lists the signalling PIDs with an RCS content descriptor (tag 0xA7) and gives the Network Clock Reference as the
// programme's PCR PID; the tables are the RMT, SCT, FCT, TCT, CMT, TBTP and TIM. Signalling only: nothing is decoded that carries user traffic.
// Reading of the tables follows DecSAT's crates/decsat-ts/src/rcs.rs (GPL-3.0-or-later, made by CasualArclamp), rewritten in C++.
#pragma once
#include <cstdint>
#include <map>
#include <set>
#include <string>
#include <vector>

namespace dect2 {

struct RcsForwardLink {
    int satelliteId = 0, beamId = 0, nccId = 0, usage = 0, localLinkId = 0;
    double frequencyHz = 0, orbitalDeg = 0, symbolRate = 0;
    int polarization = 0;       // 0 H, 1 V, 2 left, 3 right
    int standard = 0;           // 0 DVB-S, 1 DVB-S2 CCM, 2 DVB-S2 ACM, 3 DVB-S2X
    int isi = -1;
    double rollOff = 0;         // 0: not stated
    int s2xMode = -1;
};
struct RcsReturnLink { int satelliteId = 0, beamId = 0, gatewayId = 0, superframeId = 0; double orbitalDeg = 0, txOffsetHz = 0; };
struct RcsLinkage { int linkageType = 0, serviceId = 0, interactiveNetworkId = -1; std::vector<std::pair<int, int>> populations; };
struct RcsSuperframe { int id = 0; double centreHz = 0, durationS = 0; int counter = 0; struct Frame { int type; double startS; double offsetHz; }; std::vector<Frame> frames; };
struct RcsFrameType { int id = 0; double durationS = 0; int timeslots = 0; struct Group { double offsetHz; double startS; int timeslotType; int count; }; std::vector<Group> groups; };
struct RcsTimeslot { int id = 0; double symbolRate = 0, durationS = 0; bool turbo = false; int codeRate = 0, modulation = 0, payload = 0, preambleSymbols = 0; };
struct RcsTableTally { int pid = 0; uint64_t sections = 0, bytes = 0, crcBad = 0; double firstS = 0, lastS = 0; };
struct RcsNcr { int pid = 0; uint64_t packets = 0; double intervalS = 0; bool havePpm = false; double ppm = 0; uint64_t latest = 0; };

struct RcsView {
    bool active = false;
    int networkId = -1;
    std::string networkName;
    std::vector<RcsLinkage> linkages;
    std::vector<RcsForwardLink> forward;
    std::vector<RcsReturnLink> returns;
    bool haveNcr = false;
    RcsNcr ncr;
    std::map<int, RcsTableTally> tables;                 // by table id
    std::vector<std::pair<int, int>> declaredMissing;    // (pid, table id) declared in a PMT but not seen
    std::vector<RcsSuperframe> superframes;
    std::vector<RcsFrameType> frameTypes;
    std::vector<RcsTimeslot> timeslots;
    uint64_t tbtpAssignments = 0, cmtEntries = 0, timUnicast = 0;
    size_t tbtpLogons = 0, cmtLogons = 0, timMacs = 0;
    int timStatus = -1;
    std::vector<int> timDescriptors;
};

const char* rcsTableName(int id);
std::string rcsTableLabel(int id);
const char* rcsDescriptorName(int tag);
const char* rcsRateName(int code);
const char* rcsModulationName(int m);
const char* rcsPayloadName(int p);
std::vector<std::string> rcsTimFlags(int status);

class RcsRx {
public:
    void packet(const uint8_t* p188, double clockS);    // every transport stream packet; clockS: seconds of stream time
    bool active() const { return !declared_.empty() || !view_.tables.empty(); }
    RcsView view() const;
private:
    struct Asm { std::vector<uint8_t> partial; bool active = false; };
    void section(int pid, const uint8_t* s, int len);
    void pat(const uint8_t* b, int n);
    void pmt(const uint8_t* b, int n);
    void rmt(const uint8_t* b, int n);
    void sct(const uint8_t* b, int n);
    void fct(const uint8_t* b, int n);
    void tct(const uint8_t* b, int n);
    void tbtp(const uint8_t* b, int n);
    void cmt(const uint8_t* b, int n);
    void tim(const uint8_t* s, int len);
    void ncr(uint64_t ticks);
    std::map<int, Asm> asm_;
    std::set<int> pmts_;
    std::map<int, std::vector<int>> declared_;
    int ncrPid_ = -1;
    uint64_t ncrFirstT_ = 0, ncrLastT_ = 0, ncrPackets_ = 0;
    double ncrFirstS_ = 0;
    bool ncrHave_ = false;
    double clock_ = 0;
    std::set<int> tbtpLogons_, cmtLogons_;
    std::set<uint64_t> timMacs_;
    RcsView view_;
};

// builders for test signals: the tables of a small interactive network, as sections (with CRC)
struct RcsTestNetwork {
    std::vector<std::vector<uint8_t>> sections;          // PAT, PMT, then the tables, each a complete section
    std::vector<int> pids;                               // the PID of each section
    int ncrPid = 0x1600;
};
RcsTestNetwork buildRcsTestNetwork();

} // namespace dect2
