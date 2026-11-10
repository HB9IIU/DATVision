using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DatvRx.Player;

internal static class MonitorColors
{
    public static readonly Color Background = Color.FromArgb(12, 22, 32), Panel = Color.FromArgb(16, 35, 49),
        Text = Color.FromArgb(221, 232, 241), Dim = Color.FromArgb(145, 167, 186),
        Green = Color.FromArgb(80, 215, 160), Amber = Color.FromArgb(255, 189, 69), Red = Color.FromArgb(255, 107, 107);
}

public enum CarrierStage { Selected, WaitingVideo, Playing, Lost }

public sealed class MonitorSpectrum : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MonitorFrame Frame
    {
        get => _frame;
        set
        {
            bool reset = _frame == null || _frame.Local != value?.Local || _frame.StartMhz != value?.StartMhz || _frame.EndMhz != value?.EndMhz;
            _frame = value;
            if (value is not { Local: true, Values.Length: > 1 }) return;
            var sorted = value.Values.Order().ToArray();
            // Local samples are dB; use a low percentile for the noise baseline.
            _noiseFloor = sorted[(int)((sorted.Length - 1) * 0.20)];
            float peak = Math.Max(sorted[(int)((sorted.Length - 1) * 0.995)], _noiseFloor + 6);
            // Match the Pi: rise immediately, fall slowly, ignore isolated spikes.
            _viewTop = reset || peak > _viewTop ? peak : 0.9f * _viewTop + 0.1f * peak;
        }
    }
    private MonitorFrame _frame;
    private float _noiseFloor, _viewTop;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ReceiveBand Band { get; set; } = ReceiveBand.Presets[0];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TuneRequest Tuned { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CarrierStage Stage { get; set; } = CarrierStage.Selected;
    private Color TunedColor => Stage switch
    {
        CarrierStage.Selected => Color.FromArgb(87, 195, 236),
        CarrierStage.Lost => MonitorColors.Amber,
        CarrierStage.WaitingVideo => Color.FromArgb(183, 148, 246),
        _ => MonitorColors.Green
    };
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Paused { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Message { get; set; } = "";
    // The local sweep is running (set by the window): shown as a blinking red "Continuous Scanning".
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Sweeping
    {
        get => _sweeping;
        set
        {
            if (_sweeping == value) return;
            _sweeping = value;
            if (value) _scanAnimation.Start(); else _scanAnimation.Stop();   // smooth movement while the first scan has no data yet
            Invalidate();
        }
    }
    private bool _sweeping;
    private readonly System.Windows.Forms.Timer _scanAnimation = new() { Interval = 50 };
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SourceLabel { get; set; } = "LOCAL";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Receiver { get; set; } = "RTL-SDR";   // which radio the data comes from
    public event Action<double> FrequencyClicked;
    public MonitorSpectrum()
    {
        DoubleBuffered = true; Cursor = Cursors.Cross; SetStyle(ControlStyles.ResizeRedraw, true);
        _scanAnimation.Tick += (_, _) => { if (Visible && Frame == null) Invalidate(); };
    }
    protected override void Dispose(bool disposing) { if (disposing) _scanAnimation.Dispose(); base.Dispose(disposing); }
    // Pan / zoom buttons, translucent in the top-left corner, on the bands that have them (ReceiveBand.Navigable).
    public event Action<string> NavigationClicked;   // "out", "in", "full"
    private List<(string Key, string Text, Rectangle Area)> NavigationButtons()
    {
        var list = new List<(string, string, Rectangle)>();
        if (!Band.Navigable) return list;
        float scale = DeviceDpi / 96f;
        int height = (int)Math.Round(22 * scale), width = (int)Math.Round(30 * scale), gap = (int)Math.Round(4 * scale), x = gap, y = gap;
        using var face = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold);
        string full = "FULL " + Band.Name + " Band";
        foreach (var (key, text) in new[] { ("out", "–"), ("in", "+"), ("full", full) })
        {
            int w = key == "full" ? TextRenderer.MeasureText(text, face).Width + (int)Math.Round(14 * scale) : width;
            list.Add((key, text, new Rectangle(x, y, w, height)));
            x += w + gap;
        }
        return list;
    }
    // Hover readout (bands with the pan / zoom controls): a thin line under the mouse with its frequency, no button needed.
    private int _hoverX = -1;
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool overButton = NavigationButtons().Any(b => b.Area.Contains(e.Location));
        Cursor = overButton ? Cursors.Hand : Cursors.Cross;
        int hover = overButton ? -1 : e.X;   // the hover frequency is shown on every band
        if (hover != _hoverX) { _hoverX = hover; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverX >= 0) { _hoverX = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            foreach (var button in NavigationButtons())
                if (button.Area.Contains(e.Location)) { NavigationClicked?.Invoke(button.Key); base.OnMouseDown(e); return; }
            FrequencyClicked?.Invoke(Band.StartMhz + e.X / (double)Math.Max(1, Width) * (Band.EndMhz - Band.StartMhz));
        }
        base.OnMouseDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Color.FromArgb(5, 20, 29)); g.SmoothingMode = SmoothingMode.AntiAlias;
        float top = 14, bottom = Height - 24, plot = Math.Max(1, bottom - top);
        float X(double mhz) => (float)((mhz - Band.StartMhz) / (Band.EndMhz - Band.StartMhz) * Width);
        if (Frame is { Values.Length: > 1 } frame)
        {
            // Pi local display: noise at 15%, robust peak at 90% of plot height.
            float floor = frame.Local ? _noiseFloor - (_viewTop - _noiseFloor) * 0.20f : 65535 * 0.12f;
            float ceiling = frame.Local ? floor + (_viewTop - _noiseFloor) / 0.75f : 65535 * 0.58f;
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
                var area = points.Concat([new PointF(Width, bottom), new PointF(0, bottom)]).ToArray();
                g.FillPolygon(fill, area);
                g.DrawLines(pen, points.ToArray());
                if (Tuned != null)
                {
                    double half = Tuned.SymbolRateKs * 1.35 / 2000;
                    float left = Math.Clamp(X(Tuned.RfMhz - half), 0, Width);
                    float right = Math.Clamp(X(Tuned.RfMhz + half), 0, Width);
                    if (right > left)
                    {
                        // Paint only beneath the carrier's live contour; no selection box.
                        var state = g.Save();
                        g.SetClip(new RectangleF(left, top, right - left, plot), CombineMode.Intersect);
                        using var selectedFill = new SolidBrush(Color.FromArgb(Paused ? 65 : 100, TunedColor));
                        using var selectedLine = new Pen(TunedColor, 1.5f);
                        g.FillPolygon(selectedFill, area);
                        g.DrawLines(selectedLine, points.ToArray());
                        g.Restore(state);
                    }
                }
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
        if (Tuned != null && string.IsNullOrWhiteSpace(Message))
        {
            using var stageText = new SolidBrush(TunedColor);
            string stageLabel = Stage == CarrierStage.WaitingVideo ? "WAITING FOR VIDEO" : Stage.ToString().ToUpperInvariant();
            g.DrawString(stageLabel, Font, stageText, Math.Max(0, (Width - g.MeasureString(stageLabel, Font).Width) / 2), 0);
        }
        string source = Band.Name + " · " + SourceLabel + (SourceLabel == "REPLAY" ? "" : " · " + Receiver);
        float sourceLeft = Math.Max(4, Width - g.MeasureString(source, Font).Width - 5);
        g.DrawString(source, Font, text, sourceLeft, 0);
        if (Sweeping)
        {
            // Red, blinking, top centre: the scan repeats by itself until a station is tuned. Until the first picture of the band
            // is there (the scanner reports a whole pass at a time, which takes a while on a wide band) a bar moves across the
            // plot, so that it is clear that something is going on.
            bool lit = Environment.TickCount64 % 1000 < 600;
            using var red = new SolidBrush(lit ? Color.FromArgb(235, MonitorColors.Red) : Color.FromArgb(90, MonitorColors.Red));
            using var title = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Regular);
            using var topCentre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
            g.DrawString("Continuous Scanning", title, red, new RectangleF(0, 1, Width, title.Height + 2), topCentre);
            if (Frame == null)
            {
                float x = Environment.TickCount64 % 2500 / 2500f * Width;
                using var trail = new LinearGradientBrush(new RectangleF(x - 80, top, 80, plot), Color.FromArgb(0, 255, 189, 69), Color.FromArgb(110, 255, 189, 69), 0f);
                g.FillRectangle(trail, x - 80, top, 80, plot);
                using var bar = new Pen(Color.FromArgb(230, 255, 189, 69), 2f);
                g.DrawLine(bar, x, top, x, bottom);
                using var dim = new SolidBrush(MonitorColors.Dim);
                g.DrawString("first scan in progress…", Font, dim, new RectangleF(0, title.Height + 4, Width, Font.Height + 2), topCentre);
            }
        }
        if (!string.IsNullOrWhiteSpace(Message) && !Sweeping)   // "Continuous Scanning" takes the place of the message
        {
            var size = g.MeasureString(Message, Font);
            using var box = new SolidBrush(Color.FromArgb(220, MonitorColors.Background));
            float boxWidth = Math.Min(Math.Max(1, Width - 12), size.Width + 10);
            float left = (Width - boxWidth) / 2;
            g.FillRectangle(box, left, 3, boxWidth, size.Height + 5);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(Message, Font, text, new RectangleF(left + 5, 5, Math.Max(1, boxWidth - 10), size.Height + 2), centered);
        }
        if (_hoverX >= 0)
        {
            double mhz = Band.StartMhz + _hoverX / (double)Math.Max(1, Width) * (Band.EndMhz - Band.StartMhz);
            string label = mhz.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " MHz";
            using var line = new Pen(Color.FromArgb(150, 255, 255, 255), 1f);
            g.DrawLine(line, _hoverX, 0, _hoverX, Height - 22);
            using var bold = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold);
            var size = g.MeasureString(label, bold);
            float x = _hoverX + 8 + size.Width + 6 > Width ? _hoverX - 8 - size.Width - 6 : _hoverX + 8;   // flip to the left near the right edge
            float y = (int)Math.Round(30 * DeviceDpi / 96f);   // below the buttons
            using var back = new SolidBrush(Color.FromArgb(220, 5, 20, 29));
            g.FillRectangle(back, x, y, size.Width + 6, size.Height + 2);
            g.DrawString(label, bold, Brushes.White, x + 3, y + 1);
        }
        foreach (var button in NavigationButtons())
        {
            using var back = new SolidBrush(Color.FromArgb(165, 27, 49, 64));
            using var rim = new Pen(Color.FromArgb(180, 83, 118, 140));
            using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var face = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold);
            g.FillRectangle(back, button.Area); g.DrawRectangle(rim, button.Area);
            g.DrawString(button.Text, face, Brushes.White, button.Area, centred);
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
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 33 };
    private double _value, _light;
    private long _lastTick = Environment.TickCount64;
    private Bitmap _tube;
    public TuningEye()
    {
        DoubleBuffered = true; Cursor = Cursors.Hand; SetStyle(ControlStyles.ResizeRedraw, true);
        _animation.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            double dt = Math.Min(0.1, (now - _lastTick) / 1000.0); _lastTick = now;
            double target = Running && Report is { Locked: true } r
                ? 0.12 + 0.88 * Math.Clamp((r.Margin ?? r.EsN0 / 3) / 5, 0, 1) : 0;
            _value += (target - _value) * (1 - Math.Exp(-dt / (target < _value ? 0.10 : 0.22)));
            _light += ((Running ? 1 : 0) - _light) * (1 - Math.Exp(-dt / 0.35));
            if (Visible) Invalidate();
        };
        _animation.Start();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _animation.Dispose(); _tube?.Dispose(); }
        base.Dispose(disposing);
    }
    // Same target proportions and phosphor colours as the Pi EM34/6E5 eye.
    private static Bitmap MakePhosphor()
    {
        var image = new Bitmap(320, 320);
        var rng = new Random(6);
        double[] ribs = Enumerable.Range(0, 240).Select(_ => rng.NextDouble() * 0.16 - 0.08).ToArray();
        (double Position, Color Color)[] stops = [(0, Color.FromArgb(214,255,176)),
            (0.3875, Color.FromArgb(194,255,158)), (0.5275, Color.FromArgb(124,255,152)),
            (0.72, Color.FromArgb(46,232,116)), (0.9, Color.FromArgb(13,158,72)), (1, Color.FromArgb(4,61,28))];
        for (int y = 0; y < 320; y++) for (int x = 0; x < 320; x++)
        {
            double dx = (x - 159.5) / 160, dy = (y - 159.5) / 160, radius = Math.Sqrt(dx*dx + dy*dy);
            if (radius > 1) continue;
            int j = 1; while (j < stops.Length - 1 && radius > stops[j].Position) j++;
            var a = stops[j-1]; var b = stops[j]; double t = (radius-a.Position)/(b.Position-a.Position);
            double angle = (Math.Atan2(dy,dx) + Math.PI) / (2*Math.PI);
            double texture = 1 + ribs[Math.Min(239,(int)(angle*240))];
            int Blend(int u,int v) => Math.Clamp((int)((u+(v-u)*t)*texture),0,255);
            image.SetPixel(x,y,Color.FromArgb(Blend(a.Color.R,b.Color.R),Blend(a.Color.G,b.Color.G),Blend(a.Color.B,b.Color.B)));
        }
        return image;
    }
    private static void Radial(Graphics g, float x, float y, float radius, Color centre, Color edge)
    {
        using var path = new GraphicsPath(); path.AddEllipse(x-radius,y-radius,radius*2,radius*2);
        using var brush = new PathGradientBrush(path) { CenterColor = centre, SurroundColors = [edge] };
        g.FillPath(brush,path);
    }
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
        var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Max(4, Math.Min(Width, Height) - 6), cx = Width / 2f, cy = Height / 2f;
        float outer = size * 0.40f, cap = size * 0.155f;
        var bezel = new RectangleF(cx-size*.495f,cy-size*.495f,size*.99f,size*.99f);
        using var metal = new LinearGradientBrush(bezel,Color.FromArgb(92,95,93),Color.FromArgb(24,27,25),125);
        g.FillEllipse(metal,bezel);
        Radial(g,cx,cy,size*.462f,Color.FromArgb(12,21,16),Color.FromArgb(3,5,4));
        Radial(g,cx,cy,size*.455f,Color.FromArgb(5,10,7),Color.FromArgb(58,56,48));
        Radial(g,cx,cy,size*.435f,Color.FromArgb(12,21,16),Color.FromArgb(5,10,7));
        if (Constellation)
        {
            using var dark = new SolidBrush(Color.FromArgb(2,12,8));
            g.FillEllipse(dark,cx-outer,cy-outer,outer*2,outer*2);
            using var axis = new Pen(Color.FromArgb(42,86,62));
            g.DrawLine(axis,cx-outer,cy,cx+outer,cy); g.DrawLine(axis,cx,cy-outer,cx,cy+outer);
            using var dot = new SolidBrush(MonitorColors.Green);
            if (Running && Report is { Locked: true })
                foreach (var point in _cells) g.FillEllipse(dot,cx+point.X*outer*.65f-1,cy-point.Y*outer*.65f-1,2,2);
            return;
        }
        if (_light > .01)
        {
            Radial(g,cx,cy,outer*1.16f,Color.FromArgb((int)(65*_light),40,255,120),Color.FromArgb(0,40,255,120));
            _tube ??= MakePhosphor();
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)_light }; attributes.SetColorMatrix(matrix);
            g.DrawImage(_tube,Rectangle.Round(new RectangleF(cx-outer,cy-outer,outer*2,outer*2)),0,0,_tube.Width,_tube.Height,GraphicsUnit.Pixel,attributes);
            float half = (float)(2+(1-_value)*48);
            // One-degree slices reproduce the Pi's soft conical shadow edges.
            for (int degree=0;degree<360;degree++)
            {
                double distance = Math.Min(Math.Abs(degree-90),Math.Abs(degree-270));
                Color color;
                if (distance < half-2.5) color=Color.FromArgb(226,3,24,11);
                else if (distance < half+2.5)
                {
                    double t=(distance-half+2.5)/5;
                    color=Color.FromArgb((int)(226*(1-t*t*(3-2*t))),3,24,11);
                }
                else if (distance < half+6.5) color=Color.FromArgb((int)(60*(1-(distance-half-2.5)/4)),215,255,205);
                else continue;
                using var shadow=new SolidBrush(color);
                g.FillPie(shadow,cx-outer,cy-outer,outer*2,outer*2,degree,1.2f);
            }
        }
        Radial(g,cx,cy,cap,Color.FromArgb(44,49,46),Color.FromArgb(2,3,2));
        Radial(g,cx-cap*.25f,cy-cap*.3f,cap*.65f,Color.FromArgb(35,190,200,195),Color.FromArgb(0,190,200,195));
        if (_light>.01)
        {
            using var edge=new Pen(Color.FromArgb((int)(110*_light),110,255,150),1.2f);
            g.DrawEllipse(edge,cx-cap+.6f,cy-cap+.6f,cap*2-1.2f,cap*2-1.2f);
        }
        Radial(g,cx-size*.15f,cy-size*.19f,size*.24f,Color.FromArgb(62,255,255,255),Color.FromArgb(0,255,255,255));
        var saved=g.Save(); g.TranslateTransform(cx-size*.2f,cy-size*.25f); g.RotateTransform(-38);
        g.ScaleTransform(1,.43f);
        Radial(g,0,0,size*.065f,Color.FromArgb(190,255,255,255),Color.FromArgb(0,255,255,255)); g.Restore(saved);
        using var reflection=new Pen(Color.FromArgb(34,255,255,255),size*.012f) { StartCap=LineCap.Round,EndCap=LineCap.Round };
        g.DrawArc(reflection,cx-size*.42f,cy-size*.42f,size*.84f,size*.84f,15,55);
    }
}

