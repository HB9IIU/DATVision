namespace DatvRx.Player;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--check-installation"))
        {
            string installRoot = FindRoot();
            string[] required = ["build/windows/datvrx.exe", "build/windows/rtl_iq.exe", "build/windows/rtl_sweep.exe", "runtime/bin/ffplay.exe"];
            var missing = required.Where(path => !File.Exists(Path.Combine(installRoot, path))).ToArray();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { application = "DATVision", root = installRoot,
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), missing }));
            Environment.ExitCode = missing.Length == 0 ? 0 : 1;
            return;
        }
        using var singleInstance = new Mutex(true, "HB9IIU.DatvRx.Player", out bool first);
        if (!first)
        {
            MessageBox.Show("DATVision is already running.", "DATVision");
            return;
        }
        string root = args.Length > 0 && !args[0].StartsWith("--") ? Path.GetFullPath(args[0]) : FindRoot();
        if (!File.Exists(Path.Combine(root, "build/windows/datvrx.exe")))
        {
            MessageBox.Show("Build the receiver first using scripts/windows/build.ps1.", "RTL DATV");
            return;
        }
        string recording = null;
        int recordingIndex = Array.IndexOf(args, "--recording");
        if (recordingIndex >= 0 && recordingIndex + 1 < args.Length) recording = args[recordingIndex + 1];
        Application.Run(args.Contains("--pi") ? new PlayerWindow(root, "pidatvmon") : new MonitorWindow(root, recording));
    }

    private static string FindRoot()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "build/windows/datvrx.exe")))
            return AppContext.BaseDirectory;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt"))) return directory.FullName;
            directory = directory.Parent;
        }
        return Environment.CurrentDirectory;
    }
}
