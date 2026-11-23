using System.Windows;

namespace VisionBlocks.Studio;

/// <summary>
/// 界面文案（2026-11-22 改版）：key → {zh, en}，默认中文，设置里切换。
/// 只覆盖界面框架（按钮、标签、标题、提示）；积木摘要（Core 的 BlockText）、属性面板里的说明文字、日志仍是中文。
/// 切换语言时不重建界面：用 Reg 登记过的控件原地换文字（重建会重复挂事件、重复加下拉项）。
/// </summary>
internal static class L
{
    public static string Lang { get; private set; } = "zh";
    public static bool En => Lang == "en";

    private static readonly Dictionary<string, (string Zh, string En)> T_ = new()
    {
        ["appName"] = ("视搭", "VisionBlocks"), ["appError"] = ("视搭出错了", "VisionBlocks error"),
        ["unsaved"] = ("未保存", "Unsaved"),
        ["btnNew"] = ("新建", "New"), ["btnOpen"] = ("打开…", "Open…"), ["btnSave"] = ("保存", "Save"), ["btnSample"] = ("载入示例", "Load sample"),
        ["step1"] = ("靶子与窗口", "Target & window"), ["step2"] = ("取材", "Capture"), ["step3"] = ("资源", "Resources"), ["step4"] = ("拼积木", "Build blocks"),
        ["monitor"] = ("屏幕", "Monitor"), ["scene"] = ("场景", "Scene"), ["scenePatrol"] = ("巡逻小游戏", "Patrol game"), ["sceneInventory"] = ("背包", "Inventory"),
        ["practiceHead"] = ("练习靶子", "Practice target"),
        ["btnLaunch"] = ("启动靶子", "Launch target"), ["bind"] = ("绑定窗口", "Bound window"),
        ["bindTip"] = ("列出所有窗口；第一次绑定白名单外的程序时会先问你", "Lists every window; binding a program outside the allow-list asks you first"),
        ["noWindows"] = ("（没有找到窗口）", "(No windows found)"),
        ["btnRefresh"] = ("刷新", "Refresh"), ["btnFreeze"] = ("截一帧", "Capture frame"),
        ["openResults"] = ("打开结果", "Open results"), ["openResultsTip"] = ("打开上一次运行的结果文件夹（data.csv、images、manifest.json）", "Open the last run's results folder (data.csv, images, manifest.json)"),
        ["theme"] = ("主题", "Theme"), ["themeSystem"] = ("跟随系统", "Use system setting"), ["themeLight"] = ("浅色", "Light"), ["themeDark"] = ("深色", "Dark"),
        ["themeNote"] = ("「跟随系统」跟着 Windows 的浅色 / 深色走。立即生效。", "\"Use system setting\" follows Windows light / dark mode. Applies right away."),
        ["practiceTarget"] = ("显示练习靶子", "Show practice target"),
        ["practiceTargetNote"] = ("在 ① 里显示「场景」和「启动靶子」，用来练习和测试。默认关。", "Shows Scene and Launch target in step 1, for practice and testing. Off by default."),
        ["humanize"] = ("鼠标操作随机化", "Randomize mouse actions"),
        ["humanizeNote"] = ("测试你自己的软件或自制游戏能不能识别出自动操作。默认关，打开前会确认；下次运行生效。", "Tests whether your own software or game can tell automated input apart. Off by default, asks first; applies from the next run."),
        ["humanizeTiming"] = ("位置和时机：点在热区内的随机位置，按住、等待时长随机", "Position and timing: random spot inside the region, random hold and wait"),
        ["humanizePath"] = ("移动轨迹：沿弯曲路径、先快后慢移过去", "Movement path: curved path, fast then slow"),
        ["humanizeOvershoot"] = ("终点前轻微过冲再回正", "Overshoot slightly, then settle"),
        ["humanizeConfirmTitle"] = ("打开鼠标操作随机化", "Turn on mouse randomization"),
        ["humanizeConfirm"] = ("打开后，点击会落在热区内的随机位置，按住和等待时长也会随机；鼠标会沿弯曲的路径、以变化的速度移到目标点。急停和前台检查照常生效，轨迹不会移出绑定的窗口。\n\n这个功能用来测试你自己的软件或自制游戏能不能识别出自动操作。请只用在你有权测试的软件上。\n\n确定打开吗？",
            "Clicks will land at random spots inside the region, with random hold and wait times, and the mouse will move along curved paths at varying speed. E-stop and the foreground check still apply, and the path never leaves the bound window.\n\nThis is for testing whether your own software or game can detect automated input. Only use it on software you are allowed to test.\n\nTurn it on?"),
        ["btnRun"] = ("运行", "Run"), ["btnStop"] = ("停止", "Stop"), ["hkPause"] = ("暂停/继续", "Pause/Resume"), ["hkStop"] = ("急停", "E-stop"),
        ["settings"] = ("设置", "Settings"), ["close"] = ("关闭", "Close"),
        ["toolSelect"] = ("选择", "Select"), ["toolRegion"] = ("拖框建热区", "Draw region"), ["toolPoint"] = ("点选建定点", "Place point"),
        ["toolTemplate"] = ("裁模板", "Crop template"), ["toolColor"] = ("吸色", "Pick color"), ["toolOcr"] = ("OCR 试读", "Try OCR"), ["toolGrid"] = ("拖框建网格", "Draw grid"),
        ["hint_toolSelect"] = ("点热区或定点可以选中它", "Click a region or point to select it"),
        ["hint_toolRegion"] = ("在画面上拖一个框 → 起名 → 成为热区（识别只在热区里做）", "Drag a box → name it → it becomes a region (detection only looks inside regions)"),
        ["hint_toolPoint"] = ("点一下 → 起名 → 成为定点（点击积木用）", "Click once → name it → it becomes a point (for Click blocks)"),
        ["hint_toolTemplate"] = ("拖框裁模板：只框和干扰项不同的部分（比如只框字母，不框公共边框）", "Drag to crop a template: frame only what differs from decoys"),
        ["hint_toolColor"] = ("点一下要判断的颜色 → 生成 HSV 范围", "Click the color to detect → an HSV range is generated"),
        ["hint_toolOcr"] = ("拖框试读文字（不保存，只看识别结果）", "Drag a box to try OCR (nothing is saved)"),
        ["hint_toolGrid"] = ("拖框框出第一格 → 起名 → 成为网格；再在右边改列数、行数、间距，直到每个框都套住一个格子", "Drag around the first cell → name it → it becomes a grid; then adjust columns, rows and gaps on the right"),
        ["programHint"] = ("缩进 = 属于上一块", "Indented = belongs to the block above"),
        ["btnUp"] = ("上移", "Up"), ["btnDown"] = ("下移", "Down"), ["btnIndent"] = ("缩进", "Indent"), ["btnOutdent"] = ("取消缩进", "Outdent"), ["btnDelete"] = ("删除", "Delete"),
        ["btnCopy"] = ("复制", "Copy"), ["btnPaste"] = ("粘贴", "Paste"), ["btnCut"] = ("剪切", "Cut"), ["btnDuplicate"] = ("复制一份", "Duplicate"),
        ["btnUndo"] = ("撤销", "Undo"), ["btnRedo"] = ("重做", "Redo"),
        ["grpFlow"] = ("流程", "Flow"), ["grpSense"] = ("识别（生成「如果…」）", "Detect (makes an \"If…\")"), ["grpRecord"] = ("读取与记录", "Read & record"), ["grpAct"] = ("动作", "Actions"),
        ["palHint"] = ("点一下：加到选中积木的后面（选中「重复/如果」时加到里面）。也可以拖到右边的程序里。", "Click: insert after the selected block (inside it if it's a Repeat/If). Or drag it into the program."),
        ["props"] = ("属性", "Properties"), ["activity"] = ("活动", "Activity"),
        ["k_region"] = ("热区", "Region"), ["k_point"] = ("定点", "Point"), ["k_template"] = ("模板", "Template"), ["k_color"] = ("颜色", "Color"), ["k_grid"] = ("网格", "Grid"),
        ["emptyProgram"] = ("（空程序：从左边的积木架点或拖积木进来）", "(Empty program: click or drag blocks in from the shelf)"),
        ["elseRow"] = ("否则（条件不成立时）", "Else (when the condition is false)"),
        ["st_idle"] = ("就绪", "Ready"), ["st_running"] = ("运行中", "Running"), ["st_paused"] = ("已暂停", "Paused"),
        ["st_blocked"] = ("输入被拦截", "Input blocked"), ["st_estop"] = ("已急停", "E-stopped"), ["st_done"] = ("已停止", "Stopped"),
        ["sd_idleBound"] = ("已绑定窗口，随时可以运行", "Window bound, ready to run"), ["sd_idleUnbound"] = ("先启动靶子或绑定窗口", "Launch the target or bind a window first"),
        ["sd_running"] = ("请不要碰键鼠", "Hands off keyboard and mouse"), ["sd_paused"] = ("按 Ctrl+Alt+F11 继续", "Ctrl+Alt+F11 to resume"),
        ["sd_blocked"] = ("靶子不在前台，点一下靶子窗口", "The target isn't in front. Click the target window"),
        ["sd_estop"] = ("按键已全部抬起", "All keys released"),
        ["language"] = ("界面语言", "Language"),
        ["langNote"] = ("默认中文。切换后界面立即刷新；你起的热区名、程序名不会被翻译。积木摘要、说明文字和日志暂时还是中文。", "Chinese by default. The UI refreshes right away; names you gave are not translated. Block summaries, notes and the log are still in Chinese for now."),
        ["langRunning"] = ("运行中不能切换语言", "Can't switch language while running"),
        ["dpiNote"] = ("界面跟随每块屏幕的分辨率和缩放，换显示器、换电脑都不用设置。", "The UI follows each monitor's resolution and scaling; nothing to set when you change monitors or PCs."),
        ["monPrimary"] = ("主屏", "Primary"), ["monRight"] = ("主屏右侧", "Right of primary"), ["monLeft"] = ("主屏左侧", "Left of primary"),
        ["monAbove"] = ("主屏上方", "Above primary"), ["monBelow"] = ("主屏下方", "Below primary"),
        ["noFrame"] = ("还没有冻结帧：先「启动靶子」或绑定窗口，再点「截一帧」", "No frozen frame yet: launch the target or bind a window, then Capture frame"),
    };

