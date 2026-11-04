using System.Runtime.InteropServices;

namespace DatvRx.Player;

// Clicks on the video. ffplay draws it in its own window inside ours, so a click on
// the picture reaches ffplay rather than DATVision; a low-level mouse hook sees every
// click and reports those inside the video area while DATVision is in front.
internal sealed class VideoClicks : IDisposable
{
    private const int MouseHook = 14, LeftButtonUp = 0x0202;
    private readonly Control _video;
    private readonly Func<bool> _inFront;
    private readonly HookProc _proc;   // kept referenced: the hook calls it from native code
    private IntPtr _hook;
    private long _lastTick;
    private Point _lastPoint;
    public event Action<bool> Clicked;   // true for the second click of a double click

    private readonly string _logPath;

    public VideoClicks(Control video, Func<bool> inFront, string logPath)
    {
        _video = video; _inFront = inFront; _proc = OnMouse; _logPath = logPath;
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
        if (code >= 0 && message == LeftButtonUp)
        {
            var point = Marshal.PtrToStructure<HookData>(data).Point;
            // Hook calls arrive on the UI thread's message loop; checks are cheap.
            var area = _video.IsHandleCreated ? _video.RectangleToScreen(_video.ClientRectangle) : Rectangle.Empty;
            bool front = _inFront();
            Log($"click at {point.X},{point.Y} · video {area} visible {_video.Visible} · DATVision in front {front}");
            if (_video.Visible && _video.IsHandleCreated && front && area.Contains(point))
            {
                long now = Environment.TickCount64;
                var size = SystemInformation.DoubleClickSize;
                bool second = now - _lastTick <= SystemInformation.DoubleClickTime &&
                    Math.Abs(point.X - _lastPoint.X) <= size.Width && Math.Abs(point.Y - _lastPoint.Y) <= size.Height;
                _lastTick = second ? 0 : now; _lastPoint = point;
                _video.BeginInvoke(() => Clicked?.Invoke(second));
            }
        }
        return CallNextHookEx(_hook, code, message, data);
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
}
