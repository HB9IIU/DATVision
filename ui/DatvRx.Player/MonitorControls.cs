using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace DatvRx.Player;

internal static class MonitorColors
{
    public static readonly Color Background = Color.FromArgb(12, 22, 32), Panel = Color.FromArgb(16, 35, 49),
        Text = Color.FromArgb(221, 232, 241), Dim = Color.FromArgb(145, 167, 186),
        Green = Color.FromArgb(80, 215, 160), Amber = Color.FromArgb(255, 189, 69), Red = Color.FromArgb(255, 107, 107);
}

public sealed class MonitorSpectrum : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MonitorFrame Frame { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ReceiveBand Band { get; set; } = ReceiveBand.Presets[0];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TuneRequest Tuned { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TunedColor { get; set; } = MonitorColors.Green;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Paused { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Message { get; set; } = "Connect an RTL-SDR · press Start to scan";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SourceLabel { get; set; } = "LOCAL SDR";
    public event Action<double> FrequencyClicked;
    public MonitorSpectrum() { DoubleBuffered = true; Cursor = Cursors.Cross; SetStyle(ControlStyles.ResizeRedraw, true); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) FrequencyClicked?.Invoke(Band.StartMhz + e.X / (double)Math.Max(1, Width) * (Band.EndMhz - Band.StartMhz));
        base.OnMouseDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Color.FromArgb(5, 20, 29)); g.SmoothingMode = SmoothingMode.AntiAlias;
        float top = 14, bottom = Height - 24, plot = Math.Max(1, bottom - top);
        float X(double mhz) => (float)((mhz - Band.StartMhz) / (Band.EndMhz - Band.StartMhz) * Width);
        if (Tuned != null)
        {
            double half = Tuned.SymbolRateKs * 1.35 / 2000;
            var band = new RectangleF(X(Tuned.RfMhz - half), top, X(Tuned.RfMhz + half) - X(Tuned.RfMhz - half), plot);
            using var fill = new SolidBrush(Color.FromArgb(35, TunedColor)); using var edge = new Pen(TunedColor);
            g.FillRectangle(fill, band); g.DrawRectangle(edge, band.X, band.Y, band.Width, band.Height);
        }
        if (Frame is { Values.Length: > 1 } frame)
        {
            var sorted = frame.Values.Order().ToArray();
            float floor = frame.Local ? sorted[sorted.Length / 2] - 4 : 65535 * 0.12f;
            float ceiling = frame.Local ? Math.Max(floor + 12, sorted[^1] + 3) : 65535 * 0.58f;
            var points = new List<PointF>();
            for (int pixel = 0; pixel < Width; pixel++)
            {
                double mhz = Band.StartMhz + pixel / (double)Math.Max(1, Width - 1) * (Band.EndMhz - Band.StartMhz);
                double index = (mhz - frame.StartMhz) / (frame.EndMhz - frame.StartMhz) * (frame.Values.Length - 1);
                int left = Math.Clamp((int)index, 0, frame.Values.Length - 1);
                float sample = frame.Values[left];
                points.Add(new(pixel, bottom - Math.Clamp((sample - floor) / Math.Max(1, ceiling - floor), 0, 1) * plot));
            }
            if (points.Count > 1)
            {
                using var fill = new SolidBrush(Color.FromArgb(Paused ? 22 : 48, 255, 176, 0));
                using var pen = new Pen(Color.FromArgb(Paused ? 130 : 255, 255, 176, 0), 0.9f);
                g.FillPolygon(fill, points.Concat([new PointF(Width, bottom), new PointF(0, bottom)]).ToArray());
                g.DrawLines(pen, points.ToArray());
            }
        }
        using var text = new SolidBrush(MonitorColors.Dim);
        for (int i = 0; i < 8; i++)
        {
            double mhz = Band.StartMhz + i * (Band.EndMhz - Band.StartMhz) / 7;
            string label = mhz.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            float width = g.MeasureString(label, Font).Width;
            g.DrawString(label, Font, text, Math.Clamp(X(mhz) - width / 2, 0, Math.Max(0, Width - width)), Height - 20);
        }
        string source = Band.Name + " · " + SourceLabel;
        g.DrawString(source, Font, text, Math.Max(4, Width - g.MeasureString(source, Font).Width - 5), 0);
        if (!string.IsNullOrWhiteSpace(Message))
        {
            var size = g.MeasureString(Message, Font);
            using var box = new SolidBrush(Color.FromArgb(220, MonitorColors.Background));
            g.FillRectangle(box, 6, 3, Math.Min(Width - 12, size.Width + 10), size.Height + 5);
            g.DrawString(Message, Font, text, 10, 5);
        }
    }
}

