using DatvRx.Player;

static void Require(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}

static ushort[] Carrier(double center, int symbolRate)
{
    var frame = new ushort[8192];
    for (int i = 0; i < frame.Length; i++)
    {
        double frequency = Band.StartMhz + i * (Band.EndMhz - Band.StartMhz) / (frame.Length - 1);
        frame[i] = (ushort)(Math.Abs(frequency - center) < symbolRate / 2000.0 ? 30000 : 9000);
    }
    return frame;
}

foreach (var (center, rate) in new[] { (10491.5, 1500), (10492.75, 500), (10498.25, 333), (10499.25, 125) })
{
    var signal = SignalFinder.Find(Carrier(center, rate), center + 0.02);
    Require(signal.HasValue && signal.Value.SymbolRate == rate && Math.Abs(signal.Value.CenterMhz - center) < 0.01,
        $"Click near {center} MHz estimates {rate} kS/s and the carrier center");
}
Require(SignalFinder.Find(new ushort[8192], 10494) == null, "Blank spectrum does not tune");
Require(SignalFinder.Find(new ushort[0], 10494) == null, "Empty spectrum does not tune");
Require(SignalFinder.Find(Carrier(10492.75, 500), double.NaN) == null, "Invalid click is rejected");
Require(SignalFinder.Find(Carrier(10492.75, 500), 10500) == null, "Out-of-band click is rejected");
Require(LocalReceiver.Validate(new TuneRequest(10491.5, 1500)) == null, "Beacon request is accepted");
Require(LocalReceiver.Validate(new TuneRequest(10491.5, 2000)) != null, "Unsupported 2 MSym/s carrier is rejected");
Require(LocalReceiver.Validate(new TuneRequest(10491.5, 0)) != null, "Invalid symbol rate is rejected");
Require(LocalReceiver.Validate(new TuneRequest(10491.5, 500, 11000)) != null, "Invalid LNB conversion is rejected");
var localValues = Carrier(10492.75, 500).Select(value => value > 10000 ? -25f : -65f).ToArray();
var localCarrier = MonitorSignalFinder.Find(new MonitorFrame(10490.5, 10499.5, localValues, true), 10492.8);
Require(localCarrier is { SymbolRate: 500 } && Math.Abs(localCarrier.Value.CenterMhz - 10492.75) < 0.01, "Local dB spectrum detects and snaps a 500 kS/s signal");
Require(MonitorSignalFinder.Find(new MonitorFrame(146, 147, Enumerable.Repeat(-60f, 1024).ToArray(), true), 146.5) == null, "Local noise-only spectrum does not tune");
string sweep = "sweep 741000000 2343.7500 16 " + string.Join(',', Enumerable.Repeat("-500", 16));
var scan = LocalSweep.Parse(sweep, 9750);
Require(scan != null && Math.Abs(scan.StartMhz - 10491) < 0.001 && scan.Values[0] == -50, "Sweep output converts IF to RF and tenths of dB to dB");
Require(LocalSweep.Parse(sweep.Replace(" 16 ", " 17 "), 9750) == null, "Truncated sweep is rejected");
Require(LocalSweep.Parse(sweep.Replace("741000000", "NaN"), 9750) == null, "Non-finite sweep frequency is rejected");
var report = ReceptionReport.Parse("datvrx [ 2.0 s] DVB-S2 QPSK 4/5 1.500 Msym/s Es/N0 9.3 dB ok 50 bad 2 packets 100 TS lock");
Require(report is { Locked: true, Bad: 2 } && Math.Abs(report.Margin.Value - 4.62) < 0.001, "Telemetry parses lock, bad frames and approximate D reserve");
Require(ReceptionReport.Parse("datvrx: carrier not found") == null, "Acquisition text does not invent quality figures");

static byte[] Packet(int pid, byte[] section)
{
    section[1] = (byte)(0xb0 | ((section.Length - 3) >> 8)); section[2] = (byte)(section.Length - 3);
    var packet = Enumerable.Repeat((byte)0xff, 188).ToArray();
    packet[0] = 0x47; packet[1] = (byte)(0x40 | (pid >> 8)); packet[2] = (byte)pid; packet[3] = 0x10; packet[4] = 0;
    section.CopyTo(packet, 5); return packet;
}
byte[] descriptor = [0x48, 13, 1, 4, (byte)'Q', (byte)'A', (byte)'R', (byte)'S', 6, (byte)'H', (byte)'B', (byte)'9', (byte)'I', (byte)'I', (byte)'U'];
byte[] sdt = [0x42, 0, 0, 0, 1, 0xc1, 0, 0, 0, 1, 0xff, 0, 1, 0xfc, 0x80, (byte)descriptor.Length, ..descriptor, 0, 0, 0, 0];
byte[] pat = [0, 0, 0, 0, 1, 0xc1, 0, 0, 0, 1, 0xe1, 0, 0, 0, 0, 0];
byte[] pmt = [2, 0, 0, 0, 1, 0xc1, 0, 0, 0xe1, 1, 0xf0, 0, 0x24, 0xe1, 1, 0xf0, 0, 0x0f, 0xe1, 2, 0xf0, 0, 0, 0, 0, 0];
var parser = new TransportInfo();
byte[] tables = [..Packet(0, pat), ..Packet(0x100, pmt), ..Packet(0x11, sdt)];
foreach (var chunk in tables.Chunk(17)) parser.Feed(chunk);
Require(parser.Current == new StreamService("HB9IIU", "QARS", "H.265", "AAC"), "Station/provider/codecs survive arbitrarily fragmented TS pipe reads");
parser.Feed(new byte[100]);
Require(parser.Current.Name == "HB9IIU", "Malformed tail does not overwrite recovered station metadata");
if (args.Length > 0)
{
    var actual = new TransportInfo();
    using var recording = File.OpenRead(args[0]);
    byte[] chunk = new byte[3337]; int read;
    while ((read = recording.Read(chunk)) > 0) actual.Feed(chunk[..read]);
    Require(actual.Current.Name.Length > 0 && actual.Current.Video.Length > 0, "Real recorded TS yields station and codec metadata: " + actual.Current);
}
Console.WriteLine("All player checks passed.");
