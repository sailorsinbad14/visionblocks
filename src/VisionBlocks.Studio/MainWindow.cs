using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Model;
using Condition = VisionBlocks.Core.Model.Condition;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Vision;
using VisionBlocks.Core.Win32;
using VisionBlocks.Overlay;
using Point = System.Windows.Point;

namespace VisionBlocks.Studio;

/// <summary>
/// 编辑器主窗口。2026-11-22 改版为方案 C「卡片引导」：左列三张编号步骤卡（① 靶子与窗口 ② 取材 ③ 资源），
/// 中间上画布、下「④ 拼积木」（积木架 + 拼图式程序树），右列属性卡和活动卡；运行状态集中在顶栏中间的运行胶囊，
/// 另有置顶状态胶囊和任务栏按钮颜色（编辑器不在前台时也看得到）。屏幕、窗口尺寸都按当前电脑自动识别，不写死。
/// 业务逻辑（选中同步、插入规则、白名单、画布工具、运行和关窗）沿用改版前的代码。
/// </summary>
public sealed partial class MainWindow : Window
{
    private enum Tool { Select, Region, Point, Template, Color, Ocr, Grid }
    private enum RunUi { Idle, Running, Paused, Blocked, EStop, Done }
    private sealed record ElseMarker(IfBlock Owner);
    private sealed record ResourceRef(string Kind, string Name);

    private ProgramEditor _ed;
    private readonly FrameCanvas _canvas = new();
    private readonly TreeView _tree = new() { AllowDrop = true, BorderThickness = new Thickness(0) };
    private readonly StackPanel _props = new() { Margin = new Thickness(16, 0, 16, 16) };
    private readonly ListBox _resources = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ListBox _log = new() { FontSize = 12, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ComboBox _windows = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _monitor = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _scene = new();
    private readonly Border _practice = new();   // ① 里的练习靶子面板（场景 + 启动靶子），设置里打开才显示
    private bool _suppressBind;
    private readonly TextBlock _status = new() { FontWeight = FontWeights.SemiBold, FontSize = 14 };   // 运行胶囊里的状态字（AutomationId status）
    private readonly TextBlock _capSub = new() { FontSize = 12 };
    private readonly TextBlock _canvasInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Dictionary<Tool, ToggleButton> _toolButtons = new();
    private readonly ConditionTester _tester = new();
    private Button _runBtn = null!, _stopBtn = null!, _openResultsBtn = null!, _openResultsCap = null!;
    private string? _lastResults;   // 上一次运行的结果文件夹（有 data.csv 时是 data-时分秒，否则是这次的日志文件夹）
    private Tool _tool = Tool.Select;
    private NormFrame? _frozen;
    private IntPtr _bound;
    private Process? _launched;
    private string? _sessionDir;
    private StudioRun? _run;
    private object? _selected;
    private Point _dragStart;
    // 2026-11-23：复制粘贴、程序树里拖动
    private BlockClip? _clip;
    private Point _treeDragStart;
    private Block? _treeDragBlock;
    private readonly Canvas _dropLayer = new() { IsHitTestVisible = false };
    private ScrollViewer? _treeSv;
    // 2026-11-23：作用域提示：树上方的位置条、钉在树顶的外层作用域
    private readonly TextBlock _scopeText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _scopeBar = new() { Padding = new Thickness(16, 5, 12, 5) };
    private readonly StackPanel _sticky = new() { VerticalAlignment = VerticalAlignment.Top, AllowDrop = false };
    private static readonly Brush ScopeHi = Ui.Hex("#8F4300");

    // 改版新增
    private readonly ResourceDictionary _theme = Ui.Theme;
    private readonly TextBlock _progName = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _progPath = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _dirty = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1, 8, 1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Border _capsule = new() { Height = 46, CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(1), Padding = new Thickness(16, 0, 6, 0) };
    private readonly System.Windows.Shapes.Ellipse _capDot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _resCount = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<UIElement> _compactHide = new();
    private readonly UniformGrid _tiles = new() { Columns = 4, Margin = new Thickness(0, 0, -6, 2) };
    private ColumnDefinition _leftCol = null!, _rightCol = null!, _shelfCol = null!;
    private RowDefinition _headerRow = null!, _canvasRow = null!, _activityRow = null!;
    private string _density = "";
    private StatusPill? _pill;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _notInFrontTicks;

    public MainWindow(string? programPath)
    {
        int samples = 0;
        try { samples = AppPaths.EnsureSamples(); } catch { }   // 打包后第一次启动：释放示例程序（源码运行时什么也不做）
        try { AppPaths.EnsureDocs(); } catch { }                // 结果格式说明放到 results\ 里
        _ed = programPath != null && File.Exists(programPath) ? ProgramEditor.Open(programPath) : OpenOrCreateDefault();
        _ed.Changed += () => UpdateTitle();
        Resources.MergedDictionaries.Add(_theme);
        FontFamily = (FontFamily)_theme["UiFont"];
        FontSize = 14;
        Background = (Brush)_theme["Bg"];
        Foreground = (Brush)_theme["Ink"];
        UseLayoutRounding = true;
        SizeToScreen();
        Content = BuildLayout();
        AutomationProperties.SetAutomationId(_log, "log");               // 给界面自动化测试（tools/ui.ps1）定位用
        AutomationProperties.SetAutomationId(_canvasInfo, "canvasInfo");
        TaskbarItemInfo = new TaskbarItemInfo();
        UpdateTitle();
        RefreshAll();
        RefreshMonitors();
        RefreshWindows();
        ApplyRunUi(RunUi.Idle);
        Closing += OnClosing;
        PreviewKeyDown += OnShortcutKey;   // 快捷键（2026-11-23）
        SizeChanged += (_, _) => ApplyDensity();
        _poll.Tick += (_, _) => PollRun();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; };
        Ui.DarkTitleBar(this);
        StartupNotices();
        if (samples > 0) Log($"第一次启动：示例程序已放到 {AppPaths.Samples}（{samples} 个文件），点「载入示例」打开巡逻示例");
        Log($"已打开程序：{_ed.ProgramPath}");    }

    /// <summary>启动时在活动里说清楚：数据放在哪、便携版有没有退回「文档」、这台电脑有没有中文 OCR。</summary>
    private void StartupNotices()
    {
        string mode = AppPaths.Mode switch { AppPaths.Kind.Source => "源码运行", AppPaths.Kind.Portable => "便携文件夹", _ => "单个 exe / 用户目录" };
        Log($"数据目录（{mode}）：{AppPaths.Root}；设置：{AppPaths.SettingsFile}");
        if (AppPaths.Warning != null) Log("⚠ " + AppPaths.Warning);
        try
        {
            if (!OcrReader.HasLanguage("zh-Hans"))
                Log("⚠ 这台电脑没装中文（简体）OCR，「读文字」「文字包含」会读成乱码。安装：设置 → 时间和语言 → 语言和区域 → 添加语言 → 中文（简体），装好后重开编辑器");
        }
        catch (Exception e) { Log("⚠ 查不到 OCR 语言：" + e.Message); }
    }

    private static ProgramEditor OpenOrCreateDefault()
    {
        string path = Path.Combine(AppPaths.Programs, "my-t1", "program.vbp.json");
        return File.Exists(path) ? ProgramEditor.Open(path) : ProgramEditor.CreateNew(Path.GetDirectoryName(path)!, "我的巡逻程序");
    }

