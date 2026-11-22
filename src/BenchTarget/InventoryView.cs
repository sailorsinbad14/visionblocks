using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BenchTarget;

/// <summary>按基准坐标 1920×1080 画背包场景，再整体缩放到实际客户区。格子和分类画成朴素的方框，不加任何给工具用的标记。</summary>
public sealed class InventoryView : FrameworkElement
{
    private readonly InventoryScene _s;
    private readonly TargetOptions _opt;

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static SolidColorBrush B(byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
    private static readonly Brush Bg = B(0x1C, 0x26, 0x30), PanelBg = B(0x24, 0x31, 0x3C), Label = B(0x9F, 0xB0, 0xBC);
    private static readonly Brush CellBg = B(0x2A, 0x35, 0x40), SelBg = B(0x3A, 0x5A, 0x6E), DetailBg = B(0xEC, 0xF0, 0xF2), Ink = B(0x15, 0x21, 0x2B);
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Pen BoxPen = Frozen(new Pen(B(0xD8, 0xDF, 0xE4), 3));
    private static readonly Pen InkPen = Frozen(new Pen(B(0x15, 0x21, 0x2B), 3));
    private static readonly Pen AxisPen = Frozen(new Pen(B(0x56, 0x65, 0x6F), 2));
    private static readonly Brush BarFill = B(0x8A, 0xA4, 0xB4);
    private static readonly Brush[] IconColors = { B(216, 60, 60), B(60, 179, 113), B(66, 133, 244), B(224, 176, 0), B(160, 90, 220), B(0, 170, 190) };
    private static readonly Typeface Ui = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiBold = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public InventoryView(InventoryScene s, TargetOptions opt) { _s = s; _opt = opt; SnapsToDevicePixels = true; }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, bold ? UiBold : Ui, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void Centered(DrawingContext dc, string s, double size, Brush brush, Rect r, bool bold = false)
    {
        var ft = Text(s, size, brush, bold);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    /// <summary>六种图形当物品图标：圆、方、菱形、三角、六边形、星。</summary>
    private static void Icon(DrawingContext dc, int shape, int color, Point c, double size)
    {
        var fill = IconColors[color % IconColors.Length];
        double h = size / 2;
        switch (shape)
        {
            case 0: dc.DrawEllipse(fill, null, c, h, h); break;
            case 1: dc.DrawRectangle(fill, null, new Rect(c.X - h * 0.85, c.Y - h * 0.85, size * 0.85, size * 0.85)); break;
            default:
                int n = shape switch { 2 => 4, 3 => 3, 4 => 6, _ => 10 };
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    for (int i = 0; i < n; i++)
                    {
                        double rr = shape == 5 && i % 2 == 1 ? h * 0.45 : h;
                        double a = -Math.PI / 2 + i * 2 * Math.PI / n;
                        var p = new Point(c.X + rr * Math.Cos(a), c.Y + rr * Math.Sin(a));
                        if (i == 0) ctx.BeginFigure(p, true, true); else ctx.LineTo(p, true, false);
                    }
                }
                g.Freeze();
                dc.DrawGeometry(fill, null, g);
                break;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 1920.0, ActualHeight / 1080.0));
        dc.DrawRectangle(Bg, null, new Rect(0, 0, 1920, 1080));
        dc.DrawText(Text($"BenchTarget · 背包 · seed {_opt.Seed}", 22, Label), new Point(40, 26));

        // 左：分类
        dc.DrawRoundedRectangle(PanelBg, null, InventoryScene.CatPanel, 8, 8);
        dc.DrawText(Text("分类", 28, Brushes.White, true), new Point(60, 92));
        for (int i = 0; i < InventoryScene.Categories.Length; i++)
        {
            var r = InventoryScene.CatBox(i);
            dc.DrawRectangle(i == _s.Category ? SelBg : CellBg, BoxPen, r);
            Centered(dc, InventoryScene.Categories[i], 32, Brushes.White, r, i == _s.Category);
        }

        // 右：物品格（裁到视口内）
        dc.DrawRoundedRectangle(PanelBg, null, InventoryScene.ItemPanel, 8, 8);
        dc.DrawText(Text("物品", 28, Brushes.White, true), new Point(420, 92));
        dc.PushClip(new RectangleGeometry(InventoryScene.Viewport));
        foreach (var it in _s.InCategory(_s.Category))
        {
            var r = _s.CellRect(it.Idx);
            if (r.Bottom < InventoryScene.Viewport.Top || r.Top > InventoryScene.Viewport.Bottom) continue;
            dc.DrawRectangle(CellBg, BoxPen, r);
            Icon(dc, it.Shape, it.Color, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), 110);
        }
        dc.Pop();

