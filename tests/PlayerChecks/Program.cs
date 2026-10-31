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
Console.WriteLine("All player checks passed.");
