using System.Runtime.InteropServices;

namespace DatvRx.Player;

// Clicks on the video. ffplay draws it in its own, disabled window inside ours, so a
// click on the picture neither reaches DATVision nor brings it to the front. A
// low-level mouse hook sees every click; the check then asks which window is under
// the mouse, in DATVision's own coordinates (the hook's may differ with display
// scaling), and reports clicks inside the video area of one of our windows.
internal sealed class VideoClicks : IDisposable
{
    private const int MouseHook = 14, LeftButtonUp = 0x0202, RootWindow = 2;
    private readonly Control _video;
    private readonly Func<IntPtr, bool> _ours;
    private readonly HookProc _proc;   // kept referenced: the hook calls it from native code
    private readonly string _logPath;
    private IntPtr _hook;
    private long _lastTick;
    private Point _lastPoint;
    public event Action<bool> Clicked;   // true for the second click of a double click

    public VideoClicks(Control video, Func<IntPtr, bool> ours, string logPath)
    {
        _video = video; _ours = ours; _proc = OnMouse; _logPath = logPath;
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
        if (code >= 0 && message == LeftButtonUp && _video.IsHandleCreated) _video.BeginInvoke(Check);
        return CallNextHookEx(_hook, code, message, data);
    }

    private void Check()
    {
        var point = Cursor.Position;   // in the same coordinates as RectangleToScreen
        var area = _video.RectangleToScreen(_video.ClientRectangle);
        bool ours = _ours(GetAncestor(WindowFromPoint(point), RootWindow));
        Log($"click at {point.X},{point.Y} · video {area} visible {_video.Visible} · our window {ours}");
        if (!_video.Visible || !ours || !area.Contains(point)) return;
        long now = Environment.TickCount64;
        var size = SystemInformation.DoubleClickSize;
        bool second = now - _lastTick <= SystemInformation.DoubleClickTime &&
            Math.Abs(point.X - _lastPoint.X) <= size.Width && Math.Abs(point.Y - _lastPoint.Y) <= size.Height;
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
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, int flags);
}
