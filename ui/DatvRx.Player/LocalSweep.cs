using System.Diagnostics;
using System.Globalization;

namespace DatvRx.Player;

public sealed class LocalSweep(string root)
{
    private Process _process;
    private Task _reader, _errors;
    public event Action<MonitorFrame> Frame;
    public event Action<string> Status;
    // The Pluto to sweep with (usb:… or ip:…), or null for the RTL-SDR stick.
    public string PlutoUri { get; set; }
    const int PlutoGainDb = 50;   // fixed for the sweep, as on the Pi: an automatic gain would differ per step
    public async Task StartAsync(ReceiveBand band, double gain)
    {
        await StopAsync();
        double start = band.StartMhz - band.LoMhz, end = band.EndMhz - band.LoMhz;
        bool pluto = PlutoUri != null;
        if (start < (pluto ? 47 : 24) || end > (pluto ? 6000 : 1766) || start >= end) throw new ArgumentException("Band is outside the SDR's tuning range");
        string executable = Path.Combine(root, pluto ? "build/windows/pluto_sweep.exe" : "build/windows/rtl_sweep.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        string msys = Environment.GetEnvironmentVariable("DATVRX_MSYS_ROOT") ?? @"C:\msys64";
        string bundled = Path.Combine(root, "runtime/bin");
        info.Environment["PATH"] = (Directory.Exists(bundled) ? bundled : Path.Combine(msys, "ucrt64/bin")) + ";" + Environment.GetEnvironmentVariable("PATH");
        if (pluto) info.ArgumentList.Add(PlutoUri);
        foreach (string argument in new[] { Math.Round(start * 1e6).ToString(CultureInfo.InvariantCulture), Math.Round(end * 1e6).ToString(CultureInfo.InvariantCulture),
            (pluto ? PlutoGainDb : gain).ToString(CultureInfo.InvariantCulture), "--watch-stdin" }) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new IOException("Could not start the SDR scanner");
        _process = process;
        _reader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (_process != process) break;
                var parsed = Parse(line, band.LoMhz);
                if (parsed != null) Frame?.Invoke(parsed);
            }
        });
        _errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (_process == process) Status?.Invoke(line);
            if (_process == process) Status?.Invoke(PlutoUri != null ? "Scanner ended. Check the Pluto's power and cable, or reboot it."
                : "Scanner ended. Check that the RTL-SDR is connected to this PC.");
        });
    }
    public static MonitorFrame Parse(string line, double lo)
    {
        string[] pieces = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length != 5 || pieces[0] != "sweep" ||
            !double.TryParse(pieces[1], CultureInfo.InvariantCulture, out double first) ||
            !double.TryParse(pieces[2], CultureInfo.InvariantCulture, out double step) ||
            !int.TryParse(pieces[3], out int count) || count is < 16 or > 100000 || !double.IsFinite(first) || !double.IsFinite(step) || !double.IsFinite(lo) || step <= 0) return null;
        string[] samples = pieces[4].Split(',');
        if (samples.Length != count) return null;
        var values = new float[count];
        for (int i = 0; i < count; i++)
        { if (!float.TryParse(samples[i], CultureInfo.InvariantCulture, out float sample) || !float.IsFinite(sample)) return null; values[i] = sample / 10; }
        return new(first / 1e6 + lo, (first + step * (count - 1)) / 1e6 + lo, values, true);
    }
    public async Task StopAsync()
    {
        var process = _process; _process = null;
        if (process == null) return;
        try
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(_reader ?? Task.CompletedTask, _errors ?? Task.CompletedTask);
        }
        finally { process.Dispose(); }
    }
}
