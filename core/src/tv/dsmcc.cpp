// DSM-CC data and object carousels (see dsmcc.h).
#include "dect2/tv/dsmcc.h"
#include "dect2/common/inflate.h"
#include "dect2/dvbs/dvbs_ip.h"
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <sys/stat.h>

namespace dect2 {

namespace {
uint32_t u32(const uint8_t* p) { return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3]; }
uint16_t u16(const uint8_t* p) { return (uint16_t)(p[0] << 8 | p[1]); }
std::string cstr(const uint8_t* p, size_t n) { std::string s((const char*)p, n); while (!s.empty() && s.back() == 0) s.pop_back(); return s; }
const size_t kMaxModule = 32u << 20;
}

// ---------------------------------------------------------------- IOR

bool DsmccCarousel::parseIor(const uint8_t* p, int n, Ref& out, int& assocTag) {
    if (n < 12) return false;
    int i = 0;
    const uint32_t tl = u32(p);
    i += 4;
    if (tl > 64 || i + (int)tl + 4 > n) return false;
    i += (int)tl;
    while (i % 4 && i < n) i++;   // the type id is padded to a multiple of four (CDR)
    if (i + 4 > n) return false;
    const uint32_t profiles = u32(p + i); i += 4;
    bool found = false;
    for (uint32_t k = 0; k < profiles && k < 8; k++) {
        if (i + 8 > n) return found;
        const uint32_t tag = u32(p + i), pl = u32(p + i + 4);
        i += 8;
        if (i + (int)pl > n) return found;
        if (tag == 0x49534F06 && pl >= 2) {   // BIOP profile body
            const uint8_t* b = p + i;
            int j = 1;
            const int comps = b[j++];
            for (int c = 0; c < comps && j + 5 <= (int)pl; c++) {
                const uint32_t ct = u32(b + j); const int cl = b[j + 4];
                j += 5;
                if (j + cl > (int)pl) break;
                if (ct == 0x49534F50 && cl >= 9) {   // ObjectLocation
                    out.moduleId = u16(b + j + 4);
                    const int kl = b[j + 8];
                    if (9 + kl <= cl) { out.key.assign(b + j + 9, b + j + 9 + kl); found = true; }
                } else if (ct == 0x49534F40 && cl >= 2) {   // ConnBinder: the first tap
                    const int taps = b[j];
                    if (taps >= 1 && cl >= 1 + 7) assocTag = u16(b + j + 1 + 4);
                }
                j += cl;
            }
        }
        i += (int)pl;
    }
    return found;
}

// ---------------------------------------------------------------- download messages

void DsmccCarousel::section(const uint8_t* s, int len) {
    if (len < 8 + 12 + 4) return;
    const bool syntax = (s[1] & 0x80) != 0;
    if (syntax && dvbsip::crc32Mpeg2(s, (size_t)len) != 0) { badSections++; return; }
    const int end = syntax ? len - 4 : len;
    const uint8_t* m = s + 8;
    int n = end - 8;
    if (n < 12 || m[0] != 0x11 || m[1] != 0x03) return;
    const int mid = u16(m + 2);
    const int adapt = m[9];
    const int mlen = u16(m + 10);
    if (12 + adapt + mlen > n) { badSections++; return; }
    const uint8_t* body = m + 12 + adapt;
    if (s[0] == 0x3B && mid == 0x1006) dsi(body, mlen);
    else if (s[0] == 0x3B && mid == 0x1002) dii(body, mlen);
    else if (s[0] == 0x3C && mid == 0x1003) ddb(s, len, body, mlen);
}

void DsmccCarousel::dsi(const uint8_t* b, int n) {
    dsi_++;
    if (n < 20 + 2 + 2) return;
    int i = 20;
    const int cl = u16(b + i); i += 2 + cl;
    if (i + 2 > n) return;
    const int pl = u16(b + i); i += 2;
    if (i + pl > n) return;
    Ref r; int tag = -1;
    if (pl > 12 && parseIor(b + i, pl, r, tag)) { gateway_ = r; haveGateway_ = true; objectCarousel_ = true; }
}

