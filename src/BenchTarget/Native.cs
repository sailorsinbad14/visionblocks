using System.Runtime.InteropServices;

namespace BenchTarget;

/// <summary>靶子只需要少量 Win32：把客户区固定成指定的物理像素尺寸，并放到指定显示器上。</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern bool AdjustWindowRectExForDpi(ref RECT rc, uint style, bool menu, uint exStyle, uint dpi);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public sealed record MonitorDesc(IntPtr Handle, RECT Bounds, RECT Work, bool Primary, uint Dpi, string Device);

    /// <summary>主屏排第一，其余按左边界排序。</summary>
    public static List<MonitorDesc> Monitors()
    {
        var list = new List<MonitorDesc>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(h, ref mi);
            GetDpiForMonitor(h, 0, out uint dpi, out _);
            list.Add(new MonitorDesc(h, mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0, dpi, mi.szDevice ?? ""));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ToList();
    }

    /// <summary>
    /// 按参数选屏（2026-11-22）：给了 --monitor-device 就按设备名找，找不到返回 null（调用方报错退出，退出码 3）；
    /// 否则按 --monitor 序号，越界时落回最近的一块（vbrun 的老行为）。
    /// </summary>
    public static MonitorDesc? Pick(TargetOptions o)
    {
        var mons = Monitors();
        if (!string.IsNullOrEmpty(o.MonitorDevice))
            return mons.FirstOrDefault(m => string.Equals(m.Device, o.MonitorDevice, StringComparison.OrdinalIgnoreCase));
        return mons[Math.Clamp(o.Monitor - 1, 0, mons.Count - 1)];
    }
}
