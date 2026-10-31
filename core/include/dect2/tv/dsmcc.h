// DSM-CC data and object carousels (ISO/IEC 13818-6, ETSI TR 101 202 / EN 301 192): the download messages in sections of table 0x3B (DSI, DII) and 0x3C (DDB),
// modules put together from their blocks (zlib compressed modules inflated), and for an object carousel the BIOP objects: the service gateway, directories and
// files, so the file tree of a data broadcast (MHEG-5 or HbbTV applications, a software update, a data service) can be listed and saved.
// Clear broadcast data only: sections that are scrambled are never fed here, and nothing is decrypted.
#pragma once
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

namespace dect2 {

struct DsmccModuleInfo {
    int id = 0;
    uint32_t size = 0, originalSize = 0;
    int version = 0;
    uint32_t blockSize = 0;
    uint32_t blocksHave = 0, blocksTotal = 0;
    bool done = false, compressed = false;
    int assocTag = -1;
};

struct DsmccFileInfo {
    std::string path;            // "dir/sub/file.ext" within the carousel
    uint64_t size = 0;
    int moduleId = 0;
    bool complete = false;       // the whole file is available
};

struct DsmccCarouselInfo {
    int pid = 0;
    uint32_t downloadId = 0;     // the transaction of the DII
    bool objectCarousel = false; // a service gateway (DSI with an IOR) was seen
    bool gatewayKnown = false;   // the service gateway object itself has been received
    uint64_t dsi = 0, dii = 0, ddb = 0, badSections = 0;
    std::vector<DsmccModuleInfo> modules;
    std::vector<DsmccFileInfo> files;
    uint64_t bytesDone = 0, bytesTotal = 0;
};

struct DsmccFile { std::string path; std::vector<uint8_t> data; };

// one carousel on one PID
class DsmccCarousel {
public:
    void section(const uint8_t* s, int len);          // a CRC-checked section of table 0x3B or 0x3C
    DsmccCarouselInfo info(int pid = 0) const;
    std::vector<DsmccFile> files(size_t maxBytes = 256u << 20) const;   // the complete files, with their paths
    uint64_t badSections = 0;
private:
    struct Module {
        DsmccModuleInfo info;
        std::vector<uint8_t> data;
        std::vector<bool> have;
        std::vector<uint8_t> body;      // the module after inflating, when complete
        bool parsed = false;
    };
    struct Ref { int moduleId = -1; std::vector<uint8_t> key; };
    struct Obj { std::string kind; std::vector<std::pair<std::string, Ref>> bindings; std::vector<uint8_t> content; uint64_t contentSize = 0; bool haveContent = false; };
    void dsi(const uint8_t* b, int n);
    void dii(const uint8_t* b, int n);
    void ddb(const uint8_t* s, int len, const uint8_t* b, int n);
    void walkFiles(const std::function<bool(const std::string&, const std::vector<uint8_t>&)>& fn) const;
    void finish(Module& m);
    void parseBiop(int moduleId, const std::vector<uint8_t>& body);
    static bool parseIor(const uint8_t* p, int n, Ref& out, int& assocTag);
    std::map<int, Module> modules_;
    std::map<std::pair<int, std::vector<uint8_t>>, Obj> objects_;
    Ref gateway_;
    bool haveGateway_ = false, gatewayObj_ = false;
    uint64_t allocated_ = 0;
    uint32_t downloadId_ = 0;
    uint64_t dsi_ = 0, dii_ = 0, ddb_ = 0;
    bool objectCarousel_ = false;
};

// all carousels of a transport stream: the PIDs are found from their sections (table 0x3B/0x3C on a PID that is not PES)
class DsmccDemux {
public:
    void packet(const uint8_t* p188);
    std::vector<DsmccCarouselInfo> info() const;
    std::map<int, std::vector<DsmccFile>> files(size_t maxBytes = 256u << 20) const;
    // writes the complete files of every carousel to dir/pid_XXXX/path (paths cleaned of "..", absolute parts and odd characters); returns the number of files
    size_t extractTo(const std::string& dir, size_t maxBytes = 256u << 20) const;
private:
    struct Pid { std::vector<uint8_t> partial; bool active = false; DsmccCarousel c; };
    std::map<int, Pid> pids_;
    std::set<int> notDsmcc_;
};

std::string dsmccSafePath(const std::string& p);   // a path safe to create under a folder: no "..", no leading slash, characters kept to letters, digits and . _ - / space

// builders for test signals: a BIOP object carousel (DSI, DII, DDB sections) holding a directory tree
struct DsmccTestFile { std::string path; std::vector<uint8_t> data; };
std::vector<std::vector<uint8_t>> buildObjectCarousel(const std::vector<DsmccTestFile>& files, uint32_t carouselId, int blockSize, bool compress, uint32_t transactionId = 0x80000001u);

} // namespace dect2