void DsmccCarousel::dii(const uint8_t* b, int n) {
    dii_++;
    if (n < 4 + 2 + 1 + 1 + 4 + 4 + 2) return;
    int i = 0;
    downloadId_ = u32(b); i += 4;
    const int blockSize = u16(b + i); i += 2;
    i += 2 + 8;   // window size, ack period, TC download window and scenario
    const int cdl = u16(b + i); i += 2 + cdl;
    if (i + 2 > n) return;
    const int nmod = u16(b + i); i += 2;
    for (int k = 0; k < nmod && k < 4096; k++) {
        if (i + 8 > n) return;
        DsmccModuleInfo mi;
        mi.id = u16(b + i); mi.size = u32(b + i + 2); mi.version = b[i + 6];
        const int il = b[i + 7];
        i += 8;
        if (i + il > n) return;
        mi.blockSize = (uint32_t)blockSize;
        mi.blocksTotal = blockSize ? (mi.size + (uint32_t)blockSize - 1) / (uint32_t)blockSize : 0;
        mi.originalSize = mi.size;
        // BIOP::ModuleInfo: three time-outs, the taps, the user info (descriptors: 0x09 compressed module)
        const uint8_t* q = b + i;
        if (il >= 13) {
            int j = 12;
            const int taps = q[j++];
            for (int t = 0; t < taps && j + 7 <= il; t++) {
                if (t == 0) mi.assocTag = u16(q + j + 4);
                j += 7 + q[j + 6];
            }
            if (j < il) {
                const int ul = q[j++];
                int d = j;
                while (d + 2 <= j + ul && d + 2 <= il) {
                    const int tag = q[d], dl = q[d + 1];
                    if (tag == 0x09 && dl >= 5 && d + 2 + dl <= il) { mi.compressed = true; mi.originalSize = u32(q + d + 3); }
                    d += 2 + dl;
                }
            }
        }
        i += il;
        auto it = modules_.find(mi.id);
        if (it == modules_.end() || it->second.info.version != mi.version || it->second.info.size != mi.size) {
            if (mi.size > kMaxModule || allocated_ + mi.size > (256u << 20)) continue;   // memory is bounded
            allocated_ += mi.size;
            Module m;
            m.info = mi;
            m.data.assign(mi.size, 0);
            m.have.assign(mi.blocksTotal, false);
            modules_[mi.id] = std::move(m);
        }
    }
}

void DsmccCarousel::ddb(const uint8_t*, int, const uint8_t* b, int n) {
    ddb_++;
    if (n < 6) return;
    const int mod = u16(b), ver = b[2], blk = u16(b + 4);
    auto it = modules_.find(mod);
    if (it == modules_.end()) return;   // the DII has not been seen yet: the carousel repeats the block
    Module& m = it->second;
    if (m.info.version != ver || m.info.done || blk >= m.have.size() || m.have[(size_t)blk]) return;
    const size_t off = (size_t)blk * m.info.blockSize;
    const size_t take = std::min<size_t>((size_t)n - 6, m.data.size() > off ? m.data.size() - off : 0);
    if (take == 0) return;
    memcpy(&m.data[off], b + 6, take);
    m.have[(size_t)blk] = true;
    m.info.blocksHave++;
    if (m.info.blocksHave == m.info.blocksTotal) finish(m);
}

void DsmccCarousel::finish(Module& m) {
    m.info.done = true;
    if (m.info.compressed) {
        // zlib: a two-byte header, deflate, a four-byte Adler-32
        if (m.data.size() < 6 || (m.data[0] & 0x0F) != 8) { m.info.done = false; return; }
        std::vector<uint8_t> out;
        if (!inflateRaw(m.data.data() + 2, m.data.size() - 2, out, kMaxModule)) { m.info.done = false; return; }
        m.body = std::move(out);
    } else m.body = m.data;
    parseBiop(m.info.id, m.body);
}

// ---------------------------------------------------------------- BIOP

