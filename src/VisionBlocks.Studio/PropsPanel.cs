using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Model;
using Condition = VisionBlocks.Core.Model.Condition;

namespace VisionBlocks.Studio;

/// <summary>右侧属性面板：按选中的积木或资源生成编辑控件；条件可以在冻结帧上「试一下」。</summary>
public sealed partial class MainWindow
{
    private void ShowProps()
    {
        _props.Children.Clear();
        _canvas.HighlightRegion = null;
        _canvas.HighlightPoint = null;
        _canvas.HighlightGrid = null;
        _canvas.ColorMask = null;      // 只有选中颜色资源时才显示洋红高亮和放大镜里的「范围内 / 外」
        _canvas.LoupeColor = null;
        _maskColor = null;
        _ratioText = null;
        if (_selected is not ResourceRef { Kind: "color" }) _colorPick = ColorPick.None;
        _canvas.TestBox = null;
        _canvas.TestRoi = null;
        _canvas.ScrollPreview = null;
        switch (_selected)
        {
            case Block b: BlockProps(b); break;
            case ElseMarker: PTitle(L.Z("否则分支"), Ui.FlowFg); Note("选中「否则」再点积木架上的积木，积木会加到这个分支里：条件不成立时执行。"); break;
            case ResourceRef r: ResourceProps(r); break;
            default: Help(); break;
        }
        _canvas.ShowLoupe = _tool == Tool.Color || _selected is ResourceRef { Kind: "color" };
        _canvas.InvalidateVisual();
    }

