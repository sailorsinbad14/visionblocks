using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Overlay;

/// <summary>
/// 置顶状态胶囊（2026-11-22）。运行时放在靶子所在屏幕的顶部中间。
/// 和遮罩不同：不管前台是谁都显示——拦截恰好发生在前台被别的窗口抢走的时候，那时遮罩是隐藏的。
/// 窗口属性同遮罩：置顶、不进任务栏、不激活、鼠标穿透（TRANSPARENT|LAYERED|TOOLWINDOW|NOACTIVATE），所以既不抢焦点也挡不住点击。
/// 截图用 WGC 抓的是靶子窗口自己的内容，盖在上面的胶囊不会被识别进去。
/// </summary>
public sealed class StatusPill : Window
{
    public enum Kind { Running, Paused, Blocked, Stopped, Done }

    private readonly IntPtr _target;
    private readonly Border _pill;
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sub = new() { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private IntPtr _hwnd;
    private readonly bool _dark;

    /// <param name="dark">编辑器用深色主题时胶囊也用深色。被拦截、急停的红色两种主题一样。</param>
    public StatusPill(IntPtr targetHwnd, bool dark = false)
    {
        _target = targetHwnd;
        _dark = dark;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "VisionBlocks Status";
        Left = -32000; Top = -32000;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_title);
        row.Children.Add(_sub);
        _pill = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(14, 8, 16, 8), BorderThickness = new Thickness(1), Child = row, Margin = new Thickness(6) };
        Content = _pill;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
        SizeChanged += (_, _) => Place();
        // 靶子窗口被挪到另一块屏时跟过去
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Place();
        t.Start();
        Closed += (_, _) => t.Stop();
    }

    private static SolidColorBrush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    public void SetState(Kind kind, string title, string sub)
    {
        var (bg, border, fg, subFg, dot) = (kind, _dark) switch
        {
            (Kind.Running, false) => (B("#DDF5E6"), B("#17864A"), B("#1C1F2E"), B("#575F78"), B("#17864A")),
            (Kind.Paused, false) => (B("#FFF0CC"), B("#A86A00"), B("#1C1F2E"), B("#575F78"), B("#A86A00")),
            (Kind.Running, true) => (B("#17352A"), B("#3CCB7F"), B("#E8EBF2"), B("#A9B0C2"), B("#3CCB7F")),
            (Kind.Paused, true) => (B("#3A2F12"), B("#E0A100"), B("#E8EBF2"), B("#A9B0C2"), B("#E0A100")),
            (Kind.Blocked or Kind.Stopped, _) => (B("#D23B3B"), B("#D23B3B"), Brushes.White, B("#FFE1DE"), Brushes.White),
            (_, false) => (B("#FFFFFF"), B("#C9CEDB"), B("#1C1F2E"), B("#575F78"), B("#8A91A6")),
            (_, true) => (B("#1B1F29"), B("#3D4558"), B("#E8EBF2"), B("#A9B0C2"), B("#7C8499")),
        };
        _pill.Background = bg; _pill.BorderBrush = border;
        _title.Foreground = fg; _sub.Foreground = subFg; _dot.Fill = dot;
        _title.Text = title; _sub.Text = sub;
        _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>放到靶子所在屏幕工作区的顶部中间（物理像素，直接 SetWindowPos，避开 WPF 在混合 DPI 下换算 Left/Top 的问题）。</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var scr = WindowInfo.IsAlive(_target) ? Screens.OfWindow(_target) : null;
        scr ??= Screens.All().FirstOrDefault();
        if (scr == null || !GetWindowRect(_hwnd, out var r)) return;
        int w = r.R - r.L, h = r.B - r.T;
        int x = scr.Work.X + (scr.Work.W - w) / 2, y = scr.Work.Y + (int)Math.Round(10 * scr.Dpi / 96.0);
        if (x != r.L || y != r.T) SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
}
