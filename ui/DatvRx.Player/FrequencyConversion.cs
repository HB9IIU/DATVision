namespace DatvRx.Player;

public static class FrequencyConversion
{
    // Positive correction lowers the SDR tune frequency.
    public static double EffectiveLo(double nominalMhz, double correctionKhz) => nominalMhz + correctionKhz / 1000;
    public static (double NominalMhz, double CorrectionKhz) Migrate(double legacyLoMhz)
    {
        double nominal = Math.Round(legacyLoMhz);
        return (nominal, Math.Round((legacyLoMhz - nominal) * 1000, 6));
    }
    public static double Correction(double measuredEffectiveLoMhz, double nominalLoMhz) =>
        Math.Round((measuredEffectiveLoMhz - nominalLoMhz) * 1000, 3);
}
