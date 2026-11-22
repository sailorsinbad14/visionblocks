using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Vision;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace VisionBlocks.Studio;

/// <summary>
/// 坐标链的第一环：画布（WPF 的 DIP）→ 基准坐标。
/// 冻结帧已经归一化到基准宽度，画布按 Uniform 缩放居中显示，所以只需要「减偏移、除比例」。
/// 与 DPI 无关：比例由「控件实际尺寸 / 基准尺寸」决定。
/// </summary>
public static class CanvasMapper
{
    public static (double Scale, double Ox, double Oy) Layout(Size element, int baseW, int baseH)
    {
        double s = Math.Min(element.Width / baseW, element.Height / baseH);
        if (s <= 0 || double.IsNaN(s)) s = 1;
        return (s, (element.Width - baseW * s) / 2, (element.Height - baseH * s) / 2);
    }

    public static Point ToBase(Point p, Size element, int baseW, int baseH)
    {
        var (s, ox, oy) = Layout(element, baseW, baseH);
        return new Point(Math.Clamp((p.X - ox) / s, 0, baseW), Math.Clamp((p.Y - oy) / s, 0, baseH));
    }
}

/// <summary>显示冻结帧，叠加热区、定点、拖拽框和「试一下」的结果框。鼠标事件以基准坐标抛出。</summary>
public sealed class FrameCanvas : FrameworkElement
{
    public int BaseW = 1920, BaseH = 1080;
    public VbProgram? Program;
    public string? HighlightRegion, HighlightPoint, HighlightGrid;
    public RegionDef? TestRoi, TestBox;
    public bool TestHit;
    public Rect? Drag;
    public Point? Cursor;
    /// <summary>落在颜色范围内的像素（洋红、半透明），盖在冻结帧上。</summary>
    public BitmapSource? ColorMask;
    /// <summary>选中颜色资源时，放大镜里写出当前像素在不在这个范围内。</summary>
    public ColorDef? LoupeColor;
    /// <summary>吸色工具或选中颜色资源时显示放大镜。</summary>
    public bool ShowLoupe;

    private BitmapSource? _image;
    private NormFrame? _frame;
    public event Action<Point>? BaseDown, BaseMove, BaseUp;