    public static string T(string key) => T_.TryGetValue(key, out var v) ? (En ? v.En : v.Zh) : key;

    /// <summary>属性面板等由代码临时拼的中文标签：英文模式下查表换成英文，查不到就原样（说明文字不在表里，保持中文）。</summary>
    private static readonly Dictionary<string, string> Zh2En = new()
    {
        ["多久查一次 ms"] = "Check every (ms)", ["超时 ms（0 不限）"] = "Timeout ms (0 = none)", ["次数"] = "Times", ["最多等 ms"] = "Max wait (ms)",
        ["等待 ms"] = "Wait (ms)", ["按键"] = "Key", ["按住 ms"] = "Hold (ms)", ["点哪里"] = "Click where", ["点完确认"] = "Confirm after",
        ["确认最多等 ms"] = "Confirm timeout (ms)", ["文字"] = "Text", ["网格"] = "Grid", ["最多几遍"] = "Max passes", ["在哪里滚"] = "Scroll where",
        ["格数"] = "Notches", ["按什么滚"] = "Scroll by", ["按格数"] = "By notches", ["按距离 px（边滚边量）"] = "By distance px (measured)",
        ["距离 px（正数向下）"] = "Distance px (positive = down)", ["容差 px"] = "Tolerance px", ["读哪里"] = "Read where", ["存进字段"] = "Into field", ["只要数字"] = "Digits only", ["截哪里"] = "Capture where",
        ["按哪些字段去重"] = "Dedupe by fields", ["备注"] = "Note", ["条件类型"] = "Condition", ["模板"] = "Template", ["在哪个热区"] = "In region",
        ["得分阈值"] = "Score threshold", ["对比度下限"] = "Min contrast", ["颜色"] = "Color", ["比较"] = "Compare", ["占比 %"] = "Ratio %",
        ["包含文字"] = "Contains", ["取反"] = "Negate", ["看不到 / 不满足 / 不包含"] = "Not seen / not met / not contained",
        ["试一下（在冻结帧上）"] = "Try it (on the frozen frame)", ["试读（在冻结帧上）"] = "Try reading (on the frozen frame)", ["删除这个资源"] = "Delete this resource",
        ["宽"] = "Width", ["高"] = "Height", ["第一格 X"] = "First cell X", ["第一格 Y"] = "First cell Y", ["格宽"] = "Cell width", ["格高"] = "Cell height",
        ["列数"] = "Columns", ["行数"] = "Rows", ["列间距"] = "Column gap", ["行间距"] = "Row gap",
        ["积木 · "] = "Block · ", ["热区 · "] = "Region · ", ["定点 · "] = "Point · ", ["模板 · "] = "Template · ", ["网格 · "] = "Grid · ", ["颜色 · "] = "Color · ",
        ["否则分支"] = "Else branch", ["上手清单（先跑通，再动手拼）"] = "Getting started (run it first, then build)",
        ["重复直到…"] = "Repeat until…", ["重复 N 次"] = "Repeat N times", ["如果…"] = "If…", ["等到…"] = "Wait until…", ["等待"] = "Wait",
        ["点击"] = "Click", ["输入文字"] = "Type text", ["停止"] = "Stop", ["对每一格"] = "For each cell", ["重复直到没有新记录"] = "Repeat until no new rows",
        ["滚轮"] = "Wheel", ["拖动"] = "Drag", ["在哪个热区拖"] = "Drag in region", ["距离 px（正数向下翻）"] = "Distance px (positive = page down)",
        ["速度 px/秒"] = "Speed px/s", ["松手前停顿 ms"] = "Pause before release (ms)", ["读文字"] = "Read text", ["截图存图"] = "Save image", ["记录一行"] = "Record row",
        ["看到图像"] = "Sees image", ["颜色占比"] = "Color ratio", ["文字包含"] = "Text contains",
        ["直到这个条件成立就结束循环"] = "End the loop once this is true", ["条件"] = "Condition", ["等到这个条件成立"] = "Wait until this is true",
        ["点完等到这个条件成立"] = "After clicking, wait until",
        ["不确认"] = "No check", ["点击处画面有变化"] = "Frame changes where clicked", ["等到条件成立"] = "Wait for condition",
        ["色相 H（0–179）"] = "Hue H (0–179)", ["饱和度 S（横）× 明度 V（竖）"] = "Saturation S (across) × Value V (up)",
        ["深色格子：所选地方里色相在范围内的像素，越深越多"] = "Dark cells: pixels in the chosen area whose hue is in range; darker = more",
        ["下限"] = "Lower", ["上限"] = "Upper", ["（整个画面）"] = "(Whole frame)", ["整个画面"] = "Whole frame", [" 第一格"] = " first cell",
        ["看哪里的占比"] = "Show the ratio in", ["先截一帧，才能看占比"] = "Capture a frame to see the ratio",
        ["重新吸色"] = "Pick again", ["加上这一点"] = "Add this point",
    };
    public static string Z(string zh) => En && Zh2En.TryGetValue(zh, out var en) ? en : zh;

    // ───────── 登记表：切换语言时原地换文字 ─────────
    private static readonly List<(WeakReference<DependencyObject> Target, DependencyProperty Prop, Func<string> Text)> Regs = new();

    public static T Reg<T>(T target, DependencyProperty prop, string key) where T : DependencyObject => RegF(target, prop, () => L.T(key));

    public static T RegF<T>(T target, DependencyProperty prop, Func<string> text) where T : DependencyObject
    {
        target.SetValue(prop, text());
        Regs.Add((new WeakReference<DependencyObject>(target), prop, text));
        return target;
    }

    public static void Relabel()
    {
        Regs.RemoveAll(r => !r.Target.TryGetTarget(out _));
        foreach (var (w, prop, text) in Regs)
            if (w.TryGetTarget(out var t)) t.SetValue(prop, text());
    }

    // ───────── 设置文件（由 AppSettings 统一读写） ─────────
    public static void Load()
    {
        AppSettings.Load();
        Lang = AppSettings.Current.Lang;
    }

    public static void Set(string lang)
    {
        Lang = lang == "en" ? "en" : "zh";
        AppSettings.Current.Lang = Lang;
        AppSettings.Save();
        Relabel();
    }
}
