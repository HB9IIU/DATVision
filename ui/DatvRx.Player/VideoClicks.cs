using System.Runtime.InteropServices;

namespace DatvRx.Player;

// Clicks on the video. ffplay draws it in its own, disabled window inside ours, so a
// click on the picture neither reaches DATVision nor brings it to the front. A
// low-level mouse hook sees every click; Windows then names the window at that
// physical point, skipping ffplay's disabled one, and a click counts when that is
// our video area or something in it. No coordinates are compared, so display
// scaling (150 % on the test PC) cannot get in the way.
internal sealed class VideoClicks : IDisposable
{
    private const int MouseHook = 14, LeftButtonUp = 0x0202;
    private readonly Control _video;
    private readonly HookProc _proc;   // kept referenced: the hook calls it from native code
    private readonly string _logPath;
    private IntPtr _hook;
    private long _lastTick;
    private Point _lastPoint;
    public event Action<bool> Clicked;   // true for the second click of a double click

    public VideoClicks(Control video, string logPath)
    {
        _video = video; _proc = OnMouse; _logPath = logPath;
        _hook = SetWindowsHookEx(MouseHook, _proc, GetModuleHandle(null), 0);
        Log(_hook == IntPtr.Zero ? $"hook FAILED, error {Marshal.GetLastWin32Error()}" : "hook installed");
    }

    // Diagnosis: what each click looked like (player-clicks.log next to the other logs).
    private void Log(string line)
    {
        try { File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch (IOException) { }
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message == LeftButtonUp && _video.IsHandleCreated)
        {
            var point = Marshal.PtrToStructure<HookData>(data).Point;   // physical screen pixels
            _video.BeginInvoke(() => Check(point));
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void Check(Point point)
    {
        var window = WindowFromPhysicalPoint(point);
        bool inVideo = _video.Visible && (window == _video.Handle || IsChild(_video.Handle, window));
        Log($"click at {point.X},{point.Y} (physical) · on the video {inVideo}");
        if (!inVideo) return;
        long now = Environment.TickCount64;
        var size = SystemInformation.DoubleClickSize;
        bool second = now - _lastTick <= SystemInformation.DoubleClickTime &&
            Math.Abs(point.X - _lastPoint.X) <= size.Width * 2 && Math.Abs(point.Y - _lastPoint.Y) <= size.Height * 2;
        _lastTick = second ? 0 : now; _lastPoint = point;
        Clicked?.Invoke(second);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; Log("hook removed"); }
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct HookData { public Point Point; public int MouseData, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, int thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr window);
}
