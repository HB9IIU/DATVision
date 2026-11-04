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
    private readonly Button _recordButton = Button("● REC");
    private readonly Button _playButton = Button("▶ PLAY");   // opens a recording, as on the Pi
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
    private readonly TableLayoutPanel _body, _layout;
    private readonly VideoClicks _clicks;
    private bool _fullVideo;   // the video alone in the window, as on the Pi

    public MonitorWindow(string root, string recording = null)
    {
        _root = root; _settingsPath = Path.Combine(root, "windows-monitor-settings.json");
        ReadSettings();
        // Always open on the QO-100 beacon with the internet spectrum reference.
        _band = ReceiveBand.Presets[0]; _saved.Band = _band.Key; _saved.Source = "BATC";
        _receiver = new LocalReceiver(root) { UdpDestination = _saved.UdpEnabled ? _saved.UdpDestination : null, PlutoUri = _saved.Device == "Pluto" ? _saved.PlutoUri : null }; _sweep = new LocalSweep(root);
        _spectrum.Receiver = _receiver.PlutoUri != null ? "PLUTO" : "RTL-SDR";
        Text = "DATVision · HB9IIU"; Icon = BrandAssets.Icon; Font = new Font("Segoe UI", 9);
        BackColor = MonitorColors.Background; ForeColor = MonitorColors.Text;
        ClientSize = new Size(1024, 600); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = _saved.TopMost;
        var layout = _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(7) };
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
        _playButton.Click += async (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "Transport stream (*.ts)|*.ts|All files|*.*",
                InitialDirectory = Directory.Exists(Path.Combine(_root, "Record")) ? Path.Combine(_root, "Record") : _root };
            if (file.ShowDialog(this) == DialogResult.OK) await PlayRecordingAsync(file.FileName);
        };
        _bands.Click += async (_, _) => await ChooseBandAsync(); _settings.Click += async (_, _) => await ConfigureAsync();
        _quit.Click += (_, _) => Close();
        // Clicks on the picture come through VideoClicks (ffplay's window would swallow them):
        // as on the Pi, a click ends a replay, otherwise shows the video alone or brings the columns back.
        _clicks = new VideoClicks(_video, Path.Combine(root, "test-output", "player-clicks.log"));
        _clicks.Clicked += async () => { if (_recording) await EndReplayAsync(); else SetFullVideo(!_fullVideo); };
        _video.Resize += (_, _) => FitVideo();
        _tips.SetToolTip(_video, "Double-click to open a resizable video window");
        _tips.SetToolTip(_placeholder, "Double-click to open a resizable video window");
        _timer.Tick += async (_, _) =>
        {
            UpdateRecordButton();
            if (_recording && !_busy && _receiver.PlayerProcess is { HasExited: true }) await EndReplayAsync();
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
            _closing = true; _timer.Stop(); _batc.Dispose(); _clicks.Dispose(); SaveSettings();
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
        _recordButton.Text = "● REC";   // red and blinking while recording, as on the Pi
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
        // The Pi's right column: sound, volume, BEACON, REC, PLAY, BANDS, settings, quit.
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = MonitorColors.Panel, ColumnCount = 1, RowCount = 8, Padding = new Padding(6), Margin = new Padding(6, 0, 0, 0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 8));
        var volume = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        volume.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); volume.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        volume.Controls.Add(_minus); volume.Controls.Add(_plus);
        Control[] rows = [_sound, volume, _beacon, _recordButton, _playButton, _bands, _settings, _quit];
        for (int i = 0; i < rows.Length; i++) panel.Controls.Add(rows[i], 0, i);
        _settings.Font = new Font("Segoe UI Symbol", 17); _quit.Font = new Font("Segoe UI Symbol", 18); _quit.ForeColor = MonitorColors.Red;
        _tips.SetToolTip(_settings, "Settings and credits"); _tips.SetToolTip(_quit, "Quit DATVision");
        _tips.SetToolTip(_playButton, "Play a recording from the Record folder.");
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
        if (LocalReceiver.Validate(request, _receiver.PlutoUri != null) is { } error) { ShowMessage(error); return; }
        _busy = true;
        try
        {
            _spectrum.Tuned = request; _spectrum.Stage = CarrierStage.Selected; _spectrum.Message = ""; _spectrum.Refresh();
            await _sweep.StopAsync(); _scanning = false; _recording = false;
            _current = request; _report = null; _embedded = IntPtr.Zero; _embeddedProcess = null; _lastReport = default;
            _signalLoss.Reset(Environment.TickCount64); _hadReception = _signalLost = false;
            ClearQuality();
            _station.Text = "Tuning…"; _provider.Text = _receiver.PlutoUri != null ? "Pluto · " + (_saved.PlutoName ?? _receiver.PlutoUri) : "Local RTL-SDR"; _media.Text = ""; _losses = 0;
            _frequency.Text = $"{mhz:0.000} MHz"; _sr.Text = $"{symbols} kS/s"; _mode.Text = "Searching…";
            State("TUNING", MonitorColors.Amber); Placeholder("Waiting for the station\n\nand its first video keyframe…");
            _stop.Text = _saved.Source == "BATC" ? "STOP" : "RESCAN"; _spectrum.Tuned = request;
            _spectrum.Paused = _saved.Source != "BATC"; _spectrum.Message = _spectrum.Paused ? "Local sweep paused · click to rescan" : ""; _spectrum.Invalidate();
            await _receiver.TuneAsync(request, 100); _eye.Running = true; _eye.Invalidate();
        }
        catch (Exception ex) { State("SDR ERROR", MonitorColors.Red); Placeholder(ex.Message); _current = null; _stop.Text = "START"; }
        finally { _busy = false; }
    }
    // The video alone in the window (a replay): no information, controls or spectrum.
    private void SetFullVideo(bool full)
    {
        _fullVideo = full;
        _sidebar.Visible = _controls.Visible = _spectrum.Visible = !full;
        _body.ColumnStyles[0].Width = full ? 0 : 15; _body.ColumnStyles[1].Width = full ? 100 : 71; _body.ColumnStyles[2].Width = full ? 0 : 14;
        _layout.RowStyles[1].Height = full ? 0 : 132;
        FitVideo();
    }
    private async Task EndReceptionAsync()
    {
        SetFullVideo(false);
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
            // As on the Pi: the video fills the window; a click on it or the end of the file ends it.
            SetFullVideo(true);
        }
        catch (Exception ex) { State("REPLAY ERROR", MonitorColors.Red); Placeholder(ex.Message); }
        finally { _busy = false; }
    }
    private async Task EndReplayAsync()
    {
        if (!_recording || _busy) return;
        await StopAsync();
        if (_saved.Source == "Local") await StartScanningAsync(); else await TuneBeaconAsync();   // back to reception
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
        if (_recording && (line.StartsWith("Receiver pipeline stopped") || line.StartsWith("Reception stopped")))
        {
            _ = EndReplayAsync();   // the recording reached its end
            return;
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
        // Four cards as on the Pi: Spectrum, LNB oscillator, Receiver, Network stream.
        using var dialog = Dialog("DATVision · Settings", 940, 600);
        var accentBorder = Color.FromArgb(80, 215, 160);
        var amber = Color.FromArgb(255, 191, 71);
        Label Text(string text, float size, bool bold = false, Color? color = null) => new()
        {
            Text = text, Dock = DockStyle.Fill, AutoSize = false, BackColor = Color.Transparent, ForeColor = color ?? MonitorColors.Text,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 0, 2)
        };
        Button Choice(string text) { var b = Button(text); b.Font = new Font("Segoe UI", 10, FontStyle.Bold); return b; }
        void Pick(Button chosen, Button other)
        {
            ((MonitorButton)chosen).Selected = true; ((MonitorButton)other).Selected = false;
        }
        TableLayoutPanel Row(params Control[] controls)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = controls.Length, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            foreach (var control in controls) { row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / controls.Length)); row.Controls.Add(control); }
            return row;
        }
        TableLayoutPanel Card(string title, int[] heights, string hint, out Label hintLabel)
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length + 2, BackColor = MonitorColors.Panel,
                Padding = new Padding(16, 10, 16, 10), Margin = new Padding(6) };
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            foreach (int height in heights) card.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(Text(title, 12, true), 0, 0);
            hintLabel = Text(hint, 8.5f, color: MonitorColors.Dim); hintLabel.TextAlign = ContentAlignment.BottomLeft;
            card.Controls.Add(hintLabel, 0, heights.Length + 1);
            return card;
        }

        // ---- Spectrum
        bool batc = _saved.Source == "BATC" && _band.Key == "qo100";
        var local = Choice("LOCAL SCAN"); var online = Choice("BATC ONLINE");
        online.Enabled = _band.Key == "qo100";
        local.Click += (_, _) => { batc = false; Pick(local, online); };
        online.Click += (_, _) => { batc = true; Pick(online, local); };
        Pick(batc ? online : local, batc ? local : online);
        var spectrumCard = Card("Spectrum", [46, 30],
            "Local scan: your own SDR sweeps the band, no internet needed. BATC: the BATC web spectrum of the whole transponder.", out _);
        spectrumCard.Controls.Add(Row(local, online), 0, 1);
        spectrumCard.Controls.Add(_band.Key != "qo100" ? Text("BATC covers QO-100 only.", 10, color: MonitorColors.Dim)
            : _batcOnline ? Text("● Internet OK: BATC spectrum available.", 10, color: accentBorder)
            : Text("● BATC not reachable now: use LOCAL SCAN.", 10, color: amber), 0, 2);

        // ---- LNB oscillator
        var lo = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 9000, Maximum = 11000, DecimalPlaces = 4, Increment = 0.0001m,
            Value = (decimal)(measuredLo ?? _saved.EffectiveLoMhz.Value), Font = new Font("Segoe UI", 18, FontStyle.Bold), TextAlign = HorizontalAlignment.Center,
            BackColor = MonitorColors.Background, ForeColor = MonitorColors.Text, BorderStyle = BorderStyle.None };
        _tips.SetToolTip(lo, "Total downconversion frequency, including combined downconverter and SDR frequency error.");
        var calibrate = Choice("RECALIBRATE ON BEACON"); calibrate.Enabled = _band.Key == "qo100" && !_recording;
        calibrate.Click += (_, _) => { dialog.DialogResult = DialogResult.Retry; dialog.Close(); };
        var note = Text(calibrationNote ?? "", 9, color: calibrationNote == null ? MonitorColors.Dim : accentBorder);
        var lnbCard = Card("LNB oscillator · MHz", [46, 46, 44], "Measured on the QO-100 beacon: five locks, deliberately detuned.", out _);
        lnbCard.Controls.Add(lo, 0, 1); lnbCard.Controls.Add(calibrate, 0, 2); lnbCard.Controls.Add(note, 0, 3);

        // ---- Receiver: RTL-SDR stick (gain set here) or an ADALM-Pluto (automatic gain)
        // Supported manual gain steps for the R820T/R820T2/R828D tuner.
        double[] gains = [0, 0.9, 1.4, 2.7, 3.7, 7.7, 8.7, 12.5, 14.4, 15.7, 16.6, 19.7, 20.7, 22.9, 25.4, 28, 29.7, 32.8, 33.8, 36.4, 37.2, 38.6, 40.2, 42.1, 43.4, 43.9, 44.5, 48, 49.6];
        var gain = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = gains.Length - 1, TickStyle = TickStyle.None, SmallChange = 1, LargeChange = 1,
            AutoSize = false, Value = Enumerable.Range(0, gains.Length).MinBy(i => Math.Abs(gains[i] - _saved.Gain)), AccessibleName = "RF gain" };
        var gainLabel = Text("", 10);
        bool pluto = _saved.Device == "Pluto";
        var stick = Choice("RTL-SDR"); var plutoButton = Choice("PLUTO");
        var plutoBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 10), Enabled = false,
            DropDownWidth = 560 };
        var search = Choice("Search");
        var plutoRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        plutoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); plutoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        plutoRow.Controls.Add(plutoBox, 0, 0); plutoRow.Controls.Add(search, 1, 0);
        var warning = Text("", 9, color: amber);
        var receiverCard = Card("Receiver", [46, 38, 40, 44], "", out var receiverHint);
        receiverCard.Controls.Add(Row(stick, plutoButton), 0, 1); receiverCard.Controls.Add(plutoRow, 0, 2);
        var gainRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        gainRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); gainRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gainRow.Controls.Add(gainLabel, 0, 0); gainRow.Controls.Add(gain, 1, 0);
        receiverCard.Controls.Add(gainRow, 0, 3); receiverCard.Controls.Add(warning, 0, 4);
        List<PlutoFound> offered = []; List<string> twice = [];
        void ShowReceiver()
        {
            Pick(pluto ? plutoButton : stick, pluto ? stick : plutoButton);
            plutoRow.Visible = pluto; gain.Visible = !pluto;
            gainLabel.Text = pluto ? "Gain · automatic" : $"RF gain · {gains[gain.Value]:0.0} dB";
            receiverHint.Text = pluto
                ? "ADALM-Pluto or a clone, on the network or on USB."
                : "Lower the gain if strong signals overload the stick, raise it for weak stations.";
            var notes = twice.Select(name => $"⚠ Two Plutos are called {name.Replace(".local", "")}: they are told apart by their address.").ToList();
            if (pluto && offered.Any(p => p.Uri.StartsWith("usb:"))) notes.Add("⚠ A Pluto on USB also needs its own 5 V supply on its PWR socket.");
            if (pluto && plutoBox.SelectedItem is PlutoItem { Pluto.Link: true }) notes.Add("⚠ This is the Pluto's slow USB network link: connect it directly instead.");
            warning.Text = pluto ? string.Join("\n", notes) : "";
        }
        async Task SearchPlutos()
        {
            plutoBox.Enabled = search.Enabled = false; plutoBox.Items.Clear(); plutoBox.Items.Add("Searching on USB and the network…"); plutoBox.SelectedIndex = 0;
            (offered, twice) = PlutoChoice.Offered(await PlutoChoice.SearchAsync(_root));
            if (dialog.IsDisposed) return;
            plutoBox.Items.Clear();
            foreach (var found in offered) plutoBox.Items.Add(new PlutoItem(found));
            var current = PlutoChoice.Choose(_saved.PlutoUri, _saved.PlutoName, _saved.PlutoSerial, _saved.PlutoAddress, offered);
            if (offered.Count == 0) { plutoBox.Items.Add("No Pluto found · check its power and its cable"); plutoBox.SelectedIndex = 0; }
            else plutoBox.SelectedIndex = Math.Max(0, offered.IndexOf(current));
            plutoBox.Enabled = offered.Count > 0; search.Enabled = true;
            ShowReceiver();
        }
        stick.Click += (_, _) => { pluto = false; ShowReceiver(); };
        plutoButton.Click += (_, _) => { pluto = true; ShowReceiver(); };
        gain.ValueChanged += (_, _) => ShowReceiver();
        plutoBox.SelectedIndexChanged += (_, _) => ShowReceiver();
        search.Click += async (_, _) => await SearchPlutos();
        dialog.Shown += async (_, _) => await SearchPlutos();
        ShowReceiver();

        // ---- Network stream: live reception to VLC on the home network
        bool udpEnabled = _saved.UdpEnabled;
        var udpOff = Choice("OFF"); var udpOn = Choice("ON");
        udpOff.Click += (_, _) => { udpEnabled = false; Pick(udpOff, udpOn); };
        udpOn.Click += (_, _) => { udpEnabled = true; Pick(udpOn, udpOff); };
        Pick(udpEnabled ? udpOn : udpOff, udpEnabled ? udpOff : udpOn);
        var destination = new TextBox { Text = _saved.UdpDestination, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11) };
        var vlc = Text("", 10);
        var copy = Choice("Copy");
        void RefreshAddress()
        {
            try { vlc.Text = "In VLC:  " + UdpStreamAddress.Vlc(destination.Text); vlc.ForeColor = MonitorColors.Text; }
            catch (ArgumentException) { vlc.Text = "Destination: udp://IPv4:port"; vlc.ForeColor = amber; }
        }
        destination.TextChanged += (_, _) => RefreshAddress(); RefreshAddress();
        copy.Click += (_, _) => { try { Clipboard.SetText(UdpStreamAddress.Vlc(destination.Text)); } catch (ArgumentException ex) { MessageBox.Show(dialog, ex.Message, "Network stream"); } };
        var vlcRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        vlcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); vlcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        vlcRow.Controls.Add(vlc, 0, 0); vlcRow.Controls.Add(copy, 1, 0);
        var networkCard = Card("Network stream", [46, 34, 40],
            "Sends live reception to the home network: a multicast group, or one PC's address. Allow VLC through that PC's firewall; guest Wi-Fi may block multicast.", out _);
        networkCard.Controls.Add(Row(udpOff, udpOn), 0, 1); networkCard.Controls.Add(destination, 0, 2); networkCard.Controls.Add(vlcRow, 0, 3);

        // ---- Header, the four cards, footer
        var apply = Choice("APPLY"); ((MonitorButton)apply).Selected = true;
        var about = Choice("ABOUT"); about.Click += (_, _) => ShowCredits(dialog);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        header.Controls.Add(Text("Settings · " + _band.Name, 18, true), 0, 0); header.Controls.Add(about, 1, 0); header.Controls.Add(apply, 2, 0);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 44)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        grid.Controls.Add(spectrumCard, 0, 0); grid.Controls.Add(lnbCard, 1, 0); grid.Controls.Add(receiverCard, 0, 1); grid.Controls.Add(networkCard, 1, 1);
        var top = new CheckBox { Text = "Always on top", Checked = TopMost, Dock = DockStyle.Fill, ForeColor = MonitorColors.Text, Font = new Font("Segoe UI", 10) };
        var cancel = Choice("Cancel"); cancel.DialogResult = DialogResult.Cancel; dialog.CancelButton = cancel;
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        footer.Controls.Add(top, 0, 0); footer.Controls.Add(cancel, 3, 0);
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14), BackColor = MonitorColors.Background };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); page.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); page.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        page.Controls.Add(header, 0, 0); page.Controls.Add(grid, 0, 1); page.Controls.Add(footer, 0, 2);
        dialog.Controls.Add(page);

        apply.Click += (_, _) =>
        {
            string udpDestination;
            try { udpDestination = $"udp://{UdpStreamAddress.Parse(destination.Text)}"; }
            catch (ArgumentException ex) { MessageBox.Show(dialog, ex.Message, "Network stream"); return; }
            var chosen = (plutoBox.SelectedItem as PlutoItem)?.Pluto;
            if (pluto && chosen == null) { MessageBox.Show(dialog, "Choose a Pluto, or the RTL-SDR stick.", "Receiver"); return; }
            _saved.Source = batc && _band.Key == "qo100" ? "BATC" : "Local";
            _saved.EffectiveLoMhz = (double)lo.Value; _saved.Gain = gains[gain.Value]; _saved.TopMost = TopMost = top.Checked;
            _saved.UdpEnabled = udpEnabled; _saved.UdpDestination = udpDestination;
            _receiver.UdpDestination = udpEnabled ? udpDestination : null;
            _saved.Device = pluto ? "Pluto" : "RTL";
            if (chosen != null && pluto)
            {
                _saved.PlutoUri = chosen.Uri; _saved.PlutoName = chosen.Name; _saved.PlutoSerial = chosen.Serial; _saved.PlutoAddress = chosen.Address;
            }
            _receiver.PlutoUri = pluto ? _saved.PlutoUri : null;
            _spectrum.Receiver = pluto ? "PLUTO" : "RTL-SDR";
            SaveSettings(); dialog.DialogResult = DialogResult.OK; dialog.Close();
        };
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
        dialog.TopMost = owner?.TopMost ?? TopMost;
        dialog.ShowInTaskbar = false;
        dialog.Shown += (_, _) => dialog.Activate();
        dialog.KeyPreview = true;
        dialog.KeyDown += (_, e) =>
        {
            if (closeOnEscape && e.KeyCode == Keys.Escape) { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
        };
        try { return dialog.ShowDialog(owner ?? this); }
        finally
        {
            if (!_closing && !(owner?.IsDisposed ?? false)) (owner ?? this).Activate();
        }
    }
    private void UpdateSound() { _sound.Text = _saved.Muted ? "🔇  MUTE" : $"🔊  {_saved.Volume}%"; _audio.Update(_saved.Volume, _saved.Muted); SaveSettings(); }
    private void ClearQuality() { _esn0.Text = "—"; _margin.Text = "D —"; _gauge.DbMargin = null; _gauge.Invalidate(); _errors.Text = ""; _eye.Report = null; _eye.Invalidate(); }
    private void EnsureBatc() { if (!_batcStarted && _saved.Source == "BATC" && _band.Key == "qo100") { _batcStarted = true; _batc.Start(); } }
    private void State(string text, Color color) { _state.Text = text; _state.ForeColor = color; }
    private void Placeholder(string text) { _placeholder.Text = text; _placeholder.Visible = true; _placeholder.BringToFront(); if (_embedded != IntPtr.Zero) ShowWindow(_embedded, 0); }
    private void ShowMessage(string text) { _spectrum.Message = text; _spectrum.Invalidate(); }
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
    private sealed record PlutoItem(PlutoFound Pluto) { public override string ToString() => Pluto.Label; }
    private sealed class Settings { public bool UdpEnabled { get; set; } public string UdpDestination { get; set; } = UdpStreamAddress.Default; public int Volume { get; set; } = 30; public bool Muted { get; set; } public bool TopMost { get; set; } = true; public double Gain { get; set; } = 40.2; [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public double? LnbMhz { get; set; } [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public double? DownconverterLoMhz { get; set; } [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public double FrequencyCorrectionKhz { get; set; } public double? EffectiveLoMhz { get; set; } public string Source { get; set; } = "BATC"; public string Band { get; set; } = "qo100"; public string Device { get; set; } = "RTL"; public string PlutoUri { get; set; } public string PlutoName { get; set; } public string PlutoSerial { get; set; } public string PlutoAddress { get; set; } }
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

