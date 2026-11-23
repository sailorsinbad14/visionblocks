using OpenCvSharp;
using VisionBlocks.Core.Vision;

namespace VisionBlocks.Core.Model;

/// <summary>
/// 编辑操作（不依赖界面，便于自测）：在冻结帧上建热区、定点、裁模板、吸色；增删移动积木。
/// 坐标一律是客户区基准坐标（与 NormFrame / 运行时 / 遮罩同一套），这是整条坐标链里唯一的存储口径。
/// </summary>
public sealed class ProgramEditor
{
    public VbProgram Program { get; private set; }
    public string ProgramPath { get; private set; }
    public string Dir => Path.GetDirectoryName(ProgramPath)!;
    public bool Dirty { get; private set; }
    public event Action? Changed;

    private ProgramEditor(VbProgram p, string path) { Program = p; ProgramPath = path; }

    public static ProgramEditor CreateNew(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var ed = new ProgramEditor(new VbProgram { Name = name }, Path.Combine(dir, "program.vbp.json"));
        ed.Save();
        return ed;
    }

    public static ProgramEditor Open(string path) => new(ProgramIO.Load(path), Path.GetFullPath(path));

    public void Save() { ProgramIO.Save(Program, ProgramPath); Dirty = false; }

    public void Touch() { Dirty = true; Changed?.Invoke(); }

    public static string Unique(string wanted, IEnumerable<string> existing)
    {
        var set = new HashSet<string>(existing);
        string n = string.IsNullOrWhiteSpace(wanted) ? "未命名" : wanted.Trim();
        if (!set.Contains(n)) return n;
        for (int i = 2; ; i++) if (!set.Contains($"{n}{i}")) return $"{n}{i}";
    }

    // ───────────── 资源 ─────────────

    public string AddRegion(string name, RegionDef r)
    {
        name = Unique(name, Program.Regions.Keys);
        r.Label = name;
        Program.Regions[name] = r;
        Touch();
        return name;
    }

    public string AddPoint(string name, int x, int y)
    {
        name = Unique(name, Program.Points.Keys);
        Program.Points[name] = new PointDef { Label = name, X = x, Y = y };
        Touch();
        return name;
    }

    /// <summary>从冻结帧裁模板，存成基准分辨率的 PNG。画面比基准小时会提示（把模板放大会让匹配变差）。</summary>
    public (string Name, string? Warning) AddTemplate(string name, NormFrame f, RegionDef r)
    {
        name = Unique(name, Program.Templates.Keys);
        var px = f.ToPixels(r);
        using var sub = new Mat(f.Bgr, px);
        string? warn = null;
        Mat toSave = sub;
        if (f.K < 0.999)
        {
            toSave = sub.Resize(new Size(r.W, r.H), 0, 0, InterpolationFlags.Cubic);
            warn = $"当前窗口比基准小（K={f.K:0.00}），模板是放大后保存的，边缘会糊。建议在基准尺寸下重新截帧裁模板。";
        }
        string rel = Path.Combine("templates", SafeFile(name) + ".png");
        Directory.CreateDirectory(Path.Combine(Dir, "templates"));
        Cv2.ImWrite(Path.Combine(Dir, rel), toSave);
        if (!ReferenceEquals(toSave, sub)) toSave.Dispose();
        Program.Templates[name] = new TemplateDef { Label = name, File = rel.Replace('\\', '/') };
        Touch();
        return (name, warn);
    }

    /// <summary>网格：在画布上拖出第一格，行列数和间距之后在属性面板里改。</summary>
    public string AddGrid(string name, RegionDef firstCell, int cols = 4, int rows = 3, int gapX = 10, int gapY = 10)
    {
        name = Unique(name, Program.Grids.Keys);
        Program.Grids[name] = new GridDef { Label = name, X = firstCell.X, Y = firstCell.Y, W = firstCell.W, H = firstCell.H, Cols = cols, Rows = rows, GapX = gapX, GapY = gapY };
        Touch();
        return name;
    }

