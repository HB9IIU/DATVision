using System.Diagnostics;

namespace DatvRx.Player;

// One way to reach a Pluto, as pluto_find reports it.
public sealed record PlutoFound(string Uri, string Name, string Model, string Serial, string Address, bool Link = false)
{
    public string Label => Model.Split(" (")[0] + " · " +
        (Uri.StartsWith("usb:") ? "USB" : Link ? "USB network link, slow · " + Address : Name + " · " + Address);
}

// Which Plutos to offer, and which one is the saved Pluto: the same rules as the Pi's
// ui/pi_player/plutos.py. Names cannot be trusted alone: two factory-new Plutos are
// both "pluto", and a Pluto on USB also answers on its slow USB network link.
public static class PlutoChoice
{
    static readonly string[] UsbLink = ["192.168.2.", "192.168.3."];   // the Pluto's own network over its USB cable

    // Original Plutos may report all f; "?" when nothing is reported.
    public static bool RealSerial(string serial) =>
        !string.IsNullOrEmpty(serial) && serial != "?" && serial.ToLowerInvariant().Any(c => c != 'f');

    public static List<PlutoFound> Parse(IEnumerable<string> lines)
    {
        var found = new List<PlutoFound>();
        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 6 && parts[0] == "pluto") found.Add(new(parts[1], parts[2], parts[3], parts[4], parts[5]));
        }
        return found;
    }

    // The Plutos to show, and the names found more than once on the network. A USB
    // network link is left out when the same Pluto (same real serial) is on USB directly.
    public static (List<PlutoFound> Plutos, List<string> Twice) Offered(IEnumerable<PlutoFound> found)
    {
        var all = found.ToList();
        var direct = all.Where(p => p.Uri.StartsWith("usb:") && RealSerial(p.Serial)).Select(p => p.Serial).ToHashSet();
        var plutos = new List<PlutoFound>();
        foreach (var pluto in all)
        {
            bool link = UsbLink.Any(pluto.Address.StartsWith);
            if (link && direct.Contains(pluto.Serial)) continue;
            plutos.Add(pluto with { Link = link });
        }
        var names = plutos.Where(p => p.Uri.StartsWith("ip:")).Select(p => p.Name).ToList();
        return (plutos, names.Where(name => names.Count(other => other == name) > 1).Distinct().Order().ToList());
    }

    // The offered Pluto that is the saved one, or null: by real serial (the same path kind
    // as before where there are several), then by a unique name, then by the last address.
    public static PlutoFound Choose(string uri, string name, string serial, string address, IReadOnlyList<PlutoFound> plutos)
    {
        string kind = (uri ?? "").Length >= 3 ? uri[..3] : "";
        if (RealSerial(serial))
        {
            var same = plutos.Where(p => p.Serial == serial).ToList();
            if (same.Count > 0) return same.FirstOrDefault(p => p.Uri.StartsWith(kind)) ?? same[0];
        }
        var named = plutos.Where(p => p.Name == name && p.Name != "USB").ToList();
        if (named.Count == 1) return named[0];
        return plutos.FirstOrDefault(p => p.Address == address);
    }

    // Runs pluto_find.exe: every way to reach a Pluto on USB and the network (about 2 s).
    public static async Task<List<PlutoFound>> SearchAsync(string root)
    {
        string executable = Path.Combine(root, "build/windows/pluto_find.exe");
        if (!File.Exists(executable)) return [];
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        string msys = Environment.GetEnvironmentVariable("DATVRX_MSYS_ROOT") ?? @"C:\msys64";
        string bundled = Path.Combine(root, "runtime/bin");
        info.Environment["PATH"] = (Directory.Exists(bundled) ? bundled : Path.Combine(msys, "ucrt64/bin")) + ";" + Environment.GetEnvironmentVariable("PATH");
        try
        {
            using var process = Process.Start(info);
            if (process == null) return [];
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(); return []; }
            return Parse((await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return []; }
    }
}
