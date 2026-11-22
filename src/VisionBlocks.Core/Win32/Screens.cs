using System.Runtime.InteropServices;

namespace VisionBlocks.Core.Win32;

/// <summary>
/// 屏幕自动识别（2026-11-22）：换了电脑或显示器也能直接用。
/// 每次现场枚举，不缓存：插拔显示器、改缩放后再调一次就是新结果。坐标都是物理像素（进程声明了 PerMonitorV2）。
/// 排序和 BenchTarget 一致：主屏第一，其余按左边界。但对外用设备名（\\.\DISPLAY2）认屏，不靠序号。
/// </summary>
public static class Screens
{
    public sealed record Screen(string Device, WindowInfo.PxRect Bounds, WindowInfo.PxRect Work, bool Primary, uint Dpi)
    {
        public int ScalePercent => (int)Math.Round(Dpi * 100 / 96.0);
        public double WorkDipW => Work.W * 96.0 / Dpi;
        public double WorkDipH => Work.H * 96.0 / Dpi;

        /// <summary>相对主屏的位置：primary / right / left / above / below。</summary>
        public string Position(Screen primary)
        {
            if (Primary) return "primary";
            int cx = Bounds.X + Bounds.W / 2, cy = Bounds.Y + Bounds.H / 2;
            int px = primary.Bounds.X + primary.Bounds.W / 2, py = primary.Bounds.Y + primary.Bounds.H / 2;
            int dx = cx - px, dy = cy - py;
            return Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? "right" : "left") : (dy >= 0 ? "below" : "above");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rc, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private static Screen? Describe(IntPtr h)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(h, ref mi)) return null;
        if (GetDpiForMonitor(h, 0, out uint dpi, out _) != 0 || dpi == 0) dpi = 96;
        static WindowInfo.PxRect R(RECT r) => new(r.L, r.T, r.R - r.L, r.B - r.T);
        return new Screen(mi.szDevice, R(mi.rcMonitor), R(mi.rcWork), (mi.dwFlags & 1) != 0, dpi);
    }

    public static List<Screen> All()
    {
        var list = new List<Screen>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { var s = Describe(h); if (s != null) list.Add(s); return true; }, IntPtr.Zero);
        return list.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X).ToList();
    }

    public static Screen? UnderCursor() => GetCursorPos(out var p) ? Describe(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST)) : null;
    public static Screen? OfWindow(IntPtr hwnd) => Describe(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
}

/// <summary>
/// 编辑器窗口的初始尺寸：纯函数，输入是所在屏工作区（DIP），方便用假数据测（vbrun selftest-sizing）。
/// 规则：工作区不超过 1920 宽（大多数笔记本和你的主屏）→ 直接最大化；更宽 → 占约 85%×88%，居中。
/// 最小尺寸 1280×672，但不超过工作区本身；工作区比这还小时也是最大化，各卡片内部滚动。
/// </summary>
public static class WindowSizing
{
    public const double MinW = 1280, MinH = 672;
    public const double CompactBelowW = 1500;   // 窗口比这窄就用紧凑档
    public const double CompactBelowH = 800;    // 或者比这矮（宽屏笔记本横着放、窗口拉扁时）

    public sealed record Result(double W, double H, double MinW, double MinH, bool Maximize);

    public static Result Initial(double workW, double workH)
    {
        double minW = Math.Min(MinW, workW), minH = Math.Min(MinH, workH);
        if (workW <= 1920 || workW < MinW || workH < MinH) return new Result(workW, workH, minW, minH, true);
        double w = Math.Max(1920, Math.Round(workW * 0.85)), h = Math.Max(Math.Min(1080, workH), Math.Round(workH * 0.88));
        return new Result(Math.Min(w, workW), Math.Min(h, workH), minW, minH, false);
    }

    /// <summary>靶子（BenchTarget）的客户区尺寸：默认 1920×1080 物理像素；所选屏的工作区放不下时，取放得下的最大 16:9。</summary>
    public static (int W, int H) TargetClient(int workPxW, int workPxH)
    {
        const int marginW = 80, marginH = 140;   // 窗口边框、标题栏和离屏幕边的留白
        if (workPxW - marginW >= 1920 && workPxH - marginH >= 1080) return (1920, 1080);
        double w = Math.Min(workPxW - marginW, (workPxH - marginH) * 16.0 / 9.0);
        int ww = Math.Max(640, (int)(w / 16) * 16);
        return (ww, ww * 9 / 16);
    }
}
