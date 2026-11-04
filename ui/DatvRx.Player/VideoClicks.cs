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
    private IntPtr _hook;
    public event Action Clicked;

    public VideoClicks(Control video)
    {
        _video = video; _proc = OnMouse;
        _hook = SetWindowsHookEx(MouseHook, _proc, GetModuleHandle(null), 0);
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
        if (_video.Visible && (window == _video.Handle || IsChild(_video.Handle, window))) Clicked?.Invoke();
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct HookData { public Point Point; public int MouseData, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, int thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr window);
}