    public string AddColor(string name, NormFrame f, int x, int y)
    {
        name = Unique(name, Program.Colors.Keys);
        var c = ColorRatio.FromSample(f, x, y, name);
        Program.Colors[name] = c;
        Touch();
        return name;
    }

    private static string SafeFile(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
    }

    /// <summary>删除资源前检查有没有积木在用，在用就拒绝并返回使用处。</summary>
    public List<string> UsesOf(string kind, string name)
    {
        var uses = new List<string>();
        void C(Condition? c, Block b)
        {
            bool used = c switch
            {
                ImageCondition i => (kind == "region" && i.Region == name) || (kind == "template" && i.Template == name),
                ColorCondition k => (kind == "region" && k.Region == name) || (kind == "color" && k.Color == name),
                TextCondition t => kind == "region" && t.Region == name,
                _ => false
            };
            if (used) uses.Add(BlockText.Summary(b, Program));
        }
        foreach (var b in All())
        {
            switch (b)
            {
                case RepeatUntilBlock r: C(r.Until, b); break;
                case IfBlock i: C(i.Cond, b); break;
                case WaitUntilBlock w: C(w.Cond, b); break;
                case ClickBlock c:
                    if ((kind == "point" && c.Point == name) || (kind == "region" && c.Region == name)) uses.Add(BlockText.Summary(b, Program));
                    C(c.ConfirmCond, b);
                    break;
                case WheelBlock wb:
                    if ((kind == "point" && wb.Point == name) || (kind == "region" && wb.Region == name)) uses.Add(BlockText.Summary(b, Program));
                    break;
                case DragBlock db when kind == "region" && db.Region == name:
                case ReadTextBlock rt when kind == "region" && rt.Region == name:
                case SaveImageBlock si when kind == "region" && si.Region == name:
                case ForEachCellBlock fe when kind == "grid" && fe.Grid == name:
                    uses.Add(BlockText.Summary(b, Program));
                    break;
            }
        }
        return uses;
    }

    public List<string> RemoveResource(string kind, string name)
    {
        var uses = UsesOf(kind, name);
        if (uses.Count > 0) return uses;
        switch (kind)
        {
            case "region": Program.Regions.Remove(name); break;
            case "point": Program.Points.Remove(name); break;
            case "template": Program.Templates.Remove(name); break;
            case "color": Program.Colors.Remove(name); break;
            case "grid": Program.Grids.Remove(name); break;
        }
        Touch();
        return uses;
    }

    // ───────────── 积木 ─────────────

    public IEnumerable<Block> All()
    {
        IEnumerable<Block> Walk(List<Block> l)
        {
            foreach (var b in l)
            {
                yield return b;
                if (b is IContainerBlock c) foreach (var x in Walk(c.Body)) yield return x;
                if (b is IfBlock i) foreach (var x in Walk(i.Else)) yield return x;
            }
        }
        return Walk(Program.Program);
    }

    /// <summary>找到积木所在的列表和下标。</summary>
    public (List<Block> List, int Index)? Locate(Block target)
    {
        (List<Block>, int)? Find(List<Block> l)
        {
            for (int i = 0; i < l.Count; i++)
            {
                if (ReferenceEquals(l[i], target)) return (l, i);
                if (l[i] is IContainerBlock c && Find(c.Body) is { } r1) return r1;
                if (l[i] is IfBlock ib && Find(ib.Else) is { } r2) return r2;
            }
            return null;
        }
        return Find(Program.Program);
    }

