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
        TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 13, FontStyle.Bold), Text = "DATVision\n\nConnect an RTL-SDR to this PC\nthen press BEACON" };
    private readonly Label _state = Readout("READY", 11, true), _station = Readout("RTL DATV", 12, true),
        _provider = Readout("Standalone Windows SDR", 8), _frequency = Readout("— MHz"), _sr = Readout("— kS/s"),
        _mode = Readout(""), _esn0 = Readout("—", 19, true), _margin = Readout("D —", 10, true),
        _media = Readout(""), _errors = Readout(""), _caption = Readout("Es/N0 · dB", 8);
    private readonly MarginGauge _gauge = new() { Dock = DockStyle.Bottom, Height = 8 };
    private readonly TuningEye _eye = new() { Dock = DockStyle.Fill };
    private readonly Button _sound = Button("🔊  30%"), _minus = Button("−"), _plus = Button("+"),
        _beacon = Button("BEACON"), _stop = Button("START"), _bands = Button("BANDS"), _settings = Button("⚙"), _quit = Button("⏻");
    private readonly Button _recordButton = Button("● RECORD");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly ToolTip _tips = new();
    private ReceiveBand _band = ReceiveBand.Presets[0];
    private Settings _saved = new();
    private TuneRequest _current;
    private ReceptionReport _report;
    private bool _busy, _closing, _closed, _scanning, _recording, _batcOnline, _batcStarted;
    private DateTime _lastReport, _lastFrame;
    private readonly SignalLossPolicy _signalLoss = new();
    private bool _hadReception, _signalLost;
    private bool ReceivingNow => _report is { Locked: true } && DateTime.UtcNow - _lastReport < TimeSpan.FromSeconds(3)
        && _receiver.LastTransportTick != 0 && Environment.TickCount64 - _receiver.LastTransportTick < 1500;
    private int _losses;
    private IntPtr _embedded;
    private Process _embeddedProcess;
    private readonly Control _sidebar, _controls;
    private readonly TableLayoutPanel _body;
    private Form _playerWindow;

    public MonitorWindow(string root, string recording = null)
    {
        _root = root; _settingsPath = Path.Combine(root, "windows-monitor-settings.json");
        ReadSettings();
        // Always open on the QO-100 beacon with the internet spectrum reference.
        _band = ReceiveBand.Presets[0]; _saved.Band = _band.Key; _saved.Source = "BATC";
        _receiver = new LocalReceiver(root) { UdpDestination = _saved.UdpEnabled ? _saved.UdpDestination : null }; _sweep = new LocalSweep(root);
        Text = "DATVision · HB9IIU"; Icon = BrandAssets.Icon; Font = new Font("Segoe UI", 9);
        BackColor = MonitorColors.Background; ForeColor = MonitorColors.Text;
        ClientSize = new Size(1024, 600); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = _saved.TopMost;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(7) };
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
                Placeholder("Connect an RTL-SDR to this PC\n\nPress BEACON to retry\n\n" + line);
                _spectrum.Message = "Local SDR unavailable · connect stick and press BEACON"; _spectrum.Invalidate();
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
        _recordButton.ForeColor = MonitorColors.Text;
        _tips.SetToolTip(_recordButton, "Record to Record / callsign in the app folder. Click again to finish.");
        _recordButton.Click += (_, _) =>
        {
            try
            {
                if (_receiver.IsRecording) _receiver.StopRecording();
                else
                {
                    // Extract a callsign token from the received DVB service name.
                    var call = Regex.Match(_station.Text.ToUpperInvariant(), @"(?<![A-Z0-9])[A-Z0-9]{1,3}[0-9][A-Z]{1,4}(?![A-Z0-9])");
                    string callsign = call.Success ? call.Value : "Unknown";
                    string directory = Path.Combine(_root, "Record", callsign);
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, $"DATV-{DateTime.Now:yyyyMMdd-HHmmss-fff}.ts");
                    _receiver.StartRecording(path);
                }
                UpdateRecordButton();
            }
            catch (Exception ex) { ShowMessage("Recording: " + ex.Message); }
        };
        _beacon.Click += async (_, _) => await TuneBeaconAsync();
        _stop.Click += async (_, _) =>
        {
            if (_current != null && _saved.Source == "Local") await StartScanningAsync();
            else if (_current != null || _scanning || _recording) await StopAsync();
            else await StartScanningAsync();
        };
        _bands.Click += async (_, _) => await ChooseBandAsync(); _settings.Click += async (_, _) => await ConfigureAsync();
        _quit.Click += (_, _) => Close(); _video.DoubleClick += (_, _) => TogglePlayerWindow(); _placeholder.DoubleClick += (_, _) => TogglePlayerWindow();
        _video.Resize += (_, _) => FitVideo();
        _tips.SetToolTip(_video, "Double-click to open a resizable video window");
        _tips.SetToolTip(_placeholder, "Double-click to open a resizable video window");
        _timer.Tick += async (_, _) =>
        {
            UpdateRecordButton();
            if (!_busy && Enabled) EmbedVideo();
            if (_current != null && !_busy && !_signalLost && ReceivingNow && _embedded != IntPtr.Zero && !_placeholder.Visible
                && _report is { Locked: true } && DateTime.UtcNow - _lastReport <= TimeSpan.FromSeconds(5)
                && _spectrum.Stage != CarrierStage.Playing)
            {
                _spectrum.Stage = CarrierStage.Playing; _spectrum.Invalidate();
            }
            try { _audio.ProcessId = _receiver.PlayerProcess is { HasExited: false } process ? process.Id : 0; } catch (InvalidOperationException) { _audio.ProcessId = 0; }
            _audio.Update(_saved.Volume, _saved.Muted);
            if (!_recording && _current != null && !_busy && !_closing)
            {
                bool receiving = ReceivingNow;
                _hadReception |= receiving;
                double silent = _signalLoss.Update(Environment.TickCount64, receiving);
                if (receiving)
                {
                    if (_signalLost) { _signalLost = false; _spectrum.Stage = CarrierStage.WaitingVideo; _spectrum.Invalidate(); }
                }
                else
                {
                    if (_hadReception) State("REACQUIRING", MonitorColors.Amber);
                    if (SignalLossPolicy.ResumeScan(silent, _saved.Source == "Local"))
                    {
                        await StartScanningAsync();
                        return;
                    }
                    if (_hadReception && SignalLossPolicy.ShowNotice(silent))
                    {
                        _signalLost = true; _spectrum.Stage = CarrierStage.Lost; _spectrum.Invalidate();
                        ClearQuality(); State("SIGNAL LOST", MonitorColors.Amber);
                        string wait = _saved.Source == "Local" ? $"Scanning again in {Math.Max(0, 30 - (int)silent)} s" : "Waiting for the station to return";
                        Placeholder($"{_station.Text} — signal lost\n\n{wait}");
                    }
                }
            }
        };
        FormClosing += async (_, e) =>
        {
            if (_closed) return; e.Cancel = true; if (_closing) return;
            _closing = true; _playerWindow?.Close(); _timer.Stop(); _batc.Dispose(); SaveSettings();
            await _sweep.StopAsync(); await _receiver.DisposeAsync(); _closed = true; Close();
        };
        Shown += async (_, _) =>
        {
            EnsureBatc(); _timer.Start(); UpdateSound();
            if (recording != null) await PlayRecordingAsync(recording); else await TuneBeaconAsync();
        };
    }

    private void UpdateRecordButton()
    {
        bool active = _receiver.IsRecording;
        _recordButton.Text = active ? "● STOP REC" : "● RECORD";
        ((MonitorButton)_recordButton).RecordingActive = active;
        _recordButton.AccessibleName = active ? "Stop recording" : "Start recording";
        _recordButton.Invalidate();
        _recordButton.Enabled = !_busy && (active || (_current != null && !_recording && _report is { Locked: true }));
    }

    private Control BuildInformation()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = MonitorColors.Panel, ColumnCount = 1, RowCount = 10, Padding = new Padding(8), Margin = new Padding(0, 0, 6, 0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // Lift the quality block while keeping the media and eye positions stable.
        int[] heights = [23, 24, 23, 21, 20, 34, 82, 44, 30];
        foreach (int height in heights) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var quality = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        quality.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 15)); quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
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
        Control[] rows = [_sound, volume, _beacon, _recordButton, _bands, _settings, _quit];
        for (int i = 0; i < rows.Length; i++) panel.Controls.Add(rows[i], 0, i);
        _settings.Font = new Font("Segoe UI Symbol", 17); _quit.Font = new Font("Segoe UI Symbol", 18); _quit.ForeColor = MonitorColors.Red;
        _tips.SetToolTip(_settings, "Settings and credits"); _tips.SetToolTip(_quit, "Quit DATVision");
        return panel;
    }
    private ReceiveBand EffectiveBand() => _band.Key == "qo100" ? _band with { LoMhz = _saved.EffectiveLoMhz.Value } : _band;
    private async Task TuneBeaconAsync()
    {
        if (_busy || _closing) return;
        _band = ReceiveBand.Presets[0]; _saved.Band = _band.Key;
        _beacon.Enabled = true; _spectrum.Band = EffectiveBand();
        _spectrum.SourceLabel = _saved.Source == "BATC" ? "BATC" : "LOCAL";
        EnsureBatc();
        await TuneAsync(_band.BeaconMhz, 1500);
        if (_saved.Source == "BATC" && !_batcOnline) ShowMessage("Waiting for BATC spectrum · internet reference");
    }
    private async Task StartScanningAsync()
    {
        if (_busy || _closing) return; _busy = true;
        try
        {
            await EndReceptionAsync(); _scanning = true; _spectrum.Band = EffectiveBand(); _spectrum.SourceLabel = _saved.Source == "BATC" ? "BATC" : "LOCAL";
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
            _spectrum.Tuned = request; _spectrum.Stage = CarrierStage.Selected; _spectrum.Message = ""; _spectrum.Refresh();
            await _sweep.StopAsync(); _scanning = false; _recording = false;
            _current = request; _report = null; _embedded = IntPtr.Zero; _embeddedProcess = null; _lastReport = default;
            _signalLoss.Reset(Environment.TickCount64); _hadReception = _signalLost = false;
            ClearQuality();
            _station.Text = "Tuning…"; _provider.Text = "Local RTL-SDR"; _media.Text = ""; _losses = 0;
            _frequency.Text = $"{mhz:0.000} MHz"; _sr.Text = $"{symbols} kS/s"; _mode.Text = "Searching…";
            State("TUNING", MonitorColors.Amber); Placeholder("Waiting for the station\n\nand its first video keyframe…");
            _stop.Text = _saved.Source == "BATC" ? "STOP" : "RESCAN"; _spectrum.Tuned = request;
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
            Placeholder("Stopped\n\nPress BEACON to retry the band");
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
        if (line.StartsWith("UDP streaming:")) { ShowMessage(line); return; }
        if (line.StartsWith("datvrx cells ")) { _eye.SetCells(line); return; }
        if (line.Contains("synchronisation lost")) { _report = null; _losses++; State("REACQUIRING", MonitorColors.Amber); }
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
                // Lock means transport is decoding; keep green once video is playing.
                // Brief telemetry changes must not overwrite the player state.
                if (!_busy && report.Locked && _spectrum.Stage == CarrierStage.Selected)
                { _spectrum.Stage = CarrierStage.WaitingVideo; _spectrum.Invalidate(); }
            }
        }
        if (line.StartsWith("Radio:") || line.StartsWith("Receiver pipeline stopped") || line.StartsWith("Reception stopped"))
        {
            State("RECEIVER ERROR", MonitorColors.Red); _receiver.StopRecording(); ClearQuality(); _eye.Running = false; _current = null; _recording = false; _stop.Text = "START";
            Placeholder("Receiver stopped\n\nCheck the RTL-SDR connection, then press BEACON or select a station\n\n" + line);
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
        if (ShowMonitorDialog(dialog) == DialogResult.OK && selected != null)
        {
            _band = selected; _saved.Band = selected.Key;
            if (_band.Key != "qo100") _saved.Source = "Local";
            _beacon.Enabled = _band.BeaconMhz > 0; SaveSettings(); await StartScanningAsync();
        }
    }
    private async Task ConfigureAsync(double? measuredLo = null, string calibrationNote = null)
    {
        if (_busy) return;
        using var dialog = Dialog("DATVision · Settings", 590, 540);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 10, Padding = new Padding(20) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        for (int row = 0; row < 10; row++)
            table.RowStyles.Add(new RowStyle(SizeType.Percent, row == 5 && calibrationNote != null ? 16 : calibrationNote != null ? 84f / 9 : 10));
        var source = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; source.Items.AddRange(["LOCAL", "BATC"]); source.SelectedIndex = _saved.Source == "BATC" ? 1 : 0;
        var lo = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 9000, Maximum = 11000, DecimalPlaces = 4, Increment = 0.0001m, Value = (decimal)(measuredLo ?? _saved.EffectiveLoMhz.Value) };
        _tips.SetToolTip(lo, "Total downconversion frequency, including combined downconverter and SDR frequency error.");
        // Supported manual gain steps for the R820T/R820T2/R828D tuner.
        double[] gains = [0, 0.9, 1.4, 2.7, 3.7, 7.7, 8.7, 12.5, 14.4, 15.7, 16.6, 19.7, 20.7, 22.9, 25.4, 28, 29.7, 32.8, 33.8, 36.4, 37.2, 38.6, 40.2, 42.1, 43.4, 43.9, 44.5, 48, 49.6];
        lo.TextAlign = HorizontalAlignment.Center;
        var gain = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = gains.Length - 1,
            TickStyle = TickStyle.None, SmallChange = 1, LargeChange = 1, AutoSize = false,
            Value = Enumerable.Range(0, gains.Length).MinBy(i => Math.Abs(gains[i] - _saved.Gain)), AccessibleName = "RF gain" };
        var gainPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        gainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        gainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        gainPanel.Controls.Add(Readout("0"), 0, 0); gainPanel.Controls.Add(gain, 1, 0);
        gainPanel.Controls.Add(Readout("49.6"), 2, 0);
        var gainLabel = Readout($"RF gain · {gains[gain.Value]:0.0} dB");
        gain.ValueChanged += (_, _) => gainLabel.Text = $"RF gain · {gains[gain.Value]:0.0} dB";
        var top = new CheckBox { Text = "Always on top", Checked = TopMost, Dock = DockStyle.Fill, ForeColor = MonitorColors.Text };
        table.Controls.Add(Readout("Spectrum source"), 0, 0); table.Controls.Add(source, 1, 0);
        table.Controls.Add(Readout("Effective LO · MHz"), 0, 1); table.Controls.Add(lo, 1, 1);
        table.Controls.Add(gainLabel, 0, 2); table.Controls.Add(gainPanel, 1, 2);
        table.Controls.Add(top, 0, 3); table.SetColumnSpan(top, 2);
        var calibrate = Button("Calibrate effective LO using beacon…"); calibrate.Enabled = _band.Key == "qo100" && !_recording;
        calibrate.Click += (_, _) => { dialog.DialogResult = DialogResult.Retry; dialog.Close(); };
        table.Controls.Add(calibrate, 0, 4); table.SetColumnSpan(calibrate, 2);
        var note = Readout(calibrationNote ?? "", 8);
        bool udpEnabled = _saved.UdpEnabled;
        string udpDestination = _saved.UdpDestination;
        var network = Button(udpEnabled ? "UDP streaming · enabled…" : "UDP streaming…");
        network.Click += (_, _) =>
        {
            using var options = Dialog("DATVision · UDP streaming", 590, 320);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(18) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int[] heights = [32, 28, 38, 58, 36, 44, 44];
            foreach (int height in heights) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var enable = new CheckBox { Text = "Enable UDP streaming", Checked = udpEnabled, Dock = DockStyle.Fill, ForeColor = MonitorColors.Text };
            var destination = new TextBox { Text = udpDestination, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11) };
            var explanation = Readout("Multicast lets several PCs watch the live station. In VLC: Media → Open Network Stream. Allow VLC through the receiving PC’s firewall; guest Wi-Fi may block multicast.", 9);
            var address = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11) };
            void RefreshAddress()
            {
                try { address.Text = UdpStreamAddress.Vlc(destination.Text); }
                catch (ArgumentException) { address.Text = "Enter a valid destination above"; }
            }
            destination.TextChanged += (_, _) => RefreshAddress(); RefreshAddress();
            var copy = Button("Copy VLC address");
            copy.Click += (_, _) => { try { Clipboard.SetText(UdpStreamAddress.Vlc(destination.Text)); } catch (ArgumentException ex) { MessageBox.Show(options, ex.Message, "UDP streaming"); } };
            var done = Button("OK");
            done.Dock = DockStyle.Top; done.Height = copy.Height = 38; copy.Dock = DockStyle.Top;
            _tips.SetToolTip(done, "Return to Settings. Apply and save activates the changes.");
            done.Click += (_, _) =>
            {
                try
                {
                    var endpoint = UdpStreamAddress.Parse(destination.Text);
                    udpDestination = $"udp://{endpoint}"; udpEnabled = enable.Checked;
                    network.Text = udpEnabled ? "UDP streaming · enabled…" : "UDP streaming…";
                    options.DialogResult = DialogResult.OK; options.Close();
                }
                catch (ArgumentException ex) { MessageBox.Show(options, ex.Message, "UDP streaming"); }
            };
            fields.Controls.Add(enable); fields.Controls.Add(Readout("Destination · multicast group or receiving PC’s IPv4 address"));
            fields.Controls.Add(destination); fields.Controls.Add(explanation); fields.Controls.Add(address); fields.Controls.Add(copy); fields.Controls.Add(done);
            options.Controls.Add(fields); ShowMonitorDialog(options, owner: dialog);
        };
        var calibrationAndNetwork = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        calibrationAndNetwork.RowStyles.Add(new RowStyle(SizeType.Percent, calibrationNote == null ? 0 : 55));
        calibrationAndNetwork.RowStyles.Add(new RowStyle(SizeType.Percent, calibrationNote == null ? 100 : 45));
        note.AutoEllipsis = false; calibrationAndNetwork.Controls.Add(note, 0, 0); calibrationAndNetwork.Controls.Add(network, 0, 1);
        if (calibrationNote == null)
        {
            calibrationAndNetwork.Controls.Remove(network);
            calibrationAndNetwork.Dispose();
            table.Controls.Add(network, 0, 5); table.SetColumnSpan(network, 2);
        }
        else
        {
            table.Controls.Add(calibrationAndNetwork, 0, 5); table.SetColumnSpan(calibrationAndNetwork, 2);
        }
        var replay = Button("Open TS recording…"); replay.Click += async (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "Transport stream (*.ts)|*.ts|All files|*.*", InitialDirectory = Directory.Exists(Path.Combine(_root, "Record")) ? Path.Combine(_root, "Record") : _root };
            if (file.ShowDialog(dialog) == DialogResult.OK) { dialog.Close(); await PlayRecordingAsync(file.FileName); }
        };
        table.Controls.Add(replay, 0, 6); table.SetColumnSpan(replay, 2);
        var credits = Button("Credits / source"); credits.Click += (_, _) => ShowCredits(dialog);
        table.Controls.Add(credits, 0, 7); table.SetColumnSpan(credits, 2);
        var save = Button("Apply and save"); save.Click += (_, _) =>
        {
            _saved.Source = source.SelectedIndex == 1 && _band.Key == "qo100" ? "BATC" : "Local";
            _saved.EffectiveLoMhz = (double)lo.Value; _saved.Gain = gains[gain.Value]; _saved.TopMost = TopMost = top.Checked;
            if (_playerWindow != null) _playerWindow.TopMost = TopMost;
            _saved.UdpEnabled = udpEnabled; _saved.UdpDestination = udpDestination;
            _receiver.UdpDestination = udpEnabled ? udpDestination : null;
            SaveSettings(); dialog.DialogResult = DialogResult.OK; dialog.Close();
        };
        table.Controls.Add(save, 0, 8); table.SetColumnSpan(save, 2);
        var cancel = Button("Cancel"); cancel.DialogResult = DialogResult.Cancel; dialog.CancelButton = cancel;
        table.Controls.Add(cancel, 0, 9); table.SetColumnSpan(cancel, 2); dialog.Controls.Add(table);
        void EnlargeSettingsControls(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                control.Font = new Font("Segoe UI", control == note ? 10 : control is NumericUpDown ? 12 : 11,
                    control is Button ? FontStyle.Bold : FontStyle.Regular);
                if (control is Label label) label.TextAlign = ContentAlignment.MiddleLeft;
                EnlargeSettingsControls(control);
            }
        }
        EnlargeSettingsControls(table);
        var outcome = ShowMonitorDialog(dialog);
        if (outcome == DialogResult.Retry)
        {
            var measurement = await CalibrateFrequencyAsync((double)lo.Value, gains[gain.Value]);
            await ConfigureAsync(measurement.Mhz ?? (double)lo.Value, measurement.Note);
        }
        else if (outcome == DialogResult.OK)
        {
            if (_current is { } current) await TuneAsync(current.RfMhz, current.SymbolRateKs);
            else await StartScanningAsync();
        }
    }
    private void ShowCredits(Form owner)
    {
        using var dialog = Dialog("DATVision · Credits", 620, 570);
        using var artwork = BrandAssets.LoadWordmark();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var logo = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = artwork, Margin = Padding.Empty };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical, BackColor = MonitorColors.Background, ForeColor = MonitorColors.Text,
            Font = new Font("Segoe UI", 10), Text = "DATVision by HB9IIU\n\nNative Windows app; receiver and SDR run on this PC.\nDVB-S/S2 decoder based on OnAir by Kristijonas Adomaitis (adswill).\nLDPC decoder optimizations by Evariste Courjaud (F5OEO).\nAdditional DVB-S/S2 contributions by Arclamp.\nLayout and local scanner adapted from HB9IIU's Pi player.\nBATC spectrum / original finder and audio session code from HB9IIU Experimental H.266 Player.\nMagic eye inspired by Lynx DATV Receiver (G8YTZ).\n\nGPL v3; see LICENSE and the bundled third-party license notices.".Replace("\n", Environment.NewLine) };
        var close = Button("Close"); close.DialogResult = DialogResult.OK; dialog.CancelButton = close;
        layout.Controls.Add(logo, 0, 0); layout.Controls.Add(text, 0, 1); layout.Controls.Add(close, 0, 2);
        dialog.Controls.Add(layout); ShowMonitorDialog(dialog, owner: owner);
    }

    private Task<(double? Mhz, string Note)> CalibrateFrequencyAsync(double assumedMhz, double gain)
    {
        var previous = _current;
        bool wasScanning = _scanning;
        using var cancel = new CancellationTokenSource();
        using var progress = Dialog("Frequency calibration · QO-100 beacon", 460, 210);
        var status = Readout("Starting beacon measurement…", 11);
        status.TextAlign = ContentAlignment.MiddleCenter;
        var stop = Button("Cancel calibration"); stop.Dock = DockStyle.Bottom; stop.Height = 42;
        stop.Click += (_, _) => cancel.Cancel(); progress.Controls.Add(status); progress.Controls.Add(stop);
        bool finished = false;
        progress.FormClosing += (_, e) => { if (!finished) { e.Cancel = true; cancel.Cancel(); } };
        progress.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) cancel.Cancel(); };
        double? measured = null;
        string note = "Calibration cancelled. Frequency settings unchanged.";
        progress.Shown += async (_, _) =>
        {
            try
            {
                await EndReceptionAsync();
                var estimates = new List<double>();
                for (int step = 0; step < LnbCalibration.StepsKhz.Length; step++)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    int detune = LnbCalibration.StepsKhz[step];
                    status.Text = $"Measuring beacon · step {step + 1} / 5\nOffset {detune:+0;-0;0} kHz · waiting for stable lock";
                    await _receiver.TuneAsync(new TuneRequest(10491.5 + detune / 1000.0, 1500, assumedMhz, gain), 100);
                    var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var samples = new List<double>();
                    long lockedAt = 0;
                    void Sample(string line)
                    {
                        if (ReceptionReport.Parse(line) is { } report)
                        {
                            if (!report.Locked) { lockedAt = 0; samples.Clear(); }
                            else if (lockedAt == 0) lockedAt = Stopwatch.GetTimestamp();
                        }
                        if (line.Contains("synchronisation lost")) { lockedAt = 0; samples.Clear(); }
                        if (lockedAt == 0 || Stopwatch.GetElapsedTime(lockedAt).TotalSeconds < 1 || !LnbCalibration.TryCarrier(line, out double hz)) return;
                        samples.Add(hz);
                        if (samples.Count >= 8) completion.TrySetResult(LnbCalibration.Estimate(assumedMhz, samples, detune));
                    }
                    _receiver.Status += Sample;
                    try { estimates.Add(await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), cancel.Token)); }
                    catch (TimeoutException) { }
                    finally { _receiver.Status -= Sample; }
                }
                if (LnbCalibration.Summarize(estimates) is { } result)
                {
                    measured = result.Mhz;
                    note = $"Effective LO {result.Mhz:0.0000} MHz.\nSpread {result.SpreadKhz:0.0} kHz across {estimates.Count}/5 steps. Save applies; Cancel discards.";
                }
                else note = "Calibration unreliable: fewer than 3 locks or spread above 5 kHz. Frequency settings unchanged.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { note = "Calibration failed. Frequency settings unchanged. " + ex.Message; }
            finally
            {
                try
                {
                    await _receiver.StopAsync();
                    if (previous != null) await TuneAsync(previous.RfMhz, previous.SymbolRateKs);
                    else if (wasScanning) await StartScanningAsync();
                    else await StopAsync();
                }
                catch (Exception ex) { note += " Reception could not resume: " + ex.Message; }
                finished = true; progress.Close();
            }
        };
        ShowMonitorDialog(progress, closeOnEscape: false);
        return Task.FromResult((measured, note));
    }
    private DialogResult ShowMonitorDialog(Form dialog, bool closeOnEscape = true, Form owner = null)
    {
        // A topmost video sibling must not cover a modal dialog and leave
        // the disabled monitor looking frozen. Restore it after the dialog closes.
        var playerWindow = _playerWindow;
        bool restorePlayer = playerWindow is { Visible: true };
        dialog.TopMost = (owner?.TopMost ?? TopMost) || playerWindow is { TopMost: true };
        dialog.ShowInTaskbar = false;
        dialog.Shown += (_, _) => dialog.Activate();
        dialog.KeyPreview = true;
        dialog.KeyDown += (_, e) =>
        {
            if (closeOnEscape && e.KeyCode == Keys.Escape) { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
        };
        if (restorePlayer) playerWindow.Hide();
        try { return dialog.ShowDialog(owner ?? this); }
        finally
        {
            if (restorePlayer && !_closing && !playerWindow.IsDisposed) playerWindow.Show();
            if (!_closing && !(owner?.IsDisposed ?? false)) (owner ?? this).Activate();
        }
    }
    private void UpdateSound() { _sound.Text = _saved.Muted ? "🔇  MUTE" : $"🔊  {_saved.Volume}%"; _audio.Update(_saved.Volume, _saved.Muted); SaveSettings(); }
    private void ClearQuality() { _esn0.Text = "—"; _margin.Text = "D —"; _gauge.DbMargin = null; _gauge.Invalidate(); _errors.Text = ""; _eye.Report = null; _eye.Invalidate(); }
    private void EnsureBatc() { if (!_batcStarted && _saved.Source == "BATC" && _band.Key == "qo100") { _batcStarted = true; _batc.Start(); } }
    private void State(string text, Color color) { _state.Text = text; _state.ForeColor = color; }
    private void Placeholder(string text) { _placeholder.Text = text; _placeholder.Visible = true; _placeholder.BringToFront(); if (_embedded != IntPtr.Zero) ShowWindow(_embedded, 0); }
    private void ShowMessage(string text) { _spectrum.Message = text; _spectrum.Invalidate(); }
    private void TogglePlayerWindow()
    {
        if (_playerWindow != null) { _playerWindow.Close(); return; }
        var playerWindow = new Form
        {
            Text = "DATVision · Video", Icon = BrandAssets.Icon, BackColor = Color.Black,
            ClientSize = new Size(960, 540), MinimumSize = new Size(320, 220),
            StartPosition = FormStartPosition.CenterScreen, TopMost = TopMost
        };
        _playerWindow = playerWindow;
        // Move the existing host, including ffplay and its waiting overlay.
        // The decoder and audio keep running throughout.
        _body.Controls.Remove(_video);
        playerWindow.Controls.Add(_video);
        var returnVideo = new Label
        {
            Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = MonitorColors.Dim,
            Text = "Video open in separate window\n\nDouble-click to bring it back",
            TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
        };
        returnVideo.DoubleClick += (_, _) => playerWindow.Close();
        _body.Controls.Add(returnVideo, 1, 0);
        playerWindow.KeyPreview = true;
        playerWindow.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) playerWindow.Close(); };
        playerWindow.FormClosing += (_, _) =>
        {
            // Reparent before disposal so closing the pop-out cannot destroy ffplay.
            playerWindow.Controls.Remove(_video);
            _body.Controls.Remove(returnVideo); returnVideo.Dispose();
            _body.Controls.Add(_video, 1, 0); _playerWindow = null; FitVideo();
        };
        playerWindow.Show(this); FitVideo();
    }
    private void EmbedVideo()
    {
        try
        {
            var player = _receiver.PlayerProcess;
            if (player == null || player.HasExited) return;
            if (_embeddedProcess == player && _embedded != IntPtr.Zero)
            {
                if ((_recording || ReceivingNow) && !_signalLost && _placeholder.Visible) { _placeholder.Visible = false; ShowWindow(_embedded, 8); }
                FitVideo(); return;
            }
            player.Refresh(); var window = player.MainWindowHandle; if (window == IntPtr.Zero) return;
            ShowWindow(window, 0);
            long style = GetWindowLongPtr(window, -16).ToInt64(); style = (style & ~(0x80000000L | 0x00c00000L | 0x00040000L | 0x10000000L)) | 0x40000000L;
            SetParent(window, _video.Handle); SetWindowLongPtr(window, -16, new IntPtr(style)); EnableWindow(window, false);
            _embedded = window; _embeddedProcess = player; FitVideo();
            if (_recording || (ReceivingNow && !_signalLost)) { _placeholder.Visible = false; ShowWindow(window, 8); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }
    private void FitVideo() { if (_embedded != IntPtr.Zero) MoveWindow(_embedded, 0, 0, _video.ClientSize.Width, _video.ClientSize.Height, true); }
    private void Ui(Action action) { if (_closing || IsDisposed || !IsHandleCreated) return; try { BeginInvoke(() => { if (!_closing && !IsDisposed) action(); }); } catch (InvalidOperationException) { } }
    private void ReadSettings()
    {
        try { _saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath)) ?? new(); } catch (Exception ex) when (ex is IOException or JsonException) { }
        try { UdpStreamAddress.Parse(_saved.UdpDestination); }
        catch (ArgumentException) { _saved.UdpEnabled = false; _saved.UdpDestination = UdpStreamAddress.Default; }
        _saved.Volume = Math.Clamp(_saved.Volume, 0, 100); _saved.Gain = double.IsFinite(_saved.Gain) ? Math.Clamp(_saved.Gain, 0, 49.6) : 40.2;
        if (_saved.EffectiveLoMhz == null)
            _saved.EffectiveLoMhz = _saved.DownconverterLoMhz is { } nominal
                ? FrequencyConversion.EffectiveLo(nominal, _saved.FrequencyCorrectionKhz)
                : _saved.LnbMhz ?? 9750;
        _saved.EffectiveLoMhz = double.IsFinite(_saved.EffectiveLoMhz.Value) ? Math.Clamp(_saved.EffectiveLoMhz.Value, 9000, 11000) : 9750;
        _saved.LnbMhz = null; _saved.DownconverterLoMhz = null; _saved.FrequencyCorrectionKhz = 0;
    }
    private void SaveSettings() { try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_saved, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    private sealed class Settings { public bool UdpEnabled { get; set; } public string UdpDestination { get; set; } = UdpStreamAddress.Default; public int Volume { get; set; } = 30; public bool Muted { get; set; } public bool TopMost { get; set; } = true; public double Gain { get; set; } = 40.2; [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public double? LnbMhz { get; set; } [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public double? DownconverterLoMhz { get; set; } [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public double FrequencyCorrectionKhz { get; set; } public double? EffectiveLoMhz { get; set; } public string Source { get; set; } = "BATC"; public string Band { get; set; } = "qo100"; }
    private static Label Readout(string text, float size = 9, bool bold = false) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = MonitorColors.Dim, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), AutoEllipsis = true, Margin = Padding.Empty };
    private static Button Button(string text) => new MonitorButton() { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderColor = Color.FromArgb(41, 72, 92) }, BackColor = Color.FromArgb(27, 49, 64), ForeColor = MonitorColors.Text, Font = new Font("Segoe UI", 9, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(3), TabStop = false };
    private static Form Dialog(string title, int width, int height) => new() { Text = title, Icon = BrandAssets.Icon, ClientSize = new Size(width, height), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, BackColor = MonitorColors.Background, ForeColor = MonitorColors.Text, MinimizeBox = false, MaximizeBox = false };
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}

