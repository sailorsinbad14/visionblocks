using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionBlocks.Core.Model;

namespace VisionBlocks.Studio;

/// <summary>
/// 颜色范围调色盘：
/// 色相条拖两个把手调 H 的下限、上限（下限拖过上限 = 跨 0 的红色）；S×V 方块拖框的边、角或整体调 S、V。
/// 口径和运行时一致（OpenCV：H 0–179，S、V 0–255）。两个控件只改 ColorDef，属性卡负责同步数字框、画布高亮和占比。
/// </summary>
internal sealed class HueBar : FrameworkElement
{
    private readonly ColorDef _c;
    private int _drag = -1;   // 0 = 下限把手，1 = 上限把手
    public event Action? Changed, Committed;
    private const double Pad = 9, BarTop = 4, BarH = 24;

    public HueBar(ColorDef c) { _c = c; Height = 54; Focusable = true; Cursor = Cursors.Hand; }

    private static readonly Brush Spectrum = MakeSpectrum();
    private static Brush MakeSpectrum()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) g.GradientStops.Add(new GradientStop(Hsv.ToColor(i * 30, 255, 255), i / 6.0));
        g.Freeze();
        return g;
    }

    private double X(double h) => Pad + h / 179.0 * (ActualWidth - 2 * Pad);
    private int H(double x) => (int)Math.Round(Math.Clamp((x - Pad) / (ActualWidth - 2 * Pad) * 179, 0, 179));

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2 * Pad + 10) return;
        var bar = new Rect(Pad, BarTop, ActualWidth - 2 * Pad, BarH);
        dc.DrawRoundedRectangle(Spectrum, null, bar, 4, 4);
        var dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        int lo = _c.Lower[0], hi = _c.Upper[0];
        var segs = lo <= hi ? new[] { (lo, hi) } : new[] { (0, hi), (lo, 179) };
        // 范围外压暗
        double last = 0;
        foreach (var (a, b) in segs.OrderBy(s => s.Item1)) { if (a > last) dc.DrawRectangle(dim, null, new Rect(X(last), BarTop, X(a) - X(last), BarH)); last = b; }
        if (last < 179) dc.DrawRectangle(dim, null, new Rect(X(last), BarTop, X(179) - X(last), BarH));
        var ink = (Brush)Ui.Theme["Ink"];
        foreach (var (a, b) in segs) dc.DrawRectangle(null, new Pen(ink, 2), new Rect(X(a), BarTop - 1, Math.Max(2, X(b) - X(a)), BarH + 2));
        var face = new Typeface((FontFamily)Ui.Theme["UiFont"], FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        foreach (var (h, label) in new[] { (lo, L.En ? "L" : "下"), (hi, L.En ? "U" : "上") })
        {
            var r = new Rect(X(h) - 8, BarTop + BarH + 2, 16, 20);
            dc.DrawRoundedRectangle((Brush)Ui.Theme["CardBg"], new Pen(ink, 1.5), r, 4, 4);
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 11, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        int h = H(e.GetPosition(this).X);
        _drag = Math.Abs(h - _c.Lower[0]) <= Math.Abs(h - _c.Upper[0]) ? 0 : 1;
        CaptureMouse();
        OnMouseMove(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        int h = H(e.GetPosition(this).X);
        if (_drag == 0) _c.Lower[0] = h; else _c.Upper[0] = h;
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        Committed?.Invoke();
    }
}

internal sealed class SvBox : FrameworkElement
{
    private readonly ColorDef _c;
    private WriteableBitmap? _bg;
    private int _bgHue = -1;
    private int[]? _density;   // 64×64：所选区域里色相在范围内的像素，按 S（横）、V（竖）落格计数
    public event Action? Changed, Committed;
    private const double Pad = 8;
    private (bool L, bool R, bool T, bool B, bool Move, int S0, int V0, int[] Lo, int[] Hi)? _drag;

    public SvBox(ColorDef c) { _c = c; Height = 200; Focusable = true; Cursor = Cursors.Cross; }

    /// <summary>更新像素分布（调用方从冻结帧的所选区域里取样）。</summary>
    public void SetDensity(int[]? d) { _density = d; InvalidateVisual(); }

    private Rect Inner => new(Pad, Pad, Math.Max(1, ActualWidth - 2 * Pad), Math.Max(1, ActualHeight - 2 * Pad));
    private double SX(int s) => Inner.X + s / 255.0 * Inner.Width;
    private double VY(int v) => Inner.Y + (1 - v / 255.0) * Inner.Height;
    private (int S, int V) At(Point p) => ((int)Math.Round(Math.Clamp((p.X - Inner.X) / Inner.Width, 0, 1) * 255), (int)Math.Round(Math.Clamp(1 - (p.Y - Inner.Y) / Inner.Height, 0, 1) * 255));

    private int CenterHue() => _c.Lower[0] <= _c.Upper[0] ? (_c.Lower[0] + _c.Upper[0]) / 2 : ((_c.Lower[0] + _c.Upper[0] + 180) / 2) % 180;

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2 * Pad + 10) return;
        int hue = CenterHue();
        if (_bg == null || hue != _bgHue)
        {
            const int n = 96;
            _bg ??= new WriteableBitmap(n, n, 96, 96, PixelFormats.Bgr32, null);
            var px = new int[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var c = Hsv.ToColor(hue * 2, x * 255 / (n - 1), 255 - y * 255 / (n - 1));
                    px[y * n + x] = (c.R << 16) | (c.G << 8) | c.B;
                }
            _bg.WritePixels(new Int32Rect(0, 0, n, n), px, n * 4, 0);
            _bgHue = hue;
        }
        dc.DrawImage(_bg, Inner);
        if (_density != null)
        {
            int max = Math.Max(1, _density.Max());
            for (int i = 0; i < 64 * 64; i++)
            {
                if (_density[i] == 0) continue;
                int sx = i % 64, vy = i / 64;
                byte a = (byte)(70 + 170 * Math.Log(1 + _density[i]) / Math.Log(1 + max));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(a, 20, 22, 30)), null,
                    new Rect(Inner.X + sx / 64.0 * Inner.Width, Inner.Y + (63 - vy) / 64.0 * Inner.Height, Inner.Width / 64 + 0.5, Inner.Height / 64 + 0.5));
            }
        }
        var r = new Rect(new Point(SX(_c.Lower[1]), VY(_c.Upper[2])), new Point(SX(_c.Upper[1]), VY(_c.Lower[2])));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(36, 255, 43, 214)), null, r);
        dc.DrawRectangle(null, new Pen(Brushes.White, 4), r);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x2E)), 2), r);
        foreach (var p in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x2E)), 1.5), new Rect(p.X - 5, p.Y - 5, 10, 10));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var r = new Rect(new Point(SX(_c.Lower[1]), VY(_c.Upper[2])), new Point(SX(_c.Upper[1]), VY(_c.Lower[2])));
        bool Near(double a, double b) => Math.Abs(a - b) < 9;
        bool l = Near(p.X, r.Left), rr = Near(p.X, r.Right), t = Near(p.Y, r.Top), b = Near(p.Y, r.Bottom);
        bool inside = r.Contains(p);
        var (s0, v0) = At(p);
        if (!l && !rr && !t && !b && !inside)
        {
            // 点在框外：以这一点为中心放一个小框
            _c.Lower[1] = Math.Max(0, s0 - 20); _c.Upper[1] = Math.Min(255, s0 + 20);
            _c.Lower[2] = Math.Max(0, v0 - 20); _c.Upper[2] = Math.Min(255, v0 + 20);
            InvalidateVisual(); Changed?.Invoke(); Committed?.Invoke();
            e.Handled = true;
            return;
        }
        _drag = (l, rr, t, b, !(l || rr || t || b), s0, v0, (int[])_c.Lower.Clone(), (int[])_c.Upper.Clone());
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        var (s, v) = At(e.GetPosition(this));
        if (d.Move)
        {
            int ds = Math.Clamp(s - d.S0, -d.Lo[1], 255 - d.Hi[1]), dv = Math.Clamp(v - d.V0, -d.Lo[2], 255 - d.Hi[2]);
            _c.Lower[1] = d.Lo[1] + ds; _c.Upper[1] = d.Hi[1] + ds; _c.Lower[2] = d.Lo[2] + dv; _c.Upper[2] = d.Hi[2] + dv;
        }
        if (d.L) _c.Lower[1] = Math.Min(s, _c.Upper[1]);
        if (d.R) _c.Upper[1] = Math.Max(s, _c.Lower[1]);
        if (d.T) _c.Upper[2] = Math.Max(v, _c.Lower[2]);
        if (d.B) _c.Lower[2] = Math.Min(v, _c.Upper[2]);
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == null) return;
        _drag = null;
        ReleaseMouseCapture();
        Committed?.Invoke();
    }
}

/// <summary>HSV（H 0–360，S、V 0–255）→ RGB。</summary>
internal static class Hsv
{
    public static Color ToColor(double hDeg, int s, int v)
    {
        double S = s / 255.0, V = v / 255.0, C = V * S, hp = (hDeg % 360) / 60, X = C * (1 - Math.Abs(hp % 2 - 1)), m = V - C;
        var (r, g, b) = hp < 1 ? (C, X, 0.0) : hp < 2 ? (X, C, 0.0) : hp < 3 ? (0.0, C, X) : hp < 4 ? (0.0, X, C) : hp < 5 ? (X, 0.0, C) : (C, 0.0, X);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
