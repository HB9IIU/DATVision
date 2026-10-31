using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatvRx.Player;

public sealed class PlayerWindow : Form
{
    private static readonly Color Background = Color.FromArgb(12, 22, 32);
    private static readonly Color Foreground = Color.FromArgb(221, 232, 241);
    private static readonly Color Muted = Color.FromArgb(145, 167, 186);
    private static readonly Color Green = Color.FromArgb(80, 215, 160);
    private readonly SpectrumView _spectrum = new() { Dock = DockStyle.Fill };
    private readonly SpectrumFeed _feed = new();
    private readonly Panel _video = new() { BackColor = Color.Black, Dock = DockStyle.Fill };
    private readonly Label _placeholder = new() { Text = "Waiting for beacon video…", ForeColor = Muted, BackColor = Color.Black,
        TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill };
    private readonly Label _state = new() { Text = "READY", ForeColor = Muted, AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Label _local = new() { Text = "Local receiver · not tuned", ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ToolTip _tips = new();
    private readonly ToolStripMenuItem _stop = new("Stop reception");
    private readonly ToolStripMenuItem _volumeMenu = new("Volume");
    private decimal _lnbMhz = 9750;
    private int _volumePercent = 30;
    private readonly IReceiverBackend _receiver;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly string _settingsPath;
    private IntPtr _embedded;
    private Process _embeddedProcess;
    private bool _busy, _closing, _closed, _spectrumOnline;
    private DateTime _lastSpectrum, _lastTelemetry;
    private TuneRequest _current;
    private readonly Control _legend = new FrequencyScale { Dock = DockStyle.Fill, ForeColor = Muted };

    public PlayerWindow(string root)
    {
        _settingsPath = Path.Combine(root, "player-settings.json");
        _receiver = new LocalReceiver(root);
        Text = "HB9IIU · RTL DATV Player";
        Font = new Font("Segoe UI", 9);
        BackColor = Background; ForeColor = Foreground;
        ClientSize = new Size(820, 590); MinimumSize = new Size(600, 430);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        foreach (var row in new[] { 32f, 0f, 28f, 110f, 20f })
            layout.RowStyles.Add(new RowStyle(row == 0 ? SizeType.Percent : SizeType.Absolute, row == 0 ? 100 : row));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(new Label { Text = "HB9IIU  /  RTL DATV", Font = new Font("Segoe UI Semibold", 12), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        heading.Controls.Add(_state, 1, 0);
        layout.Controls.Add(heading, 0, 0);
        _video.Controls.Add(_placeholder);
        layout.Controls.Add(_video, 0, 1);
        layout.Controls.Add(_local, 0, 2);
        layout.Controls.Add(_spectrum, 0, 3);
        layout.Controls.Add(_legend, 0, 4);
        Controls.Add(layout);
        var menu = new ContextMenuStrip();
        var top = new ToolStripMenuItem("Always on top") { Checked = true, CheckOnClick = true };
        top.CheckedChanged += (_, _) => TopMost = top.Checked;
        menu.Items.Add(top);
        foreach (int volume in new[] { 0, 25, 50, 75, 100 })
        {
            var item = new ToolStripMenuItem(volume == 0 ? "Mute" : $"{volume}%") { Tag = volume };
            item.Click += async (_, _) =>
            {
                _volumePercent = volume;
                UpdateVolumeMenu(); SaveSettings();
                if (_current != null) await TuneAsync(_current);
            };
            _volumeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_volumeMenu);
        menu.Items.Add("LNB configuration…", null, async (_, _) => await ConfigureLnbAsync());
        _stop.Click += async (_, _) => await StopAsync();
        menu.Items.Add(_stop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("About / source credits", null, (_, _) => MessageBox.Show(this,
            "HB9IIU RTL DATV Player\n\nReceiver: OnAir-derived datvrx (C++ / Rust).\nSpectrum UI adapted from HB9IIU Experimental H.266 Player.\nSpectrum supplied by BATC; signal quality comes from your local RTL-SDR.\n\nGPL v3. See docs/PLAYER.md for source credits and operation.", "About RTL DATV"));
        menu.Items.Add("Quit", null, (_, _) => Close());
        ContextMenuStrip = menu; _video.ContextMenuStrip = menu; _spectrum.ContextMenuStrip = menu;

        _feed.FrameReceived += frame => Ui(() => { _spectrum.SetFrame(frame); _lastSpectrum = DateTime.UtcNow; });
        _feed.ConnectionChanged += connected => Ui(() =>
        {
            _spectrumOnline = connected; _spectrum.SetConnected(connected);
            _tips.SetToolTip(_spectrum, connected ? "BATC spectrum · click a signal to tune (remote reference)" : "BATC spectrum offline · current reception continues");
            if (!connected) _spectrum.SetFrame(null);
        });
        _spectrum.SpectrumClicked += async mhz =>
        {
            if (_busy || _closing) return;
            if (!_spectrumOnline || DateTime.UtcNow - _lastSpectrum > TimeSpan.FromSeconds(5))
            { _spectrum.ShowMessage("Spectrum unavailable — waiting for BATC"); return; }
            var found = SignalFinder.Find(_spectrum.Frame, mhz);
            if (!found.HasValue) { _spectrum.ShowMessage($"No signal at {mhz:0.000} MHz"); return; }
            await TuneAsync(new TuneRequest(found.Value.CenterMhz, found.Value.SymbolRate, (double)_lnbMhz));
        };
        _receiver.Status += line => Ui(() => OnStatus(line));
        _receiver.VideoInfo += line => Ui(() => _tips.SetToolTip(_local, line));
        _video.Resize += (_, _) => FitVideo();
        _timer.Tick += (_, _) =>
        {
            EmbedVideo();
            if (_lastTelemetry != default && DateTime.UtcNow - _lastTelemetry > TimeSpan.FromSeconds(5) && !_busy && _current != null)
            { _state.Text = "WAITING"; _state.ForeColor = Color.Goldenrod; }
            if (_spectrumOnline && DateTime.UtcNow - _lastSpectrum > TimeSpan.FromSeconds(5))
                _tips.SetToolTip(_spectrum, "BATC spectrum stale · current reception continues");
        };
        ReadSettings(); UpdateVolumeMenu();
        Shown += async (_, _) => { _feed.Start(); _timer.Start(); await TuneAsync(new TuneRequest(Band.BeaconMhz, 1500, (double)_lnbMhz)); };
        FormClosing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true; _timer.Stop(); _feed.Dispose();
            SaveSettings();
            await _receiver.DisposeAsync();
            _closed = true; Close();
        };
    }

    private async Task TuneAsync(TuneRequest request)
    {
        if (_busy || _closing) return;
        string error = LocalReceiver.Validate(request);
        if (error != null) { _spectrum.ShowMessage(error); return; }
        SetBusy(true);
        _embedded = IntPtr.Zero; _embeddedProcess = null;
        _placeholder.Visible = true; _placeholder.Text = "Tuning — waiting for video keyframe…";
        _lastTelemetry = default;
        _state.Text = "TUNING"; _state.ForeColor = Color.Goldenrod;
        try
        {
            await _receiver.TuneAsync(request, _volumePercent);
            _current = request;
            _spectrum.SetTuned(request.RfMhz, request.SymbolRateKs);
            _spectrum.ShowMessage($"Tuning {request.RfMhz:0.000} MHz · {request.SymbolRateKs} kS/s");
        }
        catch (Exception ex)
        {
            _current = null; _state.Text = "ERROR"; _state.ForeColor = Color.Salmon;
            _placeholder.Text = "Reception could not start"; _local.Text = ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async Task StopAsync()
    {
        if (_busy || _closing) return;
        SetBusy(true);
        try
        {
            await _receiver.StopAsync();
            _current = null; _embedded = IntPtr.Zero; _embeddedProcess = null;
            _state.Text = "STOPPED"; _state.ForeColor = Muted;
            _placeholder.Visible = true; _placeholder.Text = "Stopped · click a signal to resume";
            _local.Text = "Stopped · click a signal to resume";
            _spectrum.SetTuned(null, 0);
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; _stop.Enabled = _volumeMenu.Enabled = !busy;
    }

    private void OnStatus(string line)
    {
        if (line.StartsWith("datvrx ["))
        {
            _lastTelemetry = DateTime.UtcNow;
            string summary = line[(line.IndexOf(']') + 1)..].Trim();
            var mode = Regex.Match(summary, @"^(DVB-S\S*) (.*?)\s+[\d.]+ Msym/s");
            var quality = Regex.Match(summary, @"Es/N0 ([\d.-]+) dB");
            var bad = Regex.Match(summary, @"bad (\d+)");
            string frequency = _current == null ? "" : $"{_current.RfMhz:0.000} MHz · {_current.SymbolRateKs} kS/s · ";
            _local.Text = frequency + (mode.Success ? mode.Groups[1].Value + " " + mode.Groups[2].Value : "Searching")
                + (quality.Success ? $" · Es/N0 {quality.Groups[1].Value} dB" : "")
                + (bad.Success ? $" · {bad.Groups[1].Value} bad" : "");
            bool locked = summary.Contains("TS lock");
            _state.Text = locked ? "LOCKED" : "SEARCHING";
            _state.ForeColor = locked ? Green : Color.Goldenrod;
        }
        else if (line.Contains("transport stream lock")) { _state.Text = "LOCKED"; _state.ForeColor = Green; }
        else if (line.Contains("synchronisation lost")) { _state.Text = "REACQUIRING"; _state.ForeColor = Color.Goldenrod; }
        else if (line.StartsWith("Tuning")) _local.Text = line;
        else if (line.StartsWith("Radio:") || line.StartsWith("Reception stopped"))
        { _local.Text = line; _state.Text = "STOPPED"; _state.ForeColor = Color.Salmon; }
    }

    private void EmbedVideo()
    {
        try
        {
            var player = _receiver.PlayerProcess;
            if (player == null || player.HasExited) return;
            if (_embeddedProcess == player && _embedded != IntPtr.Zero) { FitVideo(); return; }
            player.Refresh();
            IntPtr handle = player.MainWindowHandle;
            if (handle == IntPtr.Zero) return;
            long style = GetWindowLongPtr(handle, -16).ToInt64();
            style = (style & ~(0x80000000L | 0x00C00000L | 0x00040000L)) | 0x40000000L | 0x10000000L;
            SetParent(handle, _video.Handle);
            SetWindowLongPtr(handle, -16, new IntPtr(style));
            EnableWindow(handle, false);
            _embedded = handle; _embeddedProcess = player; _placeholder.Visible = false;
            FitVideo();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private void FitVideo()
    {
        if (_embedded == IntPtr.Zero) return;
        MoveWindow(_embedded, 0, 0, _video.ClientSize.Width, _video.ClientSize.Height, true);
    }

    private void Ui(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!_closing && !IsDisposed) action(); }); } catch (InvalidOperationException) { }
    }

    private void UpdateVolumeMenu()
    {
        _volumeMenu.Text = _volumePercent == 0 ? "Volume · muted" : $"Volume · {_volumePercent}%";
        foreach (ToolStripMenuItem item in _volumeMenu.DropDownItems) item.Checked = (int)item.Tag == _volumePercent;
    }

    private async Task ConfigureLnbAsync()
    {
        using var dialog = new Form { Text = "LNB configuration", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(300, 110), MaximizeBox = false, MinimizeBox = false };
        var value = new NumericUpDown { Minimum = 9000, Maximum = 11000, DecimalPlaces = 3, Value = _lnbMhz,
            Location = new Point(150, 18), Width = 125, AccessibleName = "LNB oscillator MHz" };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(200, 65) };
        dialog.Controls.AddRange([new Label { Text = "Oscillator (MHz)", AutoSize = true, Location = new Point(16, 22) }, value, save]);
        dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _lnbMhz = value.Value; SaveSettings();
        if (_current != null) await TuneAsync(_current with { LnbMhz = (double)_lnbMhz });
    }

    private void ReadSettings()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
            _lnbMhz = Math.Clamp(settings.LnbMhz, 9000, 11000);
            _volumePercent = Math.Clamp(settings.Volume, 0, 100);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private void SaveSettings()
    {
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Settings { LnbMhz = _lnbMhz, Volume = _volumePercent }, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private sealed class Settings { public decimal LnbMhz { get; set; } = 9750; public int Volume { get; set; } = 30; }

    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
}

internal sealed class FrequencyScale : Control
{
    public FrequencyScale() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var brush = new SolidBrush(ForeColor);
        for (double mhz = Band.StartMhz; mhz <= Band.EndMhz; mhz += 1)
        {
            string text = mhz.ToString("0.0", CultureInfo.InvariantCulture);
            SizeF size = e.Graphics.MeasureString(text, Font);
            float center = (float)((mhz - Band.StartMhz) / (Band.EndMhz - Band.StartMhz) * Width);
            float left = Math.Clamp(center - size.Width / 2, 0, Math.Max(0, Width - size.Width));
            e.Graphics.DrawString(text, Font, brush, left, 0);
        }
    }
}

