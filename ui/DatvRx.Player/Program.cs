namespace DatvRx.Player;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(true, "HB9IIU.DatvRx.Player", out bool first);
        if (!first)
        {
            MessageBox.Show("The RTL DATV Player is already running.", "RTL DATV");
            return;
        }
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRoot();
        if (!File.Exists(Path.Combine(root, "build/windows/datvrx.exe")))
        {
            MessageBox.Show("Build the receiver first using scripts/windows/build.ps1.", "RTL DATV");
            return;
        }
        Application.Run(new PlayerWindow(root));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt"))) return directory.FullName;
            directory = directory.Parent;
        }
        return Environment.CurrentDirectory;
    }
}
