using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Runtime;

namespace VisionBlocks.Studio;

/// <summary>纯代码搭界面用的小工具：配色、按钮、输入对话框、设置窗口。2026-11-22 改成方案 C 的配色。</summary>
internal static class Ui
{
    // 积木三类颜色（Scratch 式实心块，白字对比度 ≥ 4:1）。深色主题下也不变
    public static readonly Brush FlowFg = Hex("#C96A0A"), SenseFg = Hex("#0E8A82"), ActFg = Hex("#3A66D8");
    public static readonly Brush FlowBg = Hex("#FCEBD7"), SenseBg = Hex("#D9F2EF"), ActBg = Hex("#E2E9FB");
    // 中性色和状态色跟主题走：直接用样式表里的画刷实例，切主题时它们就地变色
    public static Brush Muted => (Brush)Theme["Text2"];
    public static Brush Line => (Brush)Theme["Stroke"];
    public static Brush Panel => (Brush)Theme["Card2"];
    public static Brush Ok => (Brush)Theme["Ok"];
    public static Brush Bad => (Brush)Theme["Bad"];

    private static ResourceDictionary? _theme;
    private static readonly Dictionary<string, Color> LightColors = new();
    /// <summary>方案 C 的样式表。只合并进编辑器自己的窗口，不进 Application.Resources（遮罩和状态胶囊窗口不受影响）。</summary>
    public static ResourceDictionary Theme => _theme ??= LoadTheme();

    private static ResourceDictionary LoadTheme()
    {
        var d = new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionBlocksStudio;component/Theme.xaml", UriKind.Absolute) };
        foreach (var key in DarkColors.Keys)
            if (d[key] is SolidColorBrush b) LightColors[key] = b.Color;   // 浅色值以样式表为准
        return d;
    }

    // ───────── 深色 / 浅色 ─────────
    private static readonly Dictionary<string, string> DarkColors = new()
    {
        ["Bg"] = "#12151C", ["CardBg"] = "#1B1F29", ["Card2"] = "#232836", ["Stroke"] = "#2E3443", ["Stroke2"] = "#3D4558",
        ["Ink"] = "#E8EBF2", ["Text2"] = "#A9B0C2", ["Text3"] = "#7C8499", ["Focus"] = "#6E9BFF", ["OnInk"] = "#12151C", ["CardEdge"] = "#2E3443",
        ["Ok"] = "#3CCB7F", ["OkSoft"] = "#17352A", ["Warn"] = "#E0A100", ["WarnSoft"] = "#3A2F12", ["Bad"] = "#FF6B6B", ["BadSoft"] = "#3A1E22", ["Alarm"] = "#D23B3B",
    };

    public static bool IsDark { get; private set; }
    private static bool _themeApplied;

    /// <summary>Windows 的「应用模式」是不是深色（设置 → 个性化 → 颜色）。</summary>
    public static bool SystemIsDark()
    {
        try { return Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is int v && v == 0; }
        catch { return false; }
    }

    /// <summary>
    /// 按设置（system / light / dark）切主题。样式表里的画刷会被 WPF 冻结，不能就地改色，所以换成新画刷：
    /// 样式里用 DynamicResource 引用的地方自动跟上；代码里直接赋值的（Background = R("Ink") 这类），
    /// 遍历已打开窗口的元素树，把指向旧画刷的本地值换成新画刷。之后新建的界面直接从样式表取到新画刷。
    /// </summary>
    public static void ApplyTheme(string pref)
    {
        var theme = Theme;   // 先确保样式表已加载（LightColors 在加载时填）
        bool dark = pref == "dark" || (pref == "system" && SystemIsDark());
        if (_themeApplied && dark == IsDark) return;
        _themeApplied = true;
        IsDark = dark;
        var swap = new Dictionary<object, Brush>(ReferenceEqualityComparer.Instance);
        foreach (var (key, light) in LightColors)
        {
            var c = dark ? (Color)ColorConverter.ConvertFromString(DarkColors[key]) : light;
            var fresh = new SolidColorBrush(c);
            swap[theme[key]] = fresh;
            theme[key] = fresh;
        }
        if (Application.Current == null) return;
        foreach (Window w in Application.Current.Windows) { SwapBrushes(w, swap); DarkTitleBar(w); }
    }