    private void Help()
    {
        PTitle(L.Z("上手清单（先跑通，再动手拼）"), null);
        string[] steps =
        {
            "先在右上角 ⚙ 设置里打开「显示练习靶子」，① 里会出现「场景」和「启动靶子」。",
            "验证「运行」：点「载入示例」→ ①「启动靶子」→ 15 秒内点顶上的「▶ 运行」。跑到一半按 Ctrl+Alt+F12 急停：运行胶囊变成「已急停」，活动里出现「■ 急停」和「运行结束：已取消…」，靶子没有卡键。",
            "再动手拼：「新建」→ 启动靶子，在靶子里摆出要取材的画面（背包：点开一个物品）→ ②「截一帧」。",
            "用 ② 的画布工具建热区、裁模板、点定点、吸色、建网格，建好的都在 ③ 资源里。",
            "从 ④ 左边的积木架点或拖积木进程序；选中积木后在这里改参数，点「试一下」，最后点「▶ 运行」。跑完点「打开结果」看表格和图片。",
        };
        for (int i = 0; i < steps.Length; i++)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Ui.Panel, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var t = Ui.Label(steps[i], 13);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            _props.Children.Add(g);
        }
        Note("也可以点顶上的「载入示例」，打开手写并实测通过的版本，作为对照。");
    }

    /// <summary>属性卡顶部的彩色标题条：积木用它的类别色，资源用墨黑，帮助用浅灰底。</summary>
    private void PTitle(string t, Brush? color)
    {
        _props.Children.Add(new Border
        {
            Background = color ?? Ui.Panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = color == null || !ReferenceEquals(color, Ui.Theme["Ink"]) ? (color == null ? (Brush)Ui.Theme["Ink"] : Brushes.White) : (Brush)Ui.Theme["OnInk"], TextWrapping = TextWrapping.Wrap }
        });
    }
    private void Note(string t) { var l = Ui.Label(t, 12, (Brush)Ui.Theme["Text3"]); l.Margin = new Thickness(0, -2, 0, 10); _props.Children.Add(l); }
    private void Section(string t) => _props.Children.Add(new TextBlock { Text = L.Z(t), FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = (Brush)Ui.Theme["Ink"], Margin = new Thickness(0, 10, 0, 8) });

    /// <summary>一行编辑项：标签在上、控件在下（方案 C 的卡片式表单）。标签英文模式下查表翻译。</summary>
    private void Row(string label, UIElement editor, string? id = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var l = new TextBlock { Text = L.Z(label), Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(l);
        if (id != null) AutomationProperties.SetAutomationId(editor, id);
        AutomationProperties.SetName(editor, label);
        sp.Children.Add(editor);
        _props.Children.Add(sp);
    }

    private void Changed() { _ed.Touch(); RefreshTreeText(); _canvas.InvalidateVisual(); }

    private TextBox Num(double v, Action<double> set, double min, double max, string fmt = "0")
    {
        var tb = new TextBox { Text = v.ToString(fmt, CultureInfo.InvariantCulture) };
        void Commit()
        {
            // 小数一律按「.」解析，和系统区域设置无关
            if (double.TryParse(tb.Text.Replace('，', '.').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            {
                x = Math.Clamp(x, min, max);
                set(x);
                tb.Text = x.ToString(fmt, CultureInfo.InvariantCulture);
                Changed();
            }
            else tb.Text = v.ToString(fmt, CultureInfo.InvariantCulture);
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return tb;
    }

    private TextBox Txt(string v, Action<string> set)
    {
        var tb = new TextBox { Text = v };
        tb.LostFocus += (_, _) => { set(tb.Text); Changed(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { set(tb.Text); Changed(); } };
        return tb;
    }

    private ComboBox Combo(IEnumerable<string> items, string? sel, Action<string> set)
    {
        var cb = new ComboBox();
        foreach (var i in items) cb.Items.Add(i);
        if (sel != null && cb.Items.Contains(sel)) cb.SelectedItem = sel;
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s) { set(s); Changed(); } };
        return cb;
    }

    private CheckBox Check(string text, bool v, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = v, VerticalAlignment = VerticalAlignment.Center };
        c.Click += (_, _) => { set(c.IsChecked == true); Changed(); };
        return c;
    }

    private void BlockProps(Block b)
    {
        string kind = b switch
        {
            RepeatUntilBlock => "重复直到…", RepeatBlock => "重复 N 次", IfBlock => "如果…", WaitUntilBlock => "等到…",
            WaitBlock => "等待", KeyBlock => "按键", ClickBlock => "点击", TypeTextBlock => "输入文字", StopBlock => "停止",
            ForEachCellBlock => "对每一格", UntilNoNewRowsBlock => "重复直到没有新记录", WheelBlock => "滚轮",
            ReadTextBlock => "读文字", SaveImageBlock => "截图存图", RecordRowBlock => "记录一行", _ => b.GetType().Name
        };
        PTitle(L.Z("积木 · ") + L.Z(kind), BlockText.CategoryOf(b) switch { BlockText.Category.Flow => Ui.FlowFg, BlockText.Category.Sense => Ui.SenseFg, _ => Ui.ActFg });
        switch (b)
        {
            case RepeatUntilBlock ru:
                CondEditor("直到这个条件成立就结束循环", ru.Until, c => ru.Until = c);
                Row("多久查一次 ms", Num(ru.CheckEveryMs, v => ru.CheckEveryMs = (int)v, 0, 60000), "pCheckEvery");
                Note("OCR 这种慢识别不用每圈都跑，设 1000 表示每秒查一次，不拖慢循环里的其他判断。");
                Row("超时 ms（0 不限）", Num(ru.TimeoutMs, v => ru.TimeoutMs = (int)v, 0, 3_600_000), "pTimeout");
                break;
            case RepeatBlock r:
                Row("次数", Num(r.Times, v => r.Times = (int)v, 1, 100000), "pTimes");
                break;
            case IfBlock ib:
                CondEditor("条件", ib.Cond, c => ib.Cond = c);
                Note("条件成立时执行缩进在它下面的积木；不成立时执行「否则」分支。");
                break;
            case WaitUntilBlock wu:
                CondEditor("等到这个条件成立", wu.Cond, c => wu.Cond = c);
                Row("最多等 ms", Num(wu.TimeoutMs, v => wu.TimeoutMs = (int)v, 10, 600000), "pWaitTimeout");
                break;
            case WaitBlock w:
                Row("等待 ms", Num(w.Ms, v => w.Ms = (int)v, 0, 600000), "pMs");
                break;
            case KeyBlock k:
                Row("按键", Combo(KeyNames.All.OrderBy(x => x.Length).ThenBy(x => x), k.Key, s => k.Key = s), "pKey");
                Row("按住 ms", Num(k.HoldMs, v => k.HoldMs = (int)v, 10, 2000), "pHold");
                Note("发的是扫描码；按下到抬起之间建议 30–60ms。发之前会检查绑定的窗口在前台，并把它的输入法切到英文。");
                break;
            case ClickBlock c:
            {
                var targets = PlaceChoices();
                string? cur = c.Point != null ? "定点：" + c.Point : c.Region != null ? "热区中心：" + c.Region : null;
                Row("点哪里", Combo(targets, cur, s =>
                {
                    if (s.StartsWith("定点：")) { c.Point = s[3..]; c.Region = null; }
                    else { c.Region = s[5..]; c.Point = null; }
                }), "pClickTarget");
                Row("按住 ms", Num(c.HoldMs, v => c.HoldMs = (int)v, 10, 2000), "pClickHold");
                var modes = new[] { L.Z("不确认"), L.Z("点击处画面有变化"), L.Z("等到条件成立") };
                Row("点完确认", Combo(modes, modes[(int)c.Confirm], s =>
                {
                    c.Confirm = (ConfirmMode)Array.IndexOf(modes, s);
                    if (c.Confirm == ConfirmMode.Condition && c.ConfirmCond == null) c.ConfirmCond = MakeCondition("color");
                    Dispatcher.BeginInvoke(ShowProps);
                }), "pConfirm");
                Note("不要连续盲点固定坐标。点完截图确认一下，点丢了能在日志里看到。");
                if (c.Confirm != ConfirmMode.None)
                    Row("确认最多等 ms", Num(c.ConfirmTimeoutMs, v => c.ConfirmTimeoutMs = (int)v, 50, 10000), "pConfirmTimeout");
                if (c.Confirm == ConfirmMode.Condition)
                    CondEditor("点完等到这个条件成立", c.ConfirmCond, nc => c.ConfirmCond = nc);
                if (c.Point != null && _ed.Program.Points.TryGetValue(c.Point, out var pt)) { _canvas.HighlightPoint = c.Point; }
                break;
            }
            case TypeTextBlock t:
                Row("文字", Txt(t.Text, s => t.Text = s), "pText");
                Note("按 Unicode 字符输入，适合桌面软件的输入框；游戏一般不认。");
                break;
            case StopBlock:
                Note("执行到这里就结束整个程序。");
                break;
            case ForEachCellBlock fe:
                Row("网格", Combo(_ed.Program.Grids.Keys, fe.Grid, s => fe.Grid = s), "pGrid");
                Note("按先行后列的顺序，对网格里的每一格执行一遍缩进在下面的积木。循环里可以用「当前格」：点击当前格中心、在当前格里判断颜色或图像、读当前格的文字。");
                if (_ed.Program.Grids.Count == 0) Note("还没有网格：先用画布上的「拖框建网格」框出第一格。");
                _canvas.HighlightGrid = fe.Grid;
                break;
            case UntilNoNewRowsBlock un:
                Row("最多几遍", Num(un.MaxTimes, v => un.MaxTimes = (int)v, 1, 1000), "pMaxTimes");
                Note("反复执行下面的积木（通常是「逐格点开 → 记录一行 → 滚轮翻页」），直到某一遍一条新记录都没有，就当作翻到底了。最多几遍是保险：去重失效时也不会一直翻下去。");
                break;
            case WheelBlock wb:
            {
                // 2026-11-22：加「按距离 px」模式（边滚边量），冻结帧上预览滚完格子在哪里
                bool byDist = wb.DistancePx != null;
                var modes = new[] { L.Z("按格数"), L.Z("按距离 px（边滚边量）") };
                Row("按什么滚", Combo(modes, modes[byDist ? 1 : 0], s =>
                {
                    if (s == modes[1] && wb.DistancePx == null)
                    {
                        // 默认滚「格数 × 行距」；热区里没有网格就按一格 120 px 估
                        var g = wb.Region != null && _ed.Program.Regions.TryGetValue(wb.Region, out var r0) ? WheelPreview.GridIn(_ed.Program, r0) : null;
                        int pitch = g is { } f ? f.Grid.H + f.Grid.GapY : 120;
                        wb.DistancePx = Math.Sign(wb.Notches == 0 ? 1 : wb.Notches) * pitch * Math.Max(1, Math.Abs(wb.Notches));
                    }
                    else if (s == modes[0]) { wb.DistancePx = null; wb.TolerancePx = null; }
                    Dispatcher.BeginInvoke(ShowProps);
                }), "pWheelMode");
                string? cur = wb.Point != null ? "定点：" + wb.Point : wb.Region != null ? "热区中心：" + wb.Region : null;
                var places = byDist ? PlaceChoices().Where(x => x.StartsWith("热区中心：")).ToList() : PlaceChoices();
                Row("在哪里滚", Combo(places, cur, s =>
                {
                    if (s.StartsWith("定点：")) { wb.Point = s[3..]; wb.Region = null; }
                    else { wb.Region = s[5..]; wb.Point = null; }
                    if (byDist) Dispatcher.BeginInvoke(ShowProps);
                }), "pWheelTarget");
                if (!byDist)
                {
                    Row("格数", Num(wb.Notches, v => wb.Notches = (int)v == 0 ? 1 : (int)v, -50, 50), "pNotches");
                    Note("正数向下滚，负数向上滚。先把鼠标移到这里再滚：很多程序只滚鼠标底下的那块区域。滚一格移动多少由程序自己决定，最好先手动滚一下看看。");
                    Note("想让滚完正好对齐一行，可以切到「按距离 px」：边滚边量画面，还能在截图上预览滚完格子在哪里。");
                    break;
                }
                var hint = Ui.Label(WheelPreview.Hint(_ed.Program, wb), 12);
                AutomationProperties.SetAutomationId(hint, "pWheelHint");
                Row("距离 px（正数向下）", Num(wb.DistancePx!.Value, v => { wb.DistancePx = (int)v == 0 ? 1 : (int)v; hint.Text = WheelPreview.Hint(_ed.Program, wb); }, -20000, 20000), "pDistance");
                hint.Margin = new Thickness(0, -4, 0, 10);
                _props.Children.Add(hint);
                Row("容差 px", Num(wb.TolerancePx ?? WheelBlock.DefaultTolerancePx, v => wb.TolerancePx = (int)v == WheelBlock.DefaultTolerancePx ? null : (int)v, 1, 100), "pTolerance");
                if (wb.Point != null) Note("按距离滚要选热区：在热区里量画面移动了多少。");
                Note("每滚一下都在这个热区里量画面移动了多少，滚够就停。第一格顺便量出这个程序一格滚多少；最后不足一格时发不足一格的滚动，程序不认的话停在最近的整格。画面不动就当作到底了。距离按基准像素（1920×1080）算，和热区坐标一样。");
                Note("热区要框在会滚动的内容里面，别框进不动的标题、边框和滚动条。截图上的橙色箭头和白色虚线格子是推算的滚动结果；实际滚了多少，运行时看遮罩上的「目标 · 已滚」。");
                if (wb.Region != null && _ed.Program.Regions.ContainsKey(wb.Region)) _canvas.HighlightRegion = wb.Region;
                _canvas.ScrollPreview = wb;
                break;
            }
            case ReadTextBlock rt:
            {
                Row("读哪里", Combo(RegionChoices(), rt.Region, s => rt.Region = s), "pReadRegion");
                Row("存进字段", Txt(rt.Field, s => rt.Field = s.Trim()), "pField");
                Row("只要数字", Check(L.En ? "\"库存 23\" keeps \"23\"" : "「库存 23」只留「23」", rt.NumbersOnly, v => rt.NumbersOnly = v), "pNumbersOnly");
                Note("字段就是结果表里的一列。同名字段会被后读到的值覆盖，写一行之后值还保留（比如「分类」读一次，后面每行都带上）。");
                var result = Ui.Label("", 12);
                AutomationProperties.SetAutomationId(result, "readResult");
                var btn = Ui.Btn(L.Z("试读（在冻结帧上）"), () => TryRead(rt, result), "btnTryRead", Ui.SenseFg, Brushes.White);
                btn.HorizontalAlignment = HorizontalAlignment.Left;
                btn.Margin = new Thickness(0, 6, 0, 2);
                _props.Children.Add(btn);
                _props.Children.Add(result);
                if (_ed.Program.Regions.ContainsKey(rt.Region)) _canvas.HighlightRegion = rt.Region;
                break;
            }
            case SaveImageBlock si:
                Row("截哪里", Combo(RegionChoices(), si.Region, s => si.Region = s), "pSaveRegion");
                Row("存进字段", Txt(si.Field, s => si.Field = s.Trim()), "pField");
                Note("截下来的图存在这次运行结果目录的 images 文件夹里，表里这一列记文件路径。");
                if (_ed.Program.Regions.ContainsKey(si.Region)) _canvas.HighlightRegion = si.Region;
                break;
            case RecordRowBlock rr:
            {
                Row("按哪些字段去重", Txt(string.Join("，", rr.DedupeBy), s =>
                    rr.DedupeBy = s.Split(new[] { ',', '，', '、', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()), "pDedupe");
                var fields = VisionBlocks.Core.Runtime.Recorder.FieldsOf(_ed.Program);
                Note("把各字段当前的值写成结果表（data.csv）的一行。列出的字段组合已经写过时跳过，不算新记录。现有字段：" + (fields.Count > 0 ? string.Join("、", fields) : "（还没有，先加「读文字」或「截图存图」）"));
                Note("去重靠读出来的文字：同一画面每次读得一样，但两个名字只差一个容易认错的字时，可能被当成重复。");
                break;
            }
        }
        Section("备注");
        Row("备注", Txt(b.Note ?? "", s => b.Note = string.IsNullOrWhiteSpace(s) ? null : s), "pNote");
    }

    private void CondEditor(string title, Condition? c, Action<Condition> set)
    {
        Section(title);
        var kinds = new[] { L.Z("看到图像"), L.Z("颜色占比"), L.Z("文字包含") };
        string curKind = c switch { ColorCondition => kinds[1], TextCondition => kinds[2], _ => kinds[0] };
        Row("条件类型", Combo(kinds, curKind, s =>
        {
            var nc = MakeCondition(s == kinds[1] ? "color" : s == kinds[2] ? "text" : "image");
            nc.Negate = c?.Negate ?? false;
            set(nc);
            Dispatcher.BeginInvoke(ShowProps);
        }), "pCondKind");
        if (c == null) return;
        var regions = RegionChoices();
        switch (c)
        {
            case ImageCondition i:
                Row("模板", Combo(_ed.Program.Templates.Keys, i.Template, s => i.Template = s), "pTemplate");
                Row("在哪个热区", Combo(regions, i.Region, s => i.Region = s), "pRegion");
                Row("得分阈值", Num(i.Threshold, v => i.Threshold = v, 0.3, 1.0, "0.00"), "pThreshold");
                Row("对比度下限", Num(i.MinContrast, v => i.MinContrast = v, 0, 5, "0.0"), "pMinContrast");
                Note("灰度 TM_CCOEFF_NORMED 不看亮暗：半透明、变暗的同形干扰项也能拿高分，需要「对比度下限」挡住（实测：真 F 约 1.0，半透明 F 约 0.35）。阈值要拿含干扰项的帧试出来。");
                if (_ed.Program.Regions.ContainsKey(i.Region)) _canvas.HighlightRegion = i.Region;
                break;
            case ColorCondition k:
                Row("颜色", Combo(_ed.Program.Colors.Keys, k.Color, s => k.Color = s), "pColor");
                Row("在哪个热区", Combo(regions, k.Region, s => k.Region = s), "pRegion");
                Row("比较", Combo(new[] { ">", "<" }, k.Op, s => k.Op = s), "pOp");
                Row("占比 %", Num(k.Ratio * 100, v => k.Ratio = v / 100, 0, 100, "0.0"), "pRatio");
                Note("统计热区里落在这个颜色范围内的像素比例，比看单个像素稳。");
                if (_ed.Program.Regions.ContainsKey(k.Region)) _canvas.HighlightRegion = k.Region;
                break;
            case TextCondition t:
                Row("在哪个热区", Combo(regions, t.Region, s => t.Region = s), "pRegion");
                Row("包含文字", Txt(t.Contains, s => t.Contains = s), "pContains");
                Note("Windows 自带 OCR（zh-Hans）。比较前会去掉空格：它识别中文时常在字间插空格。");
                if (_ed.Program.Regions.ContainsKey(t.Region)) _canvas.HighlightRegion = t.Region;
                break;
        }
        Row("取反", Check(L.Z("看不到 / 不满足 / 不包含"), c.Negate, v => c.Negate = v), "pNegate");
        var result = Ui.Label("", 12);
        AutomationProperties.SetAutomationId(result, "testResult");
        var btn = Ui.Btn(L.Z("试一下（在冻结帧上）"), () => TestCondition(c, result), "btnTest", Ui.SenseFg, Brushes.White);
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.Margin = new Thickness(0, 6, 0, 2);
        _props.Children.Add(btn);
        _props.Children.Add(result);
    }

    private async void TestCondition(Condition c, TextBlock result)
    {
        if (_frozen == null) { result.Text = "先截一帧"; return; }
        try
        {
            var r = await _tester.TestAsync(_ed.Program, _ed.Dir, c, _frozen, CurrentCellFor(_selected));
            result.Text = r.Detail;
            result.Foreground = r.Hit ? Ui.Ok : Ui.Bad;
            _canvas.TestRoi = r.Roi;
            _canvas.TestBox = r.Box;
            _canvas.TestHit = r.Hit;
            _canvas.InvalidateVisual();
            Log("试一下：" + BlockText.Cond(c, _ed.Program) + " → " + r.Detail);
        }
        catch (Exception e) { result.Text = "出错：" + e.Message; }
    }

    /// <summary>热区下拉框的选项：所有热区，再加上「当前格」（只在「对每一格」里有效，放错地方运行前会报错）。</summary>
    private List<string> RegionChoices() => _ed.Program.Regions.Keys.Append(VbProgram.CurrentCell).ToList();

    private List<string> PlaceChoices() =>
        _ed.Program.Points.Keys.Select(n => "定点：" + n).Concat(RegionChoices().Select(n => "热区中心：" + n)).ToList();

    /// <summary>选中的积木在「对每一格」循环里时，「试一下」「试读」用这个网格的第一格当「当前格」。取最里层的循环。</summary>
    private RegionDef? CurrentCellFor(object? sel)
    {
        if (sel is not Block b) return null;
        static bool Has(List<Block> l, Block t) => l.Any(x => ReferenceEquals(x, t) || (x is IContainerBlock c && Has(c.Body, t)) || (x is IfBlock i && Has(i.Else, t)));
        var loop = _ed.All().OfType<ForEachCellBlock>().LastOrDefault(fe => Has(fe.Body, b));
        return loop != null && _ed.Program.Grids.TryGetValue(loop.Grid, out var g) ? g.Cell(0) : null;
    }

    private async void TryRead(ReadTextBlock rt, TextBlock result)
    {
        if (_frozen == null) { result.Text = "先截一帧"; return; }
        RegionDef? roi = rt.Region == VbProgram.CurrentCell ? CurrentCellFor(rt) : _ed.Program.Regions.GetValueOrDefault(rt.Region);
        if (roi == null) { result.Text = rt.Region == VbProgram.CurrentCell ? "「当前格」要放在「对每一格」循环里才能试" : $"热区「{rt.Region}」不存在"; return; }
        try
        {
            var (raw, norm, ms) = await _tester.ReadAsync(_frozen, roi);
            string value = rt.NumbersOnly ? new string(norm.Where(char.IsAsciiDigit).ToArray()) : norm;
            result.Text = $"{rt.Field} = 「{value}」（原文「{raw}」，{ms:0}ms）" + (rt.Region == VbProgram.CurrentCell ? "；用的是网格第一格" : "");
            result.Foreground = value.Length > 0 ? Ui.Ok : Ui.Bad;
            _canvas.TestRoi = roi;
            _canvas.InvalidateVisual();
            Log($"试读：{BlockText.Summary(rt, _ed.Program)} → 「{value}」");
        }
        catch (Exception e) { result.Text = "出错：" + e.Message; }
    }

    private void ResourceProps(ResourceRef r)
    {
        var p = _ed.Program;
        switch (r.Kind)
        {
            case "region" when p.Regions.TryGetValue(r.Name, out var rg):
                PTitle(L.Z("热区 · ") + r.Name, (Brush)Ui.Theme["Ink"]);
                Row("X", Num(rg.X, v => rg.X = (int)v, 0, 4000), "rX");
                Row("Y", Num(rg.Y, v => rg.Y = (int)v, 0, 4000), "rY");
                Row("宽", Num(rg.W, v => rg.W = (int)v, 1, 4000), "rW");
                Row("高", Num(rg.H, v => rg.H = (int)v, 1, 4000), "rH");
                Note("坐标是客户区基准坐标（按 1920 宽计）。挪窗口、换屏、换缩放都不用改。");
                _canvas.HighlightRegion = r.Name;
                break;
            case "point" when p.Points.TryGetValue(r.Name, out var pt):
                PTitle(L.Z("定点 · ") + r.Name, (Brush)Ui.Theme["Ink"]);
                Row("X", Num(pt.X, v => pt.X = (int)v, 0, 4000), "ptX");
                Row("Y", Num(pt.Y, v => pt.Y = (int)v, 0, 4000), "ptY");
                _canvas.HighlightPoint = r.Name;
                break;
            case "template" when p.Templates.TryGetValue(r.Name, out var td):
            {
                PTitle(L.Z("模板 · ") + r.Name, (Brush)Ui.Theme["Ink"]);
                string file = Path.Combine(_ed.Dir, td.File);
                if (File.Exists(file))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    _props.Children.Add(new System.Windows.Controls.Image { Source = bmp, Width = Math.Min(200, bmp.PixelWidth * 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) });
                    Note($"{bmp.PixelWidth}×{bmp.PixelHeight}，文件 {td.File}");
                }
                else Note("模板文件不存在：" + td.File);
                break;
            }
            case "grid" when p.Grids.TryGetValue(r.Name, out var gd):
                PTitle(L.Z("网格 · ") + r.Name, (Brush)Ui.Theme["Ink"]);
                Row("第一格 X", Num(gd.X, v => gd.X = (int)v, 0, 4000), "gX");
                Row("第一格 Y", Num(gd.Y, v => gd.Y = (int)v, 0, 4000), "gY");
                Row("格宽", Num(gd.W, v => gd.W = (int)v, 1, 4000), "gW");
                Row("格高", Num(gd.H, v => gd.H = (int)v, 1, 4000), "gH");
                Row("列数", Num(gd.Cols, v => gd.Cols = (int)v, 1, 100), "gCols");
                Row("行数", Num(gd.Rows, v => gd.Rows = (int)v, 1, 100), "gRows");
                Row("列间距", Num(gd.GapX, v => gd.GapX = (int)v, 0, 2000), "gGapX");
                Row("行间距", Num(gd.GapY, v => gd.GapY = (int)v, 0, 2000), "gGapY");
                Note("画布上会画出每一格，改数字直到每个框都套住一个格子。只算完整露出来的行；滚动后格子位置不变的列表才能直接用。");
                _canvas.HighlightGrid = r.Name;
                break;
            case "color" when p.Colors.TryGetValue(r.Name, out var cd):
                PTitle(L.Z("颜色 · ") + r.Name, (Brush)Ui.Theme["Ink"]);
                ColorPalette(r.Name, cd);
                Note("OpenCV 口径：H 0–179，S、V 0–255。下限 H 大于上限 H 表示色相跨过 0（红色）。");
                break;
            default:
                Note("资源不存在");
                return;
        }
        var del = Ui.Btn(L.Z("删除这个资源"), () =>
        {
            var uses = _ed.RemoveResource(r.Kind, r.Name);
            if (uses.Count > 0) { Log("还在被这些积木使用，先改掉它们：\n  " + string.Join("\n  ", uses)); return; }
            _selected = null;
            RefreshAll();
        }, "btnDeleteResource", null, Ui.Bad);
        del.HorizontalAlignment = HorizontalAlignment.Left;
        del.Margin = new Thickness(0, 12, 0, 0);
        _props.Children.Add(del);
    }
}
