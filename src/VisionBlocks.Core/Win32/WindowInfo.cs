using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace VisionBlocks.Core.Win32;

/// <summary>窗口几何与前台判断。进程声明了 PerMonitorV2，这里拿到的都是物理像素。</summary>
public static unsafe class WindowInfo
{
    public readonly record struct PxRect(int X, int Y, int W, int H)
    {
        public int Right => X + W;
        public int Bottom => Y + H;
    }

    /// <summary>客户区在屏幕上的位置和大小（物理像素）。</summary>
    public static bool TryGetClientOnScreen(IntPtr hwnd, out PxRect rect)
    {
        rect = default;
        var h = (HWND)hwnd;
        if (!PInvoke.IsWindow(h)) return false;
        if (!PInvoke.GetClientRect(h, out RECT rc)) return false;
        var origin = new System.Drawing.Point(0, 0);
        if (!PInvoke.ClientToScreen(h, ref origin)) return false;
        rect = new PxRect(origin.X, origin.Y, rc.right - rc.left, rc.bottom - rc.top);
        return true;
    }

    /// <summary>DWM 的可见边界（不含不可见的缩放边框）。WGC 对窗口截到的就是这块。</summary>
    public static bool TryGetVisibleBounds(IntPtr hwnd, out PxRect rect)
    {
        rect = default;
        RECT r;
        var hr = PInvoke.DwmGetWindowAttribute((HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT));
        if (hr.Failed) return false;
        rect = new PxRect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        return true;
    }

    /// <summary>客户区在「可见边界」里的偏移，用来从 WGC 帧里裁出客户区。</summary>
    public static bool TryGetClientInVisibleBounds(IntPtr hwnd, out PxRect crop)
    {
        crop = default;
        if (!TryGetClientOnScreen(hwnd, out var c) || !TryGetVisibleBounds(hwnd, out var b)) return false;
        crop = new PxRect(c.X - b.X, c.Y - b.Y, c.W, c.H);
        return true;
    }

    public static bool IsMinimized(IntPtr hwnd) => PInvoke.IsIconic((HWND)hwnd);
    public static bool IsAlive(IntPtr hwnd) => PInvoke.IsWindow((HWND)hwnd);
    public static uint DpiOf(IntPtr hwnd) => PInvoke.GetDpiForWindow((HWND)hwnd);

    public static IntPtr Foreground() => (IntPtr)PInvoke.GetForegroundWindow().Value;

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        uint pid = 0;
        PInvoke.GetWindowThreadProcessId((HWND)hwnd, &pid);
        return pid;
    }

    public static bool TrySetForeground(IntPtr hwnd) => PInvoke.SetForegroundWindow((HWND)hwnd);

    public static string TitleOf(IntPtr hwnd)
    {
        int len = PInvoke.GetWindowTextLength((HWND)hwnd);
        if (len <= 0) return "";
        Span<char> buf = stackalloc char[len + 1];
        fixed (char* p = buf)
        {
            int n = PInvoke.GetWindowText((HWND)hwnd, p, len + 1);
            return new string(p, 0, n);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    private const uint GA_ROOTOWNER = 3;

    /// <summary>窗口的根所有者（对话框、弹出窗口会追到拥有它的主窗口）。前台守卫用它放行目标窗口自己弹出的对话框。</summary>
    public static IntPtr RootOwner(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOTOWNER);

    public sealed record TopWindow(IntPtr Hwnd, uint Pid, string ProcessName, string Title);

    /// <summary>列出可见的顶层窗口，编辑器的「绑定窗口」下拉框用。</summary>
    public static List<TopWindow> ListTopWindows()
    {
        var list = new List<TopWindow>();
        PInvoke.EnumWindows((h, _) =>
        {
            if (!PInvoke.IsWindowVisible(h)) return true;
            string title = TitleOf(h);
            if (string.IsNullOrWhiteSpace(title)) return true;
            uint pid = ProcessIdOf(h);
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
            list.Add(new TopWindow(h, pid, name, title));
            return true;
        }, 0);
        return list;
    }

    /// <summary>虚拟桌面（所有显示器拼起来）的范围，绝对坐标鼠标移动要用。</summary>
    public static PxRect VirtualScreen() => new(
        PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);

    /// <summary>0 = 不感知，1 = 系统级，2 = 每显示器。用来自检 manifest 是否生效。</summary>
    public static int CurrentDpiAwareness() => GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
}