void DsmccCarousel::parseBiop(int moduleId, const std::vector<uint8_t>& d) {
    size_t i = 0;
    while (i + 12 <= d.size() && memcmp(&d[i], "BIOP", 4) == 0) {
        const uint32_t msz = u32(&d[i + 8]);
        const size_t end = i + 12 + msz;
        if (end > d.size()) break;
        size_t j = i + 12;
        auto need = [&](size_t n) { return j + n <= end; };
        if (!need(1)) break;
        const int kl = d[j++];
        if (!need((size_t)kl + 4)) break;
        std::vector<uint8_t> key(d.begin() + (long)j, d.begin() + (long)j + kl);
        j += (size_t)kl;
        const uint32_t okl = u32(&d[j]); j += 4;
        if (okl > 8 || !need(okl + 2)) break;
        const std::string kind = cstr(&d[j], okl); j += okl;
        const int oil = u16(&d[j]); j += 2;
        if (!need((size_t)oil + 1)) break;
        const uint8_t* info = &d[j];
        j += (size_t)oil;
        const int sc = d[j++];
        bool ok = true;
        for (int k = 0; k < sc && ok; k++) { if (!need(6)) { ok = false; break; } const int cl = u16(&d[j + 4]); j += 6 + (size_t)cl; if (j > end) ok = false; }
        if (!ok || !need(4)) break;
        const uint32_t bl = u32(&d[j]); j += 4;
        if (!need(bl)) break;
        const uint8_t* body = &d[j];
        Obj& o = objects_[{moduleId, key}];
        o = Obj();
        o.kind = kind;
        if (kind == "fil" && bl >= 4) {
            if (oil >= 8) o.contentSize = (uint64_t)u32(info) << 32 | u32(info + 4);
            const uint32_t cl = u32(body);
            if (4 + cl <= bl) { o.content.assign(body + 4, body + 4 + cl); o.haveContent = true; if (!o.contentSize) o.contentSize = cl; }
        } else if ((kind == "dir" || kind == "srg") && bl >= 2) {
            const int nb = u16(body);
            size_t p = 2;
            for (int b = 0; b < nb && p < bl; b++) {
                if (p + 1 > bl) break;
                const int nc = body[p++];
                std::string name;
                for (int c = 0; c < nc; c++) {
                    if (p + 1 > bl) break;
                    const int il = body[p++];
                    if (p + (size_t)il + 1 > bl) break;
                    const std::string id = cstr(body + p, (size_t)il); p += (size_t)il;
                    const int kl2 = body[p++];
                    p += (size_t)kl2;
                    name += (name.empty() ? "" : "/") + id;
                }
                if (p + 1 > bl) break;
                p++;   // binding type
                // the object reference: an IOR of unknown length, parsed in place
                if (p + 12 > bl) break;
                const uint32_t tl = u32(body + p);
                size_t q = p + 4 + tl;
                while (q % 4 && q < bl) q++;   // alignment is relative to the IOR start, which is 4-aligned in the body only by construction: pad inside the IOR
                // (walk the IOR to find its end)
                size_t r = p + 4 + tl;
                { size_t rel = 4 + tl; while (rel % 4) rel++; r = p + rel; }
                if (r + 4 > bl) break;
                const uint32_t profiles = u32(body + r);
                r += 4;
                for (uint32_t k = 0; k < profiles; k++) { if (r + 8 > bl) { r = bl; break; } r += 8 + u32(body + r + 4); }
                if (r > bl) break;
                Ref ref; int tag = -1;
                parseIor(body + p, (int)(r - p), ref, tag);
                p = r;
                if (p + 2 > bl) break;
                const int oi = u16(body + p);
                p += 2 + (size_t)oi;
                if (!name.empty() && ref.moduleId >= 0) o.bindings.push_back({name, ref});
            }
        }
        i = end;
    }
}

// ---------------------------------------------------------------- results

