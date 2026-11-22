using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Vision;
using Point = System.Windows.Point;

namespace VisionBlocks.Studio;

/// <summary>
/// 属性卡里颜色资源的调色盘：色相条 + S×V 方块 + 数字框（三者同步），
/// 选一个热区看占比，画布上把范围内的像素标成洋红；「重新吸色」「加上这一点」两个模式在画布上点一下生效。
/// </summary>
public sealed partial class MainWindow
{
    private enum ColorPick { None, Resample, Add }
    private ColorPick _colorPick;
    private readonly Dictionary<string, string> _ratioRegionOf = new();   // 颜色资源 → 上次选的「看占比的地方」
    private Mat? _maskHsv;            // 冻结帧缩小后的 HSV（只用于画布高亮，占比用原图算）
    private NormFrame? _maskFrame;
    private readonly DispatcherTimer _maskTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private ColorDef? _maskColor;

    private const string WholeFrame = "（整个画面）";

    private void ColorPalette(string name, ColorDef cd)
    {
        var hue = new HueBar(cd) { Margin = new Thickness(0, 0, 0, 6) };
        var sv = new SvBox(cd) { Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(hue, "colorHue");
        AutomationProperties.SetAutomationId(sv, "colorSV");
        var hueText = new TextBlock { FontSize = 12, Foreground = (Brush)Ui.Theme["Ink"], FontWeight = FontWeights.SemiBold };
        var svText = new TextBlock { FontSize = 12, Foreground = (Brush)Ui.Theme["Ink"], FontWeight = FontWeights.SemiBold };
        _props.Children.Add(LabeledLine(L.Z("色相 H（0–179）"), hueText));
        _props.Children.Add(hue);
        _props.Children.Add(LabeledLine(L.Z("饱和度 S（横）× 明度 V（竖）"), svText));
        _props.Children.Add(sv);
        _props.Children.Add(new TextBlock { Text = L.Z("深色格子：所选地方里色相在范围内的像素，越深越多"), FontSize = 11, Foreground = (Brush)Ui.Theme["Text3"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        // 数字框：两行三列（下限 / 上限 × H S V），AutomationId 仍是 cl0–2、cu0–2
        var boxes = new TextBox[6];
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < 3; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] hsv = { "H", "S", "V" };
        for (int i = 0; i < 3; i++)
        {
            var h = new TextBlock { Text = hsv[i], FontSize = 11, Foreground = (Brush)Ui.Theme["Text3"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
            Grid.SetColumn(h, i + 1); grid.Children.Add(h);
        }
        void Refresh(bool density)
        {
            for (int i = 0; i < 3; i++) { boxes[i].Text = cd.Lower[i].ToString(System.Globalization.CultureInfo.InvariantCulture); boxes[3 + i].Text = cd.Upper[i].ToString(System.Globalization.CultureInfo.InvariantCulture); }
            hueText.Text = cd.Lower[0] <= cd.Upper[0] ? $"{cd.Lower[0]} – {cd.Upper[0]}" : $"{cd.Lower[0]} – 179, 0 – {cd.Upper[0]}" + (L.En ? " (wraps red)" : "（跨 0 的红色）");
            svText.Text = $"S {cd.Lower[1]}–{cd.Upper[1]} · V {cd.Lower[2]}–{cd.Upper[2]}";
            hue.InvalidateVisual();
            sv.InvalidateVisual();
            if (density) sv.SetDensity(Density(cd, RatioRegion(name)));
            UpdateRatioText(name, cd);
            ScheduleMask(cd);
        }
        for (int k = 0; k < 6; k++)
        {
            int idx = k % 3; bool upper = k >= 3;
            var lab = new TextBlock { Text = L.Z(upper ? "上限" : "下限"), FontSize = 12, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
            if (idx == 0) { Grid.SetRow(lab, upper ? 2 : 1); grid.Children.Add(lab); }
            var tb = Num(upper ? cd.Upper[idx] : cd.Lower[idx], v => { if (upper) cd.Upper[idx] = (int)v; else cd.Lower[idx] = (int)v; Refresh(idx == 0); }, 0, idx == 0 ? 179 : 255);
            tb.Margin = new Thickness(0, 0, 4, 4);
            AutomationProperties.SetAutomationId(tb, (upper ? "cu" : "cl") + idx);
            AutomationProperties.SetName(tb, (upper ? "上限 " : "下限 ") + hsv[idx]);
            Grid.SetRow(tb, upper ? 2 : 1); Grid.SetColumn(tb, idx + 1);
            grid.Children.Add(tb);
            boxes[k] = tb;
        }
        _props.Children.Add(grid);

        // 看哪里的占比
        var regions = new List<(string Key, string Label)> { (WholeFrame, L.Z(WholeFrame)) };
        foreach (var (rn, rd) in _ed.Program.Regions) regions.Add(("r:" + rn, string.IsNullOrEmpty(rd.Label) ? rn : rd.Label));
        foreach (var (gn, gd) in _ed.Program.Grids) regions.Add(("g:" + gn, (string.IsNullOrEmpty(gd.Label) ? gn : gd.Label) + L.Z(" 第一格")));
        if (!_ratioRegionOf.ContainsKey(name)) _ratioRegionOf[name] = DefaultRatioRegion(name);
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (key, label) in regions) combo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _ratioRegionOf[name]) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string t }) { _ratioRegionOf[name] = t; Refresh(true); } };
        Row(L.Z("看哪里的占比"), combo, "colorRatioRegion");
        _ratioText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetAutomationId(_ratioText, "colorRatio");
        _props.Children.Add(new Border { Child = _ratioText, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 0), Background = (Brush)Ui.Theme["Card2"], Margin = new Thickness(0, 0, 0, 10) });

        // 画布上点一下生效的两个模式
        var resample = new ToggleButton { Content = L.Z("重新吸色"), IsChecked = _colorPick == ColorPick.Resample, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 6, 12, 6) };
        var add = new ToggleButton { Content = L.Z("加上这一点"), IsChecked = _colorPick == ColorPick.Add, Padding = new Thickness(12, 6, 12, 6) };
        AutomationProperties.SetAutomationId(resample, "colorResample");
        AutomationProperties.SetAutomationId(add, "colorAddPoint");
        resample.Checked += (_, _) => { _colorPick = ColorPick.Resample; add.IsChecked = false; };
        resample.Unchecked += (_, _) => { if (_colorPick == ColorPick.Resample) _colorPick = ColorPick.None; };
        add.Checked += (_, _) => { _colorPick = ColorPick.Add; resample.IsChecked = false; };
        add.Unchecked += (_, _) => { if (_colorPick == ColorPick.Add) _colorPick = ColorPick.None; };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        btns.Children.Add(resample);
        btns.Children.Add(add);
        _props.Children.Add(btns);
        Note("「重新吸色」：在画布上点一下，以那一点为中心重新生成范围。「加上这一点」：每点一下就把那一点并进范围，再点一次按钮退出。画布上洋红色的就是落在范围内的像素，鼠标停在画布上有放大镜。");

