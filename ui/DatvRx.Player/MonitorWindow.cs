using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatvRx.Player;

// Standalone Windows monitor. No SSH, remote receiver or Pi dependency.
public sealed class MonitorWindow : Form
{
    private readonly string _root, _settingsPath;
    private readonly LocalReceiver _receiver;
    private readonly LocalSweep _sweep;
    private readonly SpectrumFeed _batc = new();
    private readonly AudioSession _audio = new();
    private readonly MonitorSpectrum _spectrum = new() { Dock = DockStyle.Fill };
    private readonly Panel _video = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Label _placeholder = new() { Dock = DockStyle.Fill, ForeColor = MonitorColors.Text, BackColor = Color.Black,
        TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 13, FontStyle.Bold), Text = "WINDOWS DATV MONITOR\n\nConnect an RTL-SDR to this PC\nthen press START" };
    private readonly Label _state = Readout("READY", 11, true), _station = Readout("RTL DATV", 12, true),
        _provider = Readout("Standalone Windows SDR", 8), _frequency = Readout("— MHz"), _sr = Readout("— kS/s"),
        _mode = Readout(""), _esn0 = Readout("—", 19, true), _margin = Readout("D —", 10, true),
        _media = Readout(""), _errors = Readout(""), _caption = Readout("Es/N0 · dB", 8);
    private readonly MarginGauge _gauge = new() { Dock = DockStyle.Bottom, Height = 8 };
    private readonly TuningEye _eye = new() { Dock = DockStyle.Fill };
    private readonly Button _sound = Button("🔊  30%"), _minus = Button("−"), _plus = Button("+"),
        _beacon = Button("BEACON"), _stop = Button("START"), _bands = Button("BANDS"), _settings = Button("⚙"), _quit = Button("⏻");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly ToolTip _tips = new();
    private ReceiveBand _band = ReceiveBand.Presets[0];
    private Settings _saved = new();
    private TuneRequest _current;
    private ReceptionReport _report;
    private bool _busy, _closing, _closed, _scanning, _recording, _batcOnline, _fullVideo, _batcStarted;
    private DateTime _lastReport, _lastFrame;
    private int _losses;
    private IntPtr _embedded;
    private Process _embeddedProcess;
    private readonly Control _sidebar, _controls;
    private readonly TableLayoutPanel _body;
    private readonly TableLayoutPanel _layout;

    public MonitorWindow(string root, string recording = null)
    {
        _root = root; _settingsPath = Path.Combine(root, "windows-monitor-settings.json");
        ReadSettings(); _band = ReceiveBand.Presets.FirstOrDefault(b => b.Key == _saved.Band) ?? _band;
        _receiver = new LocalReceiver(root); _sweep = new LocalSweep(root);
        Text = "HB9IIU · Windows DATV Monitor"; Font = new Font("Segoe UI", 9);
        BackColor = MonitorColors.Background; ForeColor = MonitorColors.Text;
        ClientSize = new Size(1024, 600); MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen; TopMost = _saved.TopMost;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(7) };
        _layout = layout;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        _body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, 6) };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15)); _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 71)); _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        _sidebar = BuildInformation(); _controls = BuildControls();
        _video.Controls.Add(_placeholder);
        _body.Controls.Add(_sidebar, 0, 0); _body.Controls.Add(_video, 1, 0); _body.Controls.Add(_controls, 2, 0);
        layout.Controls.Add(_body, 0, 0); layout.Controls.Add(_spectrum, 0, 1); Controls.Add(layout);
        _spectrum.Band = EffectiveBand();
        _beacon.Enabled = _band.BeaconMhz > 0;
        _station.ForeColor = _esn0.ForeColor = MonitorColors.Text;
        _minus.Font = _plus.Font = new Font("Segoe UI", 17);
        _tips.SetToolTip(_eye, "Click to alternate magic eye and live constellation. D is approximate Es/N0 reserve.");
        _tips.SetToolTip(_spectrum, "Click a carrier to tune. During local reception click to rescan first.");
        _spectrum.FrequencyClicked += async mhz =>
        {
            if (_busy || _closing) return;
            if (_saved.Source == "Local" && !_scanning) { await StartScanningAsync(); return; }
            if (_saved.Source == "BATC" && (!_batcOnline || DateTime.UtcNow - _lastFrame > TimeSpan.FromSeconds(5))) { ShowMessage("Waiting for fresh BATC data"); return; }
            if (_band.EndMhz - _band.StartMhz > 10)
            {
                double center = Math.Clamp(mhz, _band.StartMhz + 1.5, _band.EndMhz - 1.5);
                _band = _band with { StartMhz = center - 1.5, EndMhz = center + 1.5 };
                _spectrum.Band = EffectiveBand(); await StartScanningAsync(); return;
            }
            var signal = MonitorSignalFinder.Find(_spectrum.Frame, mhz, _band.BeaconMhz);
            if (signal is not { } found) { ShowMessage($"No carrier near {mhz:0.000} MHz"); return; }
            await TuneAsync(found.CenterMhz, found.SymbolRate);
        };
        _sweep.Frame += frame => Ui(() => { if (_scanning) { _spectrum.Frame = frame; _spectrum.Message = ""; _spectrum.Invalidate(); } });
        _sweep.Status += line => Ui(() =>
        {
            if (!_scanning) return;
            if (line.StartsWith("Scanner ended") || line.Contains("No supported") || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                State("SDR NOT READY", MonitorColors.Amber); _scanning = false; _stop.Text = "START";
                Placeholder("Connect an RTL-SDR to this PC\n\nPress START to scan\n\n" + line);
                _spectrum.Message = "Local SDR unavailable · connect stick and press Start"; _spectrum.Invalidate();
            }
        });
        _batc.FrameReceived += values => Ui(() =>
        {
            if (_saved.Source != "BATC" || _band.Key != "qo100") return;
            _lastFrame = DateTime.UtcNow;
            _spectrum.Frame = new(Band.StartMhz, Band.EndMhz, values.Select(v => (float)v).ToArray(), false);
            _spectrum.Message = ""; _spectrum.Invalidate();
        });
        _batc.ConnectionChanged += online => Ui(() => { _batcOnline = online; if (_saved.Source == "BATC" && !online) ShowMessage("BATC reconnecting · internet reference"); });
        _receiver.Status += line => Ui(() => OnStatus(line));
        _receiver.ServiceInfo += info => Ui(() =>
        {
            if (info.Name.Length > 0) _station.Text = info.Name;
            if (info.Provider.Length > 0) _provider.Text = info.Provider;
            _media.Text = string.Join("\n", new[] { info.Video, info.Audio }.Where(s => s.Length > 0));
        });
        _receiver.VideoInfo += line => Ui(() =>
        {
            var size = Regex.Match(line, @"Video: (\w+).*? (\d{2,5}x\d{2,5})");
            if (size.Success) _media.Text = size.Groups[1].Value.ToUpperInvariant() + " · " + size.Groups[2].Value + "\n" + (_media.Text.Split('\n').LastOrDefault() ?? "");
            if (_recording && line.Contains("service_name")) _station.Text = line.Split(':', 2).Last().Trim();
        });
        _sound.Click += (_, _) => { _saved.Muted = !_saved.Muted; UpdateSound(); };
        _minus.Click += (_, _) => { _saved.Volume = Math.Max(0, _saved.Volume - 5); UpdateSound(); };
        _plus.Click += (_, _) => { _saved.Volume = Math.Min(100, _saved.Volume + 5); UpdateSound(); };
        _beacon.Click += async (_, _) => { _band = ReceiveBand.Presets[0]; _spectrum.Band = EffectiveBand(); await TuneAsync(10491.5, 1500); };
        _stop.Click += async (_, _) =>
        {
            if (_current != null && _saved.Source == "Local") await StartScanningAsync();
            else if (_current != null || _scanning || _recording) await StopAsync();
            else await StartScanningAsync();
        };
        _bands.Click += async (_, _) => await ChooseBandAsync(); _settings.Click += async (_, _) => await ConfigureAsync();
        _quit.Click += (_, _) => Close(); _video.DoubleClick += (_, _) => ToggleFullVideo(); _placeholder.DoubleClick += (_, _) => ToggleFullVideo();
        _video.Resize += (_, _) => FitVideo();
        _timer.Tick += (_, _) =>
        {
            EmbedVideo();
            try { _audio.ProcessId = _receiver.PlayerProcess is { HasExited: false } process ? process.Id : 0; } catch (InvalidOperationException) { _audio.ProcessId = 0; }
            _audio.Update(_saved.Volume, _saved.Muted);
            if (!_recording && _current != null && _lastReport != default && DateTime.UtcNow - _lastReport > TimeSpan.FromSeconds(5))
            { State("WAITING", MonitorColors.Amber); Placeholder(_station.Text + " — signal lost\n\nWaiting for the station to return"); }
        };
        FormClosing += async (_, e) =>
        {
            if (_closed) return; e.Cancel = true; if (_closing) return;
            _closing = true; _timer.Stop(); _batc.Dispose(); SaveSettings();
            await _sweep.StopAsync(); await _receiver.DisposeAsync(); _closed = true; Close();
        };
        Shown += async (_, _) =>
        {
            EnsureBatc(); _timer.Start(); UpdateSound();
            if (recording != null) await PlayRecordingAsync(recording); else await StartScanningAsync();
        };
    }

    private Control BuildInformation()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = MonitorColors.Panel, ColumnCount = 1, RowCount = 10, Padding = new Padding(8), Margin = new Padding(0, 0, 6, 0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int[] heights = [23, 24, 29, 21, 20, 34, 70, 44, 30];
        foreach (int height in heights) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var quality = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        quality.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 15)); quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        quality.Controls.Add(_caption, 0, 0); quality.Controls.Add(_esn0, 0, 1); quality.Controls.Add(_margin, 0, 2); quality.Controls.Add(_gauge, 0, 3);
        Control[] rows = [_state, _station, _provider, _frequency, _sr, _mode, quality, _media, _errors, _eye];
        for (int i = 0; i < rows.Length; i++) panel.Controls.Add(rows[i], 0, i);
        return panel;
    }
    private Control BuildControls()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = MonitorColors.Panel, ColumnCount = 1, RowCount = 7, Padding = new Padding(6), Margin = new Padding(6, 0, 0, 0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++) panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 7));
        var volume = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        volume.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); volume.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        volume.Controls.Add(_minus); volume.Controls.Add(_plus);
        Control[] rows = [_sound, volume, _beacon, _stop, _bands, _settings, _quit];
        for (int i = 0; i < rows.Length; i++) panel.Controls.Add(rows[i], 0, i);
        _settings.Font = new Font("Segoe UI Symbol", 17); _quit.Font = new Font("Segoe UI Symbol", 18); _quit.ForeColor = MonitorColors.Red;
        _tips.SetToolTip(_settings, "Settings and credits"); _tips.SetToolTip(_quit, "Quit Windows DATV Monitor");
        return panel;
    }
    private ReceiveBand EffectiveBand() => _band.Key == "qo100" ? _band with { LoMhz = _saved.LnbMhz } : _band;
    private async Task StartScanningAsync()
    {
        if (_busy || _closing) return; _busy = true;
        try
        {
            await EndReceptionAsync(); _scanning = true; _spectrum.Band = EffectiveBand(); _spectrum.SourceLabel = _saved.Source == "BATC" ? "BATC reference" : "LOCAL SDR";
            EnsureBatc();
            _spectrum.Frame = null; _spectrum.Tuned = null; _spectrum.Paused = false; _spectrum.Message = "Scanning · click a signal to watch";
            _spectrum.Invalidate(); State("SCANNING", MonitorColors.Amber); _stop.Text = "STOP";
            Placeholder("Click a signal in the spectrum\n\nto watch DATV on this PC");
            if (_saved.Source == "BATC" && _band.Key == "qo100") return;
            await _sweep.StartAsync(EffectiveBand(), _saved.Gain);
        }
        catch (Exception ex) { _scanning = false; State("SDR ERROR", MonitorColors.Red); Placeholder(ex.Message); _stop.Text = "START"; }
        finally { _busy = false; }
    }
    private async Task TuneAsync(double mhz, int symbols)
    {
        if (_busy || _closing) return;
        var request = new TuneRequest(mhz, symbols, EffectiveBand().LoMhz, _saved.Gain);
        if (LocalReceiver.Validate(request) is { } error) { ShowMessage(error); return; }
        _busy = true;
        try
        {
            await _sweep.StopAsync(); _scanning = false; _recording = false;
            _current = request; _report = null; _embedded = IntPtr.Zero; _embeddedProcess = null; _lastReport = default;
            ClearQuality();
            _station.Text = "Tuning…"; _provider.Text = "Local RTL-SDR"; _media.Text = ""; _losses = 0;
            _frequency.Text = $"{mhz:0.000} MHz"; _sr.Text = $"{symbols} kS/s"; _mode.Text = "Searching…";
            State("TUNING", MonitorColors.Amber); Placeholder("Waiting for the station\n\nand its first video keyframe…");
            _stop.Text = _saved.Source == "BATC" ? "STOP" : "RESCAN"; _spectrum.Tuned = request; _spectrum.TunedColor = MonitorColors.Amber;
            _spectrum.Paused = _saved.Source != "BATC"; _spectrum.Message = _spectrum.Paused ? "Local sweep paused · click to rescan" : ""; _spectrum.Invalidate();
            await _receiver.TuneAsync(request, 100); _eye.Running = true; _eye.Invalidate();
        }
        catch (Exception ex) { State("SDR ERROR", MonitorColors.Red); Placeholder(ex.Message); _current = null; _stop.Text = "START"; }
        finally { _busy = false; }
    }
    private async Task EndReceptionAsync()
    {
        await _sweep.StopAsync(); await _receiver.StopAsync();
        _current = null; _recording = false; _scanning = false; _embedded = IntPtr.Zero; _embeddedProcess = null;
        _report = null; _eye.Report = null; _eye.Running = false; _eye.Invalidate();
        ClearQuality();
    }
    private async Task StopAsync()
    {
        if (_busy || _closing) return; _busy = true;
        try
        {
            await EndReceptionAsync(); State("STOPPED", MonitorColors.Dim); _stop.Text = "START";
            _spectrum.Tuned = null; _spectrum.Message = "Press Start to scan"; _spectrum.Invalidate();
            Placeholder("Stopped\n\nPress START to scan the band");
        }
        finally { _busy = false; }
    }
    private async Task PlayRecordingAsync(string path)
    {
        if (_busy || _closing) return; _busy = true;
        try
        {
            await EndReceptionAsync(); _recording = true; State("REPLAY", MonitorColors.Amber);
            _station.Text = Path.GetFileNameWithoutExtension(path); _provider.Text = "Recorded reception · not live RF";
            _frequency.Text = "RECORDING"; _sr.Text = ""; _mode.Text = "Saved transport stream"; _esn0.Text = "—"; _margin.Text = "D —";
            _gauge.DbMargin = null; _gauge.Invalidate(); _errors.Text = ""; _stop.Text = "STOP";
            _spectrum.Frame = null; _spectrum.Tuned = null; _spectrum.Message = "Recorded video · RF scanner stopped";
            _spectrum.SourceLabel = "REPLAY"; _spectrum.Invalidate(); Placeholder("Opening recorded reception…");
            await _receiver.PlayRecordingAsync(path);
        }
        catch (Exception ex) { State("REPLAY ERROR", MonitorColors.Red); Placeholder(ex.Message); }
        finally { _busy = false; }
    }
    private void OnStatus(string line)
    {
        if (line.StartsWith("datvrx cells ")) { _eye.SetCells(line); return; }
        if (line.Contains("synchronisation lost")) { _losses++; State("REACQUIRING", MonitorColors.Amber); }
        if (line.StartsWith("datvrx ["))
        {
            _lastReport = DateTime.UtcNow; _report = ReceptionReport.Parse(line);
            if (_report is { } report)
            {
                State(report.Locked ? "LOCKED" : "SEARCHING", report.Locked ? MonitorColors.Green : MonitorColors.Amber);
                _mode.Text = $"{report.Standard}\n{report.Modulation} {report.Fec}";
                _esn0.Text = report.EsN0.ToString("0.0", CultureInfo.InvariantCulture);
                _margin.Text = report.Margin is { } margin ? $"D {margin:+0.0;-0.0;0.0} dB" : "D —";
                _gauge.DbMargin = report.Margin; _gauge.Invalidate(); _errors.Text = $"Bad {report.Bad}\nSync losses {_losses}";
                _eye.Report = report; _eye.Running = true; _eye.Invalidate();
                _spectrum.TunedColor = report.Locked ? MonitorColors.Green : MonitorColors.Red; _spectrum.Invalidate();
            }
        }
        if (line.StartsWith("Radio:") || line.StartsWith("Reception stopped"))
        {
            State("SDR NOT READY", MonitorColors.Amber); _current = null; _recording = false; _stop.Text = "START";
            Placeholder("Reception stopped\n\nConnect the RTL-SDR to this PC and press START\n\n" + line);
        }
    }
    private async Task ChooseBandAsync()
    {
        if (_busy) return;
        using var dialog = Dialog("Choose receiving band", 540, 365);
        var tiles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(12) };
        for (int i = 0; i < 2; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 4; i++) tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        ReceiveBand selected = null;
        foreach (var band in ReceiveBand.Presets)
        {
            var tile = Button($"{band.Name}\n{band.StartMhz:0.0}–{band.EndMhz:0.0} MHz");
            tile.Click += (_, _) => { selected = band; dialog.DialogResult = DialogResult.OK; dialog.Close(); };
            tiles.Controls.Add(tile);
        }
        dialog.Controls.Add(tiles);
        if (dialog.ShowDialog(this) == DialogResult.OK && selected != null)
        {
            _band = selected; _saved.Band = selected.Key;
            if (_band.Key != "qo100") _saved.Source = "Local";
            _beacon.Enabled = _band.BeaconMhz > 0; SaveSettings(); await StartScanningAsync();
        }
    }
    private async Task ConfigureAsync()
    {
        if (_busy) return;
        using var dialog = Dialog("Windows DATV Monitor · Settings", 475, 400);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Padding(20) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        var source = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; source.Items.AddRange(["Local SDR scan", "BATC reference (QO-100)"]); source.SelectedIndex = _saved.Source == "BATC" ? 1 : 0;
        var lo = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 9000, Maximum = 11000, DecimalPlaces = 3, Value = (decimal)_saved.LnbMhz };
        var gain = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 49.6m, DecimalPlaces = 1, Value = (decimal)_saved.Gain };
        var top = new CheckBox { Text = "Always on top", Checked = TopMost, Dock = DockStyle.Fill, ForeColor = MonitorColors.Text };
        table.Controls.Add(Readout("Spectrum source"), 0, 0); table.Controls.Add(source, 1, 0);
        table.Controls.Add(Readout("QO-100 LNB · MHz"), 0, 1); table.Controls.Add(lo, 1, 1);
        table.Controls.Add(Readout("RF gain · dB"), 0, 2); table.Controls.Add(gain, 1, 2);
        table.Controls.Add(top, 0, 3); table.SetColumnSpan(top, 2);
        var replay = Button("Open TS recording…"); replay.Click += async (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "Transport stream (*.ts)|*.ts|All files|*.*", InitialDirectory = Path.Combine(_root, "test-output") };
            if (file.ShowDialog(dialog) == DialogResult.OK) { dialog.Close(); await PlayRecordingAsync(file.FileName); }
        };
        table.Controls.Add(replay, 0, 4); table.SetColumnSpan(replay, 2);
        var credits = Button("Credits / source"); credits.Click += (_, _) => MessageBox.Show(dialog,
            "HB9IIU Windows DATV Monitor\n\nNative Windows app; receiver and SDR run on this PC.\nC++/Rust decoder from OnAir.\nLayout and local scanner adapted from HB9IIU's Pi player.\nBATC spectrum / original finder and audio session code from HB9IIU Experimental H.266 Player.\nMagic eye inspired by Lynx DATV Receiver (G8YTZ).\n\nGPL v3; see docs/WINDOWS_MONITOR.md and LICENSE.", "Credits");
        table.Controls.Add(credits, 0, 5); table.SetColumnSpan(credits, 2);
        var save = Button("Save settings"); save.Click += (_, _) =>
        {
            _saved.Source = source.SelectedIndex == 1 && _band.Key == "qo100" ? "BATC" : "Local";
            _saved.LnbMhz = (double)lo.Value; _saved.Gain = (double)gain.Value; _saved.TopMost = TopMost = top.Checked;
            SaveSettings(); dialog.DialogResult = DialogResult.OK; dialog.Close();
        };
        table.Controls.Add(save, 0, 7); table.SetColumnSpan(save, 2); dialog.Controls.Add(table);
        if (dialog.ShowDialog(this) == DialogResult.OK) await StartScanningAsync();
    }
    private void UpdateSound() { _sound.Text = _saved.Muted ? "🔇  MUTE" : $"🔊  {_saved.Volume}%"; _audio.Update(_saved.Volume, _saved.Muted); SaveSettings(); }
    private void ClearQuality() { _esn0.Text = "—"; _margin.Text = "D —"; _gauge.DbMargin = null; _gauge.Invalidate(); _errors.Text = ""; _eye.Report = null; _eye.Invalidate(); }
    private void EnsureBatc() { if (!_batcStarted && _saved.Source == "BATC" && _band.Key == "qo100") { _batcStarted = true; _batc.Start(); } }
    private void State(string text, Color color) { _state.Text = text; _state.ForeColor = color; }
    private void Placeholder(string text) { _placeholder.Text = text; _placeholder.Visible = true; _placeholder.BringToFront(); if (_embedded != IntPtr.Zero) ShowWindow(_embedded, 0); }
    private void ShowMessage(string text) { _spectrum.Message = text; _spectrum.Invalidate(); }
    private void ToggleFullVideo()
    {
        _fullVideo = !_fullVideo; _sidebar.Visible = _controls.Visible = _spectrum.Visible = !_fullVideo;
        _layout.RowStyles[1].Height = _fullVideo ? 0 : 132;
        _body.ColumnStyles[0].Width = _fullVideo ? 0 : 15; _body.ColumnStyles[2].Width = _fullVideo ? 0 : 14;
        _body.ColumnStyles[1].Width = _fullVideo ? 100 : 71; FitVideo();
    }
    private void EmbedVideo()
    {
        try
        {
            var player = _receiver.PlayerProcess;
            if (player == null || player.HasExited) return;
            if (_embeddedProcess == player && _embedded != IntPtr.Zero)
            {
                if ((_recording || _report is { Locked: true }) && _placeholder.Visible) { _placeholder.Visible = false; ShowWindow(_embedded, 5); }
                FitVideo(); return;
            }
            player.Refresh(); var window = player.MainWindowHandle; if (window == IntPtr.Zero) return;
            long style = GetWindowLongPtr(window, -16).ToInt64(); style = (style & ~(0x80000000L | 0x00c00000L | 0x00040000L)) | 0x40000000L | 0x10000000L;
            SetParent(window, _video.Handle); SetWindowLongPtr(window, -16, new IntPtr(style)); EnableWindow(window, false);
            _embedded = window; _embeddedProcess = player; _placeholder.Visible = false; FitVideo();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }
    private void FitVideo() { if (_embedded != IntPtr.Zero) MoveWindow(_embedded, 0, 0, _video.ClientSize.Width, _video.ClientSize.Height, true); }
    private void Ui(Action action) { if (_closing || IsDisposed || !IsHandleCreated) return; try { BeginInvoke(() => { if (!_closing && !IsDisposed) action(); }); } catch (InvalidOperationException) { } }
    private void ReadSettings()
    {
        try { _saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath)) ?? new(); } catch (Exception ex) when (ex is IOException or JsonException) { }
        _saved.Volume = Math.Clamp(_saved.Volume, 0, 100); _saved.Gain = double.IsFinite(_saved.Gain) ? Math.Clamp(_saved.Gain, 0, 49.6) : 40.2;
        _saved.LnbMhz = double.IsFinite(_saved.LnbMhz) ? Math.Clamp(_saved.LnbMhz, 9000, 11000) : 9750;
    }
    private void SaveSettings() { try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_saved, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    private sealed class Settings { public int Volume { get; set; } = 30; public bool Muted { get; set; } public bool TopMost { get; set; } = true; public double Gain { get; set; } = 40.2; public double LnbMhz { get; set; } = 9750; public string Source { get; set; } = "Local"; public string Band { get; set; } = "qo100"; }
    private static Label Readout(string text, float size = 9, bool bold = false) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = MonitorColors.Dim, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), AutoEllipsis = true, Margin = Padding.Empty };
    private static Button Button(string text) => new() { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderColor = Color.FromArgb(41, 72, 92) }, BackColor = Color.FromArgb(27, 49, 64), ForeColor = MonitorColors.Text, Font = new Font("Segoe UI", 9, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(3), TabStop = false };
    private static Form Dialog(string title, int width, int height) => new() { Text = title, ClientSize = new Size(width, height), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, BackColor = MonitorColors.Background, ForeColor = MonitorColors.Text, MinimizeBox = false, MaximizeBox = false };
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}

