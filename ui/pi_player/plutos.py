"""Which Plutos to offer, and which one is the saved Pluto. GPL-3.0-or-later.

pluto_find reports every way to reach a Pluto: (uri, name, model, serial, address).
Names cannot be trusted alone: two factory-new Plutos are both "pluto", and a Pluto
on USB also answers on its slow USB network link (192.168.2.1) under its name.
"""

USB_LINK = ("192.168.2.", "192.168.3.")   # the Pluto's own network over its USB cable


def real_serial(serial):
    # Original Plutos may report all f; "?" when nothing is reported.
    return bool(serial) and serial != "?" and set(serial.lower()) != {"f"}


def offered(found):
    """The Plutos to show: (list of dicts, names found more than once on the network).

    A Pluto's USB network link is left out when the same Pluto (same real serial) is
    also on USB directly, the better path; otherwise it is kept and marked as link.
    """
    direct = {serial for uri, _, _, serial, _ in found if uri.startswith("usb:") and real_serial(serial)}
    plutos = []
    for uri, name, model, serial, address in found:
        link = address.startswith(USB_LINK)
        if link and serial in direct:
            continue
        plutos.append({"uri": uri, "name": name, "model": model, "serial": serial,
                       "address": address, "link": link})
    names = [p["name"] for p in plutos if p["uri"].startswith("ip:")]
    return plutos, sorted({name for name in names if names.count(name) > 1})


def choose(saved, plutos):
    """The offered Pluto that is the saved one, or None.

    In this order: the real serial (the same path kind as before where there are
    several); the name, if only one Pluto has it; the last address.
    """
    kind = saved.get("uri", "")[:3]
    if real_serial(saved.get("serial", "")):
        same = [p for p in plutos if p["serial"] == saved["serial"]]
        if same:
            return next((p for p in same if p["uri"].startswith(kind)), same[0])
    named = [p for p in plutos if p["name"] == saved.get("name") and p["name"] != "USB"]
    if len(named) == 1:
        return named[0]
    return next((p for p in plutos if p["address"] == saved.get("address")), None)