    private void UpdateTitle()
    {
        // 标题栏只写应用名；程序名、相对路径和「未保存」都在顶栏左上角，鼠标停在路径上看完整路径
        Title = L.T("appName");
        _progName.Text = _ed.Program.Name;
        _progPath.Text = Path.GetRelativePath(AppPaths.Root, _ed.ProgramPath);
        _progPath.ToolTip = _ed.ProgramPath;
        _dirty.Visibility = _ed.Dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private Brush R(string key) => (Brush)_theme[key];
    private Style S(string key) => (Style)_theme[key];

    // ───────────────────────── 窗口尺寸（按所在屏自动算，不写死） ─────────────────────────

    /// <summary>窗口开在鼠标所在的屏上，尺寸按那块屏的工作区算（WindowSizing 是纯函数，vbrun selftest-sizing 有测试）。</summary>
    private void SizeToScreen()
    {
        // 测试用：环境变量 VB_STUDIO_SCREEN=设备名（如 \\.\DISPLAY1）指定开在哪块屏，不靠鼠标位置（自动化测试不能挪鼠标）
        string? forced = Environment.GetEnvironmentVariable("VB_STUDIO_SCREEN");
        var scr = (forced != null ? Screens.All().FirstOrDefault(s => string.Equals(s.Device, forced, StringComparison.OrdinalIgnoreCase)) : null) ?? Screens.UnderCursor();
        var sz = WindowSizing.Initial(scr?.WorkDipW ?? 1600, scr?.WorkDipH ?? 952);
        Width = sz.W; Height = sz.H;
        MinWidth = sz.MinW; MinHeight = sz.MinH;
        WindowStartupLocation = scr == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        // 混合 DPI 下 WPF 换算 Left/Top 不可靠：窗口句柄建好后按物理像素摆到那块屏的工作区中间，需要最大化的再最大化（在那块屏上）
        // 窗口句柄先建在主屏上；挪到缩放不同的屏时 Windows 会按 DPI 比例把窗口缩一次，所以启动阶段遇到 DPI 变化要再摆一次
        bool starting = true;
        void Place()
        {
            if (scr == null) { if (sz.Maximize) WindowState = WindowState.Maximized; return; }
            if (WindowState == WindowState.Maximized) return;
            var h = new WindowInteropHelper(this).Handle;
            int w = (int)Math.Round(sz.W * scr.Dpi / 96.0), hh = (int)Math.Round(sz.H * scr.Dpi / 96.0);
            if (sz.Maximize) { w = (int)(scr.Work.W * 0.8); hh = (int)(scr.Work.H * 0.8); }   // 还原时的大小
            Native.SetWindowPos(h, IntPtr.Zero, scr.Work.X + (scr.Work.W - w) / 2, scr.Work.Y + (scr.Work.H - hh) / 2, w, hh, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            if (sz.Maximize) WindowState = WindowState.Maximized;
        }
        SourceInitialized += (_, _) => { Place(); Dispatcher.BeginInvoke(() => starting = false, DispatcherPriority.ApplicationIdle); };
        DpiChanged += (_, _) => { if (starting) Dispatcher.BeginInvoke(Place, DispatcherPriority.Background); };
    }

    /// <summary>界面按窗口宽度分档（不按屏幕型号）：紧凑 &lt; 1500 DIP ≤ 标准 &lt; 2100 ≤ 宽。原地改列宽，不重建界面。</summary>
    private void ApplyDensity()
    {
        string d = ActualWidth < WindowSizing.CompactBelowW || ActualHeight < WindowSizing.CompactBelowH ? "compact" : ActualWidth >= 2100 ? "wide" : "standard";
        if (d == _density) return;
        _density = d;
        bool c = d == "compact", w = d == "wide";
        _leftCol.Width = new GridLength(c ? 264 : w ? 340 : 304);
        _rightCol.Width = new GridLength(c ? 292 : w ? 430 : 340);
        _shelfCol.Width = new GridLength(c ? 150 : w ? 230 : 196);
        _activityRow.Height = new GridLength(c ? 168 : w ? 320 : 222);
        _headerRow.Height = new GridLength(c ? 56 : 64);
        _canvasRow.Height = new GridLength(c ? 1 : 1.2, GridUnitType.Star);
        _tiles.Columns = c ? 7 : 4;
        foreach (var (el, normal, compact, isPad) in _tight)
        {
            if (isPad && el is Border b) b.Padding = c ? compact : normal;
            else el.Margin = c ? compact : normal;
        }
        foreach (var e in _compactHide) e.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───────────────────────── 布局 ─────────────────────────

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(_headerRow = new RowDefinition { Height = new GridLength(64) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = BuildHeader();
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(12, 0, 12, 12) };
        body.ColumnDefinitions.Add(_leftCol = new ColumnDefinition { Width = new GridLength(304) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(_rightCol = new ColumnDefinition { Width = new GridLength(340) });
        Grid.SetRow(body, 1);
        var steps = BuildSteps();
        var center = BuildCenter(); Grid.SetColumn(center, 2);
        var right = BuildRight(); Grid.SetColumn(right, 4);
        body.Children.Add(steps); body.Children.Add(center); body.Children.Add(right);
        root.Children.Add(body);
        return root;
    }

    private Border Card(UIElement child, Thickness? pad = null) => new() { Style = S("Card"), Child = child, Padding = pad ?? new Thickness(0) };

    // 紧凑档收紧的间距：登记（元素, 标准值, 紧凑值），ApplyDensity 时统一切换
    private readonly List<(FrameworkElement El, Thickness Normal, Thickness Compact, bool IsPadding)> _tight = new();
    private T Tight<T>(T e, Thickness normal, Thickness compact) where T : FrameworkElement { e.Margin = normal; _tight.Add((e, normal, compact, false)); return e; }
    private Border TightPad(Border b, Thickness normal, Thickness compact) { b.Padding = normal; _tight.Add((b, normal, compact, true)); return b; }

    private TextBlock Txt(string key, double size = 14, string brush = "Ink", FontWeight? weight = null) =>
        L.Reg(new TextBlock { FontSize = size, Foreground = R(brush), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, TextBlock.TextProperty, key);

    /// <summary>带图标的按钮。文字用登记表管理，切语言时原地换；compactHide=true 时紧凑档只留图标（提示里仍有全名）。</summary>
    private Button IconBtn(string icon, string key, Action onClick, string aid, string? style = null, bool compactHide = false, string? action = null)
    {
        var t = Txt(key, 14);
        // 文字跟随按钮前景色：墨黑底按钮（启动靶子、截一帧、运行）上是白字
        t.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var b = new Button { Content = Icons.WithText(icon, t) };
        if (style != null) b.Style = S(style);
        if (action != null) L.RegF(b, ToolTipProperty, () => L.T(key) + AppSettings.Suffix(action));   // 提示里带快捷键（2026-11-23）
        else L.Reg(b, ToolTipProperty, key);
        L.Reg(b, AutomationProperties.NameProperty, key);
        AutomationProperties.SetAutomationId(b, aid);
        b.Click += (_, _) => onClick();
        if (compactHide) _compactHide.Add(t);
        return b;
    }

    private StackPanel StepHead(string number, string key, UIElement? right = null)
    {
        var sp = Tight(new DockPanel { LastChildFill = false }, new Thickness(0, 0, 0, 10), new Thickness(0, 0, 0, 6));
        var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(8), Background = R("Ink"), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = number, Foreground = R("OnInk"), FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        sp.Children.Add(badge);
        var t = Txt(key, 15, "Ink", FontWeights.SemiBold);
        t.FontFamily = (FontFamily)_theme["DisplayFont"];
        sp.Children.Add(t);
        if (right != null) { DockPanel.SetDock(right, Dock.Right); sp.Children.Add(right); }
        return new StackPanel { Children = { sp } };
    }

    private UIElement Field(string key, UIElement control)
    {
        var sp = Tight(new StackPanel(), new Thickness(0, 0, 0, 8), new Thickness(0, 0, 0, 6));
        var label = Txt(key, 12, "Text2");
        label.Margin = new Thickness(0, 0, 0, 4);
        _compactHide.Add(label);
        sp.Children.Add(label);
        sp.Children.Add(control);
        return sp;
    }

    private UIElement BuildHeader()
    {
        // 列：logo | 程序名 | 文件按钮 | 弹性 | 运行胶囊 | 弹性 | 热键提示 | 设置
        var g = new Grid { Margin = new Thickness(16, 0, 12, 0) };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Add(UIElement e, int c) { Grid.SetColumn(e, c); g.Children.Add(e); }

        var logo = Icons.AppLogo(36);   // 应用图标（取景积木），换掉原型里写着「VB」的占位块
        logo.Margin = new Thickness(0, 0, 10, 0);
        logo.VerticalAlignment = VerticalAlignment.Center;
        L.Reg(logo, ToolTipProperty, "appName");
        AutomationProperties.SetAutomationId(logo, "appLogo");
        Add(logo, 0);

        _progName.Foreground = R("Ink");
        _progPath.Foreground = R("Text3");
        _dirty.Background = R("WarnSoft");
        _dirty.Child = Txt("unsaved", 11, "Ink");
        ((TextBlock)_dirty.Child).Foreground = R("Warn");
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(_progName);
        nameRow.Children.Add(_dirty);
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Margin = new Thickness(0, 0, 12, 0) };
        name.Children.Add(nameRow);
        name.Children.Add(_progPath);
        Add(name, 1);

        var files = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new[]
        {
            IconBtn("new", "btnNew", NewProgram, "btnNew", compactHide: true, action: "new"),
            IconBtn("open", "btnOpen", OpenProgram, "btnOpen", compactHide: true, action: "open"),
            IconBtn("save", "btnSave", SaveProgram, "btnSave", compactHide: true, action: "save"),
            IconBtn("sample", "btnSample", LoadSample, "btnSample", compactHide: true, action: "sample"),
            IconBtn("undo", "btnUndo", UndoEdit, "btnUndo", compactHide: true, action: "undo"),
            IconBtn("redo", "btnRedo", RedoEdit, "btnRedo", compactHide: true, action: "redo"),
        })
        { b.Margin = new Thickness(0, 0, 6, 0); files.Children.Add(b); }
        Add(files, 2);

        Add(BuildCapsule(), 4);

        var hk = new TextBlock { FontSize = 11, Foreground = R("Text3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0), LineHeight = 15 };
        L.RegF(hk, TextBlock.TextProperty, () => $"{AppSettings.KeyOf(Keymap.Pause)}  {L.T("hkPause")}\n{AppSettings.KeyOf(Keymap.EStop)}  {L.T("hkStop")}");
        AutomationProperties.SetAutomationId(hk, "hotkeyHint");
        Add(hk, 6);

        var gear = new Button { Content = Icons.Get("gear", 18), Width = 40, Padding = new Thickness(0) };
        L.RegF(gear, ToolTipProperty, () => L.T("settings") + AppSettings.Suffix("settings"));
        L.Reg(gear, AutomationProperties.NameProperty, "settings");
        AutomationProperties.SetAutomationId(gear, "btnSettings");
        gear.Click += (_, _) => OpenSettings();
        Add(gear, 7);
        return g;
    }

    private UIElement BuildCapsule()
    {
        _runBtn = IconBtn("play", "btnRun", Run, "btnRun", "PillButton", action: "run");
        _runBtn.Background = R("Ink"); _runBtn.Foreground = R("OnInk");
        _runBtn.Margin = new Thickness(12, 0, 4, 0);
        _runBtn.Height = 34;
        _stopBtn = IconBtn("stop", "btnStop", () => _run?.Stop(), "btnStop", "PillButton", action: "stop");
        _stopBtn.Height = 34;
        _stopBtn.IsEnabled = false;
        // 运行胶囊旁的「打开结果」：只有图标，有结果时才出现
        _openResultsCap = new Button { Content = Icons.Get("open", 16), Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), Style = S("PillButton"), Visibility = Visibility.Collapsed };
        L.Reg(_openResultsCap, ToolTipProperty, "openResultsTip");
        L.Reg(_openResultsCap, AutomationProperties.NameProperty, "openResults");
        AutomationProperties.SetAutomationId(_openResultsCap, "btnOpenResultsCap");
        _openResultsCap.Click += (_, _) => OpenResults();
        AutomationProperties.SetAutomationId(_status, "status");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
        text.Children.Add(_status);
        text.Children.Add(_capSub);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_capDot);
        row.Children.Add(text);
        row.Children.Add(_runBtn);
        row.Children.Add(_stopBtn);
        row.Children.Add(_openResultsCap);
        _capsule.Child = row;
        _capsule.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        return _capsule;
    }

    private UIElement BuildSteps()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });

        // ① 靶子与窗口
        var s1 = StepHead("1", "step1");
        AutomationProperties.SetAutomationId(_monitor, "monitor");
        L.Reg(_monitor, ToolTipProperty, "monitor");
        s1.Children.Add(Field("monitor", _monitor));
        // 练习靶子（场景 + 启动靶子）：收进设置里的「显示练习靶子」，默认不显示
        _scene.Items.Add(L.Reg(new ComboBoxItem(), ContentControl.ContentProperty, "scenePatrol"));
        _scene.Items.Add(L.Reg(new ComboBoxItem(), ContentControl.ContentProperty, "sceneInventory"));
        _scene.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_scene, "scene");
        L.Reg(_scene, ToolTipProperty, "scene");
        var practice = new StackPanel();
        practice.Children.Add(Tight(Txt("practiceHead", 12, "Text2"), new Thickness(0, 0, 0, 6), new Thickness(0, 0, 0, 4)));
        practice.Children.Add(Field("scene", _scene));
        var launch = IconBtn("target", "btnLaunch", LaunchTarget, "btnLaunch", "InkButton", action: "launch");
        launch.Margin = new Thickness(0);
        practice.Children.Add(launch);
        _practice.Child = practice;
        _practice.Padding = new Thickness(10);
        _practice.CornerRadius = new CornerRadius(12);
        _practice.Background = R("Card2");
        _practice.BorderBrush = R("Stroke2");
        _practice.BorderThickness = new Thickness(1);
        Tight(_practice, new Thickness(0, 0, 0, 10), new Thickness(0, 0, 0, 6));
        ApplyPracticeVisibility();
        s1.Children.Add(_practice);
        AutomationProperties.SetAutomationId(_windows, "bindWindow");
        L.Reg(_windows, ToolTipProperty, "bindTip");
        _windows.SelectionChanged += (_, _) => OnBindSelected();
        s1.Children.Add(Field("bind", _windows));
        var refresh = IconBtn("refresh", "btnRefresh", RefreshScreens, "btnRefresh", action: "refresh");
        refresh.HorizontalAlignment = HorizontalAlignment.Right;
        s1.Children.Add(refresh);
        var c1 = TightPad(Card(s1), new Thickness(16, 14, 16, 16), new Thickness(12, 10, 12, 10));
        g.Children.Add(c1);

        // ② 取材
        var s2 = StepHead("2", "step2");
        var freeze = IconBtn("camera", "btnFreeze", Freeze, "btnFreeze", "InkButton", action: "freeze");
        Tight(freeze, new Thickness(0, 0, 0, 10), new Thickness(0, 0, 0, 6));
        s2.Children.Add(freeze);
        var tiles = _tiles;
        foreach (var (t, icon, key) in new[] { (Tool.Select, "select", "toolSelect"), (Tool.Region, "region", "toolRegion"), (Tool.Point, "point", "toolPoint"), (Tool.Template, "template", "toolTemplate"), (Tool.Color, "color", "toolColor"), (Tool.Ocr, "ocr", "toolOcr"), (Tool.Grid, "grid", "toolGrid") })
        {
            var label = Txt(key, 11);
            label.TextAlignment = TextAlignment.Center;
            label.TextWrapping = TextWrapping.Wrap;   // 中文 5 个字一行放得下；英文（Draw region 等）折成两行
            _compactHide.Add(label);   // 紧凑档：工具格只留图标，排成一行（提示里有全名和用法）
            label.Foreground = null;   // 跟随按钮前景（选中时变白）
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1) });
            var content = new StackPanel();
            var ic = Icons.Get(icon, 18);
            ic.Margin = new Thickness(0, 0, 0, 3);
            content.Children.Add(ic);
            content.Children.Add(label);
            var tb = new ToggleButton { Style = S("ToolTile"), Content = content, IsChecked = t == Tool.Select };
            L.Reg(tb, AutomationProperties.NameProperty, key);
            L.RegF(tb, ToolTipProperty, () => L.T("hint_" + key) + AppSettings.Suffix("tool" + t));
            AutomationProperties.SetAutomationId(tb, key);
            tb.Click += (_, _) => SetTool(t);
            _toolButtons[t] = tb;
            tiles.Children.Add(tb);
        }
        s2.Children.Add(tiles);
        _canvasInfo.Foreground = R("Text3");
        s2.Children.Add(_canvasInfo);
        _compactHide.Add(_canvasInfo);   // 紧凑档省一块高度：用法仍在每个工具格的提示里
        var c2 = TightPad(Card(s2), new Thickness(16, 14, 16, 14), new Thickness(12, 10, 12, 8));
        Grid.SetRow(c2, 2);
        g.Children.Add(c2);

        // ③ 资源：两列卡片，带缩略图
        _resCount.Foreground = R("Text3");
        var head3 = StepHead("3", "step3", _resCount);
        AutomationProperties.SetAutomationId(_resources, "resources");
        L.Reg(_resources, AutomationProperties.NameProperty, "step3");
        _resources.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(UniformGrid)) { });
        _resources.ItemsPanel.VisualTree.SetValue(UniformGrid.ColumnsProperty, 2);
        _resources.ItemsPanel.VisualTree.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        ScrollViewer.SetHorizontalScrollBarVisibility(_resources, ScrollBarVisibility.Disabled);
        _resources.Margin = new Thickness(0, 0, -8, 0);
        _resources.SelectionChanged += (_, _) =>
        {
            if (_resources.SelectedItem is ListBoxItem { Tag: ResourceRef r }) { _selected = r; ShowProps(); }
        };
        var s3 = new DockPanel();
        DockPanel.SetDock(head3, Dock.Top);
        s3.Children.Add(head3);
        s3.Children.Add(_resources);
        var c3 = TightPad(Card(s3), new Thickness(16, 14, 16, 8), new Thickness(12, 10, 12, 6));
        Grid.SetRow(c3, 4);
        g.Children.Add(c3);
        // 屏幕很矮时（比最小尺寸还小、窗口最大化的情况）整列可以滚动。
        // 够高时把这一列钉在可视高度上，③ 资源卡片吃掉剩下的空间、自己内部滚动；不够高时 ③ 至少留 140，整列滚。
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = g };
        void Fit()
        {
            double need = c1.DesiredSize.Height + c2.DesiredSize.Height + 24 + 140;
            double h = Math.Max(sv.ActualHeight, need);
            if (sv.ActualHeight > 0 && (double.IsNaN(g.Height) || Math.Abs(g.Height - h) > 0.5)) g.Height = h;
        }
        sv.SizeChanged += (_, _) => Fit();
        sv.Loaded += (_, _) => Fit();
        c1.SizeChanged += (_, _) => Fit();
        c2.SizeChanged += (_, _) => Fit();
        return sv;
    }

    private UIElement BuildPalette()
    {
        var sp = new StackPanel { Margin = new Thickness(10, 10, 10, 12) };
        void Group(string key, (string Type, string Text)[] items, Brush color)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
            head.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = color, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(Txt(key, 12, "Text2", FontWeights.SemiBold));
            sp.Children.Add(head);
            foreach (var (type, text) in items)
            {
                var label = L.RegF(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White }, TextBlock.TextProperty, () => L.Z(text));
                var b = new Button { Style = S("PaletteBlock"), Content = label, Background = color, Tag = type };
                AutomationProperties.SetAutomationId(b, "pal_" + type);
                b.Click += (_, _) => InsertFromPalette(type);
                b.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
                b.PreviewMouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed) return;
                    var d = e.GetPosition(this) - _dragStart;
                    if (Math.Abs(d.X) + Math.Abs(d.Y) < 8) return;
                    DragDrop.DoDragDrop(b, new DataObject("vb-block", type), DragDropEffects.Copy);
                };
                sp.Children.Add(b);
            }
            sp.Children.Add(new Border { Height = 6 });
        }
        Group("grpFlow", new[] { ("repeatUntil", "重复直到…"), ("repeat", "重复 N 次"), ("if", "如果…"), ("waitUntil", "等到…"), ("stop", "停止"),
            ("forEachCell", "对每一格"), ("untilNoNewRows", "重复直到没有新记录") }, Ui.FlowFg);
        Group("grpSense", new[] { ("cond:image", "看到图像"), ("cond:color", "颜色占比"), ("cond:text", "文字包含") }, Ui.SenseFg);
        Group("grpRecord", new[] { ("readText", "读文字"), ("saveImage", "截图存图"), ("recordRow", "记录一行") }, Ui.SenseFg);
        Group("grpAct", new[] { ("click", "点击"), ("key", "按键"), ("typeText", "输入文字"), ("wait", "等待"), ("wheel", "滚轮"), ("drag", "拖动") }, Ui.ActFg);
        sp.Children.Add(Txt("palHint", 11.5, "Text3"));
        return new Border
        {
            Background = R("Card2"), BorderBrush = R("Stroke"), BorderThickness = new Thickness(0, 0, 1, 0), CornerRadius = new CornerRadius(0, 0, 0, 16),
            Child = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
    }

    private UIElement BuildCenter()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(_canvasRow = new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star), MinHeight = 160 });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 220 });

        _canvas.Margin = new Thickness(0);
        _canvas.Program = _ed.Program;
        _canvas.BaseDown += OnCanvasDown;
        _canvas.BaseMove += OnCanvasMove;
        _canvas.BaseUp += OnCanvasUp;
        AutomationProperties.SetAutomationId(_canvas, "frameCanvas");
        grid.Children.Add(Card(_canvas, new Thickness(8)));

        var split = new GridSplitter { Height = 12, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.SizeNS };
        Grid.SetRow(split, 1);
        grid.Children.Add(split);

        // ④ 拼积木：标题和按钮一行，下面左边积木架、右边程序
        var head = new DockPanel { Margin = new Thickness(16, 0, 12, 0), Height = 50, LastChildFill = false };
        var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(8), Background = R("Ink"), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = "4", Foreground = R("OnInk"), FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        head.Children.Add(badge);
        var title = Txt("step4", 15, "Ink", FontWeights.SemiBold);
        title.FontFamily = (FontFamily)_theme["DisplayFont"];
        head.Children.Add(title);
        var hint = Txt("programHint", 12, "Text3");
        hint.Margin = new Thickness(10, 0, 0, 0);
        _compactHide.Add(hint);
        head.Children.Add(hint);
        var moves = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new[]
        {
            IconBtn("copy", "btnCopy", CopySelected, "btnCopy", compactHide: true, action: "copy"),
            IconBtn("paste", "btnPaste", PasteAtSelection, "btnPaste", compactHide: true, action: "paste"),
            IconBtn("up", "btnUp", MoveSelectedUp, "btnUp", compactHide: true, action: "moveUp"),
            IconBtn("down", "btnDown", MoveSelectedDown, "btnDown", compactHide: true, action: "moveDown"),
            IconBtn("indent", "btnIndent", IndentSelected, "btnIndent", compactHide: true, action: "indent"),
            IconBtn("outdent", "btnOutdent", OutdentSelected, "btnOutdent", compactHide: true, action: "outdent"),
            IconBtn("trash", "btnDelete", DeleteSelected, "btnDelete", compactHide: true, action: "delete"),
        })
        { b.Margin = new Thickness(4, 0, 0, 0); b.MinHeight = 32; moves.Children.Add(b); }
        ((Button)moves.Children[^1]).Foreground = Ui.Bad;
        DockPanel.SetDock(moves, Dock.Right);
        head.Children.Add(moves);

        AutomationProperties.SetAutomationId(_tree, "programTree");
        _tree.Background = DotGrid();
        _tree.Padding = new Thickness(16, 14, 12, 24);
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);   // 积木文字按宽度换行
        _tree.SelectedItemChanged += (_, _) => { if (_tree.SelectedItem is TreeViewItem { Tag: var tag }) { _selected = tag; ShowProps(); } };
        // 2026-11-20 实测：先点了资源或画布，再点回树里原本就高亮的那块，SelectedItemChanged 不触发，
        // _selected 还停在资源上，按「删除」提示「先在程序树里选中一块积木」。点击或获得焦点时按树的当前选中项再同步一次。
        void SyncTreeSelection() { if (_tree.SelectedItem is TreeViewItem { Tag: var tag } && !ReferenceEquals(tag, _selected)) { _selected = tag; ShowProps(); } }
        _tree.PreviewMouseLeftButtonUp += (_, _) => SyncTreeSelection();
        _tree.GotKeyboardFocus += (_, _) => SyncTreeSelection();
        _tree.Drop += OnTreeDrop;
        _tree.DragOver += OnTreeDragOver;
        _tree.DragLeave += (_, e) =>
        {
            // DragLeave 会从树里的子元素冒泡上来：鼠标还在树的范围里就不清，免得指示线和位置条一闪一闪
            var p = e.GetPosition(_tree);
            if (p.X < 0 || p.Y < 0 || p.X > _tree.ActualWidth || p.Y > _tree.ActualHeight) ClearDropHint();
        };
        // 2026-11-23：树里的积木可以拖（按住 Ctrl 松开 = 复制一份过去）；右键菜单；右键先选中鼠标下那一块
        _tree.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _treeDragStart = e.GetPosition(_tree);
            _treeDragBlock = ItemUnder(e.OriginalSource)?.Tag as Block;
        };
        _tree.PreviewMouseMove += OnTreeMouseMove;
        _tree.PreviewMouseRightButtonDown += (_, e) => { if (ItemUnder(e.OriginalSource) is { } it) { it.IsSelected = true; it.Focus(); } };
        _tree.ContextMenu = BuildTreeMenu();

        var body = new Grid();
        body.ColumnDefinitions.Add(_shelfCol = new ColumnDefinition { Width = new GridLength(196) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(BuildPalette());
        // 位置条和钉顶层都是程序树的兄弟控件，不放进树里（ui.ps1 读树里的全部文字）
        _scopeBar.Child = _scopeText;
        // WPF 的 Border / StackPanel 不进 UI 自动化树，id 放在里面的文字上：位置条 scopeBar，钉顶的每一行 stickyScope
        AutomationProperties.SetAutomationId(_scopeText, "scopeBar");
        var treeArea = new Grid { Children = { _tree, _dropLayer, _sticky } };
        var treeDock = new DockPanel();
        DockPanel.SetDock(_scopeBar, Dock.Top);
        treeDock.Children.Add(_scopeBar);
        treeDock.Children.Add(treeArea);
        _tree.Loaded += (_, _) =>
        {
            _treeSv ??= FindChild<ScrollViewer>(_tree);
            if (_treeSv != null) _treeSv.ScrollChanged += (_, _) => UpdateSticky();
        };
        var treeHost = new Border { CornerRadius = new CornerRadius(0, 0, 16, 0), ClipToBounds = true, Child = treeDock };
        Grid.SetColumn(treeHost, 1);
        body.Children.Add(treeHost);

        var script = new DockPanel();
        var headBorder = new Border { BorderBrush = R("Stroke"), BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
        DockPanel.SetDock(headBorder, Dock.Top);
        script.Children.Add(headBorder);
        script.Children.Add(body);
        var scriptCard = Card(script);
        Grid.SetRow(scriptCard, 2);
        grid.Children.Add(scriptCard);
        return grid;
    }

    /// <summary>脚本区背景的点阵（每 16 DIP 一个点），给拼图积木一个「工作台」的感觉。</summary>
    private Brush DotGrid()
    {
        var dot = new GeometryDrawing(R("Stroke2"), null, new EllipseGeometry(new Point(1, 1), 1, 1));
        var b = new DrawingBrush(new DrawingGroup { Children = { new GeometryDrawing(R("CardBg"), null, new RectangleGeometry(new Rect(0, 0, 16, 16))), dot } })
        { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        return b;   // 不冻结：里面的底色、点色跟主题变
    }

    private UIElement BuildRight()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        g.RowDefinitions.Add(_activityRow = new RowDefinition { Height = new GridLength(222) });

        var propsDock = new DockPanel();
        var ph = Txt("props", 15, "Ink", FontWeights.SemiBold);
        ph.FontFamily = (FontFamily)_theme["DisplayFont"];
        ph.Margin = new Thickness(16, 14, 16, 8);
        DockPanel.SetDock(ph, Dock.Top);
        propsDock.Children.Add(ph);
        propsDock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _props });
        g.Children.Add(Card(propsDock));

        var logDock = new DockPanel();
        var lhRow = new DockPanel { Margin = new Thickness(16, 10, 12, 4) };
        var lh = Txt("activity", 15, "Ink", FontWeights.SemiBold);
        lh.FontFamily = (FontFamily)_theme["DisplayFont"];
        // 运行结束后打开这次的结果文件夹
        _openResultsBtn = IconBtn("open", "openResults", OpenResults, "btnOpenResults", action: "openResults");
        L.Reg(_openResultsBtn, ToolTipProperty, "openResultsTip");
        _openResultsBtn.Height = 30;
        _openResultsBtn.FontSize = 12;
        _openResultsBtn.IsEnabled = false;
        DockPanel.SetDock(_openResultsBtn, Dock.Right);
        lhRow.Children.Add(_openResultsBtn);
        lhRow.Children.Add(lh);
        DockPanel.SetDock(lhRow, Dock.Top);
        logDock.Children.Add(lhRow);
        _log.ItemContainerStyle = S("LogLine");
        _log.Margin = new Thickness(12, 0, 8, 10);
        _log.FontFamily = (FontFamily)_theme["UiFont"];
        _log.ItemTemplate = null;
        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Disabled);
        // 日志项保持纯字符串（ui.ps1 按 Name 读），只让它们换行显示
        var tf = new FrameworkElementFactory(typeof(TextBlock));
        tf.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        tf.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        _log.ItemTemplate = new DataTemplate { VisualTree = tf };
        logDock.Children.Add(_log);
        var act = Card(logDock);
        Grid.SetRow(act, 2);
        g.Children.Add(act);
        return g;
    }

    // ───────────────────────── 运行状态：运行胶囊 + 置顶胶囊 + 任务栏 ─────────────────────────

    private void ApplyRunUi(RunUi s, string? sub = null, int blocked = 0)
    {
        string title = s switch
        {
            RunUi.Running => L.T("st_running"), RunUi.Paused => L.T("st_paused"),
            RunUi.Blocked => L.T("st_blocked") + (blocked > 0 ? $" ×{blocked}" : ""), RunUi.EStop => L.T("st_estop"),
            RunUi.Done => L.T("st_done"), _ => L.T("st_idle")
        };
        sub ??= s switch
        {
            RunUi.Running => L.T("sd_running") + (blocked > 0 ? $" · {(L.En ? "blocked" : "拦截")} {blocked}" : ""),
            RunUi.Paused => string.Format(L.T("sd_paused"), AppSettings.KeyOf(Keymap.Pause)), RunUi.Blocked => L.T("sd_blocked"), RunUi.EStop => L.T("sd_estop"),
            _ => _bound != IntPtr.Zero && WindowInfo.IsAlive(_bound) ? L.T("sd_idleBound") : L.T("sd_idleUnbound")
        };
        _status.Text = title;
        _capSub.Text = sub;
        Brush bg, border, fg = R("Ink"), subFg = R("Text2"), dot;
        switch (s)
        {
            case RunUi.Running: bg = R("OkSoft"); border = Ui.Ok; dot = Ui.Ok; break;
            case RunUi.Paused: bg = R("WarnSoft"); border = R("Warn"); dot = R("Warn"); break;
            case RunUi.Blocked: bg = R("Alarm"); border = R("Alarm"); fg = Brushes.White; subFg = Ui.Hex("#FFE1DE"); dot = Brushes.White; break;
            case RunUi.EStop: bg = R("BadSoft"); border = Ui.Bad; dot = Ui.Bad; break;
            default: bg = R("CardBg"); border = R("Stroke"); dot = R("Text3"); break;
        }
        _capsule.Background = bg; _capsule.BorderBrush = border;
        _status.Foreground = fg; _capSub.Foreground = subFg; _capDot.Fill = dot;
        _stopBtn.Background = s is RunUi.Running or RunUi.Paused ? R("Alarm") : s == RunUi.Blocked ? Brushes.White : R("Card2");
        _stopBtn.Foreground = s is RunUi.Running or RunUi.Paused ? Brushes.White : s == RunUi.Blocked ? R("Alarm") : Ui.Bad;

        try
        {
            TaskbarItemInfo.ProgressValue = 1;
            TaskbarItemInfo.ProgressState = s switch
            {
                RunUi.Running => TaskbarItemProgressState.Normal, RunUi.Paused => TaskbarItemProgressState.Paused,
                RunUi.Blocked or RunUi.EStop => TaskbarItemProgressState.Error, _ => TaskbarItemProgressState.None
            };
            _pill?.SetState(s switch { RunUi.Running => StatusPill.Kind.Running, RunUi.Paused => StatusPill.Kind.Paused, RunUi.Blocked => StatusPill.Kind.Blocked, RunUi.EStop => StatusPill.Kind.Stopped, _ => StatusPill.Kind.Done }, title, sub);
        }
        catch (Exception e) { Debug.WriteLine("状态通道更新失败：" + e.Message); }   // 状态显示出错不能影响运行和抬键
    }

    /// <summary>
    /// 运行中每 250ms 一次：按守卫同样的规则看前台（前台的根所有者是不是绑定窗口）。
    /// 拦截事件只在程序要发输入时才产生，等待期间不会有，所以状态看前台、次数看事件（偏离了决策页「最近 2 秒有拦截」的写法，见任务日志）。
    /// 连续 2 次（约 0.5 秒）不在前台才算拦截，避开开始运行时把靶子切到前台的那一下。
    /// </summary>
    private void PollRun()
    {
        if (_run == null) return;
        var fg = WindowInfo.Foreground();
        bool inFront = fg == _run.Target || WindowInfo.RootOwner(fg) == _run.Target;
        _notInFrontTicks = inFront ? 0 : _notInFrontTicks + 1;
        int blocked = _run.BlockedEvents;
        if (_run.Paused) ApplyRunUi(RunUi.Paused, blocked: blocked);
        else if (_notInFrontTicks >= 2) ApplyRunUi(RunUi.Blocked, blocked: blocked);
        else ApplyRunUi(RunUi.Running, blocked: blocked);
    }

    // ───────────────────────── 刷新 ─────────────────────────

    private void RefreshAll()
    {
        _canvas.Program = _ed.Program;
        _canvas.InvalidateVisual();
        RebuildTree();
        RebuildResources();
        ShowProps();
        UpdateTitle();
    }

    private void RebuildResources()
    {
        _resources.Items.Clear();
        void Add(string kind, string name, string text, Brush fg, ImageSource? thumb, Brush? swatch = null)
        {
            var visual = new Border { Height = 54, Background = swatch ?? Ui.Hex("#263240") };
            if (thumb != null) visual.Child = new Image { Source = thumb, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var kindText = new TextBlock { Text = L.T("k_" + kind), FontSize = 11, Foreground = fg, Margin = new Thickness(8, 4, 8, 0) };
            var nameText = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = R("Ink") };
            var sp = new StackPanel { Children = { visual, kindText, nameText } };
            var item = new ListBoxItem { Content = sp, Tag = new ResourceRef(kind, name), Style = S("ResourceCard"), ToolTip = text };
            AutomationProperties.SetName(item, text);   // ui.ps1 的 selectres 按这个全名匹配（格式和改版前一样）
            _resources.Items.Add(item);
        }
        foreach (var (n, r) in _ed.Program.Regions) Add("region", n, $"热区 · {n}　({r.X},{r.Y}) {r.W}×{r.H}", Ui.SenseFg, Crop(r.X, r.Y, r.W, r.H));
        foreach (var (n, p) in _ed.Program.Points) Add("point", n, $"定点 · {n}　({p.X},{p.Y})", Ui.FlowFg, Crop(p.X - 60, p.Y - 40, 120, 80));
        foreach (var (n, t) in _ed.Program.Templates) Add("template", n, $"模板 · {n}　{t.File}", Ui.ActFg, LoadImage(Path.Combine(_ed.Dir, t.File)));
        foreach (var (n, c) in _ed.Program.Colors) Add("color", n, $"颜色 · {n}　H{c.Lower[0]}–{c.Upper[0]}", R("Text2"), null, Swatch(c));
        foreach (var (n, g) in _ed.Program.Grids) Add("grid", n, $"网格 · {n}　{g.Cols}×{g.Rows}，每格 {g.W}×{g.H}", Ui.FlowFg, Crop(g.X, g.Y, g.Cols * g.W + (g.Cols - 1) * g.GapX, g.Rows * g.H + (g.Rows - 1) * g.GapY));
        _resCount.Text = _resources.Items.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>资源缩略图：从冻结帧上裁（基准坐标 × K = 帧像素）。没截帧时返回 null，卡片显示纯色底。</summary>
    private ImageSource? Crop(int x, int y, int w, int h)
    {
        if (_frozen == null || w <= 0 || h <= 0) return null;
        try
        {
            var m = _frozen.Bgr;
            double k = _frozen.K;
            int x0 = Math.Clamp((int)Math.Round(x * k), 0, m.Width - 1), y0 = Math.Clamp((int)Math.Round(y * k), 0, m.Height - 1);
            int x1 = Math.Clamp((int)Math.Round((x + w) * k), x0 + 1, m.Width), y1 = Math.Clamp((int)Math.Round((y + h) * k), y0 + 1, m.Height);
            using var roi = new OpenCvSharp.Mat(m, new OpenCvSharp.Rect(x0, y0, x1 - x0, y1 - y0));
            double s = Math.Min(1.0, 240.0 / roi.Width);
            using var small = roi.Resize(new OpenCvSharp.Size(Math.Max(1, (int)(roi.Width * s)), Math.Max(1, (int)(roi.Height * s))), 0, 0, OpenCvSharp.InterpolationFlags.Area);
            return FrameCanvas.ToBitmap(small);
        }
        catch { return null; }
    }

    private static ImageSource? LoadImage(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>颜色资源的色块：取 HSV 范围的中点（OpenCV 口径 H 0–179；下限 H 大于上限 H 表示跨过 0 的红色）。</summary>
    private static Brush Swatch(ColorDef c)
    {
        double h = c.Lower[0] <= c.Upper[0] ? (c.Lower[0] + c.Upper[0]) / 2.0 : ((c.Lower[0] + c.Upper[0] + 180) / 2.0) % 180;
        double s = (c.Lower[1] + c.Upper[1]) / 2.0 / 255, v = (c.Lower[2] + c.Upper[2]) / 2.0 / 255;
        double hh = h * 2 / 60, cc = v * s, xx = cc * (1 - Math.Abs(hh % 2 - 1)), mm = v - cc;
        var (r, g, b) = hh switch { < 1 => (cc, xx, 0.0), < 2 => (xx, cc, 0.0), < 3 => (0.0, cc, xx), < 4 => (0.0, xx, cc), < 5 => (xx, 0.0, cc), _ => (cc, 0.0, xx) };
        var brush = new SolidColorBrush(Color.FromRgb((byte)((r + mm) * 255), (byte)((g + mm) * 255), (byte)((b + mm) * 255)));
        brush.Freeze();
        return brush;
    }

    private void RebuildTree()
    {
        var sel = _selected;
        _treeSv ??= FindChild<ScrollViewer>(_tree);
        double keep = _treeSv?.VerticalOffset ?? 0;   // 重建会让树跳回顶部：先记下滚动位置，重建完放回去（2026-11-23）
        _tree.Items.Clear();
        TreeViewItem? toSelect = null;
        TreeViewItem Node(Block b)
        {
            bool container = b is IContainerBlock or IfBlock;
            var item = new TreeViewItem { Header = Header(b), Tag = b, IsExpanded = true, AllowDrop = true, Style = S(container ? "ContainerItem" : "BlockItem"), Background = CategoryBrush(b) };
            if (ReferenceEquals(b, sel)) toSelect = item;
            if (b is IContainerBlock c) foreach (var ch in c.Body) item.Items.Add(Node(ch));
            if (b is IfBlock ib)
            {
                var marker = sel is ElseMarker em && ReferenceEquals(em.Owner, ib) ? (ElseMarker)sel : new ElseMarker(ib);
                var els = new TreeViewItem { Header = ElseHeader(), Tag = marker, IsExpanded = true, AllowDrop = true, Style = S("ElseItem"), Background = Ui.FlowFg };
                if (ReferenceEquals(marker, sel)) toSelect = els;
                foreach (var ch in ib.Else) els.Items.Add(Node(ch));
                item.Items.Add(els);
            }
            return item;
        }
        foreach (var b in _ed.Program.Program) _tree.Items.Add(Node(b));
        if (_ed.Program.Program.Count == 0)
            _tree.Items.Add(new TreeViewItem { Header = new TextBlock { Text = L.T("emptyProgram"), Foreground = R("Text3"), TextWrapping = TextWrapping.Wrap }, IsEnabled = false });
        if (toSelect != null) toSelect.IsSelected = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _treeSv ??= FindChild<ScrollViewer>(_tree);
            _treeSv?.ScrollToVerticalOffset(keep);
            toSelect?.BringIntoView();
            ApplyScopeHighlight();
            UpdateSticky();
        });
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t) return t;
            if (FindChild<T>(ch) is { } d) return d;
        }
        return null;
    }

    private static Brush CategoryBrush(Block b) => BlockText.CategoryOf(b) switch { BlockText.Category.Flow => Ui.FlowFg, BlockText.Category.Sense => Ui.SenseFg, _ => Ui.ActFg };

    /// <summary>积木上的字：代码建的 TextBlock（ui.ps1 的 TextOf 要读得到），按宽度换行。</summary>
    private UIElement Header(Block b) =>
        new TextBlock { Text = BlockText.Summary(b, _ed.Program), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };

    private static UIElement ElseHeader() => new TextBlock { Text = L.T("elseRow"), Foreground = Brushes.White, FontStyle = FontStyles.Italic, FontSize = 12.5 };

    /// <summary>属性改了以后只刷新树上的文字，不重建（重建会让正在输入的文本框丢焦点）。</summary>
    private void RefreshTreeText()
    {
        void Walk(ItemCollection items)
        {
            foreach (var o in items)
            {
                if (o is not TreeViewItem it) continue;
                if (it.Tag is Block b) { it.Header = Header(b); it.Background = CategoryBrush(b); }
                Walk(it.Items);
            }
        }
        Walk(_tree.Items);
        ApplyScopeHighlight();
        UpdateTitle();
    }

    private void WithSelectedBlock(Action<Block> act)
    {
        if (_selected is ElseMarker) { Log("「否则」这一行不能单独移动或删除：选中它上面的「如果」，或者选中「否则」里面的积木"); return; }
        if (_selected is not Block b) { Log("先在程序树里选中一块积木"); return; }
        act(b);
        RefreshAll();
    }

    // ───────────────────────── 积木插入 ─────────────────────────

    private Block? MakeBlock(string type)
    {
        if (type.StartsWith("cond:"))
        {
            var ib = (IfBlock)_ed.CreateDefault("if");
            ib.Cond = MakeCondition(type[5..]);
            return ib;
        }
        return _ed.CreateDefault(type);
    }

    private Condition MakeCondition(string kind)
    {
        var p = _ed.Program;
        string region = p.Regions.Keys.FirstOrDefault() ?? "";
        return kind switch
        {
            "image" => new ImageCondition { Template = p.Templates.Keys.FirstOrDefault() ?? "", Region = region, Threshold = 0.9 },
            "color" => new ColorCondition { Color = p.Colors.Keys.FirstOrDefault() ?? "", Region = region, Op = ">", Ratio = 0.02 },
            _ => new TextCondition { Region = region, Contains = "完成" }
        };
    }

    private void InsertFromPalette(string type) => InsertAt(type, _selected);

    private void InsertAt(string type, object? anchor)
    {
        var nb = MakeBlock(type);
        if (nb == null) return;
        InsertBlock(nb, anchor);
        if (_ed.Program.Regions.Count == 0) Log("提示：还没有热区。先在画布上「拖框建热区」，再回来设置这块积木。");
    }

    /// <summary>插入规则（积木架点击、粘贴共用）：选中普通积木 → 放在它后面；选中容器 → 放进它末尾；选中「否则」→ 放进否则分支；没选 → 程序末尾。</summary>
    private void InsertBlock(Block nb, object? anchor)
    {
        switch (anchor)
        {
            case ElseMarker em: _ed.Insert(nb, em.Owner, intoElse: true); break;
            case IContainerBlock and Block c: _ed.Insert(nb, c, into: true); break;
            case Block b: _ed.Insert(nb, b); break;
            default: _ed.Insert(nb, null); break;
        }
        _selected = nb;
        RefreshAll();
        WarnCurrentCell();
    }

    // ───────────────────────── 快捷键（2026-11-23） ─────────────────────────

    private Dictionary<string, Action>? _commands;

    /// <summary>每个动作做什么：和按钮、右键菜单调同一个方法。暂停、急停是运行时的全局热键，不在这里。</summary>
    private Dictionary<string, Action> Commands => _commands ??= BuildCommands();

    private Dictionary<string, Action> BuildCommands()
    {
        var c = new Dictionary<string, Action>
        {
            ["new"] = NewProgram, ["open"] = OpenProgram, ["save"] = SaveProgram, ["sample"] = LoadSample,
            ["undo"] = UndoEdit, ["redo"] = RedoEdit, ["settings"] = OpenSettings,
            ["launch"] = LaunchTarget, ["refresh"] = RefreshScreens,
            ["run"] = Run, ["stop"] = () => _run?.Stop(), ["openResults"] = OpenResults, ["freeze"] = Freeze,
            ["copy"] = CopySelected, ["cut"] = CutSelected, ["paste"] = PasteAtSelection, ["duplicate"] = DuplicateSelected,
            ["delete"] = DeleteSelected, ["moveUp"] = MoveSelectedUp, ["moveDown"] = MoveSelectedDown, ["indent"] = IndentSelected, ["outdent"] = OutdentSelected,
        };
        foreach (Tool t in Enum.GetValues<Tool>()) { var tt = t; c["tool" + t] = () => SetTool(tt); }
        foreach (var type in Keymap.PaletteTypes) { var ty = type; c["pal:" + type] = () => InsertFromPalette(ty); }
        return c;
    }

    /// <summary>
    /// 编辑器窗口里的按键 → 动作。光标在输入框里时，打字、复制粘贴、删字这些键留给输入框（Keymap.IsTextEditing）；
    /// 中文输入法开着时按键先到输入法，WPF 给的是 ImeProcessed，要取 ImeProcessedKey 才是真正按的键。
    /// </summary>
    private void OnShortcutKey(object sender, KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (Keymap.NameOfVk(KeyInterop.VirtualKeyFromKey(key)) is not { } name) return;
        var m = Keyboard.Modifiers;
        var g = new Keymap.Gesture(m.HasFlag(ModifierKeys.Control), m.HasFlag(ModifierKeys.Alt), m.HasFlag(ModifierKeys.Shift), name);
        var focus = Keyboard.FocusedElement as DependencyObject;
        bool inText = focus is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox || (focus != null && FindAncestor<ComboBox>(focus) is { IsEditable: true });
        if (inText && Keymap.IsTextEditing(g)) return;
        var map = Keymap.Effective(AppSettings.Current.Shortcuts);
        if (Keymap.HolderOf(map, g, "") is not { } id || Keymap.Find(id) is { Global: true }) return;
        if (!Commands.TryGetValue(id, out var act)) return;
        e.Handled = true;
        act();
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }

    private void SaveProgram() { _ed.Save(); UpdateTitle(); Log("已保存"); }
    private void RefreshScreens() { RefreshMonitors(); RefreshWindows(); }
    private void LoadSample() => LoadProgram(AppPaths.SamplePatrol);
    private void DeleteSelected() => WithSelectedBlock(b => { _ed.Remove(b); _selected = null; });
    private void MoveSelectedUp() => WithSelectedBlock(b => { if (!_ed.MoveUp(b)) Log("已经在最上面了"); });
    private void MoveSelectedDown() => WithSelectedBlock(b => { if (!_ed.MoveDown(b)) Log("已经在最下面了"); });
    private void IndentSelected() => WithSelectedBlock(b => { if (!_ed.Indent(b)) Log("上面一块不是「重复/如果」，没法缩进进去"); });
    private void OutdentSelected() => WithSelectedBlock(b => _ed.Outdent(b));

    // ───────────────────────── 复制、粘贴、撤销（2026-11-23） ─────────────────────────

    private void CopySelected() => WithSelectedBlock(b =>
    {
        _clip = BlockClip.Capture(_ed, b);
        Log($"已复制：{_clip.Summary}");
    });

    private void CutSelected() => WithSelectedBlock(b =>
    {
        _clip = BlockClip.Capture(_ed, b);
        _ed.Remove(b);
        _selected = null;
        Log($"已剪切：{_clip.Summary}（粘贴放回去，撤销也能恢复）");
    });

    private void PasteAtSelection()
    {
        if (_clip == null) { Log("剪贴板是空的：先选中一块积木点「复制」"); return; }
        var (nb, notes) = _clip.Paste(_ed);
        InsertBlock(nb, _selected is ResourceRef ? null : _selected);
        Log($"已粘贴：{BlockText.Summary(nb, _ed.Program)}");
        foreach (var n in notes) Log("  " + n);
        if (notes.Count > 0) RebuildResources();
    }

    private void DuplicateSelected() => WithSelectedBlock(b =>
    {
        var nb = ProgramEditor.Clone(b);
        var loc = _ed.Locate(b)!.Value;
        _ed.InsertAt(nb, loc.List, loc.Index + 1);
        _selected = nb;
        WarnCurrentCell();
    });

    private void UndoEdit() => History(_ed.Undo, "没有可以撤销的了", "已撤销");
    private void RedoEdit() => History(_ed.Redo, "没有可以重做的了", "已重做");

    /// <summary>撤销 / 重做以后程序是新对象：按原来的位置找回选中的积木，资源按名字找回。</summary>
    private void History(Func<bool> op, string none, string done)
    {
        var path = _selected switch { Block b => _ed.PathOf(b), ElseMarker em => _ed.PathOf(em.Owner), _ => null };
        bool wasElse = _selected is ElseMarker;
        var res = _selected as ResourceRef;
        if (!op()) { Log(none); return; }
        _selected = path != null && _ed.BlockAt(path) is { } nb ? (wasElse && nb is IfBlock ib ? new ElseMarker(ib) : nb)
            : res != null && ResourceExists(res) ? res : null;
        RefreshAll();
        Log(done);
    }

    private bool ResourceExists(ResourceRef r) => r.Kind switch
    {
        "region" => _ed.Program.Regions.ContainsKey(r.Name), "point" => _ed.Program.Points.ContainsKey(r.Name),
        "template" => _ed.Program.Templates.ContainsKey(r.Name), "color" => _ed.Program.Colors.ContainsKey(r.Name),
        "grid" => _ed.Program.Grids.ContainsKey(r.Name), _ => false
    };

    /// <summary>「当前格」被挪到「对每一格」外面时马上提醒（以前要到运行时才报）。</summary>
    private void WarnCurrentCell()
    {
        foreach (var e in ProgramIO.Validate(_ed.Program).Where(x => x.Contains(VbProgram.CurrentCell)))
            Log("⚠ " + e);
    }

    private ContextMenu BuildTreeMenu()
    {
        var m = new ContextMenu();
        void Item(string key, Action act, string aid, Brush? fg = null)
        {
            var mi = new MenuItem();
            L.Reg(mi, HeaderedItemsControl.HeaderProperty, key);
            string action = aid[3..].ToLowerInvariant();
            L.RegF(mi, MenuItem.InputGestureTextProperty, () => AppSettings.KeyOf(action));
            AutomationProperties.SetAutomationId(mi, aid);
            if (fg != null) mi.Foreground = fg;
            mi.Click += (_, _) => act();
            m.Items.Add(mi);
        }
        Item("btnCopy", CopySelected, "ctxCopy");
        Item("btnCut", CutSelected, "ctxCut");
        Item("btnPaste", PasteAtSelection, "ctxPaste");
        Item("btnDuplicate", DuplicateSelected, "ctxDuplicate");
        m.Items.Add(new Separator());
        Item("btnDelete", DeleteSelected, "ctxDelete", Ui.Bad);
        return m;
    }

    // ───────────────────────── 程序树里拖动（2026-11-23） ─────────────────────────

    private static TreeViewItem? ItemUnder(object? src)
    {
        var dep = src as DependencyObject;
        while (dep != null && dep is not TreeViewItem) dep = dep is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        return dep as TreeViewItem;
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _treeDragBlock == null) return;
        var d = e.GetPosition(_tree) - _treeDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var b = _treeDragBlock;
        _treeDragBlock = null;
        try { DragDrop.DoDragDrop(_tree, new DataObject("vb-move", b), DragDropEffects.Move | DragDropEffects.Copy); }
        finally { ClearDropHint(); }
    }

    private static FrameworkElement? HeaderOf(TreeViewItem it) => it.Template?.FindName("PART_Header", it) as FrameworkElement;

    /// <summary>
    /// 鼠标在树上的位置 → 落点。按每一行顶块和底盖的竖直位置算（积木之间的缝、立柱旁边的空白都不挡鼠标，命中测试会直接落到树的背景上）：
    /// 积木的上半 = 放在它上面；下半 = 放在它下面，容器的下半 = 放进去当第一块；容器的底盖 = 放到容器后面（出作用域）；
    /// 「否则」行 = 否则分支第一块；两行之间的缝 = 跟着上面那一行（容器顶块下面的缝 = 放进去，底盖下面的缝 = 容器后面）；最下面 = 程序末尾。
    /// 返回落点和画指示线用的位置（相对 _dropLayer）。
    /// </summary>
    private (List<Block> List, int Index, Point LineFrom, double LineW)? DropAt(DragEventArgs e)
    {
        double y = e.GetPosition(_tree).Y;
        var rows = new List<(double Top, double Bottom, TreeViewItem Item, FrameworkElement Header, bool IsCap)>();
        void Walk(ItemCollection items)
        {
            foreach (var o in items)
            {
                if (o is not TreeViewItem { Tag: not null } it || HeaderOf(it) is not { IsVisible: true } h) continue;   // 空程序那行提示没有 Tag
                double top = h.TranslatePoint(new Point(0, 0), _tree).Y;
                rows.Add((top, top + h.ActualHeight, it, h, false));
                Walk(it.Items);
                if (it.Tag is IContainerBlock)
                {
                    double bottom = it.TranslatePoint(new Point(0, it.ActualHeight), _tree).Y;
                    rows.Add((bottom - 21, bottom, it, h, true));   // 底盖 14 + 下边距 7
                }
            }
        }
        Walk(_tree.Items);
        if (rows.Count == 0) return (_ed.Program.Program, _ed.Program.Program.Count, new Point(16, 14), 220);
        rows.Sort((a, b) => a.Top.CompareTo(b.Top));
        Point Pt(FrameworkElement el, double x, double yy) => el.TranslatePoint(new Point(x, yy), _dropLayer);
        double W(FrameworkElement h) => Math.Max(160, Math.Min(h.ActualWidth, 420));
        (List<Block>, int, Point, double)? Slot(Block? anchor, ProgramEditor.DropZone z, Point line, double w)
            => _ed.Slot(anchor, z) is { } s ? (s.List, s.Index, line, w) : null;
        (List<Block>, int, Point, double)? Below(TreeViewItem it, FrameworkElement h, bool isCap) => (isCap, it.Tag) switch
        {
            (true, Block c) => Slot(c, ProgramEditor.DropZone.After, Pt(it, 0, it.ActualHeight - 4), W(h)),
            (false, ElseMarker em) => Slot(em.Owner, ProgramEditor.DropZone.ElseFirst, Pt(h, 30, h.ActualHeight + 4), W(h)),
            (false, IContainerBlock and Block c) => Slot(c, ProgramEditor.DropZone.IntoFirst, Pt(h, 30, h.ActualHeight + 4), W(h)),
            (false, Block b) => Slot(b, ProgramEditor.DropZone.After, Pt(h, 0, h.ActualHeight + 4), W(h)),
            _ => null
        };
        // 在某一行顶块上（顶块外面那圈选中框往上多算 4）
        foreach (var r in rows)
            if (!r.IsCap && y >= r.Top - 4 && y <= r.Bottom)
            {
                if (r.Item.Tag is Block b && y < (r.Top + r.Bottom) / 2) return Slot(b, ProgramEditor.DropZone.Before, Pt(r.Header, 0, -4), W(r.Header));
                return Below(r.Item, r.Header, false);
            }
        // 在某个容器的底盖上
        foreach (var r in rows)
            if (r.IsCap && y >= r.Top && y <= r.Bottom) return Below(r.Item, r.Header, true);
        // 缝里：跟着上面最近的那一行；最上面一行的上面 = 放在第一块前面
        var above = rows.Where(r => r.Bottom <= y).OrderBy(r => r.Bottom).ToList();
        if (above.Count == 0)
        {
            var first = rows[0];
            return first.Item.Tag is Block fb ? Slot(fb, ProgramEditor.DropZone.Before, Pt(first.Header, 0, -4), W(first.Header)) : null;
        }
        var a = above[^1];
        return Below(a.Item, a.Header, a.IsCap);
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool move = e.Data.GetDataPresent("vb-move"), add = e.Data.GetDataPresent("vb-block");
        if (!move && !add) { e.Effects = DragDropEffects.None; return; }
        // 靠近上下边时自动滚动
        _treeSv ??= FindChild<ScrollViewer>(_tree);
        double y = e.GetPosition(_tree).Y;
        if (_treeSv != null) { if (y < 28) _treeSv.LineUp(); else if (y > _tree.ActualHeight - 28) _treeSv.LineDown(); }
        var drop = DropAt(e);
        bool copy = move && e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        bool ok = drop != null && !(move && !copy && e.Data.GetData("vb-move") is Block src && ProgramEditor.Contains(src, drop.Value.List));
        e.Effects = !ok ? DragDropEffects.None : move && !copy ? DragDropEffects.Move : DragDropEffects.Copy;
        ShowDropHint(drop, ok);
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var drop = DropAt(e);
        ClearDropHint();
        if (drop is not { } d) return;
        if (e.Data.GetData("vb-block") is string type)
        {
            if (MakeBlock(type) is not { } nb) return;
            _ed.InsertAt(nb, d.List, d.Index);
            _selected = nb;
        }
        else if (e.Data.GetData("vb-move") is Block b)
        {
            if (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey))
            {
                var nb = ProgramEditor.Clone(b);
                _ed.InsertAt(nb, d.List, d.Index);
                _selected = nb;
            }
            else if (ProgramEditor.Contains(b, d.List)) { Log("不能把积木拖进它自己里面"); return; }
            else if (!_ed.MoveTo(b, d.List, d.Index)) return;
            else _selected = b;
        }
        else return;
        RefreshAll();
        WarnCurrentCell();
    }

    /// <summary>落点指示：一条横线（不合法时红色），以及要放进去的那个容器的虚线框。</summary>
    private void ShowDropHint((List<Block> List, int Index, Point LineFrom, double LineW)? drop, bool ok)
    {
        _dropLayer.Children.Clear();
        if (drop is not { } d) return;
        SetScopeBar(L.T("scopeDrop") + SlotText(d.List, d.Index) + (ok ? "" : " · " + L.T("scopeNoSelf")), true);
        var brush = ok ? R("Ink") : Ui.Bad;
        var line = new System.Windows.Shapes.Rectangle { Width = d.LineW, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = brush };
        Canvas.SetLeft(line, d.LineFrom.X);
        Canvas.SetTop(line, d.LineFrom.Y - 1.5);
        _dropLayer.Children.Add(line);
        var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = brush };
        Canvas.SetLeft(dot, d.LineFrom.X - 4);
        Canvas.SetTop(dot, d.LineFrom.Y - 4.5);
        _dropLayer.Children.Add(dot);
        if (_ed.OwnerOf(d.List) is { } own && FindItem(own.Owner) is { } oi)
        {
            var tl = oi.TranslatePoint(new Point(-6, -6), _dropLayer);
            var frame = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(40, oi.ActualWidth + 8), Height = Math.Max(20, oi.ActualHeight + 4), Stroke = brush, StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 3, 2 }, RadiusX = 12, RadiusY = 12
            };
            Canvas.SetLeft(frame, tl.X);
            Canvas.SetTop(frame, tl.Y);
            _dropLayer.Children.Add(frame);
        }
    }

    private void ClearDropHint() { _dropLayer.Children.Clear(); UpdateScopeUi(); }

    // ───────────────────────── 作用域提示（2026-11-23） ─────────────────────────

    private string Scope(List<Block> list) => _ed.ScopeText(list, L.T("scopeRoot"), L.T("scopeElse"));

    /// <summary>落点写成一句话：「程序 › 重复 3 次 · 「点击…」后面」。</summary>
    private string SlotText(List<Block> list, int index) =>
        Scope(list) + " · " + (index <= 0 || list.Count == 0 ? L.T("scopeStart") : string.Format(L.T("scopeAfter"), ProgramEditor.Short(BlockText.Summary(list[Math.Min(index, list.Count) - 1], _ed.Program), 18)));

    private void SetScopeBar(string text, bool dragging)
    {
        _scopeText.Text = text;
        _scopeBar.Background = dragging ? Ui.FlowFg : R("Card2");
        _scopeText.Foreground = dragging ? Brushes.White : R("Text2");
    }

    /// <summary>选中变了、树重建了：位置条写选中积木在哪个作用域；它所在的容器（和否则行）换成深一点的橙色。</summary>
    private void UpdateScopeUi()
    {
        switch (_selected)
        {
            case Block b when _ed.Locate(b) is { } loc:
                SetScopeBar(L.T("scopeAt") + Scope(loc.List) + " · " + string.Format(L.T("scopeNth"), loc.Index + 1, loc.List.Count), false);
                break;
            case ElseMarker em when _ed.Locate(em.Owner) is not null:
                SetScopeBar(L.T("scopeAt") + Scope(em.Owner.Else), false);
                break;
            default:
                SetScopeBar(L.T("scopeHint"), false);
                break;
        }
        ApplyScopeHighlight();
        UpdateSticky();
    }

    private void ApplyScopeHighlight()
    {
        (Block Owner, bool IsElse)? scope = _selected switch
        {
            Block b when _ed.Locate(b) is { } loc => _ed.OwnerOf(loc.List),
            ElseMarker em => (em.Owner, true),
            _ => null
        };
        void Walk(ItemCollection items)
        {
            foreach (var o in items)
            {
                if (o is not TreeViewItem it) continue;
                if (it.Tag is Block b) it.Background = scope is { } s && ReferenceEquals(s.Owner, b) ? ScopeHi : CategoryBrush(b);
                else if (it.Tag is ElseMarker em) it.Background = scope is { IsElse: true } s2 && ReferenceEquals(s2.Owner, em.Owner) ? ScopeHi : Ui.FlowFg;
                Walk(it.Items);
            }
        }
        Walk(_tree.Items);
    }

    /// <summary>
    /// 钉顶（像 VS Code 的 sticky scroll）：看树的可见区最上面那一行属于哪些容器，顶块已经滚出去的那些，
    /// 按层级缩成一行钉在树顶（最多 5 层，再多写「外面还有 n 层」）。点一行就滚回那个容器并选中它。
    /// </summary>
    private void UpdateSticky()
    {
        _sticky.Children.Clear();
        if (!AppSettings.Current.StickyScope || _treeSv == null || _treeSv.VerticalOffset < 1 || !_tree.IsLoaded) return;
        // 可见区最上面那一行 = 按显示顺序第一个「顶块底边在 limit 以下」的积木（不用命中测试：鼠标点不到立柱旁边的空白）。
        // 算两遍：第二遍把已经钉住的那几行盖住的高度也当成「滚出去了」，盖在下面的那一层也钉上
        IEnumerable<TreeViewItem> Flat(ItemCollection items)
        {
            foreach (var o in items)
                if (o is TreeViewItem it) { yield return it; foreach (var c in Flat(it.Items)) yield return c; }
        }
        double TopOf(TreeViewItem it) => HeaderOf(it) is { } h ? h.TranslatePoint(new Point(0, 0), _tree).Y : double.MaxValue;
        List<TreeViewItem> ChainAt(double limit)
        {
            TreeViewItem? top = null;
            foreach (var it in Flat(_tree.Items))
            {
                if (HeaderOf(it) is not { } h || !h.IsVisible) continue;
                if (h.TranslatePoint(new Point(0, h.ActualHeight), _tree).Y > limit) { top = it; break; }
            }
            var c = new List<TreeViewItem>();
            if (top == null) return c;
            for (var it = ItemsControl.ItemsControlFromItemContainer(top) as TreeViewItem; it != null; it = ItemsControl.ItemsControlFromItemContainer(it) as TreeViewItem)
                c.Insert(0, it);
            if (top.Tag is IContainerBlock or ElseMarker) c.Add(top);
            return c.Where(x => TopOf(x) < limit).ToList();
        }
        var chain = ChainAt(4);
        if (chain.Count > 0) chain = ChainAt(4 + Math.Min(chain.Count + 1, 6) * 20);
        if (chain.Count == 0) return;
        int skip = Math.Max(0, chain.Count - 5);
        if (skip > 0) _sticky.Children.Add(StickyRow(null, string.Format(L.T("stickyMore"), skip), 0));
        for (int i = skip; i < chain.Count; i++)
        {
            var c = chain[i];
            string text = c.Tag switch { Block b => BlockText.Summary(b, _ed.Program), ElseMarker => L.T("elseRow"), _ => "" };
            _sticky.Children.Add(StickyRow(c, text, Depth(c)));
        }
        _sticky.Background = R("CardBg");
    }

    private static int Depth(TreeViewItem it)
    {
        int d = 0;
        for (var p = ItemsControl.ItemsControlFromItemContainer(it) as TreeViewItem; p != null; p = ItemsControl.ItemsControlFromItemContainer(p) as TreeViewItem)
            if (p.Tag is not ElseMarker) d++;
        return it.Tag is ElseMarker ? Math.Max(0, d - 1) : d;
    }

    private UIElement StickyRow(TreeViewItem? target, string text, int depth)
    {
        var tb = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new Border
        {
            Child = tb, Background = target?.Background ?? R("Text3"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 10, 2),
            Margin = new Thickness(16 + depth * 30, 2, 12, 0), HorizontalAlignment = HorizontalAlignment.Left, Cursor = target != null ? Cursors.Hand : null
        };
        AutomationProperties.SetAutomationId(tb, "stickyScope");
        if (target != null)
            row.MouseLeftButtonUp += (_, _) => { target.IsSelected = true; (HeaderOf(target) ?? target).BringIntoView(); };
        return row;
    }

    private TreeViewItem? FindItem(Block b)
    {
        TreeViewItem? Walk(ItemCollection items)
        {
            foreach (var o in items)
            {
                if (o is not TreeViewItem it) continue;
                if (ReferenceEquals(it.Tag, b)) return it;
                if (Walk(it.Items) is { } f) return f;
            }
            return null;
        }
        return Walk(_tree.Items);
    }

    // ───────────────────────── 程序文件 ─────────────────────────

    private void NewProgram()
    {
        string? name = Ui.Ask(this, "新建程序", "程序名称：", "我的程序");
        if (string.IsNullOrWhiteSpace(name)) return;
        string dir = Path.Combine(AppPaths.Programs, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _ed = ProgramEditor.CreateNew(dir, name.Trim());
        _ed.Changed += () => UpdateTitle();
        _selected = null;
        RefreshAll();
        RefreshWindows();
        Log($"已新建：{_ed.ProgramPath}");
    }

    private void OpenProgram()
    {
        var dlg = new OpenFileDialog { Filter = "VisionBlocks 程序|*.vbp.json|所有文件|*.*", InitialDirectory = AppPaths.Programs };
        if (dlg.ShowDialog(this) == true) LoadProgram(dlg.FileName);
    }

    private void LoadProgram(string path)
    {
        try
        {
            _ed = ProgramEditor.Open(path);
            _ed.Changed += () => UpdateTitle();
            _selected = null;
            RefreshAll();
            RefreshWindows();   // 白名单跟着程序走，换程序后下拉框要重列
            Log($"已打开：{path}");
        }
        catch (Exception e) { Log("打开失败：" + e.Message); }
    }

    // ───────────────────────── 设置（界面语言） ─────────────────────────

    private void OpenSettings()
    {
        Ui.ShowSettings(this, lang =>
        {
            if (_run != null) { Log(L.T("langRunning")); return false; }
            L.Set(lang);
            ApplyLanguage();
            return true;
        }, ApplySettings, () => _run != null);
    }

    /// <summary>设置里除语言以外的项改了：练习靶子面板显示与否、主题。鼠标随机化在下次运行开始时读。</summary>
    private void ApplySettings()
    {
        ApplyPracticeVisibility();
        UpdateSticky();
        L.Relabel();   // 快捷键改了：按钮提示、顶栏热键提示跟着变
        bool wasDark = Ui.IsDark;
        try { Ui.ApplyTheme(AppSettings.Current.Theme); }
        catch (Exception e) { Log("切换主题失败：" + e); }
        if (Ui.IsDark != wasDark)
        {
            // 点阵背景是画刷里套画刷（换不到里面那层），以及积木树、资源卡、属性卡这些由代码生成的部分，重建一次
            _tree.Background = DotGrid();
            RebuildTree();
            RebuildResources();
            ShowProps();
            _canvas.InvalidateVisual();
        }
    }

    /// <summary>「跟随系统」时 Windows 切了浅色 / 深色，跟着切。</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && AppSettings.Current.Theme == "system") Dispatcher.BeginInvoke(ApplySettings);
    }

    private void ApplyPracticeVisibility() => _practice.Visibility = AppSettings.Current.ShowPracticeTarget ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>切语言：登记过的控件已由 L.Set 原地换字；这里再刷新由数据生成的部分。不重跑 Build*。</summary>
    private void ApplyLanguage()
    {
        UpdateTitle();   // 标题栏里的应用名：视搭 / VisionBlocks
        RebuildTree();
        RebuildResources();
        ShowProps();
        RefreshMonitors();
        UpdateCanvasInfo();
        ApplyRunUi(RunUi.Idle);
        _canvas.InvalidateVisual();
    }

    // ───────────────────────── 屏幕（自动识别） ─────────────────────────

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        RefreshMonitors();
        Log($"屏幕有变化：现在检测到 {Screens.All().Count} 块屏，屏幕下拉框已刷新");
    });

    /// <summary>屏幕下拉框列出实际检测到的屏：位置 · 分辨率 · 缩放。项的 Tag 是设备名，传给靶子时用它，不用序号。</summary>
    private void RefreshMonitors()
    {
        var all = Screens.All();
        string? keep = (_monitor.SelectedItem as ComboBoxItem)?.Tag as string;
        _monitor.Items.Clear();
        var primary = all.FirstOrDefault(s => s.Primary) ?? all.FirstOrDefault();
        foreach (var s in all)
        {
            string pos = primary == null ? "monPrimary" : s.Position(primary) switch { "primary" => "monPrimary", "right" => "monRight", "left" => "monLeft", "above" => "monAbove", _ => "monBelow" };
            _monitor.Items.Add(new ComboBoxItem { Content = $"{L.T(pos)} · {s.Bounds.W}×{s.Bounds.H} · {s.ScalePercent}%", Tag = s.Device, ToolTip = s.Device });
        }
        var sel = _monitor.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == keep);
        if (sel == null && keep != null) Log("之前选的屏幕不见了，已改回主屏；要换屏请在「屏幕」里重选");
        _monitor.SelectedItem = sel ?? _monitor.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    // ───────────────────────── 靶子、绑定、截帧 ─────────────────────────

    private bool IsAllowed(string pname) => _ed.Program.Target.AllowedProcesses.Contains(pname, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 绑定下拉框列出所有顶层窗口（第一次绑定白名单外的程序先确认）。
    /// 白名单内的窗口排在前面；只自动选白名单内的窗口，白名单外的一律要用户自己选、再确认。
    /// </summary>
    private void RefreshWindows()
    {
        _suppressBind = true;
        _windows.Items.Clear();
        var items = new List<(bool Ok, ComboBoxItem Item)>();
        foreach (var w in WindowInfo.ListTopWindows())
        {
            string name = EffectiveName(w.Pid, w.ProcessName, w.Title);
            bool ok = IsAllowed(name);
            items.Add((ok, new ComboBoxItem { Content = $"{(ok ? "" : "［白名单外］")}{name} · {w.Title} · PID {w.Pid}", Tag = w.Hwnd }));
        }
        foreach (var (_, item) in items.Where(i => i.Ok).Concat(items.Where(i => !i.Ok))) _windows.Items.Add(item);
        ComboBoxItem? keep = _windows.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is IntPtr h && h == _bound);
        // 只在白名单里正好开着一个窗口时自动绑定：白名单里有资源管理器这类多窗口程序时，不能替用户挑一个他没选过的窗口
        if (keep == null && items.Count(i => i.Ok) == 1) keep = items.First(i => i.Ok).Item;
        if (_windows.Items.Count == 0) _windows.Items.Add(L.Reg(new ComboBoxItem { IsEnabled = false }, ContentControl.ContentProperty, "noWindows"));
        _windows.SelectedItem = keep;
        _suppressBind = false;
        if (keep is { Tag: IntPtr kh } && kh != _bound) { _bound = kh; Log($"已绑定窗口 0x{kh:X}"); }
        if (_run == null) ApplyRunUi(RunUi.Idle);
    }

    /// <summary>编辑器自己的进程名（单个 exe 改了文件名时也对）。</summary>
    private static readonly string SelfName = Process.GetCurrentProcess().ProcessName;

    /// <summary>
    /// 2026-11-22：靶子做进了编辑器的 exe（以 --bench-target 启动），进程名和编辑器一样。
    /// 另一个进程、同一个 exe、窗口标题是 BenchTarget → 当作 BenchTarget（白名单、窗口列表照旧按这个名字）。
    /// </summary>
    private static string EffectiveName(uint pid, string processName, string title) =>
        pid != (uint)Environment.ProcessId && string.Equals(processName, SelfName, StringComparison.OrdinalIgnoreCase) && title == "BenchTarget" ? "BenchTarget" : processName;

    private static string ProcessNameOf(IntPtr h)
    {
        try
        {
            uint pid = WindowInfo.ProcessIdOf(h);
            return EffectiveName(pid, Process.GetProcessById((int)pid).ProcessName, WindowInfo.TitleOf(h));
        }
        catch { return "?"; }
    }

    private void OnBindSelected()
    {
        if (_suppressBind || _windows.SelectedItem is not ComboBoxItem { Tag: IntPtr h } item) return;
        string pname = ProcessNameOf(h);
        if (!IsAllowed(pname))
        {
            var ans = MessageBox.Show(this,
                $"「{pname}」不在这个程序的白名单里。\n\n" +
                "加入后，运行时只会向你绑定的这一个窗口（和它弹出的对话框）发键鼠，每次发之前都检查它在前台。\n" +
                "只用于你自己的软件或自制的离线单机游戏；不要绑定别人的网络游戏、聊天或邮件软件、登录着账号的浏览器。\n\n" +
                "把它加进白名单并绑定吗？",
                "加入白名单", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes)
            {
                _suppressBind = true;
                _windows.SelectedItem = _windows.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is IntPtr b && b == _bound);
                _suppressBind = false;
                Log($"没有绑定 {pname}");
                return;
            }
            _ed.Program.Target.AllowedProcesses.Add(pname);
            _ed.Touch();
            item.Content = ((string)item.Content).Replace("［白名单外］", "");
            Log($"已把 {pname} 加进白名单（保存程序后生效到文件）");
        }
        _bound = h;
        Log($"已绑定窗口 0x{h:X}（{pname}）");
        if (_run == null) ApplyRunUi(RunUi.Idle);
    }

    private async void LaunchTarget()
    {
        try
        {
            // 用设备名指定屏幕；启动前再枚举一次，确认这块屏还在
            string? device = (_monitor.SelectedItem as ComboBoxItem)?.Tag as string;
            var scr = Screens.All().FirstOrDefault(s => string.Equals(s.Device, device, StringComparison.OrdinalIgnoreCase));
            if (scr == null) { RefreshMonitors(); Log("所选的屏幕已经不在了，请在「屏幕」里重选"); return; }
            var (tw, th) = WindowSizing.TargetClient(scr.Work.W, scr.Work.H);
            _sessionDir = Path.Combine(AppPaths.Results, "studio-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            bool inventory = _scene.SelectedIndex == 1;
            string args = $"--monitor-device \"{scr.Device}\" --log \"{Path.Combine(_sessionDir, "target")}\" --duration 1800"
                + (tw != 1920 ? $" --size {tw}x{th}" : "")
                + (inventory ? " --scene inventory" : " --start-delay 15");
            if (tw != 1920) Log($"所选屏幕放不下 1920×1080 的靶子，改用 {tw}×{th}。这个尺寸下文字识别会比 1920 宽差一些（11-20 实测 1600×900 时名称、分类读错更多）");
            Log("启动靶子：" + args);
            var (p, h) = await BenchLauncher.LaunchAsync(args);
            _launched = p;
            RefreshWindows();
            foreach (var o in _windows.Items) if (o is ComboBoxItem { Tag: IntPtr hh } it && hh == h) _windows.SelectedItem = it;
            _bound = h;
            Log($"靶子已启动并绑定（PID {p.Id}）。" + (inventory
                ? "背包：要取材就在靶子里点开一个物品再「截一帧」，按 Esc 关掉详情；随时可以点「▶ 运行」。"
                : "靶子 15 秒后开始出题和掉血（看靶子右下「状态」的倒计时），请在这之前点「▶ 运行」。"));
            ApplyRunUi(RunUi.Idle);
            Activate();
        }
        catch (Exception e) { Log("启动失败：" + e.Message); }
    }

    private async void Freeze()
    {
        if (_bound == IntPtr.Zero || !WindowInfo.IsAlive(_bound)) { Log("先启动靶子或在「绑定窗口」里选一个窗口"); return; }
        var h = _bound;
        var f = await Task.Run(() => CaptureOnce(h));
        if (f == null) { Log("截帧失败（窗口最小化了？）"); return; }
        _frozen?.Dispose();
        _frozen = f;
        _canvas.SetFrame(f);
        UpdateCanvasInfo();
        RebuildResources();   // 资源卡片的缩略图从新帧上裁
        if (_selected is ResourceRef { Kind: "color" }) ShowProps();   // 调色盘的占比、洋红高亮跟着新帧算
        Log("已截一帧");
    }

    private string FrameInfo() => _frozen == null ? "" : $"冻结帧：客户区归一化后 {_frozen.Bgr.Width}×{_frozen.Bgr.Height}，K={_frozen.K:0.###}（基准宽 1920）";

    private void UpdateCanvasInfo()
    {
        string key = _tool switch { Tool.Region => "toolRegion", Tool.Point => "toolPoint", Tool.Template => "toolTemplate", Tool.Color => "toolColor", Tool.Ocr => "toolOcr", Tool.Grid => "toolGrid", _ => "toolSelect" };
        _canvasInfo.Text = _tool == Tool.Select && _frozen != null ? FrameInfo() : L.T("hint_" + key);
    }

    private static NormFrame? CaptureOnce(IntPtr hwnd)
    {
        try
        {
            using var wgc = new WgcFrameSource();
            wgc.Start(hwnd);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                using var fr = wgc.TryGetLatest();
                if (fr != null) return NormFrame.FromBgra(fr.Bgra, 1920, fr.Seq, fr.ArrivalQpc);
                Thread.Sleep(20);
            }
        }
        catch { }
        var g = new GdiFrameSource();
        g.Start(hwnd);
        using var gf = g.TryGetLatest();
        return gf == null ? null : NormFrame.FromBgra(gf.Bgra);
    }

    // ───────────────────────── 画布工具 ─────────────────────────

    private void SetTool(Tool t)
    {
        _tool = t;
        foreach (var (k, b) in _toolButtons) b.IsChecked = k == t;
        _canvas.Cursor = null;
        _canvas.ShowLoupe = t == Tool.Color || _selected is ResourceRef { Kind: "color" };   // 放大镜
        UpdateCanvasInfo();
    }

    private void OnCanvasDown(Point p) { _dragStart = p; _canvas.Drag = null; }

    private void OnCanvasMove(Point p)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        if (_tool is Tool.Region or Tool.Template or Tool.Ocr or Tool.Grid)
            _canvas.Drag = new Rect(_dragStart, p);
    }

    private async void OnCanvasUp(Point p)
    {
        var rect = new Rect(_dragStart, p);
        _canvas.Drag = null;
        _canvas.InvalidateVisual();
        if (ColorPickAt(p)) return;   // 调色盘的「重新吸色」「加上这一点」优先
        var r = new RegionDef { X = (int)Math.Round(rect.X), Y = (int)Math.Round(rect.Y), W = (int)Math.Round(rect.Width), H = (int)Math.Round(rect.Height) };
        bool isDrag = r.W >= 4 && r.H >= 4;
        switch (_tool)
        {
            case Tool.Select:
                var hitR = _ed.Program.Regions.FirstOrDefault(kv => p.X >= kv.Value.X && p.X <= kv.Value.X + kv.Value.W && p.Y >= kv.Value.Y && p.Y <= kv.Value.Y + kv.Value.H);
                var hitP = _ed.Program.Points.FirstOrDefault(kv => Math.Abs(kv.Value.X - p.X) < 20 && Math.Abs(kv.Value.Y - p.Y) < 20);
                if (hitP.Key != null) SelectResource("point", hitP.Key);
                else if (hitR.Key != null) SelectResource("region", hitR.Key);
                break;
            case Tool.Region when isDrag:
            {
                string? n = Ui.Ask(this, "新热区", $"热区名称（{r}）：", $"热区{_ed.Program.Regions.Count + 1}");
                if (n == null) return;
                SelectResource("region", _ed.AddRegion(n, r));
                break;
            }
            case Tool.Grid when isDrag:
            {
                string? n = Ui.Ask(this, "新网格", $"网格名称（第一格 {r}）：", $"网格{_ed.Program.Grids.Count + 1}");
                if (n == null) return;
                SelectResource("grid", _ed.AddGrid(n, r));
                break;
            }
            case Tool.Point:
            {
                string? n = Ui.Ask(this, "新定点", $"定点名称（{(int)p.X},{(int)p.Y}）：", $"定点{_ed.Program.Points.Count + 1}");
                if (n == null) return;
                SelectResource("point", _ed.AddPoint(n, (int)Math.Round(p.X), (int)Math.Round(p.Y)));
                break;
            }
            case Tool.Template when isDrag:
            {
                if (_frozen == null) { Log("先截一帧"); return; }
                string? n = Ui.Ask(this, "新模板", $"模板名称（{r.W}×{r.H}）：", $"模板{_ed.Program.Templates.Count + 1}");
                if (n == null) return;
                var (name, warn) = _ed.AddTemplate(n, _frozen, r);
                if (warn != null) Log("⚠ " + warn);
                Log($"已裁模板「{name}」→ {_ed.Program.Templates[name].File}");
                SelectResource("template", name);
                break;
            }
            case Tool.Color:
            {
                if (_frozen == null) { Log("先截一帧"); return; }
                string? n = Ui.Ask(this, "新颜色", $"颜色名称（取样点 {(int)p.X},{(int)p.Y}）：", $"颜色{_ed.Program.Colors.Count + 1}");
                if (n == null) return;
                string name = _ed.AddColor(n, _frozen, (int)Math.Round(p.X), (int)Math.Round(p.Y));
                var c = _ed.Program.Colors[name];
                Log($"已取颜色「{name}」：HSV 下限 [{string.Join(",", c.Lower)}] 上限 [{string.Join(",", c.Upper)}]");
                SelectResource("color", name);
                break;
            }
            case Tool.Ocr when isDrag:
            {
                if (_frozen == null) { Log("先截一帧"); return; }
                try
                {
                    var (raw, norm, ms) = await new OcrReader().ReadAsync(_frozen, r);
                    Log($"OCR（{r}）：「{norm}」，原文「{raw}」，用时 {ms:0.0}ms");
                }
                catch (Exception e) { Log("OCR 失败：" + e.Message); }
                break;
            }
        }
    }

    private void SelectResource(string kind, string name)
    {
        RefreshAll();
        foreach (var o in _resources.Items)
            if (o is ListBoxItem { Tag: ResourceRef r } it && r.Kind == kind && r.Name == name) { _resources.SelectedItem = it; it.BringIntoView(); }
        _selected = new ResourceRef(kind, name);
        ShowProps();
    }

    // ───────────────────────── 运行 ─────────────────────────

    private async void Run()
    {
        if (_run != null) return;
        var errs = ProgramIO.Validate(_ed.Program);
        if (_ed.Program.Program.Count == 0) errs.Add("程序是空的");
        if (errs.Count > 0) { Log("程序还有问题，先改好：\n  " + string.Join("\n  ", errs)); return; }
        if (_bound == IntPtr.Zero || !WindowInfo.IsAlive(_bound)) { Log("先启动靶子或绑定窗口"); return; }
        string pname;
        pname = ProcessNameOf(_bound);
        if (!_ed.Program.Target.AllowedProcesses.Contains(pname, StringComparer.OrdinalIgnoreCase)) { Log($"进程 {pname} 不在白名单里，不运行"); return; }
        _ed.Save();
        UpdateTitle();
        _sessionDir ??= Path.Combine(AppPaths.Results, "studio-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _run = new StudioRun(_sessionDir);
        _runBtn.IsEnabled = false;
        _stopBtn.IsEnabled = true;
        _notInFrontTicks = 0;
        try { _pill?.Close(); _pill = new StatusPill(_bound, Ui.IsDark); _pill.Show(); } catch (Exception e) { _pill = null; Log("⚠ 置顶状态胶囊没能显示：" + e.Message); }
        ApplyRunUi(RunUi.Running);
        _poll.Start();
        Log($"▶ 开始运行。接下来请不要碰键鼠；随时可按 {AppSettings.KeyOf(Keymap.EStop)} 急停。");
        var run = _run;
        string result = await run.RunAsync(_ed, _bound, s => Dispatcher.BeginInvoke(() => Log(s)), _ => { });
        _poll.Stop();
        Log(result);
        _lastResults = Directory.Exists(run.DataDir) ? run.DataDir : _sessionDir;
        _openResultsBtn.IsEnabled = true;
        _openResultsCap.Visibility = Visibility.Visible;
        Log(Directory.Exists(run.DataDir)
            ? $"结果在 {run.DataDir}（data.csv、images、manifest.json），点「打开结果」可以直接打开"
            : $"日志在 {_sessionDir}");
        if (_launched != null) Log("关闭练习靶子窗口后，target\\summary.json 里有靶子这一侧的统计");
        bool estop = run.EmergencyStopped;
        string head = result.Split('，')[0];
        ApplyRunUi(estop ? RunUi.EStop : RunUi.Done, estop ? null : head);
        // 置顶胶囊把最后的状态留 4 秒再关
        var pill = _pill;
        _pill = null;
        if (pill != null) { var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; t.Tick += (_, _) => { t.Stop(); try { pill.Close(); } catch { } }; t.Start(); }
        _run = null;
        _runBtn.IsEnabled = true;
        _stopBtn.IsEnabled = false;
        if (_closeRequested) { Close(); return; }
        Activate();
    }

    /// <summary>用资源管理器打开上一次运行的结果文件夹。</summary>
    private void OpenResults()
    {
        if (_lastResults == null || !Directory.Exists(_lastResults)) { Log("还没有运行结果"); return; }
        try { Process.Start(new ProcessStartInfo { FileName = _lastResults, UseShellExecute = true }); }
        catch (Exception e) { Log("打不开结果文件夹：" + e.Message); }
    }

    private bool _closeRequested;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 运行中关窗口：先停下并等运行器在 finally 里抬起所有键，再真正关闭，避免卡键
        if (_run != null)
        {
            _closeRequested = true;
            _run.Stop();
            e.Cancel = true;
            Log("正在停止运行，停好后自动关闭……");
            return;
        }
        if (_ed.Dirty && MessageBox.Show(this, "程序有未保存的修改，要保存吗？", L.T("appName"), MessageBoxButton.YesNo) == MessageBoxResult.Yes) _ed.Save();
        try { _pill?.Close(); } catch { }
        _tester.Dispose();
    }

    private void Log(string s)
    {
        foreach (var line in s.Split('\n'))
            _log.Items.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_log.Items.Count > 400) _log.Items.RemoveAt(0);
        _log.ScrollIntoView(_log.Items[^1]);
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    }
}