    private static readonly Brush Empty = Frozen(new SolidColorBrush(Color.FromRgb(0x24, 0x31, 0x3C)));
    private static readonly Pen RegionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0xB3, 0xC0)), 2) { DashStyle = DashStyles.Dash });
    private static readonly Pen RegionHi = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0xB3, 0xC0)), 4));
    private static readonly Pen PointPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x30)), 3));
    private static readonly Pen DragPen = Frozen(new Pen(Brushes.White, 2) { DashStyle = DashStyles.Dot });
    private static readonly Pen HitPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x46, 0xDC, 0x78)), 3));
    private static readonly Pen MissPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x50)), 3));
    private static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(200, 10, 20, 28)));
    private static readonly Typeface Face = new("Microsoft YaHei UI");
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public FrameCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        MinHeight = 200;
    }

    public void SetFrame(NormFrame? f)
    {
        _image = f == null ? null : ToBitmap(f.Bgr);
        _frame = f;
        if (f != null) BaseH = (int)Math.Round(f.Bgr.Height / f.K);
        InvalidateVisual();
    }

    public static BitmapSource ToBitmap(Mat bgr)
    {
        int stride = bgr.Width * 3;
        var buf = new byte[stride * bgr.Height];
        long step = bgr.Step();
        for (int y = 0; y < bgr.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(bgr.Data + (int)(y * step), buf, y * stride, stride);
        var bs = BitmapSource.Create(bgr.Width, bgr.Height, 96, 96, PixelFormats.Bgr24, null, buf, stride);
        bs.Freeze();
        return bs;
    }

    private Point ToBase(MouseEventArgs e) => CanvasMapper.ToBase(e.GetPosition(this), new Size(ActualWidth, ActualHeight), BaseW, BaseH);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); BaseDown?.Invoke(ToBase(e)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { Cursor = ToBase(e); BaseMove?.Invoke(Cursor.Value); InvalidateVisual(); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); BaseUp?.Invoke(ToBase(e)); e.Handled = true; }
    protected override void OnMouseLeave(MouseEventArgs e) { Cursor = null; InvalidateVisual(); }

    private FormattedText Text(string s, double size) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Empty, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var (s, ox, oy) = CanvasMapper.Layout(new Size(ActualWidth, ActualHeight), BaseW, BaseH);
        if (_image == null)
        {
            var ft = Text(L.T("noFrame"), 15);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
            return;
        }
        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(s, s));
        dc.DrawImage(_image, new Rect(0, 0, BaseW, BaseH));
        if (ColorMask != null) dc.DrawImage(ColorMask, new Rect(0, 0, BaseW, BaseH));
        double inv = 1 / s;   // 让线宽和字号在屏幕上保持固定大小
        if (Program != null)
        {
            foreach (var (name, r) in Program.Regions)
            {
                var pen = name == HighlightRegion ? RegionHi : RegionPen;
                dc.DrawRectangle(null, new Pen(pen.Brush, pen.Thickness * inv) { DashStyle = pen.DashStyle }, new Rect(r.X, r.Y, r.W, r.H));
                Label(dc, string.IsNullOrEmpty(r.Label) ? name : r.Label, r.X, r.Y, inv);
            }
            foreach (var (name, g) in Program.Grids)
            {
                // 网格：每一格画一个框；选中的网格画实线，第一格标名字
                var pen = name == HighlightGrid ? new Pen(PointPen.Brush, 3 * inv) : new Pen(PointPen.Brush, 1.5 * inv) { DashStyle = DashStyles.Dash };
                for (int i = 0; i < g.Cols * g.Rows; i++) { var cell = g.Cell(i); dc.DrawRectangle(null, pen, new Rect(cell.X, cell.Y, cell.W, cell.H)); }
                Label(dc, $"{(string.IsNullOrEmpty(g.Label) ? name : g.Label)} {g.Cols}×{g.Rows}", g.X, g.Y, inv);
            }
            foreach (var (name, p) in Program.Points)
            {
                var pen = new Pen(PointPen.Brush, PointPen.Thickness * inv * (name == HighlightPoint ? 1.6 : 1));
                dc.DrawLine(pen, new Point(p.X - 14 * inv, p.Y), new Point(p.X + 14 * inv, p.Y));
                dc.DrawLine(pen, new Point(p.X, p.Y - 14 * inv), new Point(p.X, p.Y + 14 * inv));
                Label(dc, string.IsNullOrEmpty(p.Label) ? name : p.Label, p.X + 10 * inv, p.Y - 4 * inv, inv);
            }
        }
        if (TestRoi != null) dc.DrawRectangle(null, new Pen(RegionHi.Brush, 3 * inv), new Rect(TestRoi.X, TestRoi.Y, TestRoi.W, TestRoi.H));
        if (TestBox != null) dc.DrawRectangle(null, new Pen((TestHit ? HitPen : MissPen).Brush, 3 * inv), new Rect(TestBox.X, TestBox.Y, TestBox.W, TestBox.H));
        if (Drag is { } d) dc.DrawRectangle(null, new Pen(DragPen.Brush, 2 * inv) { DashStyle = DashStyles.Dot }, d);
        dc.Pop();
        dc.Pop();
        if (Cursor is { } c)
        {
            var ft = Text($"基准坐标 {c.X:0}, {c.Y:0}", 12);
            dc.DrawRectangle(LabelBg, null, new Rect(6, ActualHeight - ft.Height - 10, ft.Width + 10, ft.Height + 4));
            dc.DrawText(ft, new Point(11, ActualHeight - ft.Height - 8));
            if (ShowLoupe) DrawLoupe(dc, c, ox + c.X * s, oy + c.Y * s);
        }
    }

    /// <summary>放大镜：光标处 11×11 个原始像素放大显示，下面写 HSV；选中颜色资源时再写「在范围内 / 不在」。</summary>
    private void DrawLoupe(DrawingContext dc, Point basePt, double sx, double sy)
    {
        if (_frame == null || _image == null) return;
        int px = Math.Clamp((int)Math.Round(basePt.X * _frame.K), 5, _frame.Bgr.Width - 6), py = Math.Clamp((int)Math.Round(basePt.Y * _frame.K), 5, _frame.Bgr.Height - 6);
        var crop = new CroppedBitmap(_image, new Int32Rect(px - 5, py - 5, 11, 11));
        var v = _frame.Hsv.At<Vec3b>(py, px);
        string line = $"H {v.Item0}  S {v.Item1}  V {v.Item2}";
        if (LoupeColor is { } cd)
        {
            bool wrap = cd.Lower[0] > cd.Upper[0];
            bool inH = wrap ? v.Item0 >= cd.Lower[0] || v.Item0 <= cd.Upper[0] : v.Item0 >= cd.Lower[0] && v.Item0 <= cd.Upper[0];
            bool hit = inH && v.Item1 >= cd.Lower[1] && v.Item1 <= cd.Upper[1] && v.Item2 >= cd.Lower[2] && v.Item2 <= cd.Upper[2];
            line += L.En ? (hit ? "\nin range" : "\nout of range") : (hit ? "\n在范围内" : "\n不在范围内");
        }
        var ft = Text(line, 12);
        double size = 110, w = Math.Max(size, ft.Width) + 12, h = size + ft.Height + 18;
        double x = sx + 18 + w > ActualWidth ? sx - 18 - w : sx + 18, y = Math.Clamp(sy + 18, 4, Math.Max(4, ActualHeight - h - 4));
        dc.DrawRoundedRectangle(LabelBg, null, new Rect(x, y, w, h), 8, 8);
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        group.Children.Add(new ImageDrawing(crop, new Rect(x + 6, y + 6, size, size)));
        dc.DrawDrawing(group);
        double cell = size / 11;
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x2B, 0xD6)), 1.5), new Rect(x + 6 + 5 * cell, y + 6 + 5 * cell, cell, cell));
        dc.DrawText(ft, new Point(x + 6, y + size + 10));
    }

    private void Label(DrawingContext dc, string text, double x, double y, double inv)
    {
        var ft = Text(text, 13 * inv);
        var bg = new Rect(x, y - ft.Height - 2 * inv, ft.Width + 8 * inv, ft.Height + 2 * inv);
        dc.DrawRectangle(LabelBg, null, bg);
        dc.DrawText(ft, new Point(x + 4 * inv, bg.Y + 1 * inv));
    }
}
