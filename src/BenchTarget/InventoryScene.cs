using System.Diagnostics;
using System.Windows;

namespace BenchTarget;

/// <summary>
/// 背包场景（2026-11-20）：
/// 左侧分类；右侧 4 列物品格，可滚动；点开物品弹出详情页：左上两段页签，左边折线（点上标数字）加柱状图，右上图片和物品名，下面是库存。
/// 所有坐标都是基准 1920×1080。物品数据由种子决定，启动时整份写进 truth.jsonl，供评测对答案。
/// </summary>
public sealed class InventoryScene
{
    /// <param name="Badge">图片右下角的角标文字（2026-11-25，--twins）；空 = 没角标。</param>
    /// <param name="BadgeColor">角标底色：0 红、1 蓝（字一样、只差底色的那一对用）。</param>
    public sealed record Item(int Cat, int Idx, string Name, int Stock, int[] Prices, int[] Vols, int Shape, int Color, string Badge = "", int BadgeColor = 0);

    public static readonly string[] Categories = { "武器", "防具", "药品", "材料" };
    private static readonly int[] Counts = { 13, 9, 6, 18 };
    private static readonly string[][] Prefix =
    {
        new[] { "青铜", "精钢", "寒铁", "赤焰", "雷鸣", "玄冰", "疾风", "碧落" },
        new[] { "皮革", "锁子", "秘银", "龙鳞", "藤编", "骨甲" },
        new[] { "小瓶", "中瓶", "大瓶", "特制" },
        new[] { "粗糙", "普通", "优质", "稀有", "传说" },
    };
    private static readonly string[][] Suffix =
    {
        new[] { "短剑", "长枪", "战斧", "弯刀", "法杖", "长弓" },
        new[] { "头盔", "护腕", "胸甲", "战靴", "披风" },
        new[] { "回血药", "回蓝药", "解毒剂", "力量药" },
        new[] { "铁矿", "木材", "兽皮", "灵石", "丝线", "草药" },
    };

    // ── 版面（基准坐标） ──
    public static readonly Rect CatPanel = new(40, 80, 340, 960);
    public static Rect CatBox(int i) => new(70, 160 + i * 120, 280, 90);
    public static readonly Rect ItemPanel = new(400, 80, 1480, 960);
    public static readonly Rect Viewport = new(420, 150, 1440, 870);
    public const double CellW = 300, CellH = 200, PitchX = 360, PitchY = 240, GridX = 450, ContentPad = 20;
    public const int Cols = 4, VisibleRows = 3;

    public static readonly Rect Panel = new(180, 70, 1560, 940);
    public static readonly Rect TabA = new(220, 100, 170, 56), TabB = new(390, 100, 170, 56);
    public static readonly Rect CloseBtn = new(1660, 90, 60, 60);
    public static readonly Rect Chart = new(220, 180, 1040, 790);
    public static readonly Rect Picture = new(1320, 150, 340, 340);
    public const double PointX0 = 300, PointDx = 150, LineTop = 270, LineBottom = 560, BarBase = 940, BarMaxH = 290;

    public readonly List<Item> Items = new();
    public int Category { get; private set; }
    public double Offset { get; private set; }
    public Item? Open { get; private set; }
    public int ScrollStep { get; }

    public int Clicks, Stray, Wheels, Opens, Drags;
    public readonly HashSet<(int, int)> OpenedSet = new();
    public readonly HashSet<int> CatsVisited = new() { 0 };

    private readonly TruthLog _log;
    private readonly double _dragThreshold, _dragRatio;
    private readonly bool _dragInertia, _naturalEnd;

