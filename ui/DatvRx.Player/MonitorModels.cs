using System.Globalization;
using System.Text.RegularExpressions;

namespace DatvRx.Player;

public sealed record ReceiveBand(string Key, string Name, double StartMhz, double EndMhz, double LoMhz, double BeaconMhz = 0)
{
    public static readonly ReceiveBand[] Presets = [
        new("qo100", "QO-100", 10490.5, 10499.5, 9750, 10491.5),
        new("4m", "4 m", 70.5, 71.5, 0), new("2m", "2 m", 146, 147, 0),
        new("70cm", "70 cm", 435, 439, 0), new("23a", "23 cm A", 1243, 1260, 0),
        new("23b", "23 cm B", 1272, 1291, 0), new("6cm", "6 cm / 5200 LO", 5690, 5720, 5200)
    ];
}

public sealed record MonitorFrame(double StartMhz, double EndMhz, float[] Values, bool Local);

public static class MonitorSignalFinder
{
    public static FoundSignal? Find(MonitorFrame frame, double click, double beacon = 0)
    {
        if (frame == null || frame.Values.Length < 16 || !double.IsFinite(click) || click < frame.StartMhz || click > frame.EndMhz) return null;
        int count = frame.Values.Length;
        double step = (frame.EndMhz - frame.StartMhz) / (count - 1);
        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            int left = Math.Max(0, i - 3), right = Math.Min(count - 1, i + 3);
            for (int j = left; j <= right; j++) values[i] += frame.Values[j];
            values[i] /= right - left + 1;
        }
        var sorted = values.Order().ToArray();
        double noise = sorted[count / 2], rise = frame.Local ? 3 : 2000;
        var found = new List<FoundSignal>();
        for (int i = 1; i < count - 1; i++)
        {
            if (values[i] - noise < rise) continue;
            int end = i, peak = i;
            while (end + 1 < count && values[end + 1] - noise >= rise) { end++; if (values[end] > values[peak]) peak = end; }
            if (i <= 1 || end >= count - 1) { i = end; continue; }
            double threshold = noise + (values[peak] - noise) / 2;
            int l = peak, r = peak;
            while (l > 0 && values[l - 1] > threshold) l--;
            while (r + 1 < count && values[r + 1] > threshold) r++;
            double center = frame.StartMhz + (l + r) * 0.5 * step;
            double width = (r - l + 1) * step * 1000;
            int rate = Band.StandardSrs.MinBy(value => Math.Abs(Math.Log(value / width)));
            if (beacon > 0 && Math.Abs(center - beacon) < 0.3) { center = beacon; rate = 1500; }
            found.Add(new FoundSignal { CenterMhz = center, SymbolRate = rate });
            i = end;
        }
        var nearest = found.OrderBy(s => Math.Max(0, Math.Abs(s.CenterMhz - click) - s.SymbolRate * 1.35 / 2000)).ToArray();
        return nearest.Length > 0 && Math.Max(0, Math.Abs(nearest[0].CenterMhz - click) - nearest[0].SymbolRate * 1.35 / 2000) <= 0.25 ? nearest[0] : null;
    }
}

public sealed record ReceptionReport(string Standard, string Modulation, string Fec, double EsN0, long Bad, bool Locked)
{
    private static readonly Regex Report = new(@"(DVB-S2X|DVB-S2|DVB-S) (\S+) (\d+/\d+)\S*(?: short)?(?: pilots)?\s+[\d.]+ Msym/s\s+Es/N0 ([\d.-]+) dB\s+ok \d+\s+bad (\d+)", RegexOptions.Compiled);
    public static ReceptionReport Parse(string line)
    {
        var match = Report.Match(line);
        return match.Success ? new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value,
            double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture), long.Parse(match.Groups[5].Value), line.Contains("TS lock")) : null;
    }
    // Same approximate AWGN thresholds as the Pi's panels.py; not a measured decoder margin.
    public double? Required => (Standard, Modulation, Fec) switch
    {
        ("DVB-S2", "QPSK", "1/4") => -2.35, ("DVB-S2", "QPSK", "1/3") => -1.24,
        ("DVB-S2", "QPSK", "2/5") => -0.30, ("DVB-S2", "QPSK", "1/2") => 1,
        ("DVB-S2", "QPSK", "3/5") => 2.23, ("DVB-S2", "QPSK", "2/3") => 3.10,
        ("DVB-S2", "QPSK", "3/4") => 4.03, ("DVB-S2", "QPSK", "4/5") => 4.68,
        ("DVB-S2", "QPSK", "5/6") => 5.18, ("DVB-S2", "QPSK", "8/9") => 6.20,
        ("DVB-S2", "QPSK", "9/10") => 6.42,
        ("DVB-S2", "8PSK", "3/5") => 5.50, ("DVB-S2", "8PSK", "2/3") => 6.62,
        ("DVB-S2", "8PSK", "3/4") => 7.91, ("DVB-S2", "8PSK", "5/6") => 9.35,
        ("DVB-S2", "8PSK", "8/9") => 10.69, ("DVB-S2", "8PSK", "9/10") => 10.98,
        ("DVB-S", "QPSK", "1/2") => 4.1, ("DVB-S", "QPSK", "2/3") => 5.9,
        ("DVB-S", "QPSK", "3/4") => 6.9, ("DVB-S", "QPSK", "5/6") => 7.9,
        ("DVB-S", "QPSK", "7/8") => 8.5, _ => null
    };
    public double? Margin => Required is { } required ? EsN0 - required : null;
}
