using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Overlay;

/// <summary>
/// 盖在目标窗口客户区上的调试遮罩。窗口属性组合参照开源项目 BetterGI 遮罩窗口的公开做法
/// （AllowsTransparency、不激活、不进任务栏、置顶，扩展样式加 TRANSPARENT|LAYERED|TOOLWINDOW），另加 NOACTIVATE。
/// 绘制按自己的节拍（30Hz）合并刷新，运行器只替换场景，不跨线程逐个画框。
/// 只有前台是目标进程或本进程时才显示，避免盖在别的窗口上。
/// </summary>
public sealed class OverlayWindow : Window, IOverlaySink
{
    private readonly IntPtr _target;
    private readonly uint _targetPid;
    private readonly int _baseW;
    private readonly OverlayCanvas _canvas;
    private OverlayScene _scene = OverlayScene.Empty;
    private OverlayScene? _drawn;
    private IntPtr _hwnd;
    private WindowInfo.PxRect _lastRect;

    public OverlayWindow(IntPtr targetHwnd, int baseW = 1920)
    {
        _target = targetHwnd;
        _targetPid = WindowInfo.ProcessIdOf(targetHwnd);
        _baseW = baseW;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "VisionBlocks Overlay";
        Width = 10; Height = 10; Left = -32000; Top = -32000;
        _canvas = new OverlayCanvas(baseW);
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void SetScene(OverlayScene scene) => Volatile.Write(ref _scene, scene);

    /// <summary>约每秒一次：遮罩是否可见、遮罩窗口矩形、目标客户区矩形（都是物理像素）。用来事后判断遮罩有没有对齐。</summary>
    public event Action<bool, WindowInfo.PxRect, WindowInfo.PxRect>? StateSampled;
    private int _ticks;

    private void Sample(WindowInfo.PxRect client)
    {
        if (++_ticks % 30 != 0 || StateSampled == null) return;
        GetWindowRect(_hwnd, out var wr);
        bool vis = IsWindowVisible(_hwnd);
        StateSampled(vis, new WindowInfo.PxRect(wr.L, wr.T, wr.R - wr.L, wr.B - wr.T), client);
    }

    private void Tick()
    {
        if (_hwnd == IntPtr.Zero) return;
        uint fgPid = WindowInfo.ProcessIdOf(WindowInfo.Foreground());
        bool show = WindowInfo.IsAlive(_target) && !WindowInfo.IsMinimized(_target)
                    && (fgPid == _targetPid || fgPid == (uint)Environment.ProcessId);
        bool hasClient = WindowInfo.TryGetClientOnScreen(_target, out var r) && r.W > 0;
        if (!show || !hasClient)
        {
            if (IsVisible) ShowWindow(_hwnd, SW_HIDE);
            Sample(r);
            return;
        }
        if (!IsVisible || r != _lastRect)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, r.X, r.Y, r.W, r.H, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _lastRect = r;
        }
        Sample(r);
        var scene = Volatile.Read(ref _scene);
        if (!ReferenceEquals(scene, _drawn))
        {
            _drawn = scene;
            _canvas.Scene = scene;
            _canvas.InvalidateVisual();
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const int SW_HIDE = 0;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    /// <summary>在一个 STA 线程上创建遮罩并跑消息循环（命令行程序用）。</summary>
    public static OverlayWindow StartOnOwnThread(IntPtr targetHwnd, int baseW = 1920)
    {
        var tcs = new TaskCompletionSource<OverlayWindow>();
        var th = new Thread(() =>
        {
            var w = new OverlayWindow(targetHwnd, baseW);
            w.Show();
            tcs.SetResult(w);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "overlay" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task.Result;
    }

    public void CloseFromAnyThread() => Dispatcher.BeginInvoke(() => { Close(); Dispatcher.InvokeShutdown(); });
}

internal sealed class OverlayCanvas : FrameworkElement
{
    private readonly int _baseW;
    public OverlayScene Scene = OverlayScene.Empty;
    private static readonly Pen RegionPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(220, 80, 200, 220)), 3) { DashStyle = DashStyles.Dash });
    private static readonly Pen MatchPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(70, 220, 120)), 4));
    private static readonly Pen MissPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, 240, 160, 40)), 2));
    private static readonly Pen PointPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(250, 210, 60)), 3));
    private static readonly Pen ArrowPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 140, 40)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Brush RunDot = Frozen(new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)));
    private static readonly Brush PausedDot = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x3D)));
    private static readonly Brush BadDot = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)));
    private static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(190, 10, 20, 28)));
    private static readonly Typeface Face = new("Microsoft YaHei UI");
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public OverlayCanvas(int baseW) { _baseW = baseW; IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0) return;
        double s = ActualWidth / _baseW;          // 与 NormFrame 一致：按宽度比例
        dc.PushTransform(new ScaleTransform(s, s));
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var b in Scene.Boxes)
        {
            var r = new Rect(b.Box.X, b.Box.Y, b.Box.W, b.Box.H);
            if (b.Kind is "scrollUp" or "scrollDown")
            {
                // 滚动箭头（2026-11-22）：竖线 + 箭头，箭头朝内容移动的方向
                bool up = b.Kind == "scrollUp";
                var tip = new Point(r.X, up ? r.Top : r.Bottom);
                var tail = new Point(r.X, up ? r.Bottom : r.Top);
                dc.DrawLine(ArrowPen, tail, tip);
                double d = up ? 18 : -18;
                dc.DrawLine(ArrowPen, tip, new Point(tip.X - 12, tip.Y + d));
                dc.DrawLine(ArrowPen, tip, new Point(tip.X + 12, tip.Y + d));
                dc.DrawEllipse(ArrowPen.Brush, null, tail, 6, 6);
                r = new Rect(r.X - 8, r.Y, 0, r.Height);   // 标签贴着箭头上端
            }
            else
            {
                var pen = b.Kind switch { "match" => MatchPen, "miss" => MissPen, "point" => PointPen, _ => RegionPen };
                dc.DrawRectangle(null, pen, r);
            }
            if (!string.IsNullOrEmpty(b.Label))
            {
                var ft = new FormattedText(b.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 20, Brushes.White, dip);
                // 滚动箭头靠热区右边，标签往左放，免得出窗口
                double lx = b.Kind is "scrollUp" or "scrollDown" ? Math.Max(0, r.X - ft.Width - 12) : r.X;
                var bg = new Rect(lx, Math.Max(0, r.Y - 28), ft.Width + 12, 26);
                dc.DrawRectangle(LabelBg, null, bg);
                dc.DrawText(ft, new Point(bg.X + 6, bg.Y + 2));
            }
        }
        if (!string.IsNullOrEmpty(Scene.Status))
        {
            var ft = new FormattedText("VisionBlocks · " + Scene.Status, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 24, Brushes.White, dip);
            var bg = new Rect(16, 1000, ft.Width + 48, 38);
            dc.DrawRoundedRectangle(LabelBg, null, bg, 8, 8);
            // 2026-11-22：角标加状态色点（暂停黄、截不到画面红、其余绿）。遮罩只在靶子或编辑器在前台时显示，所以这里不会出现「拦截」，拦截靠置顶胶囊
            var dot = Scene.Status.StartsWith("已暂停", StringComparison.Ordinal) ? PausedDot : Scene.Status.StartsWith("截不到画面", StringComparison.Ordinal) ? BadDot : RunDot;
            dc.DrawEllipse(dot, null, new Point(34, 1019), 8, 8);
            dc.DrawText(ft, new Point(52, 1004));
        }
        dc.Pop();
    }
}