DsmccCarouselInfo DsmccCarousel::info(int pid) const {
    DsmccCarouselInfo o;
    o.pid = pid; o.downloadId = downloadId_; o.objectCarousel = objectCarousel_; o.dsi = dsi_; o.dii = dii_; o.ddb = ddb_; o.badSections = badSections;
    for (const auto& kv : modules_) {
        o.modules.push_back(kv.second.info);
        o.bytesTotal += kv.second.info.size;
        if (kv.second.info.done) o.bytesDone += kv.second.info.size;
    }
    auto it = objects_.end();
    if (haveGateway_) it = objects_.find({gateway_.moduleId, gateway_.key});
    o.gatewayKnown = it != objects_.end() && it->second.kind == "srg";
    walkFiles([&](const std::string& path, const std::vector<uint8_t>& data) {
        DsmccFileInfo fi;
        fi.path = path; fi.size = data.size(); fi.complete = true;
        o.files.push_back(fi);
        return o.files.size() < 2048;
    });
    return o;
}

void DsmccCarousel::walkFiles(const std::function<bool(const std::string&, const std::vector<uint8_t>&)>& fn) const {
    std::set<std::pair<int, std::vector<uint8_t>>> used;
    bool stop = false;
    size_t count = 0;
    std::function<void(const std::pair<int, std::vector<uint8_t>>&, const std::string&, int)> walk = [&](const std::pair<int, std::vector<uint8_t>>& k, const std::string& path, int depth) {
        if (stop || depth > 32 || count > 65536) return;
        auto it = objects_.find(k);
        if (it == objects_.end() || used.count(k)) return;
        used.insert(k);
        const Obj& o = it->second;
        if (o.kind == "fil") { if (o.haveContent) { count++; if (!fn(path, o.content)) stop = true; } }
        else if (o.kind == "dir" || o.kind == "srg") for (const auto& b : o.bindings) walk({b.second.moduleId, b.second.key}, path.empty() ? b.first : path + "/" + b.first, depth + 1);
    };
    if (haveGateway_) walk({gateway_.moduleId, gateway_.key}, "", 0);
    // files that no directory has been seen for yet
    for (const auto& kv : objects_) {
        if (stop || kv.second.kind != "fil" || used.count(kv.first) || !kv.second.haveContent) continue;
        char b[48];
        snprintf(b, sizeof b, "unlinked/module_%04X/", kv.first.first);
        std::string hex;
        for (uint8_t c : kv.first.second) { char h[4]; snprintf(h, sizeof h, "%02X", c); hex += h; }
        if (!fn(std::string(b) + (hex.empty() ? "object" : hex), kv.second.content)) stop = true;
    }
}

std::vector<DsmccFile> DsmccCarousel::files(size_t maxBytes) const {
    std::vector<DsmccFile> out;
    size_t total = 0;
    walkFiles([&](const std::string& path, const std::vector<uint8_t>& data) {
        if (total + data.size() > maxBytes) return true;
        out.push_back({path, data});
        total += data.size();
        return true;
    });
    return out;
}

// ---------------------------------------------------------------- demux