    /// <summary>把元素树里所有「本地值是旧主题画刷」的属性换成新画刷（逻辑树 + 可视树都走一遍）。</summary>
    private static void SwapBrushes(DependencyObject root, Dictionary<object, Brush> swap)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (!seen.Add(o)) continue;
            var changes = new List<(DependencyProperty P, Brush B)>();
            var e = o.GetLocalValueEnumerator();
            while (e.MoveNext())
                if (e.Current.Value is Brush b && swap.TryGetValue(b, out var nb) && !e.Current.Property.ReadOnly) changes.Add((e.Current.Property, nb));
            foreach (var (p, b) in changes) o.SetValue(p, b);
            if (o is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) stack.Push(VisualTreeHelper.GetChild(o, i));
            foreach (var c in LogicalTreeHelper.GetChildren(o)) if (c is DependencyObject d) stack.Push(d);
        }
    }

    /// <summary>标题栏跟主题（Windows 10 2004 起支持 DWMWA_USE_IMMERSIVE_DARK_MODE = 20）。窗口还没有句柄时等 SourceInitialized。</summary>
    public static void DarkTitleBar(Window w)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) { w.SourceInitialized += (_, _) => DarkTitleBar(w); return; }
        int on = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(h, 20, ref on, sizeof(int)); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Button Btn(string text, Action onClick, string automationId, Brush? bg = null, Brush? fg = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4), MinWidth = 54 };
        if (bg != null) { b.Background = bg; b.BorderBrush = bg; }
        if (fg != null) b.Foreground = fg;
        AutomationProperties.SetAutomationId(b, automationId);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static TextBlock Label(string text, double size = 12, Brush? fg = null, FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, Foreground = fg ?? (Brush)Theme["Ink"], FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    private static Window Dialog(Window owner, string title)
    {
        var w = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = (Brush)Theme["CardBg"], Foreground = (Brush)Theme["Ink"], FontFamily = (FontFamily)Theme["UiFont"], FontSize = 14,
            Resources = { MergedDictionaries = { Theme } }
        };
        DarkTitleBar(w);
        return w;
    }

    public static string? Ask(Window owner, string title, string prompt, string def)
    {
        var w = Dialog(owner, title);
        var tb = new TextBox { Text = def, MinWidth = 300, Margin = new Thickness(0, 8, 0, 14) };
        AutomationProperties.SetAutomationId(tb, "askText");
        string? result = null;
        var ok = new Button { Content = "确定", IsDefault = true, Width = 88, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Theme["InkButton"] };
        var cancel = new Button { Content = "取消", IsCancel = true, Width = 88 };
        ok.Click += (_, _) => { result = tb.Text; w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var sp = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        sp.Children.Add(Label(prompt, 14));
        sp.Children.Add(tb);
        sp.Children.Add(buttons);
        w.Content = sp;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>
    /// 设置窗口：语言、主题、显示练习靶子、鼠标操作随机化。
    /// applyLang 返回 false 表示这次不能切（例如运行中），单选框退回原值。其他项改动后立即存盘，再调 changed 让主窗口跟着变。
    /// 语言切换后这个窗口本身不重排（下次打开就是新语言），和以前一样。
    /// </summary>
    public static void ShowSettings(Window owner, Func<string, bool> applyLang, Action changed, Func<bool>? isRunning = null)
    {
        var w = Dialog(owner, L.T("settings"));
        var s = AppSettings.Current;
        var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 460 };
        sp.Children.Add(new TextBlock { Text = L.T("settings"), FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Theme["DisplayFont"], Margin = new Thickness(0, 0, 0, 14) });
        void Head(string icon, string text)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var ic = Icons.Get(icon, 20, (Brush)Theme["Ink"]);
            ic.Margin = new Thickness(0, 0, 10, 0);
            head.Children.Add(ic);
            head.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
        }
        StackPanel Radios(string aid, string name)
        {
            var g = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 14) };
            AutomationProperties.SetAutomationId(g, aid);
            AutomationProperties.SetName(g, name);
            return g;
        }
        RadioButton Radio(StackPanel group, string value, string text, string aid, bool on, Func<string, bool> pick)
        {
            var r = new RadioButton { Content = text, GroupName = "vb-" + AutomationProperties.GetAutomationId(group), IsChecked = on, Tag = value, Margin = new Thickness(0, 0, 24, 0), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(r, aid);
            r.Checked += (_, _) =>
            {
                if (!pick(value)) w.Dispatcher.BeginInvoke(() => { foreach (var o in group.Children) if (o is RadioButton rb) rb.IsChecked = (string)rb.Tag == L.Lang; });
            };
            group.Children.Add(r);
            return r;
        }

        // 界面语言
        Head("lang", L.T("language"));
        sp.Children.Add(Label(L.T("langNote"), 12, (Brush)Theme["Text2"]));
        var langs = Radios("language", L.T("language"));
        bool PickLang(string lang)
        {
            if (L.Lang == lang) return true;
            if (!applyLang(lang)) return false;
            w.Title = L.T("settings");
            return true;
        }
        Radio(langs, "zh", "中文（简体）", "langZh", L.Lang == "zh", PickLang);
        Radio(langs, "en", "English", "langEn", L.Lang == "en", PickLang);
        sp.Children.Add(langs);

        // 主题
        Head("color", L.T("theme"));
        sp.Children.Add(Label(L.T("themeNote"), 12, (Brush)Theme["Text2"]));
        var themes = Radios("theme", L.T("theme"));
        bool PickTheme(string t) { if (s.Theme != t) { s.Theme = t; AppSettings.Save(); changed(); } return true; }
        Radio(themes, "system", L.T("themeSystem"), "themeSystem", s.Theme == "system", PickTheme);
        Radio(themes, "light", L.T("themeLight"), "themeLight", s.Theme == "light", PickTheme);
        Radio(themes, "dark", L.T("themeDark"), "themeDark", s.Theme == "dark", PickTheme);
        sp.Children.Add(themes);

        // 显示练习靶子
        Head("target", L.T("practiceTarget"));
        sp.Children.Add(Label(L.T("practiceTargetNote"), 12, (Brush)Theme["Text2"]));
        var practice = new CheckBox { Content = L.T("practiceTarget"), IsChecked = s.ShowPracticeTarget, Margin = new Thickness(0, 6, 0, 14) };
        AutomationProperties.SetAutomationId(practice, "showPracticeTarget");
        practice.Checked += (_, _) => { s.ShowPracticeTarget = true; AppSettings.Save(); changed(); };
        practice.Unchecked += (_, _) => { s.ShowPracticeTarget = false; AppSettings.Save(); changed(); };
        sp.Children.Add(practice);

        // 钉住外层作用域（2026-11-23）
        Head("grid", L.T("stickyScope"));
        sp.Children.Add(Label(L.T("stickyScopeNote"), 12, (Brush)Theme["Text2"]));
        var sticky = new CheckBox { Content = L.T("stickyScope"), IsChecked = s.StickyScope, Margin = new Thickness(0, 6, 0, 14) };
        AutomationProperties.SetAutomationId(sticky, "stickyScopeSetting");
        sticky.Checked += (_, _) => { s.StickyScope = true; AppSettings.Save(); changed(); };
        sticky.Unchecked += (_, _) => { s.StickyScope = false; AppSettings.Save(); changed(); };
        sp.Children.Add(sticky);

        // 鼠标操作随机化：打开前用和「加入白名单」同样的系统确认框
        Head("select", L.T("humanize"));
        sp.Children.Add(Label(L.T("humanizeNote"), 12, (Brush)Theme["Text2"]));
        var hum = new CheckBox { Content = L.T("humanize"), IsChecked = s.Humanize, Margin = new Thickness(0, 6, 0, 6) };
        AutomationProperties.SetAutomationId(hum, "humanize");
        var timing = new CheckBox { Content = L.T("humanizeTiming"), IsChecked = s.HumanizeTiming, Margin = new Thickness(26, 2, 0, 4) };
        var path = new CheckBox { Content = L.T("humanizePath"), IsChecked = s.HumanizePath, Margin = new Thickness(26, 2, 0, 4) };
        var over = new CheckBox { Content = L.T("humanizeOvershoot"), IsChecked = s.HumanizeOvershoot, Margin = new Thickness(52, 2, 0, 8) };
        AutomationProperties.SetAutomationId(timing, "humanizeTiming");
        AutomationProperties.SetAutomationId(path, "humanizePath");
        AutomationProperties.SetAutomationId(over, "humanizeOvershoot");
        timing.IsEnabled = path.IsEnabled = over.IsEnabled = s.Humanize;
        bool undo = false;
        hum.Checked += (_, _) =>
        {
            if (undo) return;
            var ans = MessageBox.Show(w, L.T("humanizeConfirm"), L.T("humanizeConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes) { undo = true; hum.IsChecked = false; undo = false; return; }
            s.Humanize = true; AppSettings.Save(); timing.IsEnabled = path.IsEnabled = over.IsEnabled = true; changed();
        };
        hum.Unchecked += (_, _) => { if (undo) return; s.Humanize = false; AppSettings.Save(); timing.IsEnabled = path.IsEnabled = over.IsEnabled = false; changed(); };
        timing.Checked += (_, _) => { s.HumanizeTiming = true; AppSettings.Save(); };
        timing.Unchecked += (_, _) => { s.HumanizeTiming = false; AppSettings.Save(); };
        path.Checked += (_, _) => { s.HumanizePath = true; AppSettings.Save(); };
        path.Unchecked += (_, _) => { s.HumanizePath = false; AppSettings.Save(); };
        over.Checked += (_, _) => { s.HumanizeOvershoot = true; AppSettings.Save(); };
        over.Unchecked += (_, _) => { s.HumanizeOvershoot = false; AppSettings.Save(); };
        sp.Children.Add(hum);
        sp.Children.Add(timing);
        sp.Children.Add(path);
        sp.Children.Add(over);

        // 快捷键（2026-11-23）：单独一个窗口，列表太长放不进设置
        Head("select", L.T("shortcuts"));
        sp.Children.Add(Label(L.T("shortcutsNote"), 12, (Brush)Theme["Text2"]));
        var scBtn = new Button { Content = L.T("btnShortcuts"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(14, 4, 14, 4) };
        AutomationProperties.SetAutomationId(scBtn, "btnShortcuts");
        scBtn.Click += (_, _) => ShowShortcuts(w, changed, isRunning ?? (() => false));
        sp.Children.Add(scBtn);

        sp.Children.Add(Label(L.T("dpiNote"), 12, (Brush)Theme["Text3"]));
        var close = new Button { Content = L.T("close"), IsCancel = true, IsDefault = true, Width = 96, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Style = (Style)Theme["InkButton"] };
        AutomationProperties.SetAutomationId(close, "settingsClose");
        close.Click += (_, _) => w.Close();
        sp.Children.Add(close);
        w.Content = sp;
        w.ShowDialog();
    }

    /// <summary>动作在界面上叫什么（快捷键窗口用，按钮上的名字）。</summary>
    public static string ActionName(string id) => id switch
    {
        "launch" => L.T("btnLaunch"), "refresh" => L.T("btnRefresh"),
        "new" => L.T("btnNew"), "open" => L.T("btnOpen"), "save" => L.T("btnSave"), "sample" => L.T("btnSample"),
        "undo" => L.T("btnUndo"), "redo" => L.T("btnRedo"), "settings" => L.T("settings"),
        "run" => L.T("btnRun"), "stop" => L.T("btnStop"), "openResults" => L.T("openResults"), "freeze" => L.T("btnFreeze"),
        "copy" => L.T("btnCopy"), "cut" => L.T("btnCut"), "paste" => L.T("btnPaste"), "duplicate" => L.T("btnDuplicate"), "delete" => L.T("btnDelete"),
        "moveUp" => L.T("btnUp"), "moveDown" => L.T("btnDown"), "indent" => L.T("btnIndent"), "outdent" => L.T("btnOutdent"),
        "fold" => L.T("foldClose"), "unfold" => L.T("foldOpen"), "foldAll" => L.T("foldAll"), "unfoldAll" => L.T("unfoldAll"),
        Keymap.Pause => L.T("hkPause"), Keymap.EStop => L.T("hkStop"),
        _ when id.StartsWith("tool", StringComparison.Ordinal) => L.T(id),
        _ when id.StartsWith("pal:", StringComparison.Ordinal) => L.T("scPalette") + L.Z(L.PaletteName(id[4..])),
        _ => id
    };

    /// <summary>
    /// 快捷键窗口（2026-11-23）：每个动作一行，右边的框点一下再按组合键就改；也可以直接往框里写「Ctrl+Shift+K」（界面自动化测试用）。
    /// 撞键时问要不要替换；暂停、急停按 Keymap.Check 的规矩查，再试注册一下看有没有被别的程序占着。改完立即保存。
    /// </summary>
    public static void ShowShortcuts(Window owner, Action changed, Func<bool> isRunning)
    {
        var w = Dialog(owner, L.T("shortcuts"));
        var s = AppSettings.Current;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 540 };
        root.Children.Add(new TextBlock { Text = L.T("shortcuts"), FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Theme["DisplayFont"], Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(Label(L.T("shortcutsHow"), 12, (Brush)Theme["Text2"]));
        var err = Label("", 12, Bad);
        AutomationProperties.SetAutomationId(err, "scError");
        root.Children.Add(err);
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height - 300), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        bool quiet = false;

        void Apply(string id, Keymap.Gesture? g)
        {
            var map = Keymap.Effective(s.Shortcuts);
            var def = Keymap.Find(id)!;
            string want = g?.ToString() ?? "";
            if (want == map[id]) { err.Text = ""; Rebuild(); return; }
            if (Keymap.Check(id, g, map) is { } code) { err.Text = L.T("scErr_" + code); Rebuild(); return; }
            if (def.Global && g is { } gg)
            {
                if (isRunning()) { err.Text = L.T("scErr_running"); Rebuild(); return; }
                if (!HotkeyService.Probe(gg.ToString())) { err.Text = L.T("scErr_taken"); Rebuild(); return; }
            }
            if (g is { } x && Keymap.HolderOf(map, x, id) is { } holder)
            {
                var ans = MessageBox.Show(w, string.Format(L.T("scConflict"), x, ActionName(holder), ActionName(id)), L.T("shortcuts"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (ans != MessageBoxResult.Yes) { Rebuild(); return; }
                s.Shortcuts[holder] = "";
            }
            // 和默认值一样就不存，设置文件里只留改过的
            string dflt = Keymap.TryParse(def.Default, out var dg) ? dg.ToString() : "";
            if (want == dflt) s.Shortcuts.Remove(id); else s.Shortcuts[id] = want;
            AppSettings.Save();
            err.Text = "";
            changed();
            Rebuild();
        }

        void Rebuild()
        {
            quiet = true;
            list.Children.Clear();
            var map = Keymap.Effective(s.Shortcuts);
            string group = "";
            foreach (var a in Keymap.Actions)
            {
                if (a.Group != group)
                {
                    group = a.Group;
                    list.Children.Add(new TextBlock { Text = L.T("scGroup_" + group), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 10, 0, 4) });
                }
                string aid = a.Id.Replace(':', '_');
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock { Text = ActionName(a.Id), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var box = new TextBox { Text = map[a.Id], Margin = new Thickness(8, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetAutomationId(box, "sc_" + aid);
                AutomationProperties.SetName(box, ActionName(a.Id));
                string id = a.Id;
                // 按下组合键就改；字母、数字不会被打进框里
                box.PreviewKeyDown += (_, e) =>
                {
                    var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
                    if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) { e.Handled = true; return; }
                    if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;   // Tab 照常切到下一个框
                    if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Keyboard.ClearFocus(); return; }   // Esc = 不改了
                    e.Handled = true;
                    if (Keymap.NameOfVk(KeyInterop.VirtualKeyFromKey(key)) is not { } kn) return;
                    var m = Keyboard.Modifiers;
                    Apply(id, new Keymap.Gesture(m.HasFlag(ModifierKeys.Control), m.HasFlag(ModifierKeys.Alt), m.HasFlag(ModifierKeys.Shift), kn));
                };
                box.PreviewTextInput += (_, e) => e.Handled = true;
                // 直接写进去的文字（界面自动化的 ValuePattern / WM_SETTEXT）：解析得了就照样改
                // 放到下一轮再处理：撞键时要弹确认框，而且 Apply 会重建列表（把正在触发事件的这个框换掉）
                box.TextChanged += (_, _) =>
                {
                    if (quiet) return;
                    string t = box.Text;
                    if (Keymap.TryParse(t, out var g)) w.Dispatcher.BeginInvoke(() => Apply(id, g));
                    else if (t.Trim().Length == 0) w.Dispatcher.BeginInvoke(() => Apply(id, null));
                };
                var clear = new Button { Content = L.T("scClear"), Padding = new Thickness(10, 2, 10, 2) };
                AutomationProperties.SetAutomationId(clear, "scClear_" + aid);
                clear.Click += (_, _) => Apply(id, null);
                Grid.SetColumn(box, 1);
                Grid.SetColumn(clear, 2);
                row.Children.Add(name);
                row.Children.Add(box);
                row.Children.Add(clear);
                list.Children.Add(row);
            }
            quiet = false;
        }

        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var reset = new Button { Content = L.T("scReset"), Padding = new Thickness(14, 4, 14, 4) };
        AutomationProperties.SetAutomationId(reset, "scReset");
        reset.Click += (_, _) => { s.Shortcuts.Clear(); AppSettings.Save(); err.Text = ""; changed(); Rebuild(); };
        var close = new Button { Content = L.T("close"), IsCancel = true, Width = 96, Style = (Style)Theme["InkButton"] };
        AutomationProperties.SetAutomationId(close, "scClose");
        close.Click += (_, _) => w.Close();
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(reset);
        bottom.Children.Add(close);
        root.Children.Add(bottom);
        Rebuild();
        w.Content = root;
        w.ShowDialog();
    }
}