    public InventoryScene(TargetOptions opt, TruthLog log)
    {
        _log = log;
        ScrollStep = opt.ScrollStep > 0 ? opt.ScrollStep : (int)PitchY;
        _dragThreshold = opt.DragThreshold; _dragRatio = opt.DragRatio; _dragInertia = opt.DragInertia; _naturalEnd = opt.NaturalEnd;
        var rng = new Random(opt.Seed);
        for (int c = 0; c < Categories.Length; c++)
        {
            var combos = (from p in Prefix[c] from s in Suffix[c] select p + s).OrderBy(_ => rng.Next()).Take(Counts[c]).ToList();
            for (int i = 0; i < combos.Count; i++)
            {
                var prices = Enumerable.Range(0, 7).Select(_ => rng.Next(3, 96)).ToArray();
                var vols = Enumerable.Range(0, 7).Select(_ => rng.Next(1, 11)).ToArray();
                Items.Add(new Item(c, i, combos[i], rng.Next(1, 100), prices, vols, rng.Next(6), rng.Next(6)));
            }
        }
        if (opt.Twins) AddTwins(opt.Seed);
        _log.Write(new
        {
            ev = "items", scrollStep = ScrollStep, dragThreshold = _dragThreshold, dragRatio = _dragRatio, dragInertia = _dragInertia,
            twins = opt.Twins, naturalEnd = _naturalEnd,
            items = Items.Select(it => new { cat = Categories[it.Cat], idx = it.Idx, name = it.Name, stock = it.Stock, prices = it.Prices, vols = it.Vols, shape = it.Shape, color = it.Color, badge = it.Badge, badgeColor = it.BadgeColor })
        });
        if (opt.OpenItem >= 0) Open = InCategory(0).ElementAtOrDefault(opt.OpenItem);
    }

    /// <summary>
    /// 同名物品（2026-11-25，--twins）：每个分类第 1 件加角标「1天」，紧跟一件同名同图、没角标的，分类末尾再放一件「3天」（库存也一样，只差角标）
    /// 和一件「7天」（给工具测「没裁过模板的角标」）；第 2 件加红底「限时」，紧跟一件同名同图、蓝底「限时」（字一样、只差底色）。
    /// 不动原来的随机序列：没加 --twins 时物品和以前逐个一样。
    /// </summary>
    private void AddTwins(int seed)
    {
        var rng = new Random(seed ^ 0x7A15);
        var all = new List<Item>();
        for (int c = 0; c < Categories.Length; c++)
        {
            var list = InCategory(c).ToList();
            var a = list[0] with { Badge = "1天" };
            var b = list[1] with { Badge = "限时" };
            var cat = new List<Item> { a, a with { Badge = "", Stock = rng.Next(1, 100) }, b, b with { BadgeColor = 1, Stock = rng.Next(1, 100) } };
            cat.AddRange(list.Skip(2));
            cat.Add(a with { Badge = "3天" });
            cat.Add(a with { Badge = "7天", Stock = rng.Next(1, 100) });
            all.AddRange(cat.Select((it, i) => it with { Idx = i }));
        }
        Items.Clear();
        Items.AddRange(all);
    }

    public IEnumerable<Item> InCategory(int c) => Items.Where(i => i.Cat == c);
    private int RowsIn(int c) => (InCategory(c).Count() + Cols - 1) / Cols;
    /// <summary>
    /// 最多能滚多远。默认 =（行数 − 3）整行，到底时正好对齐；--natural-end（2026-11-25）停在「内容高 − 可视高」：
    /// 内容高 = 上下各留 20 + 行数 × 行距 − 最后一行下面的间距，和真实列表一样一般不是整行（武器 4 行只能滚 90）。
    /// </summary>
    public double MaxOffset => _naturalEnd
        ? Math.Max(0, 2 * ContentPad + RowsIn(Category) * PitchY - (PitchY - CellH) - Viewport.Height)
        : Math.Max(0, RowsIn(Category) - VisibleRows) * PitchY;

    /// <summary>第 idx 个物品格在屏幕上的位置（已减去滚动量，可能在视口外）。</summary>
    public Rect CellRect(int idx) => new(GridX + idx % Cols * PitchX, Viewport.Y + ContentPad + idx / Cols * PitchY - Offset, CellW, CellH);

    public void OnClick(Point p, long qpc)
    {
        Clicks++;
        string hit = "none";
        if (Open != null)
        {
            if (CloseBtn.Contains(p)) { hit = "close"; Close("x", qpc); }
            else if (TabA.Contains(p) || TabB.Contains(p)) hit = "tab";
            else if (Panel.Contains(p)) hit = "panel";
        }
        else
        {
            for (int i = 0; i < Categories.Length; i++)
                if (CatBox(i).Contains(p))
                {
                    hit = "cat:" + i;
                    Category = i; Offset = 0; CatsVisited.Add(i);
                    _log.Write(new { ev = "cat", cat = Categories[i], t = qpc });
                }
            if (hit == "none" && Viewport.Contains(p))
            {
                foreach (var it in InCategory(Category))
                    if (CellRect(it.Idx).Contains(p))
                    {
                        hit = "cell:" + it.Idx;
                        Open = it; Opens++; OpenedSet.Add((it.Cat, it.Idx));
                        _log.Write(new { ev = "open", cat = Categories[it.Cat], idx = it.Idx, name = it.Name, badge = it.Badge, t = qpc });
                    }
            }
        }
        if (hit == "none") Stray++;
        _log.Write(new { ev = "md", x = Math.Round(p.X, 1), y = Math.Round(p.Y, 1), hit, t = qpc });
    }