    /// <summary>新积木的默认参数取当前已有的第一个资源，用户再在属性面板里改。</summary>
    public Block CreateDefault(string type)
    {
        string region = Program.Regions.Keys.FirstOrDefault() ?? "";
        string tpl = Program.Templates.Keys.FirstOrDefault() ?? "";
        string color = Program.Colors.Keys.FirstOrDefault() ?? "";
        Condition DefaultCond() => tpl != "" ? new ImageCondition { Template = tpl, Region = region }
            : color != "" ? new ColorCondition { Color = color, Region = region }
            : new TextCondition { Region = region, Contains = "完成" };
        return type switch
        {
            "repeatUntil" => new RepeatUntilBlock { Until = new TextCondition { Region = region, Contains = "完成" }, CheckEveryMs = 1000 },
            "repeat" => new RepeatBlock { Times = 3 },
            "if" => new IfBlock { Cond = DefaultCond() },
            "waitUntil" => new WaitUntilBlock { Cond = DefaultCond(), TimeoutMs = 1000 },
            "wait" => new WaitBlock { Ms = 100 },
            "key" => new KeyBlock { Key = "F", HoldMs = 40 },
            "click" => Program.Points.Count > 0
                ? new ClickBlock { Point = Program.Points.Keys.First(), Confirm = ConfirmMode.PixelChange }
                : new ClickBlock { Region = region, Confirm = ConfirmMode.PixelChange },
            "typeText" => new TypeTextBlock { Text = "" },
            "stop" => new StopBlock(),
            "forEachCell" => new ForEachCellBlock { Grid = Program.Grids.Keys.FirstOrDefault() ?? "" },
            "untilNoNewRows" => new UntilNoNewRowsBlock { MaxTimes = 10 },
            "wheel" => new WheelBlock { Region = region, Notches = 1 },
            "drag" => new DragBlock { Region = region, DistancePx = 240 },
            "readText" => new ReadTextBlock { Region = region, Field = "名称" },
            "saveImage" => new SaveImageBlock { Region = region, Field = "图片" },
            "recordRow" => new RecordRowBlock(),
            _ => throw new ArgumentException(type)
        };
    }

    /// <summary>
    /// 插入规则：锚点是容器（重复/如果）且 into=true 时放进它的末尾；
    /// intoElse=true 时放进「如果」的「否则」分支；否则放在锚点后面；没有锚点就追加到程序末尾。
    /// </summary>
    public void Insert(Block nb, Block? anchor, bool into = false, bool intoElse = false)
    {
        if (anchor == null) Program.Program.Add(nb);
        else if (intoElse && anchor is IfBlock ib) ib.Else.Add(nb);
        else if (into && anchor is IContainerBlock c) c.Body.Add(nb);
        else if (Locate(anchor) is { } loc) loc.List.Insert(loc.Index + 1, nb);
        else Program.Program.Add(nb);
        Touch();
    }

    public void Remove(Block b)
    {
        if (Locate(b) is { } loc) { loc.List.RemoveAt(loc.Index); Touch(); }
    }

    public void Move(Block b, int delta)
    {
        if (Locate(b) is not { } loc) return;
        int j = loc.Index + delta;
        if (j < 0 || j >= loc.List.Count) return;
        loc.List.RemoveAt(loc.Index);
        loc.List.Insert(j, b);
        Touch();
    }

    /// <summary>缩进：放进前一个兄弟容器的末尾。</summary>
    public bool Indent(Block b)
    {
        if (Locate(b) is not { } loc || loc.Index == 0) return false;
        if (loc.List[loc.Index - 1] is not IContainerBlock prev) return false;
        loc.List.RemoveAt(loc.Index);
        prev.Body.Add(b);
        Touch();
        return true;
    }

    /// <summary>取消缩进：移到所在容器的后面。</summary>
    public bool Outdent(Block b)
    {
        if (Locate(b) is not { } loc) return false;
        var parent = All().FirstOrDefault(x => (x is IContainerBlock c && ReferenceEquals(c.Body, loc.List)) || (x is IfBlock i && ReferenceEquals(i.Else, loc.List)));
        if (parent == null || Locate(parent) is not { } ploc) return false;
        loc.List.RemoveAt(loc.Index);
        ploc.List.Insert(ploc.Index + 1, b);
        Touch();
        return true;
    }
}
