using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace DatvRx.Player;

// UI requests use RF MHz and kS/s. This backend either owns a local USB/decode
// pipeline or receives TS and diagnostics from the Pi over SSH. The spectrum
// and compact player controls are shared by both modes.
public record TuneRequest(double RfMhz, int SymbolRateKs, double LnbMhz = 9750);

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
    public event Action<string> Status;
    public event Action<string> VideoInfo;
    public Process PlayerProcess => _session?.Player;

    public LocalReceiver(string root, string remoteHost = null)
    {
        _root = root;
        _remoteHost = remoteHost;
        _msysBin = Path.Combine(Environment.GetEnvironmentVariable("DATVRX_MSYS_ROOT") ?? @"C:\msys64", "ucrt64/bin");
    }

    public static string Validate(TuneRequest request)
    {
        double intermediate = request.RfMhz - request.LnbMhz;
        if (!double.IsFinite(intermediate) || intermediate < 24 || intermediate > 1766)
            return "Frequency is outside this RTL-SDR setup's range.";
        if (request.SymbolRateKs < 25 || request.SymbolRateKs > 1500)
            return "This setup supports 25–1500 kS/s. Choose a narrower signal.";
        return null;
    }

    public async Task TuneAsync(TuneRequest request, int volume)
    {
        string error = Validate(request);
        if (error != null) throw new ArgumentException(error);
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            int sampleRate = request.SymbolRateKs >= 1250 ? 2560000 : 2400000;
            long frequency = (long)Math.Round((request.RfMhz - request.LnbMhz) * 1e6);
            var session = new Session(Path.Combine(_root, "test-output"));
            _session = session;
            void Report(string line)
            {
                if (_session == session && !session.Stop.IsCancellationRequested) Status?.Invoke(line);
            }
            try
            {
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
                     "--std", "0", "--roll", "0.25", "--no-realtime"], input: true, output: true);
                session.Source = Start(Path.Combine(_root, "build/windows/rtl_iq.exe"),
                    [frequency.ToString(CultureInfo.InvariantCulture), sampleRate.ToString(), "40.2"], output: true);
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
                    if (_session == session && (line.Contains("Video:") || line.Contains("service_name"))) VideoInfo?.Invoke(line.Trim());
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
        if (!File.Exists(executable)) throw new FileNotFoundException("Required program not found", executable);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root,
            RedirectStandardInput = input, RedirectStandardOutput = output, RedirectStandardError = true
        };
        info.Environment["PATH"] = _msysBin + ";" + Environment.GetEnvironmentVariable("PATH");
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
                Status?.Invoke("Reception stopped. Check USB connection or click Tune to retry.");
                // Stop the whole owned pipeline if any stage fails.
                session.Stop.Cancel();
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
        session.Stop.Cancel();
        session.Kill();
        foreach (var process in new[] { session.Source, session.Decoder, session.Player })
            try { if (process != null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { await Task.WhenAll(session.Tasks).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        foreach (var process in new[] { session.Source, session.Decoder, session.Player }) process?.Dispose();
        session.Stop.Dispose();
        // WinUSB may take a moment to release a terminated capture's handle.
        await Task.Delay(250);
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _gate.Dispose(); }

    private sealed class Session
    {
        public Process Source, Decoder, Player;
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
