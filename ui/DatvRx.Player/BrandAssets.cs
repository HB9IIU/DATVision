namespace DatvRx.Player;

internal static class BrandAssets
{
    public static Icon Icon { get; } = LoadIcon();

    private static Icon LoadIcon()
    {
        using var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream("DATVision.Branding.Icon")
            ?? throw new InvalidOperationException("DATVision icon resource is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public static Bitmap LoadWordmark()
    {
        using var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream("DATVision.Branding.Wordmark")
            ?? throw new InvalidOperationException("DATVision wordmark resource is missing.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
}