public sealed class TuningEye : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ReceptionReport Report { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Running { get; set; }
    public bool Constellation { get; private set; }
    private PointF[] _cells = [];
    public TuningEye() { DoubleBuffered = true; Cursor = Cursors.Hand; SetStyle(ControlStyles.ResizeRedraw, true); }
    public void SetCells(string line)
    {
        string hex = line[13..].Trim();
        if (hex.Length % 4 != 0 || hex.Length > 8192) return;
        try
        {
            byte[] data = Convert.FromHexString(hex);
            _cells = Enumerable.Range(0, data.Length / 2).Select(i => new PointF((data[i * 2] - 128) / 64f, (data[i * 2 + 1] - 128) / 64f)).ToArray();
            Invalidate();
        }
        catch (FormatException) { }
    }
    protected override void OnClick(EventArgs e) { Constellation = !Constellation; Invalidate(); base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Max(4, Math.Min(Width, Height) - 6), cx = Width / 2f, cy = Height / 2f, radius = size / 2;
        var bounds = new RectangleF(cx - radius, cy - radius, size, size);
        using var rim = new LinearGradientBrush(bounds, Color.FromArgb(105, 116, 111), Color.FromArgb(15, 21, 18), 55f);
        g.FillEllipse(rim, bounds); bounds.Inflate(-4, -4);
        using var glassPath = new GraphicsPath(); glassPath.AddEllipse(bounds);
        using var glow = new PathGradientBrush(glassPath) { CenterColor = Running ? Color.FromArgb(99, 255, 139) : Color.FromArgb(20, 48, 30), SurroundColors = [Color.FromArgb(3, 31, 17)] };
        g.FillEllipse(glow, bounds);
        if (Constellation)
        {
            using var dark = new SolidBrush(Color.FromArgb(230, 2, 12, 8)); g.FillEllipse(dark, bounds);
            using var axis = new Pen(Color.FromArgb(42, 86, 62));
            g.DrawLine(axis, cx - radius + 7, cy, cx + radius - 7, cy); g.DrawLine(axis, cx, cy - radius + 7, cx, cy + radius - 7);
            using var dot = new SolidBrush(MonitorColors.Green);
            if (Running && Report is { Locked: true })
                foreach (var p in _cells) g.FillEllipse(dot, cx + p.X * radius * 0.55f - 1, cy - p.Y * radius * 0.55f - 1, 2, 2);
        }
        else
        {
            double level = Report is { Locked: true } report ? Math.Clamp((report.Margin ?? report.EsN0 / 3) / 5, 0, 1) : 0;
            float half = (float)(3 + (1 - level) * 46);
            using var shadow = new SolidBrush(Color.FromArgb(218, 1, 24, 10));
            g.FillPie(shadow, Rectangle.Round(bounds), 90 - half, half * 2); g.FillPie(shadow, Rectangle.Round(bounds), 270 - half, half * 2);
            float cap = radius * 0.32f;
            using var capBrush = new LinearGradientBrush(new PointF(cx - cap, cy - cap), new PointF(cx + cap, cy + cap), Color.FromArgb(54, 66, 57), Color.Black);
            g.FillEllipse(capBrush, cx - cap, cy - cap, cap * 2, cap * 2);
            using var shine = new SolidBrush(Color.FromArgb(65, Color.White));
            g.FillEllipse(shine, cx - radius * 0.5f, cy - radius * 0.7f, radius * 0.22f, radius * 0.11f);
        }
    }
}

public sealed class MarginGauge : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double? DbMargin { get; set; }
    public MarginGauge() { DoubleBuffered = true; Height = 9; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(27, 49, 64));
        if (DbMargin is not { } margin) return;
        using var fill = new SolidBrush(margin < 0 ? MonitorColors.Red : margin < 2 ? MonitorColors.Amber : MonitorColors.Green);
        e.Graphics.FillRectangle(fill, 0, 0, (float)Math.Clamp((margin + 2) / 10, 0.03, 1) * Width, Height);
    }
}

