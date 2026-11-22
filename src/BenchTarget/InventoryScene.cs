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
    public sealed record Item(int Cat, int Idx, string Name, int Stock, int[] Prices, int[] Vols, int Shape, int Color);

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

    public int Clicks, Stray, Wheels, Opens;
    public readonly HashSet<(int, int)> OpenedSet = new();
    public readonly HashSet<int> CatsVisited = new() { 0 };

    private readonly TruthLog _log;

    public InventoryScene(TargetOptions opt, TruthLog log)
    {
        _log = log;
        ScrollStep = opt.ScrollStep > 0 ? opt.ScrollStep : (int)PitchY;
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
        _log.Write(new
        {
            ev = "items", scrollStep = ScrollStep,
            items = Items.Select(it => new { cat = Categories[it.Cat], idx = it.Idx, name = it.Name, stock = it.Stock, prices = it.Prices, vols = it.Vols, shape = it.Shape, color = it.Color })
        });
        if (opt.OpenItem >= 0) Open = InCategory(0).ElementAtOrDefault(opt.OpenItem);
    }

    public IEnumerable<Item> InCategory(int c) => Items.Where(i => i.Cat == c);
    private int RowsIn(int c) => (InCategory(c).Count() + Cols - 1) / Cols;
    public double MaxOffset => Math.Max(0, RowsIn(Category) - VisibleRows) * PitchY;

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
                        _log.Write(new { ev = "open", cat = Categories[it.Cat], idx = it.Idx, name = it.Name, t = qpc });
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

    private void Close(string how, long qpc)
    {
        _log.Write(new { ev = "close", how, name = Open?.Name, t = qpc });
        Open = null;
    }
}
