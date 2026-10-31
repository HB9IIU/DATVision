// Broadcast character sets to UTF-8: appendUtf8 (see text_charset.h). The converters and their tables are Rust (text_charset_rs.cpp over rust/onair_dvbsi); the
// tables are generated from: welle.io src/backend/charsets.cpp (EBU Latin, GPL-2+), checked entry by entry against ODR-PadEnc src/charset.cpp; glibc
// iconvdata/iso_6937.c (ISO/IEC 6937, LGPL-2.1+).
#include "dect2/common/text_charset.h"

namespace dect2::text {

void appendUtf8(std::string& o, uint32_t c) {
    if (c < 0x80) o += (char)c;
    else if (c < 0x800) { o += (char)(0xC0 | (c >> 6)); o += (char)(0x80 | (c & 0x3F)); }
    else if (c < 0x10000) { o += (char)(0xE0 | (c >> 12)); o += (char)(0x80 | ((c >> 6) & 0x3F)); o += (char)(0x80 | (c & 0x3F)); }
    else { o += (char)(0xF0 | (c >> 18)); o += (char)(0x80 | ((c >> 12) & 0x3F)); o += (char)(0x80 | ((c >> 6) & 0x3F)); o += (char)(0x80 | (c & 0x3F)); }
}

} // namespace dect2::text
