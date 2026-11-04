// Finds ADALM-Pluto radios on USB and on the network (DNS-SD), for the player's
// Settings. GPL-3.0-or-later, like this project.
//
//   pluto_find
//
// One tab-separated line per Pluto on standard output:
//
//   pluto <uri> <model> <serial>
//
// The uri names the Pluto by host name (ip:fishball.local) where it has one, so a
// changed DHCP address does not lose it. Nothing found: no lines, exit code 0.
#include <iio.h>
#include <cstdio>
#include <cstring>
#include <string>

namespace {

// The context attribute value, or "?" when the Pluto does not report it.
std::string attribute(iio_context* context, const char* name) {
    const char* value = iio_context_get_attr_value(context, name);
    return value && *value ? value : "?";
}

}  // namespace

int main() {
    iio_scan_context* scan = iio_create_scan_context("usb:ip", 0);
    if (!scan) {
        std::fprintf(stderr, "pluto_find: cannot search (libiio without USB or network support?)\n");
        return 1;
    }
    iio_context_info** found = nullptr;
    const ssize_t count = iio_scan_context_get_info_list(scan, &found);
    if (count < 0) {
        std::fprintf(stderr, "pluto_find: search failed (%zd)\n", count);
        iio_scan_context_destroy(scan);
        return 1;
    }
    for (ssize_t n = 0; n < count; n++) {
        const char* uri = iio_context_info_get_uri(found[n]);
        const char* description = iio_context_info_get_description(found[n]);
        // Open each one: the scan description alone does not say it is a Pluto.
        iio_context* context = iio_create_context_from_uri(uri);
        if (!context) {
            std::fprintf(stderr, "pluto_find: %s (%s) did not answer\n", uri, description ? description : "?");
            continue;
        }
        if (iio_context_find_device(context, "ad9361-phy")) {
            std::string serial = attribute(context, "hw_serial");
            if (serial == "?") serial = attribute(context, "usb,serial");
            std::printf("pluto\t%s\t%s\t%s\n", uri, attribute(context, "hw_model").c_str(), serial.c_str());
        }
        iio_context_destroy(context);
    }
    std::fflush(stdout);
    iio_context_info_list_free(found);
    iio_scan_context_destroy(scan);
    return 0;
}