void DsmccDemux::packet(const uint8_t* p) {
    const int pid = (p[1] & 0x1F) << 8 | p[2];
    if ((p[1] & 0x80) || pid < 0x20 || pid == 0x1FFF || notDsmcc_.count(pid)) return;
    const int afc = (p[3] >> 4) & 3;
    if ((p[3] >> 6) != 0 || !(afc & 1)) return;   // scrambled: never fed
    const size_t at = 4 + ((afc & 2) ? 1 + (size_t)p[4] : 0);
    if (at >= 188) return;
    const bool pusi = (p[1] & 0x40) != 0;
    const uint8_t* payload = p + at;
    const size_t n = 188 - at;
    auto it = pids_.find(pid);
    if (it == pids_.end()) {
        if (!pusi) return;
        if (n >= 3 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1) { notDsmcc_.insert(pid); return; }
        const size_t ptr = payload[0];
        if (1 + ptr >= n || (payload[1 + ptr] != 0x3B && payload[1 + ptr] != 0x3C)) return;
        it = pids_.emplace(pid, Pid()).first;
    }
    Pid& st = it->second;
    // sections (as in the section assembler, per PID)
    auto emit = [&](const uint8_t* s, size_t len) { if (len >= 3 && (s[0] == 0x3B || s[0] == 0x3C)) st.c.section(s, (int)len); };
    auto secLen = [](const uint8_t* s) { return 3 + (size_t)(((s[1] & 0x0F) << 8) | s[2]); };
    if (pusi) {
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

std::vector<DsmccCarouselInfo> DsmccDemux::info() const {
    std::vector<DsmccCarouselInfo> v;
    for (const auto& kv : pids_) v.push_back(kv.second.c.info(kv.first));
    return v;
}

std::map<int, std::vector<DsmccFile>> DsmccDemux::files(size_t maxBytes) const {
    std::map<int, std::vector<DsmccFile>> m;
    for (const auto& kv : pids_) m[kv.first] = kv.second.c.files(maxBytes);
    return m;
}

std::string dsmccSafePath(const std::string& p) {
    std::string out, comp;
    auto flush = [&]() { if (!comp.empty() && comp != "." && comp != "..") out += (out.empty() ? "" : "/") + comp; comp.clear(); };
    for (char c : p) {
        if (c == '/' || c == '\\') { flush(); continue; }
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' || c == ' ') comp += c;
        else comp += '_';
    }
    flush();
    return out.empty() ? "file" : out;
}

static bool mkdirs(const std::string& path) {   // std::filesystem: POSIX mkdir(path, mode) does not exist on MinGW
    std::error_code ec;
    std::filesystem::create_directories(std::filesystem::path(path), ec);
    return std::filesystem::is_directory(std::filesystem::path(path), ec);
}

static std::string htmlEsc(const std::string& s) {
    std::string o;
    for (char c : s) {
        if (c == '&') o += "&amp;"; else if (c == '<') o += "&lt;"; else if (c == '>') o += "&gt;"; else if (c == '"') o += "&quot;"; else o += c;
    }
    return o;
}

static std::string lowerExt(const std::string& p) {
    const size_t d = p.find_last_of('.');
    std::string e = d == std::string::npos ? std::string() : p.substr(d + 1);
    for (char& c : e) if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
    return e;
}

// Saves the complete files under dir/pid_XXXX/ and an index.html next to them: images are shown, text is previewed (the first 2 KB, escaped), everything else is linked
// with its size. Nothing is run or decoded. Only plain bytes of the carousels are written (scrambled sections never get this far).
size_t DsmccDemux::extractTo(const std::string& dir, size_t maxBytes) const {
    size_t n = 0;
    std::string html = "<!doctype html><meta charset=utf-8><title>Carousel files</title><style>body{font:14px sans-serif;margin:16px}"
                       ".f{display:inline-block;vertical-align:top;margin:6px;padding:8px;border:1px solid #888;max-width:340px}"
                       "img{max-width:320px;max-height:240px;display:block}pre{white-space:pre-wrap;max-height:160px;overflow:auto;font-size:12px}</style><h1>Carousel files</h1>";
    for (const auto& kv : files(maxBytes)) {
        char sub[32];
        snprintf(sub, sizeof sub, "pid_%04X", kv.first);
        html += std::string("<h2>PID 0x") + (sub + 4) + "</h2>";
        for (const DsmccFile& f : kv.second) {
            const std::string rel = dsmccSafePath(f.path);
            const std::string full = dir + "/" + sub + "/" + rel;
            const size_t slash = full.find_last_of('/');
            if (!mkdirs(full.substr(0, slash))) continue;
            std::ofstream o(full, std::ios::binary);
            if (!o) continue;
            o.write((const char*)f.data.data(), (std::streamsize)f.data.size());
            n++;
            const std::string href = std::string(sub) + "/" + htmlEsc(rel), e = lowerExt(rel);
            html += "<div class=f><a href=\"" + href + "\">" + htmlEsc(f.path) + "</a> (" + std::to_string(f.data.size()) + " bytes)";
            if (e == "png" || e == "jpg" || e == "jpeg" || e == "gif" || e == "bmp" || e == "svg" || e == "webp") html += "<img src=\"" + href + "\">";
            else if (e == "txt" || e == "xml" || e == "html" || e == "htm" || e == "css" || e == "js" || e == "json" || e == "ait" || e == "xhtml" || e == "csv" || e == "srt") {
                std::string t((const char*)f.data.data(), std::min<size_t>(f.data.size(), 2048));
                for (char& c : t) if ((unsigned char)c < 9 || ((unsigned char)c > 13 && (unsigned char)c < 32)) c = '.';
                html += "<pre>" + htmlEsc(t) + "</pre>";
            }
            html += "</div>";
        }
    }
    if (n) { std::ofstream o(dir + "/index.html", std::ios::binary); o << html; }
    return n;
}

// ---------------------------------------------------------------- test signal

namespace {
void put32(std::vector<uint8_t>& v, uint32_t x) { v.push_back((uint8_t)(x >> 24)); v.push_back((uint8_t)(x >> 16)); v.push_back((uint8_t)(x >> 8)); v.push_back((uint8_t)x); }
void put16(std::vector<uint8_t>& v, uint16_t x) { v.push_back((uint8_t)(x >> 8)); v.push_back((uint8_t)x); }
std::vector<uint8_t> ior(const char* type, uint32_t carousel, int module, const std::vector<uint8_t>& key) {
    std::vector<uint8_t> o;
    put32(o, 4); o.insert(o.end(), type, type + 4);   // type id "srg\0", "dir\0", "fil\0"
    put32(o, 1);
    put32(o, 0x49534F06);
    std::vector<uint8_t> pb{0x00, 0x02};              // big endian, two lite components
    pb.push_back(0x49); pb.push_back(0x53); pb.push_back(0x4F); pb.push_back(0x50);   // ObjectLocation
    std::vector<uint8_t> ol;
    put32(ol, carousel); put16(ol, (uint16_t)module); ol.push_back(1); ol.push_back(0); ol.push_back((uint8_t)key.size()); ol.insert(ol.end(), key.begin(), key.end());
    pb.push_back((uint8_t)ol.size()); pb.insert(pb.end(), ol.begin(), ol.end());
    pb.push_back(0x49); pb.push_back(0x53); pb.push_back(0x4F); pb.push_back(0x40);   // ConnBinder
    std::vector<uint8_t> cb{0x01, 0x00, 0x01, 0x00, 0x16, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
    // taps_count 1: id 1, use 0x16 (BIOP_DELIVERY_PARA_USE), assocTag 0x000A, selector_length 0
    cb = {0x01, 0x00, 0x01, 0x00, 0x16, 0x00, 0x0A, 0x00};
    pb.push_back((uint8_t)cb.size()); pb.insert(pb.end(), cb.begin(), cb.end());
    put32(o, (uint32_t)pb.size()); o.insert(o.end(), pb.begin(), pb.end());
    return o;
}
std::vector<uint8_t> biopObject(const std::vector<uint8_t>& key, const char* kind, const std::vector<uint8_t>& info, const std::vector<uint8_t>& body) {
    std::vector<uint8_t> m;
    m.push_back((uint8_t)key.size()); m.insert(m.end(), key.begin(), key.end());
    put32(m, 4); m.insert(m.end(), kind, kind + 4);
    put16(m, (uint16_t)info.size()); m.insert(m.end(), info.begin(), info.end());
    m.push_back(0);   // no service contexts
    put32(m, (uint32_t)body.size()); m.insert(m.end(), body.begin(), body.end());
    std::vector<uint8_t> o{'B', 'I', 'O', 'P', 1, 0, 0, 0};
    put32(o, (uint32_t)m.size()); o.insert(o.end(), m.begin(), m.end());
    return o;
}
std::vector<uint8_t> storedZlib(const std::vector<uint8_t>& d) {
    std::vector<uint8_t> o{0x78, 0x01};
    size_t i = 0;
    do {
        const size_t n = std::min<size_t>(65535, d.size() - i);
        const bool last = i + n >= d.size();
        o.push_back(last ? 1 : 0); o.push_back((uint8_t)n); o.push_back((uint8_t)(n >> 8)); o.push_back((uint8_t)~n); o.push_back((uint8_t)(~n >> 8));
        o.insert(o.end(), d.begin() + (long)i, d.begin() + (long)(i + n));
        i += n;
    } while (i < d.size());
    uint32_t a = 1, b = 0;
    for (uint8_t c : d) { a = (a + c) % 65521; b = (b + a) % 65521; }
    put32(o, b << 16 | a);
    return o;
}
std::vector<uint8_t> dsmccSection(int tid, int ext, int version, int secNum, int lastSec, const std::vector<uint8_t>& msg) {
    std::vector<uint8_t> s{(uint8_t)tid, 0, 0, (uint8_t)(ext >> 8), (uint8_t)ext, (uint8_t)(0xC1 | (version << 1)), (uint8_t)secNum, (uint8_t)lastSec};
    s.insert(s.end(), msg.begin(), msg.end());
    const size_t len = s.size() - 3 + 4;
    s[1] = (uint8_t)(0xB0 | (len >> 8)); s[2] = (uint8_t)len;
    const uint32_t c = dvbsip::crc32Mpeg2(s.data(), s.size());
    put32(s, c);
    return s;
}
std::vector<uint8_t> header(int messageId, uint32_t id, const std::vector<uint8_t>& body) {
    std::vector<uint8_t> m{0x11, 0x03};
    put16(m, (uint16_t)messageId); put32(m, id); m.push_back(0xFF); m.push_back(0);
    put16(m, (uint16_t)body.size());
    m.insert(m.end(), body.begin(), body.end());
    return m;
}
}

// files: "a/b.txt" ... one module per directory and one for the gateway; each file object in a module of its own (keeps the test simple and exercises many modules)
std::vector<std::vector<uint8_t>> buildObjectCarousel(const std::vector<DsmccTestFile>& files, uint32_t carouselId, int blockSize, bool compress, uint32_t txId) {
    struct Node { std::map<std::string, int> kids; int fileIdx = -1; int id = 0; };
    std::vector<Node> nodes(1);
    for (size_t f = 0; f < files.size(); f++) {
        int cur = 0;
        std::string p = files[f].path, comp;
        std::vector<std::string> parts;
        for (char c : p) { if (c == '/') { parts.push_back(comp); comp.clear(); } else comp += c; }
        parts.push_back(comp);
        for (size_t k = 0; k < parts.size(); k++) {
            auto it = nodes[(size_t)cur].kids.find(parts[k]);
            if (it == nodes[(size_t)cur].kids.end()) { nodes.push_back(Node()); nodes[(size_t)cur].kids[parts[k]] = (int)nodes.size() - 1; cur = (int)nodes.size() - 1; }
            else cur = it->second;
        }
        nodes[(size_t)cur].fileIdx = (int)f;
    }
    // modules: node i is module i + 1; its object key is the node index + 1 as one byte
    std::vector<std::vector<uint8_t>> modules(nodes.size());
    auto keyOf = [](int node) { return std::vector<uint8_t>{(uint8_t)(node + 1)}; };
    for (size_t i = 0; i < nodes.size(); i++) {
        if (nodes[i].fileIdx >= 0) {
            const auto& d = files[(size_t)nodes[i].fileIdx].data;
            std::vector<uint8_t> info; put32(info, 0); put32(info, (uint32_t)d.size());
            std::vector<uint8_t> body; put32(body, (uint32_t)d.size()); body.insert(body.end(), d.begin(), d.end());
            modules[i] = biopObject(keyOf((int)i), "fil\0", info, body);
        } else {
            std::vector<uint8_t> body;
            put16(body, (uint16_t)nodes[i].kids.size());
            for (const auto& kv : nodes[i].kids) {
                const int c = kv.second;
                body.push_back(1);
                body.push_back((uint8_t)(kv.first.size() + 1)); body.insert(body.end(), kv.first.begin(), kv.first.end()); body.push_back(0);
                body.push_back(4); body.push_back(nodes[(size_t)c].fileIdx >= 0 ? 'f' : 'd'); body.push_back(nodes[(size_t)c].fileIdx >= 0 ? 'i' : 'i'); body.push_back(nodes[(size_t)c].fileIdx >= 0 ? 'l' : 'r'); body.push_back(0);
                body.push_back(1);   // nobject
                const auto o = ior(nodes[(size_t)c].fileIdx >= 0 ? "fil\0" : "dir\0", carouselId, c + 1, keyOf(c));
                body.insert(body.end(), o.begin(), o.end());
                put16(body, 0);
            }
            modules[i] = biopObject(keyOf((int)i), i == 0 ? "srg\0" : "dir\0", {}, body);
        }
    }
    std::vector<std::vector<uint8_t>> sections;
    // DSI: the service gateway reference
    {
        std::vector<uint8_t> body(20, 0xFF);
        put16(body, 0);   // compatibility descriptor
        std::vector<uint8_t> pd = ior("srg\0", carouselId, 1, keyOf(0));
        pd.push_back(0); pd.push_back(0); put16(pd, 0);   // taps, service contexts, user info
        put16(body, (uint16_t)pd.size()); body.insert(body.end(), pd.begin(), pd.end());
        sections.push_back(dsmccSection(0x3B, 0x0000, 0, 0, 0, header(0x1006, txId, body)));
    }
    // DII
    std::vector<std::vector<uint8_t>> payloads;
    for (auto& m : modules) payloads.push_back(compress ? storedZlib(m) : m);
    {
        std::vector<uint8_t> body;
        put32(body, txId + 1); put16(body, (uint16_t)blockSize); body.push_back(0); body.push_back(0); put32(body, 0); put32(body, 0); put16(body, 0);
        put16(body, (uint16_t)payloads.size());
        for (size_t i = 0; i < payloads.size(); i++) {
            put16(body, (uint16_t)(i + 1)); put32(body, (uint32_t)payloads[i].size()); body.push_back(0);
            std::vector<uint8_t> mi; put32(mi, 0xFFFFFFFF); put32(mi, 0xFFFFFFFF); put32(mi, 0); mi.push_back(1);
            put16(mi, 1); put16(mi, 0x0016); put16(mi, 0x000B); mi.push_back(0);   // a tap
            std::vector<uint8_t> ui;
            if (compress) { ui.push_back(0x09); ui.push_back(5); ui.push_back(8); put32(ui, (uint32_t)modules[i].size()); }
            mi.push_back((uint8_t)ui.size()); mi.insert(mi.end(), ui.begin(), ui.end());
            body.push_back((uint8_t)mi.size()); body.insert(body.end(), mi.begin(), mi.end());
        }
        put16(body, 0);
        sections.push_back(dsmccSection(0x3B, 0x0001, 0, 0, 0, header(0x1002, txId + 1, body)));
    }
    // DDB blocks
    for (size_t i = 0; i < payloads.size(); i++) {
        const size_t nb = (payloads[i].size() + (size_t)blockSize - 1) / (size_t)blockSize;
        for (size_t b = 0; b < nb; b++) {
            std::vector<uint8_t> body;
            put16(body, (uint16_t)(i + 1)); body.push_back(0); body.push_back(0xFF); put16(body, (uint16_t)b);
            const size_t off = b * (size_t)blockSize, n = std::min<size_t>((size_t)blockSize, payloads[i].size() - off);
            body.insert(body.end(), payloads[i].begin() + (long)off, payloads[i].begin() + (long)(off + n));
            std::vector<uint8_t> m{0x11, 0x03}; put16(m, 0x1003); put32(m, txId + 1); m.push_back(0xFF); m.push_back(0); put16(m, (uint16_t)body.size()); m.insert(m.end(), body.begin(), body.end());
            sections.push_back(dsmccSection(0x3C, (int)(i + 1), 0, (int)(b & 0xFF), (int)((nb - 1) & 0xFF), m));
        }
    }
    return sections;
}

} // namespace dect2