        if (_s.Open is { } o) DrawDetail(dc, o);
        dc.Pop();
    }

    private void DrawDetail(DrawingContext dc, InventoryScene.Item it)
    {
        dc.DrawRectangle(Dim, null, new Rect(0, 0, 1920, 1080));
        dc.DrawRoundedRectangle(DetailBg, InkPen, InventoryScene.Panel, 10, 10);

        // 左上两段页签
        dc.DrawRectangle(SelBg, InkPen, InventoryScene.TabA);
        Centered(dc, "走势", 28, Brushes.White, InventoryScene.TabA, true);
        dc.DrawRectangle(DetailBg, InkPen, InventoryScene.TabB);
        Centered(dc, "属性", 28, Ink, InventoryScene.TabB);
        dc.DrawText(Text("物品详情", 30, Ink, true), new Point(640, 108));
        dc.DrawRectangle(DetailBg, InkPen, InventoryScene.CloseBtn);
        Centered(dc, "×", 44, Ink, InventoryScene.CloseBtn, true);

        // 走势图：折线（点上标价格）+ 柱状图（成交量，不标数字）
        var ch = InventoryScene.Chart;
        dc.DrawRectangle(Brushes.White, InkPen, ch);
        dc.DrawText(Text("价格走势", 24, Ink, true), new Point(ch.X + 20, ch.Y + 12));
        double Y(int v) => InventoryScene.LineBottom - v / 100.0 * (InventoryScene.LineBottom - InventoryScene.LineTop);
        var pts = it.Prices.Select((v, i) => new Point(InventoryScene.PointX0 + i * InventoryScene.PointDx, Y(v))).ToArray();
        for (int i = 1; i < pts.Length; i++) dc.DrawLine(InkPen, pts[i - 1], pts[i]);
        for (int i = 0; i < pts.Length; i++)
        {
            dc.DrawEllipse(Ink, null, pts[i], 8, 8);
            var ft = Text(it.Prices[i].ToString(CultureInfo.InvariantCulture), 30, Ink, true);
            dc.DrawText(ft, new Point(pts[i].X - ft.Width / 2, pts[i].Y - 16 - ft.Height));
        }
        dc.DrawLine(AxisPen, new Point(ch.X + 40, InventoryScene.BarBase), new Point(ch.Right - 30, InventoryScene.BarBase));
        dc.DrawLine(AxisPen, new Point(ch.X + 40, InventoryScene.BarBase), new Point(ch.X + 40, ch.Y + 60));
        for (int i = 0; i < it.Vols.Length; i++)
        {
            double h = it.Vols[i] / 10.0 * InventoryScene.BarMaxH;
            dc.DrawRectangle(BarFill, null, new Rect(InventoryScene.PointX0 + i * InventoryScene.PointDx - 25, InventoryScene.BarBase - h, 50, h));
        }

        // 右上：图片、物品名、库存
        dc.DrawRectangle(Brushes.White, InkPen, InventoryScene.Picture);
        var pc = InventoryScene.Picture;
        Icon(dc, it.Shape, it.Color, new Point(pc.X + pc.Width / 2, pc.Y + pc.Height / 2), 220);
        Centered(dc, it.Name, 40, Ink, new Rect(1290, 500, 400, 64), true);
        Centered(dc, $"库存 {it.Stock}", 34, Ink, new Rect(1290, 580, 400, 56));
    }
}