        hue.Changed += () => Refresh(false);
        hue.Committed += () => { Refresh(true); Changed(); };
        sv.Changed += () => Refresh(false);
        sv.Committed += () => { Refresh(false); Changed(); };
        _canvas.LoupeColor = cd;
        Refresh(true);
    }

    private TextBlock? _ratioText;

    private UIElement LabeledLine(string label, TextBlock value)
    {
        var d = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
        DockPanel.SetDock(value, Dock.Right);
        d.Children.Add(value);
        d.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Ui.Muted });
        return d;
    }

    /// <summary>默认看哪里：程序里第一个用到这个颜色的「颜色占比」条件的热区；用的是「当前格」时取它所在网格的第一格。</summary>
    private string DefaultRatioRegion(string color)
    {
        string? Walk(IEnumerable<Block> blocks, string? grid)
        {
            foreach (var b in blocks)
            {
                foreach (var c in new[] { (b as IfBlock)?.Cond, (b as WaitUntilBlock)?.Cond, (b as RepeatUntilBlock)?.Until, (b as ClickBlock)?.ConfirmCond })
                    if (c is ColorCondition cc && cc.Color == color)
                        return cc.Region == VbProgram.CurrentCell ? (grid != null ? "g:" + grid : null) : "r:" + cc.Region;
                string? inner = b is IContainerBlock ct ? Walk(ct.Body, b is ForEachCellBlock fe ? fe.Grid : grid) : null;
                inner ??= b is IfBlock ib ? Walk(ib.Else, grid) : null;
                if (inner != null) return inner;
            }
            return null;
        }
        return Walk(_ed.Program.Program, null) ?? WholeFrame;
    }

    private RegionDef RatioRegion(string color)
    {
        string key = _ratioRegionOf.TryGetValue(color, out var k) ? k : WholeFrame;
        if (key.StartsWith("r:", StringComparison.Ordinal) && _ed.Program.Regions.TryGetValue(key[2..], out var r)) return r;
        if (key.StartsWith("g:", StringComparison.Ordinal) && _ed.Program.Grids.TryGetValue(key[2..], out var g)) return g.Cell(0);
        return new RegionDef { X = 0, Y = 0, W = _canvas.BaseW, H = _canvas.BaseH };
    }

    private void UpdateRatioText(string name, ColorDef cd)
    {
        if (_ratioText == null) return;
        if (_frozen == null) { _ratioText.Text = L.Z("先截一帧，才能看占比"); _ratioText.Foreground = (Brush)Ui.Theme["Text3"]; return; }
        try
        {
            var (ratio, _) = ColorRatio.Measure(_frozen, RatioRegion(name), cd);
            string key = _ratioRegionOf.TryGetValue(name, out var k) ? k : WholeFrame;
            string where = key == WholeFrame ? L.Z("整个画面")
                : key.StartsWith("g:", StringComparison.Ordinal) ? (_ed.Program.Grids.TryGetValue(key[2..], out var g) && !string.IsNullOrEmpty(g.Label) ? g.Label : key[2..]) + L.Z(" 第一格")
                : _ed.Program.Regions.TryGetValue(key[2..], out var rg) && !string.IsNullOrEmpty(rg.Label) ? rg.Label : key[2..];
            _ratioText.Text = (L.En ? $"Ratio in \"{where}\": " : $"「{where}」里占比 ") + $"{ratio * 100:0.0}%";
            _ratioText.Foreground = (Brush)Ui.Theme["Ink"];
        }
        catch (Exception e) { _ratioText.Text = e.Message; }
    }

    /// <summary>所选地方里色相在范围内的像素，按 S、V 落进 64×64 的格子（最多取 2 万个点）。</summary>
    private int[]? Density(ColorDef cd, RegionDef roi)
    {
        if (_frozen == null) return null;
        var r = _frozen.ToPixels(roi);
        if (r.Width <= 0 || r.Height <= 0) return null;
        int step = Math.Max(1, (int)Math.Sqrt(r.Width * (double)r.Height / 20000));
        var d = new int[64 * 64];
        var hsv = _frozen.Hsv;
        bool wrap = cd.Lower[0] > cd.Upper[0];
        for (int y = r.Y; y < r.Y + r.Height; y += step)
            for (int x = r.X; x < r.X + r.Width; x += step)
            {
                var v = hsv.At<Vec3b>(y, x);
                bool inH = wrap ? v.Item0 >= cd.Lower[0] || v.Item0 <= cd.Upper[0] : v.Item0 >= cd.Lower[0] && v.Item0 <= cd.Upper[0];
                if (inH) d[(v.Item2 >> 2) * 64 + (v.Item1 >> 2)]++;
            }
        return d;
    }

    /// <summary>画布高亮最多 20 次每秒：拖动时合并请求。</summary>
    private void ScheduleMask(ColorDef? cd)
    {
        _maskColor = cd;
        if (!_maskTimer.IsEnabled) { _maskTimer.Tick -= OnMaskTick; _maskTimer.Tick += OnMaskTick; _maskTimer.Start(); }
    }

    private void OnMaskTick(object? sender, EventArgs e)
    {
        _maskTimer.Stop();
        UpdateColorMask(_maskColor);
    }

    /// <summary>把冻结帧里落在范围内的像素画成洋红色，叠在画布上。用缩小（最近邻）后的图算，只为显示；占比用原图算。</summary>
    private void UpdateColorMask(ColorDef? cd)
    {
        if (cd == null || _frozen == null) { _canvas.ColorMask = null; _canvas.InvalidateVisual(); return; }
        if (!ReferenceEquals(_maskFrame, _frozen) || _maskHsv == null)
        {
            _maskHsv?.Dispose();
            int w = Math.Min(1280, _frozen.Bgr.Width), h = (int)Math.Round(_frozen.Bgr.Height * (double)w / _frozen.Bgr.Width);
            using var small = _frozen.Bgr.Resize(new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Nearest);
            _maskHsv = small.CvtColor(ColorConversionCodes.BGR2HSV);
            _maskFrame = _frozen;
        }
        using var m = ColorRatio.InRange(_maskHsv, cd);
        int mw = m.Width, mh = m.Height;
        var mask = new byte[mw * mh];
        System.Runtime.InteropServices.Marshal.Copy(m.Data, mask, 0, mask.Length);   // InRange 的结果是连续的单通道
        var bgra = new byte[mw * mh * 4];
        for (int i = 0, j = 0; i < mask.Length; i++, j += 4)
            if (mask[i] != 0) { bgra[j] = 214; bgra[j + 1] = 43; bgra[j + 2] = 255; bgra[j + 3] = 230; }
        var bmp = BitmapSource.Create(mw, mh, 96, 96, PixelFormats.Bgra32, null, bgra, mw * 4);
        bmp.Freeze();
        _canvas.ColorMask = bmp;
        _canvas.InvalidateVisual();
    }

    /// <summary>「重新吸色」「加上这一点」：画布上点了一下。返回 true 表示这一下被调色盘用掉了。</summary>
    private bool ColorPickAt(Point p)
    {
        if (_colorPick == ColorPick.None || _selected is not ResourceRef { Kind: "color" } r || !_ed.Program.Colors.TryGetValue(r.Name, out var cd)) return false;
        if (_frozen == null) { Log("先截一帧"); return true; }
        int bx = (int)Math.Round(p.X), by = (int)Math.Round(p.Y);
        if (_colorPick == ColorPick.Resample)
        {
            var s = ColorRatio.FromSample(_frozen, bx, by, cd.Label);
            cd.Lower = s.Lower; cd.Upper = s.Upper;
            _colorPick = ColorPick.None;
            Log($"已在 ({bx},{by}) 重新吸色「{r.Name}」：HSV 下限 [{string.Join(",", cd.Lower)}] 上限 [{string.Join(",", cd.Upper)}]");
        }
        else
        {
            int px = Math.Clamp((int)Math.Round(bx * _frozen.K), 0, _frozen.Bgr.Width - 1), py = Math.Clamp((int)Math.Round(by * _frozen.K), 0, _frozen.Bgr.Height - 1);
            var v = _frozen.Hsv.At<Vec3b>(py, px);
            int h = v.Item0, s = v.Item1, val = v.Item2;
            bool wrap = cd.Lower[0] > cd.Upper[0];
            bool inH = wrap ? h >= cd.Lower[0] || h <= cd.Upper[0] : h >= cd.Lower[0] && h <= cd.Upper[0];
            if (!inH)
            {
                // 色相往离得近的那一边扩
                int dl = (cd.Lower[0] - h + 180) % 180, du = (h - cd.Upper[0] + 180) % 180;
                if (dl < du) cd.Lower[0] = (h - 3 + 180) % 180; else cd.Upper[0] = (h + 3) % 180;
            }
            cd.Lower[1] = Math.Min(cd.Lower[1], Math.Max(0, s - 5)); cd.Upper[1] = Math.Max(cd.Upper[1], Math.Min(255, s + 5));
            cd.Lower[2] = Math.Min(cd.Lower[2], Math.Max(0, val - 5)); cd.Upper[2] = Math.Max(cd.Upper[2], Math.Min(255, val + 5));
            Log($"已把 ({bx},{by}) 的颜色 H{h} S{s} V{val} 加进「{r.Name}」");
        }
        _ed.Touch();
        ShowProps();
        return true;
    }
}
