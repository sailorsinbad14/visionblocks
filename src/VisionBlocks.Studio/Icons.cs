using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace VisionBlocks.Studio;

/// <summary>矢量小图标（20×20 坐标，和设计稿原型同一套路径）。不依赖图标字体，任何缩放下都清楚。</summary>
internal static class Icons
{
    private static readonly Dictionary<string, (string Data, bool Fill)> Map = new()
    {
        ["new"] = ("M5 2.5h6l4 4v11H5z M11 2.5v4h4", false),
        ["open"] = ("M2.5 5.5h5l1.5 1.5h8.5v9h-15z", false),
        ["save"] = ("M4 3h10l3 3v11H4z M7 3v4h6V3 M7 17v-5h7v5", false),
        ["sample"] = ("M4 3h9l3 3v11H4z M7 10h6 M7 13h6 M7 7h3", false),
        ["target"] = ("M3 10a7 7 0 1 0 14 0a7 7 0 1 0 -14 0 M7 10a3 3 0 1 0 6 0a3 3 0 1 0 -6 0", false),
        ["refresh"] = ("M16 10a6 6 0 1 1 -2 -4.5 M16 3.5v3h-3", false),
        ["camera"] = ("M2.5 6.5h3l1.5-2h6l1.5 2h3v9.5h-15z M7 11a3 3 0 1 0 6 0a3 3 0 1 0 -6 0", false),
        ["play"] = ("M6 4l10 6-10 6z", true),
        ["stop"] = ("M5 5h10v10H5z", true),
        ["gear"] = ("M7.4 10a2.6 2.6 0 1 0 5.2 0a2.6 2.6 0 1 0 -5.2 0 M10 2.5v2.2 M10 15.3v2.2 M2.5 10h2.2 M15.3 10h2.2 M4.7 4.7l1.6 1.6 M13.7 13.7l1.6 1.6 M4.7 15.3l1.6-1.6 M13.7 6.3l1.6-1.6", false),
        ["select"] = ("M5 3l10 6.5-4.4 1 2.6 5-1.9 1-2.6-5L5 14.5z", false),
        ["region"] = ("M3 4h3 M8 4h4 M14 4h3v3 M17 9v3 M17 14v2h-3 M12 16H8 M6 16H3v-3 M3 11V8 M3 6V4", false),
        ["point"] = ("M10 3v14 M3 10h14 M7 10a3 3 0 1 0 6 0a3 3 0 1 0 -6 0", false),
        ["template"] = ("M6 2v12h12 M2 6h12v12", false),
        ["color"] = ("M13 3l4 4-8.5 8.5-3 .5.5-3z M11 5l4 4 M3 17h3", false),
        ["ocr"] = ("M3 6V3h3 M14 3h3v3 M17 14v3h-3 M6 17H3v-3 M7 7h6 M10 7v7", false),
        ["grid"] = ("M3 3h14v14H3z M3 10h14 M10 3v14", false),
        ["up"] = ("M10 16V4 M5 9l5-5 5 5", false),
        ["down"] = ("M10 4v12 M5 11l5 5 5-5", false),
        ["indent"] = ("M3 4h14 M9 8h8 M9 12h8 M3 16h14 M3 8l3 2-3 2", false),
        ["outdent"] = ("M3 4h14 M9 8h8 M9 12h8 M3 16h14 M6 8l-3 2 3 2", false),
        ["trash"] = ("M4 6h12 M8 6V4h4v2 M5.5 6l1 11h7l1-11", false),
        ["lang"] = ("M3 5h8 M7 3v2 M4.5 5c.5 3 2.5 5 5 6 M9.5 5c-.5 3-2.5 5-5.5 6.5 M11 17l3.5-8 3.5 8 M12 14.5h5", false),
    };

    /// <summary>
    /// 应用图标（A 取景积木）的矢量版，顶栏左上角用。和 Assets\visionblocks.ico 用同一套路径（viewBox 256），
    /// 只省掉了投影：34 DIP 下看不出来。两种主题共用：图标本身没有底板，浅、深底色上都清楚。
    /// </summary>
    public static FrameworkElement AppLogo(double size)
    {
        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        static LinearGradientBrush Down(string top, string bottom) => new(C(top), C(bottom), new Point(0, 0), new Point(0, 1));
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 256, 256))));   // 保持 256 的画布，留白和 SVG 一致
        g.Children.Add(new GeometryDrawing(Down("#628DF5", "#2E53C2"), null, Geometry.Parse("M46 154H178A18 18 0 0 1 196 172V200A18 18 0 0 1 178 218H96V222A8 8 0 0 1 88 230H64A8 8 0 0 1 56 222V218H46A18 18 0 0 1 28 200V172A18 18 0 0 1 46 154Z")));
        g.Children.Add(new GeometryDrawing(Down("#26BBAD", "#0A7A72"), null, Geometry.Parse("M54 26H202A26 26 0 0 1 228 52V120A26 26 0 0 1 202 146H96V152A8 8 0 0 1 88 160H64A8 8 0 0 1 56 152V146H54A26 26 0 0 1 28 120V52A26 26 0 0 1 54 26Z")));
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(77, 255, 255, 255), 0));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.5));
        g.Children.Add(new GeometryDrawing(sheen, null, new RectangleGeometry(new Rect(28, 26, 200, 120), 26, 26)));
        var corner = new Pen(Brushes.White, 11) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var d in new[] { "M78 72V50H100", "M156 50H178V72", "M78 100V122H100", "M156 122H178V100" })
            g.Children.Add(new GeometryDrawing(null, corner, Geometry.Parse(d)));
        var cross = Geometry.Parse("M128 66V106M108 86H148");
        g.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 20) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, cross));
        g.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(C("#F39419")), 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, cross));
        var img = new DrawingImage(g);
        img.Freeze();
        return new Image { Source = img, Width = size, Height = size };
    }

    public static FrameworkElement Get(string name, double size = 16, Brush? brush = null)
    {
        var (data, fill) = Map[name];
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = fill ? 0 : 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 20, Height = 20,
        };
        // 不设颜色时跟随所在控件的前景色（按钮变墨黑底时图标自动变白）
        if (brush != null) { if (fill) path.Fill = brush; else path.Stroke = brush; }
        else
        {
            var b = new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) };
            path.SetBinding(fill ? Shape.FillProperty : Shape.StrokeProperty, b);
        }
        return new Viewbox { Width = size, Height = size, Child = path };
    }

    /// <summary>图标 + 文字（横排），给按钮当内容。文字是代码建的 TextBlock，UIA 读得到。</summary>
    public static StackPanel WithText(string icon, TextBlock text, double size = 16)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var ic = Get(icon, size);
        ic.Margin = new Thickness(0, 0, 6, 0);
        ic.VerticalAlignment = VerticalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(ic);
        sp.Children.Add(text);
        return sp;
    }
}