    public void OnWheel(int delta, Point p, long qpc)
    {
        Wheels++;
        if (Open != null || !Viewport.Contains(p)) { _log.Write(new { ev = "wheel", delta, applied = false, t = qpc }); return; }
        double before = Offset;
        Offset = Math.Clamp(Offset - delta / 120.0 * ScrollStep, 0, MaxOffset);
        _log.Write(new { ev = "wheel", delta, applied = true, from = before, offset = Offset, t = qpc });
    }

    public void OnEsc(long qpc) { if (Open != null) Close("esc", qpc); }

    // ── 按住拖动（2026-11-23）──
    private bool _pressed, _dragging;
    private Point _pressAt;
    private double _dragStartY, _dragStartOffset, _vel, _inertiaV, _lastOffset;

    /// <summary>按下：开了拖动、详情页没开、按在列表视口里时先记下来，等移动或抬起再决定是拖动还是点击。返回 true = 这一下先不当点击。</summary>
    public bool OnPress(Point p, long qpc)
    {
        if (_dragThreshold <= 0 || Open != null || !Viewport.Contains(p)) return false;
        _pressed = true; _dragging = false; _pressAt = p; _vel = 0; _inertiaV = 0;
        _log.Write(new { ev = "press", x = Math.Round(p.X, 1), y = Math.Round(p.Y, 1), t = qpc });
        return true;
    }

    /// <summary>按住移动：竖着移过门槛才开始拖（从过门槛的那一点算起，门槛这一段不算），之后列表跟着鼠标走（乘 DragRatio）。</summary>
    public void OnMove(Point p, long qpc)
    {
        if (!_pressed) return;
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _pressAt.Y) < _dragThreshold) return;
            _dragging = true; _dragStartY = p.Y; _dragStartOffset = Offset; Drags++;
            _log.Write(new { ev = "dragBegin", y = Math.Round(p.Y, 1), from = Offset, t = qpc });
        }
        Offset = Math.Clamp(_dragStartOffset + _dragRatio * (_dragStartY - p.Y), 0, MaxOffset);
    }

    /// <summary>抬起：拖过了就结束拖动（开了惯性就按松手时的速度再滑一段）；没拖动就算一次点击，按下的位置为准（点开物品的时机从按下改到了这里）。</summary>
    public bool OnRelease(long qpc)
    {
        if (!_pressed) return false;
        _pressed = false;
        if (_dragging)
        {
            _dragging = false;
            if (_dragInertia) _inertiaV = _vel;
            _log.Write(new { ev = "dragEnd", offset = Offset, inertia = Math.Round(_inertiaV, 1), t = qpc });
            return true;
        }
        OnClick(_pressAt, qpc);
        return true;
    }

    /// <summary>每帧调用：拖动时跟踪速度（按帧平滑，停住不动时速度慢慢降下来）；惯性滑动时每秒剩 13.5%，到头或速度很小就停。</summary>
    public void Tick(double dt)
    {
        if (dt > 0 && dt < 0.25)
        {
            if (_dragging) _vel += ((Offset - _lastOffset) / dt - _vel) * Math.Min(1, dt * 10);
            else if (_inertiaV != 0)
            {
                double before = Offset;
                Offset = Math.Clamp(Offset + _inertiaV * dt, 0, MaxOffset);
                _inertiaV *= Math.Pow(0.135, dt);
                if (Math.Abs(_inertiaV) < 1 || Offset == before)
                {
                    _inertiaV = 0;
                    _log.Write(new { ev = "inertiaEnd", offset = Offset, t = Stopwatch.GetTimestamp() });
                }
            }
        }
        _lastOffset = Offset;
    }

    private void Close(string how, long qpc)
    {
        _log.Write(new { ev = "close", how, name = Open?.Name, t = qpc });
        Open = null;
    }
}
