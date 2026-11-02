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


foreach (int detune in LnbCalibration.StepsKhz)
{
    // A true LO 54 kHz above nominal produces -54 kHz CFO at zero detune.
    double cfo = -54000 - detune * 1000;
    double estimate = LnbCalibration.Estimate(9750, new[] { cfo, cfo - 100, cfo + 100, cfo, cfo, cfo, cfo, cfo + 99999 }, detune);
    Require(Math.Abs(estimate - 9750.054) < 0.000001, $"Beacon calibration correct sign and detune {detune} kHz, robust to outlier");
}
Require(LnbCalibration.Summarize(new[] {9750.054, 9750.055, 9750.053}) is { } calibrated && Math.Abs(calibrated.Mhz - 9750.054) < 0.000001, "Consistent beacon locks yield corrected LNB oscillator");
Require(LnbCalibration.Summarize(new[] {9750.054, 9750.055}) == null, "Calibration requires at least three locks");
Require(LnbCalibration.Summarize(new[] {9750.054, 9750.055, 9750.064}) == null, "Disagreement above 5 kHz rejects calibration");
Require(LnbCalibration.TryCarrier("datvrx carrier -54000.0", out var cfoHz) && cfoHz == -54000, "Tracked carrier telemetry parses signed Hz");
Require(!LnbCalibration.TryCarrier("datvrx carrier NaN", out _) && !LnbCalibration.TryCarrier("datvrx carrier Infinity", out _), "Non-finite carrier offsets rejected");



foreach (double legacy in new[] { 9750.0532, 9749.9456, 9750.0, 10000.1234 })
{
    var migrated = FrequencyConversion.Migrate(legacy);
    Require(Math.Abs(FrequencyConversion.EffectiveLo(migrated.NominalMhz, migrated.CorrectionKhz) - legacy) < 1e-9,
        $"Legacy oscillator {legacy} migrates without changing SDR tune frequency");
}
Require(Math.Abs(FrequencyConversion.Correction(9750.0532, 9750) - 53.2) < 1e-9, "Beacon estimate becomes combined correction in kHz");
Require(Math.Abs((10491.5 - FrequencyConversion.EffectiveLo(9750, 53.2)) - 741.4468) < 1e-9, "Positive frequency correction lowers SDR tune frequency");


var loss = new SignalLossPolicy();
loss.Reset(1000);
Require(loss.Update(2000, true) == 0, "Live transport clears signal-loss timer");
Require(loss.Update(3000, false) == 0, "Loss begins on first missing reception");
Require(!SignalLossPolicy.ShowNotice(loss.Update(5999, false)), "Brief fades preserve the picture");
Require(SignalLossPolicy.ShowNotice(loss.Update(6000, false)), "Three seconds of loss shows notice");
Require(!SignalLossPolicy.ResumeScan(loss.Update(32999, false), true), "Local waits for full 30 seconds");
Require(SignalLossPolicy.ResumeScan(loss.Update(33000, false), true), "Local resumes scanning at 30 seconds");
Require(!SignalLossPolicy.ResumeScan(loss.Update(99000, false), false), "BATC stays tuned through long loss");
Require(loss.Update(100000, true) == 0 && !loss.Waiting, "Returning transmission recovers automatically");
Require(loss.Update(101000, false) == 0, "A later loss gets a fresh countdown");



Require(UdpStreamAddress.Vlc(UdpStreamAddress.Default) == "udp://@239.255.42.42:1234", "Multicast yields copyable VLC group address");
Require(UdpStreamAddress.Vlc("udp://192.168.1.42:5000") == "udp://@:5000", "Unicast yields VLC listen address");
foreach (string invalid in new[] { "http://239.1.1.1:1234", "udp://0.0.0.0:1234", "udp://239.1.1.1:0", "udp://239.1.1.1:1234/path" })
{
    bool rejected = false; try { UdpStreamAddress.Parse(invalid); } catch (ArgumentException) { rejected = true; }
    Require(rejected, "Invalid UDP destination rejected: " + invalid);
}
byte[] tsPackets = new byte[188 * 14];
for (int i = 0; i < tsPackets.Length; i++) tsPackets[i] = (byte)(i % 251);
for (int i = 0; i < 14; i++) tsPackets[i * 188] = 0x47;
var packetizer = new TsUdpPacketizer();
var datagrams = new List<byte[]>();
byte[] fragmentedTs = new byte[] { 1, 2, 3 }.Concat(tsPackets).ToArray();
for (int i = 0; i < fragmentedTs.Length; i += 97)
    datagrams.AddRange(packetizer.Feed(fragmentedTs[i..Math.Min(i + 97, fragmentedTs.Length)]));
Require(datagrams.Count == 2 && datagrams.All(d => d.Length == 1316), "Fragmented TS produces Ethernet-sized seven-packet datagrams");
Require(datagrams.SelectMany(d => d).SequenceEqual(tsPackets), "UDP packetizer preserves stream bytes and recovers initial alignment");
using (var listener = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
{
    int port = ((System.Net.IPEndPoint)listener.Client.LocalEndPoint).Port;
    string udpError = null;
    await using var sender = new UdpTsSender($"udp://127.0.0.1:{port}", error => udpError = error);
    sender.Feed(tsPackets);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    var first = await listener.ReceiveAsync(timeout.Token);
    var second = await listener.ReceiveAsync(timeout.Token);
    Require(first.Buffer.Concat(second.Buffer).SequenceEqual(tsPackets) && udpError == null, "UDP socket forwards live TS bytes intact over loopback");
}
Console.WriteLine("All player checks passed.");
