using System.Globalization;

namespace DatvRx.Player;

// Same beacon measurement and acceptance criteria as the Pi calibration.py.
public static class LnbCalibration
{
    public static readonly int[] StepsKhz = [-100, -50, 0, 50, 100];
    public static double Estimate(double assumedMhz, IEnumerable<double> offsetsHz, int detuneKhz)
    {
        var samples = offsetsHz.Order().ToArray();
        if (samples.Length == 0 || samples.Any(v => !double.IsFinite(v))) throw new ArgumentException("Invalid carrier samples");
        double median = (samples[(samples.Length - 1) / 2] + samples[samples.Length / 2]) / 2;
        return assumedMhz - median / 1e6 - detuneKhz / 1000.0;
    }
    public static (double Mhz, double SpreadKhz)? Summarize(IEnumerable<double> estimates)
    {
        var values = estimates.Where(double.IsFinite).ToArray();
        if (values.Length < 3) return null;
        double spread = (values.Max() - values.Min()) * 1000;
        double mhz = Math.Round(values.Average(), 4);
        return spread <= 5 && mhz >= 9000 && mhz <= 11000 ? (mhz, spread) : null;
    }
    public static bool TryCarrier(string line, out double hz)
    {
        hz = 0;
        return line.StartsWith("datvrx carrier ") && double.TryParse(line[15..], NumberStyles.Float,
            CultureInfo.InvariantCulture, out hz) && double.IsFinite(hz);
    }
}
