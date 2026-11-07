using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DatvRx.Player;

// A text report to attach to a message when something does not work (About, "Save diagnostics").
// Nothing is sent anywhere. IP and MAC addresses, the user name and the computer name are removed before it is written.
internal static partial class DiagnosticsReport
{
    private static readonly string[] ProgramNames = ["DATVision", "DatvRx.Player", "datvrx", "rtl_iq", "rtl_sweep", "pluto_iq", "pluto_sweep", "pluto_find", "ffplay"];
    private static readonly string[] Files = ["build/windows/datvrx.exe", "build/windows/rtl_iq.exe", "build/windows/rtl_sweep.exe",
        "build/windows/pluto_iq.exe", "build/windows/pluto_sweep.exe", "build/windows/pluto_find.exe", "runtime/bin/ffplay.exe"];

    [GeneratedRegex(@"\b(\d{1,3}\.){3}\d{1,3}\b")] private static partial Regex Ipv4();
    [GeneratedRegex(@"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")] private static partial Regex Mac();

    // Writes the report to the desktop and returns its path.
    public static string Save(string root, Form window)
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string path = Path.Combine(folder, $"DATVision-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, Redact(Build(root, window)), new UTF8Encoding(false));
        return path;
    }

    private static string Redact(string text)
    {
        text = Ipv4().Replace(text, "x.x.x.x");
        text = Mac().Replace(text, "xx:xx:xx:xx:xx:xx");
        if (Environment.UserName.Length > 2) text = text.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
        if (Environment.MachineName.Length > 2) text = text.Replace(Environment.MachineName, "<host>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string Build(string root, Form window)
    {
        var report = new StringBuilder();
        void Section(string title) => report.Append("\n===== ").Append(title).Append(" =====\n");
        void Line(string text) => report.AppendLine(text);

        Line($"DATVision diagnostics, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line("(IP and MAC addresses, the computer name and the user name are removed)");

        Section("DATVision");
        Line("Version: " + Application.ProductVersion);
        Line("Installed in: " + root);

        Section("System");
        Line(RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture);
        Line(RuntimeInformation.FrameworkDescription);
        Line($"Language: {System.Globalization.CultureInfo.CurrentCulture.Name}, decimal separator \"{System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}\"");
        Line($"Processors: {Environment.ProcessorCount}, memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB, up {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm}");

        Section("Screens");
        if (window != null) Line($"DATVision window: {window.Bounds.Width}x{window.Bounds.Height}, display scale {window.DeviceDpi * 100 / 96}% ({window.DeviceDpi} dpi)");
        foreach (var screen in Screen.AllScreens)
            Line($"{screen.DeviceName}: {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.Primary ? " (main)" : "")}");

        Section("Program files");
        foreach (string relative in Files)
        {
            string file = Path.Combine(root, relative);
            Line(File.Exists(file) ? $"{relative}: {new FileInfo(file).Length / 1024} KB, {File.GetLastWriteTime(file):yyyy-MM-dd}" : $"{relative}: MISSING");
        }

        Section("Running programs (DATVision, receivers, video)");
        foreach (string name in ProgramNames)
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try { Line($"{process.ProcessName} (pid {process.Id}): started {process.StartTime:HH:mm:ss}, CPU {process.TotalProcessorTime.TotalSeconds:0} s, memory {process.WorkingSet64 / (1024 * 1024)} MB"); }
                    catch (Exception) { Line($"{process.ProcessName} (pid {process.Id})"); }
                }

        Section("USB devices (connected)");
        Line("(RTL-SDR sticks show as Bulk-In Interface or RTL2838; a Pluto as PlutoSDR or ADALM-PLUTO; WinUSB ones under USBDevice)");
        Line(Devices("USB")); Line(Devices("USBDevice"));

        Section("Settings (as saved)");
        string settings = Path.Combine(root, "windows-monitor-settings.json");
        Line(File.Exists(settings) ? string.Join('\n', File.ReadLines(settings).Take(40)) : "none saved yet");

        string logs = Path.Combine(root, "test-output");
        if (Directory.Exists(logs))
            foreach (var log in new DirectoryInfo(logs).GetFiles("*.log").OrderByDescending(file => file.LastWriteTime).Take(5))
            {
                Section($"Log: {log.Name} (last 60 lines, {log.LastWriteTime:yyyy-MM-dd HH:mm})");
                try
                {
                    using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var tail = new Queue<string>();
                    while (reader.ReadLine() is { } text) { tail.Enqueue(text.Length > 200 ? text[..200] : text); if (tail.Count > 60) tail.Dequeue(); }
                    foreach (string text in tail) Line(text);
                }
                catch (IOException) { Line("(could not be read)"); }
            }
        return report.ToString();
    }

    // One line per connected device of a class: its ID (with vendor and product), name, state and driver.
    private static string Devices(string deviceClass)
    {
        string raw = Run("pnputil.exe", $"/enum-devices /connected /class {deviceClass}", 600);
        var lines = new List<string>(); string id = "", name = "", status = "", driver = "";
        void Flush() { if (id.Length > 0) lines.Add($"{id} | {name} | {status} | {driver}"); id = name = status = driver = ""; }
        foreach (string line in raw.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (key == "Instance ID") { Flush(); id = value; }
            else if (key == "Device Description") name = value;
            else if (key == "Status") status = value;
            else if (key == "Driver Name") driver = value;
        }
        Flush();
        return lines.Count > 0 ? string.Join('\n', lines) : $"({deviceClass}: none listed)";
    }

    // Runs a small Windows tool and returns its first lines of output; empty if it is not available.
    private static string Run(string program, string arguments, int maxLines)
    {
        try
        {
            var info = new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(info);
            if (process == null) return "(not available)";
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10000)) { try { process.Kill(); } catch (Exception) { } return "(timed out)"; }
            return string.Join('\n', output.Result.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0).Take(maxLines));
        }
        catch (Exception) { return "(not available)"; }
    }
}