// CPU load as a small two-colour bar sampled once a second: the whole PC in blue-grey, DATVision's own share (the player plus the
// receiver, SDR and video processes it starts) in green on top of it. The text reads "DATVision · PC".
public sealed class CpuMeter : Control
{
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    private static readonly string[] ProcessNames = ["DatvRx.Player", "datvrx", "rtl_iq", "rtl_sweep", "pluto_iq", "pluto_sweep", "ffplay"];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly ToolTip _tip = new();
    private readonly Dictionary<int, TimeSpan> _cpu = [];
    private DateTime _last = DateTime.UtcNow;
    private ulong _idle, _total;
    private double _load = -1, _pc = -1;
    public CpuMeter()
    {
        DoubleBuffered = true; Font = new Font("Segoe UI", 8); ForeColor = MonitorColors.Dim; Margin = Padding.Empty;
        Sample();
        _timer.Tick += (_, _) => { Sample(); _tip.SetToolTip(this, _pc < 0 ? "CPU used by DATVision" : $"CPU: cyan = DATVision ({_load * 100:0}%), amber = whole PC ({_pc * 100:0}%)"); Invalidate(); };
        _timer.Start();
    }
    private void Sample()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong total = kernel.Value + user.Value, i = idle.Value;   // kernel time includes idle
            if (_total != 0 && total > _total) _pc = Math.Clamp(1.0 - (double)(i - _idle) / (total - _total), 0, 1);
            _idle = i; _total = total;
        }
        var now = DateTime.UtcNow; double wall = (now - _last).TotalSeconds; _last = now;
        var seen = new Dictionary<int, TimeSpan>(); double used = 0; bool known = false;
        foreach (var name in ProcessNames)
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        var time = process.TotalProcessorTime; seen[process.Id] = time;
                        if (_cpu.TryGetValue(process.Id, out var before)) { used += (time - before).TotalSeconds; known = true; }
                    }
                    catch (Exception) { }   // a process that just ended or cannot be read
                }
        _cpu.Clear(); foreach (var pair in seen) _cpu[pair.Key] = pair.Value;
        if (known && wall > 0) _load = Math.Clamp(used / (wall * Environment.ProcessorCount), 0, 1);
    }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _tip.Dispose(); } base.Dispose(disposing); }
    private static readonly Color AppColor = Color.FromArgb(56, 189, 216), TotalColor = Color.FromArgb(255, 189, 69);
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        // Top line "CPU      App 6%  Total 43%", underneath one bar: the whole PC in amber, DATVision's own share in cyan at the left.
        double pc = Math.Max(_pc, _load), own = Math.Min(Math.Max(_load, 0), pc);
        int barHeight = Math.Max(5, DeviceDpi * 6 / 96), textHeight = Height - barHeight - 3;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        using var bold = new Font(Font, FontStyle.Bold);
        int right = Width;
        void Part(string text, Font font, Color color)   // drawn leftwards from the right edge
        {
            int width = TextRenderer.MeasureText(g, text, font, Size.Empty, flags).Width;
            right -= width; TextRenderer.DrawText(g, text, font, new Rectangle(right, 0, width, textHeight), color, flags | TextFormatFlags.VerticalCenter);
        }
        bool known = _pc >= 0 || _load >= 0;
        Part(known ? $"{pc * 100:0}%" : "—", bold, pc < 0.85 ? TotalColor : MonitorColors.Red); Part("Total ", Font, Color.FromArgb(200, 150, 60));
        right -= 8;
        Part(known ? $"{own * 100:0}%" : "—", bold, AppColor); Part("App ", Font, Color.FromArgb(60, 140, 160));
        TextRenderer.DrawText(g, "CPU", Font, new Rectangle(0, 0, Math.Max(0, right), textHeight), ForeColor, flags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        int y = Height - barHeight;
        using var track = new SolidBrush(Color.FromArgb(27, 49, 64));
        g.FillRectangle(track, 0, y, Width, barHeight);
        if (pc > 0) { using var total = new SolidBrush(pc < 0.85 ? TotalColor : MonitorColors.Red); g.FillRectangle(total, 0, y, (float)pc * Width, barHeight); }
        if (own > 0) { using var mine = new SolidBrush(AppColor); g.FillRectangle(mine, 0, y, Math.Max(2f, (float)own * Width), barHeight); }
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


// A restrained raised face that stays within the monitor's dark palette.
internal sealed class MonitorButton : Button
{
    private bool _hovered, _pressed;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RecordingActive { get; set; }
    // The chosen one of a pair (LOCAL / BATC, RTL-SDR / PLUTO, OFF / ON): drawn green.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get; set { field = value; Invalidate(); } }
    // Optional smaller lines under the main text (a band tile: the name big, its frequencies below).
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Detail { get; set { field = value; Invalidate(); } }

    public MonitorButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) _pressed = true; Invalidate(); base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 4) return;
        var g = e.Graphics;
        bool down = _pressed && Enabled;
        var face = new Rectangle(1, 1, Width - 3, Height - 3);
        Color top = down ? Color.FromArgb(19, 37, 50) : Selected ? Color.FromArgb(38, 112, 84) : _hovered && Enabled ? Color.FromArgb(55, 88, 109) : Color.FromArgb(40, 66, 84);
        Color bottom = down ? Color.FromArgb(30, 51, 67) : Selected ? Color.FromArgb(24, 74, 56) : Color.FromArgb(20, 39, 53);
        g.Clear(Parent?.BackColor ?? MonitorColors.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = Math.Min(8f * DeviceDpi / 96f, Math.Min(face.Width, face.Height) / 2f);
        float cornerDiameter = radius * 2;
        using var outline = new GraphicsPath();
        outline.AddArc(face.Left, face.Top, cornerDiameter, cornerDiameter, 180, 90);
        outline.AddArc(face.Right - cornerDiameter, face.Top, cornerDiameter, cornerDiameter, 270, 90);
        outline.AddArc(face.Right - cornerDiameter, face.Bottom - cornerDiameter, cornerDiameter, cornerDiameter, 0, 90);
        outline.AddArc(face.Left, face.Bottom - cornerDiameter, cornerDiameter, cornerDiameter, 90, 90);
        outline.CloseFigure();
        using var fill = new LinearGradientBrush(face, top, bottom, 90f);
        g.FillPath(fill, outline);
        using var rim = new Pen(down ? Color.FromArgb(35, 60, 77) : Selected ? Color.FromArgb(80, 215, 160) : Color.FromArgb(83, 118, 140));
        g.DrawPath(rim, outline);
        var label = ClientRectangle; if (down) label.Offset(1, 1);
        var textColor = Enabled ? ForeColor : MonitorColors.Dim;
        if (Text.StartsWith("● "))
        {
            string caption = Text[2..];
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int width = TextRenderer.MeasureText(g, caption, Font, Size.Empty, flags).Width;
            int diameter = (int)Math.Round(10 * DeviceDpi / 96f), gap = (int)Math.Round(8 * DeviceDpi / 96f);
            int left = label.Left + (label.Width - width - diameter - gap) / 2;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool lit = RecordingActive && Environment.TickCount64 % 1000 < 500;
            using var dot = new SolidBrush(RecordingActive ? (lit ? MonitorColors.Red : Color.FromArgb(100, 48, 52)) : MonitorColors.Dim);
            g.FillEllipse(dot, left, label.Top + (label.Height - diameter) / 2, diameter, diameter);
            TextRenderer.DrawText(g, caption, Font, new Rectangle(left + diameter + gap, label.Top, width, label.Height), textColor, flags);
        }
        else if (Text.StartsWith("🔊") || Text.StartsWith("🔇"))
        {
            // Give the speaker the same visual weight as the settings gear,
            // while keeping the volume/mute caption at its normal readable size.
            using var iconFont = new Font("Segoe UI Emoji", 18);
            string icon = Text[..2], caption = Text[2..].Trim();
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int iconWidth = TextRenderer.MeasureText(g, icon, iconFont, Size.Empty, flags).Width;
            int captionWidth = TextRenderer.MeasureText(g, caption, Font, Size.Empty, flags).Width;
            int gap = (int)Math.Round(5 * DeviceDpi / 96f);
            int left = label.Left + (label.Width - iconWidth - gap - captionWidth) / 2;
            TextRenderer.DrawText(g, icon, iconFont, new Rectangle(left, label.Top, iconWidth, label.Height), textColor, flags);
            TextRenderer.DrawText(g, caption, Font, new Rectangle(left + iconWidth + gap, label.Top, captionWidth, label.Height), textColor, flags);
        }
        else if (!string.IsNullOrEmpty(Detail))
        {
            // The main text large and bold, the detail lines under it in the button's own (smaller) font, the block centred.
            using var headFont = new Font(Font.FontFamily, Font.Size * 1.7f, FontStyle.Bold);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top;
            int headHeight = TextRenderer.MeasureText(g, Text, headFont, Size.Empty, flags).Height;
            int detailHeight = TextRenderer.MeasureText(g, Detail, Font, new Size(label.Width, 0), flags).Height;
            int gap = (int)Math.Round(2 * DeviceDpi / 96f);
            int y = label.Top + (label.Height - headHeight - gap - detailHeight) / 2;
            TextRenderer.DrawText(g, Text, headFont, new Rectangle(label.Left, y, label.Width, headHeight), textColor, flags);
            TextRenderer.DrawText(g, Detail, Font, new Rectangle(label.Left, y + headHeight + gap, label.Width, detailHeight),
                Enabled ? Color.FromArgb(190, 208, 222) : MonitorColors.Dim, flags);
        }
        else
            TextRenderer.DrawText(g, Text, Font, label, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -5, -5));
    }
}
