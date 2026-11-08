using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace DatvRx.Player;

// UI requests use RF MHz and kS/s. This backend either owns a local USB/decode
// pipeline or receives TS and diagnostics from the Pi over SSH. The spectrum
// and compact player controls are shared by both modes.
public record TuneRequest(double RfMhz, int SymbolRateKs, double LnbMhz = 9750, double GainDb = 40.2);

public interface IReceiverBackend : IAsyncDisposable
{
    event Action<string> Status;
    event Action<string> VideoInfo;
    Process PlayerProcess { get; }
    Task TuneAsync(TuneRequest request, int volume);
    Task StopAsync();
}

public sealed class LocalReceiver : IReceiverBackend
{
    private readonly string _root;
    private readonly string _msysBin;
    private readonly string _remoteHost;
    private readonly SemaphoreSlim _gate = new(1);
    private Session _session;
    public string UdpDestination { get; set; }
    // The ADALM-Pluto to receive with (usb:… or ip:…), or null for the RTL-SDR stick.
    public string PlutoUri { get; set; }
    public event Action<string> Status;
    public event Action<string> VideoInfo;
    public event Action<StreamService> ServiceInfo;
    public Process PlayerProcess => _session?.Player;

    private readonly object _recordGate = new();
    private FileStream _recordFile;
    private long _lastTransportTick;
    public long LastTransportTick => Interlocked.Read(ref _lastTransportTick);
    public bool IsRecording { get { lock (_recordGate) return _recordFile != null; } }
    public void StartRecording(string path)
    {
        lock (_recordGate)
        {
            if (_session?.Decoder == null || _session.Stop.IsCancellationRequested)
                throw new InvalidOperationException("Tune a live station before recording.");
            if (_recordFile != null) throw new InvalidOperationException("Already recording.");
            _recordFile = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
    }
    public void StopRecording()
    {
        lock (_recordGate)
        {
            var file = _recordFile; _recordFile = null;
            file?.Dispose();
        }
    }
    private void RecordTs(byte[] block)
    {
        lock (_recordGate)
        {
            try { _recordFile?.Write(block); }
            catch (IOException ex)
            {
                StopRecording();
                Status?.Invoke("Recording failed: " + ex.Message);
            }
        }
    }

    public LocalReceiver(string root, string remoteHost = null)
    {
        _root = root;
        _remoteHost = remoteHost;
        string bundled = Path.Combine(root, "runtime/bin");
        _msysBin = Directory.Exists(bundled) ? bundled : Path.Combine(Environment.GetEnvironmentVariable("DATVRX_MSYS_ROOT") ?? @"C:\msys64", "ucrt64/bin");
    }

    public static string Validate(TuneRequest request, bool pluto = false)
    {
        double intermediate = request.RfMhz - request.LnbMhz;
        if (!double.IsFinite(intermediate) || intermediate < (pluto ? 47 : 24) || intermediate > (pluto ? 6000 : 1766))
            return pluto ? "Frequency is outside the Pluto's range." : "Frequency is outside this RTL-SDR setup's range.";
        // The Pluto's gain is automatic; the stick's is set by hand.
        if (!pluto && (!double.IsFinite(request.GainDb) || request.GainDb < 0 || request.GainDb > 50))
            return "RF gain must be between 0 and 50 dB.";
        if (request.SymbolRateKs < 25 || request.SymbolRateKs > 1500)
            return "This setup supports 25–1500 kS/s. Choose a narrower signal.";
        return null;
    }

    public async Task TuneAsync(TuneRequest request, int volume)
    {
        string error = Validate(request, PlutoUri != null);
        if (error != null) throw new ArgumentException(error);
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            Interlocked.Exchange(ref _lastTransportTick, 0);
            // The Pluto's AD936x starts at about 2.08 MS/s.
            int sampleRate = PlutoUri != null || request.SymbolRateKs >= 1250 ? 2560000 : 2400000;
            long frequency = (long)Math.Round((request.RfMhz - request.LnbMhz) * 1e6);
            var session = new Session(Path.Combine(_root, "test-output"));
            _session = session;
            void Report(string line)
            {
                if (_session == session && !session.Stop.IsCancellationRequested) Status?.Invoke(line);
            }
            try
            {
                if (UdpDestination != null)
                {
                    try { session.Udp = new UdpTsSender(UdpDestination, Report); }
                    catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
                    { Report("UDP streaming: " + ex.Message); }
                }
                // Start consumers first, then USB. Each relay drains its source independently.
                session.Player = Start(Path.Combine(_msysBin, "ffplay.exe"),
                    ["-hide_banner", "-loglevel", "info", "-nostats", "-autoexit",
                     "-window_title", "RTL DATV Video", "-x", "800", "-y", "450",
                     "-volume", Math.Clamp(volume, 0, 100).ToString(), "-probesize", "1000000",
                     "-analyzeduration", "1000000", "-f", "mpegts", "-i", "pipe:0"], input: true);
                if (_remoteHost != null)
                {
                    string remoteCommand = "bash ~/projects/pi-datv-rx/scripts/linux/remote-receive.sh "
                        + frequency.ToString(CultureInfo.InvariantCulture) + " " + sampleRate + " " + request.SymbolRateKs;
                    session.Decoder = Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/OpenSSH/ssh.exe"),
                        ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", _remoteHost, remoteCommand], output: true);
                }
                else
                {
                    session.Decoder = Start(Path.Combine(_root, "build/windows/datvrx.exe"),
                    ["-", "--rate", (sampleRate / 1e6).ToString(CultureInfo.InvariantCulture),
                     "--format", "cu8", "--rs", (request.SymbolRateKs / 1000.0).ToString(CultureInfo.InvariantCulture),
                     "--std", "0", "--roll", "0.25", "--no-realtime", "--cells", "256"], input: true, output: true);
                session.Source = PlutoUri != null
                    ? Start(Path.Combine(_root, "build/windows/pluto_iq.exe"),
                        [PlutoUri, frequency.ToString(CultureInfo.InvariantCulture), sampleRate.ToString(), "auto", "--watch-stdin"], input: true, output: true)
                    : Start(Path.Combine(_root, "build/windows/rtl_iq.exe"),
                        [frequency.ToString(CultureInfo.InvariantCulture), sampleRate.ToString(), request.GainDb.ToString(CultureInfo.InvariantCulture), "--watch-stdin"], input: true, output: true);
                session.Tasks.Add(ReadLog(session, session.Source, "radio", line =>
                {
                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                        Report("Radio: " + line);
                }));
                    session.Tasks.Add(RelayIq(session));
                }
                session.Tasks.Add(ReadLog(session, session.Decoder, "receiver", Report));
                session.Tasks.Add(ReadLog(session, session.Player, "video", line =>
                {
                    if (_session == session && (line.Contains("Video:") || line.Contains("Audio:") || line.Contains("service_name") || line.Contains("service_provider"))) VideoInfo?.Invoke(line.Trim());
                }));
                session.Tasks.Add(RelayTs(session));
                session.Tasks.Add(Monitor(session));
                Report($"Tuning {request.RfMhz:0.000} MHz · {request.SymbolRateKs} kS/s · USB {sampleRate / 1e6:0.00} MSps");
            }
            catch
            {
                await StopCoreAsync();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private Process Start(string executable, string[] arguments, bool input = false, bool output = false)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException($"The program {Path.GetFileName(executable)} is missing from the DATVision folder. Extract the whole zip again; an antivirus program may have removed it.", executable);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root,
            RedirectStandardInput = input, RedirectStandardOutput = output, RedirectStandardError = true
        };
        info.Environment["PATH"] = _msysBin + ";" + Environment.GetEnvironmentVariable("PATH");
        // ffplay creates its SDL window before the UI can reparent it. Start
        // outside the desktop so the standalone window cannot flash on screen.
        if (Path.GetFileName(executable).Equals("ffplay.exe", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("-left"); info.ArgumentList.Add("-32000");
            info.ArgumentList.Add("-top"); info.ArgumentList.Add("-32000");
        }
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Could not start " + executable);
    }

    private async Task ReadLog(Session session, Process process, string name, Action<string> report)
    {
        try
        {
            using var log = new StreamWriter(Path.Combine(session.LogDirectory, "player-" + name + ".log"), false) { AutoFlush = true };
            while (await process.StandardError.ReadLineAsync(session.Stop.Token) is { } line)
            {
                await log.WriteLineAsync(line);
                report(line);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private async Task RelayIq(Session session)
    {
        try
        {
            await session.Source.StandardOutput.BaseStream.CopyToAsync(session.Decoder.StandardInput.BaseStream, 262144, session.Stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { try { session.Decoder.StandardInput.Close(); } catch { } }
    }

    private async Task RelayTs(Session session)
    {
        var service = new TransportInfo();
        service.Changed += info => { if (_session == session) ServiceInfo?.Invoke(info); };
        // USB reception must never stall while a player is probing or waiting for
        // a keyframe. Bound the video queue to ~4 MB and drop old video on overload.
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        var producer = Task.Run(async () =>
        {
            try
            {
                while (!session.Stop.IsCancellationRequested)
                {
                    var block = new byte[188 * 348];
                    int count = await session.Decoder.StandardOutput.BaseStream.ReadAsync(block, session.Stop.Token);
                    if (count == 0) break;
                    if (count != block.Length) Array.Resize(ref block, count);
                    Interlocked.Exchange(ref _lastTransportTick, Environment.TickCount64);
                    session.Udp?.Feed(block);
                    RecordTs(block);
                    service.Feed(block);
                    await queue.Writer.WriteAsync(block, session.Stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { queue.Writer.TryComplete(); }
        });
        try
        {
            await foreach (byte[] block in queue.Reader.ReadAllAsync(session.Stop.Token))
                await session.Player.StandardInput.BaseStream.WriteAsync(block, session.Stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            try { session.Player.StandardInput.Close(); } catch { }
            await producer;
        }
    }

    private async Task Monitor(Session session)
    {
        try
        {
            await Task.WhenAny(new[] { session.Source, session.Decoder, session.Player }
                .Where(process => process != null).Select(process => process.WaitForExitAsync(session.Stop.Token)));
            if (!session.Stop.IsCancellationRequested && _session == session)
            {
                Status?.Invoke(PlutoUri != null ? "Receiver pipeline stopped. Check the Pluto's power and cable, or reboot it, or select a station to retry."
                    : "Receiver pipeline stopped. Check the RTL-SDR connection or select a station to retry.");
                // Stop the whole owned pipeline if any stage fails.
                session.Stop.Cancel();
                StopRecording();
                session.Kill();
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var session = _session;
        _session = null;
        if (session == null) return;
        StopRecording();
        session.Stop.Cancel();
        if (_remoteHost == null && session.Source != null)
        {
            try
            {
                session.Source.StandardInput.Close();
                await session.Source.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException) { }
        }
        session.Kill();
        foreach (var process in new[] { session.Source, session.Decoder, session.Player })
            try { if (process != null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { await Task.WhenAll(session.Tasks).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        foreach (var process in new[] { session.Source, session.Decoder, session.Player }) process?.Dispose();
        if (session.Udp != null) await session.Udp.DisposeAsync();
        session.Stop.Dispose();
        // WinUSB may take a moment to release a terminated capture's handle.
        await Task.Delay(250);
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _gate.Dispose(); }

    public async Task PlayRecordingAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Recording not found", path);
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            var session = new Session(Path.Combine(_root, "test-output")); _session = session;
            try
            {
                session.Player = Start(Path.Combine(_msysBin, "ffplay.exe"),
                    ["-hide_banner", "-loglevel", "info", "-nostats", "-autoexit", "-volume", "100", "-window_title", "RTL DATV Video", "-i", Path.GetFullPath(path)]);
                session.Tasks.Add(ReadLog(session, session.Player, "video", line => { if (_session == session) VideoInfo?.Invoke(line.Trim()); }));
                session.Tasks.Add(Monitor(session));
                var parser = new TransportInfo();
                parser.Changed += info => ServiceInfo?.Invoke(info);
                using var file = File.OpenRead(path);
                byte[] buffer = new byte[Math.Min(file.Length, 188 * 10000)];
                int count = await file.ReadAsync(buffer);
                parser.Feed(buffer[..count]);
            }
            catch { await StopCoreAsync(); throw; }
        }
        finally { _gate.Release(); }
    }

    private sealed class Session
    {
        public Process Source, Decoder, Player;
        public UdpTsSender Udp;
        public CancellationTokenSource Stop { get; } = new();
        public List<Task> Tasks { get; } = [];
        public string LogDirectory { get; }
        public Session(string logDirectory) { LogDirectory = logDirectory; Directory.CreateDirectory(logDirectory); }
        public void Kill()
        {
            foreach (var process in new[] { Source, Decoder, Player })
                try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
