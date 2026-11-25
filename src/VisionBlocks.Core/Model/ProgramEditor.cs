using System.Text.Json;
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

    private ProgramEditor(VbProgram p, string path) { Program = p; ProgramPath = path; _baseline = Snapshot(); }

    public static ProgramEditor CreateNew(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var ed = new ProgramEditor(new VbProgram { Name = name }, Path.Combine(dir, "program.vbp.json"));
        ed.Save();
        return ed;
    }

    public static ProgramEditor Open(string path) => new(ProgramIO.Load(path), Path.GetFullPath(path));

    public void Save() { ProgramIO.Save(Program, ProgramPath); Dirty = false; }

    /// <summary>属性面板这类小改动：连续 1 秒内的几次并成一步撤销（2026-11-23）。</summary>
    public void Touch() => Record(coalesce: true);

    /// <summary>插入、删除、移动、加删资源这类结构性改动：每次单独一步撤销。</summary>
    private void Commit() => Record(coalesce: false);

    /// <summary>
    /// 只改了显示（程序树折叠 / 展开，2026-11-25）：算改动（关窗时问要不要保存），但不占一步撤销——
    /// 撤销是撤程序本身的改动，不该一步步撤回折叠。下一步改动的撤销点从折叠以后算。
    /// </summary>
    public void ViewChanged()
    {
        _baseline = Snapshot();
        Dirty = true;
        Changed?.Invoke();
    }

    /// <summary>b 所在的每一层容器（从外到里）；b 在「否则」分支里时那一层记 IsElse = true。折叠时用来一路展开到选中的积木。</summary>
    public List<(Block Owner, bool IsElse)> AncestorsOf(Block b)
    {
        var path = new List<(Block, bool)>();
        bool Find(List<Block> list)
        {
            foreach (var x in list)
            {
                if (ReferenceEquals(x, b)) return true;
                if (x is IContainerBlock c) { path.Add((x, false)); if (Find(c.Body)) return true; path.RemoveAt(path.Count - 1); }
                if (x is IfBlock ib) { path.Add((x, true)); if (Find(ib.Else)) return true; path.RemoveAt(path.Count - 1); }
            }
            return false;
        }
        return Find(Program.Program) ? path : new List<(Block, bool)>();
    }

    /// <summary>把 b 外面折着的容器都展开，返回有没有改。</summary>
    public bool Reveal(Block b)
    {
        bool changed = false;
        foreach (var (owner, isElse) in AncestorsOf(b))
        {
            if (isElse && owner is IfBlock ib && ib.ElseCollapsed) { ib.ElseCollapsed = false; changed = true; }
            else if (!isElse && owner.Collapsed) { owner.Collapsed = false; changed = true; }
        }
        return changed;
    }

    // ───────────── 撤销 / 重做（2026-11-23）：每次改动前整个程序存一份 JSON，最多 50 步 ─────────────

    public const int MaxUndo = 50;
    private readonly List<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _baseline;
    private long _lastRecordMs = long.MinValue / 2;
    private bool _lastCoalesced;

    private string Snapshot() => JsonSerializer.Serialize(Program, ProgramIO.Options);

    private void Record(bool coalesce)
    {
        long now = Environment.TickCount64;
        if (!(coalesce && _lastCoalesced && now - _lastRecordMs < 1000))
        {
            _undo.Add(_baseline);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }
        _baseline = Snapshot();
        _redo.Clear();
        _lastRecordMs = now;
        _lastCoalesced = coalesce;
        Dirty = true;
        Changed?.Invoke();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>撤销一步。Program 会换成一个新对象，界面要重新取 Program 和选中项（用 PathOf / BlockAt 按位置找回来）。</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_baseline);
        _baseline = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(_baseline);
        _baseline = _redo.Pop();
        Restore();
        return true;
    }

    private void Restore()
    {
        Program = JsonSerializer.Deserialize<VbProgram>(_baseline, ProgramIO.Options)!;
        _lastCoalesced = false;
        Dirty = true;
        Changed?.Invoke();
    }

    /// <summary>积木在树里的位置：每一层是「下标」，往里走时再跟一个分支号（0 = 里面 / 那么，1 = 否则）。撤销以后按它找回同一个位置的积木。</summary>
    public List<int>? PathOf(Block target)
    {
        List<int>? Find(List<Block> l)
        {
            for (int i = 0; i < l.Count; i++)
            {
                if (ReferenceEquals(l[i], target)) return new List<int> { i };
                if (l[i] is IContainerBlock c && Find(c.Body) is { } a) { a.InsertRange(0, new[] { i, 0 }); return a; }
                if (l[i] is IfBlock ib && Find(ib.Else) is { } e) { e.InsertRange(0, new[] { i, 1 }); return e; }
            }
            return null;
        }
        return Find(Program.Program);
    }

    public Block? BlockAt(IReadOnlyList<int> path)
    {
        List<Block> l = Program.Program;
        for (int k = 0; k < path.Count; k += 2)
        {
            if (path[k] < 0 || path[k] >= l.Count) return null;
            var b = l[path[k]];
            if (k + 1 >= path.Count) return b;
            List<Block>? next = path[k + 1] == 1 ? (b as IfBlock)?.Else : (b as IContainerBlock)?.Body;
            if (next == null) return null;
            l = next;
        }
        return null;
    }

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
        Commit();
        return name;
    }

    public string AddPoint(string name, int x, int y)
    {
        name = Unique(name, Program.Points.Keys);
        Program.Points[name] = new PointDef { Label = name, X = x, Y = y };
        Commit();
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
        Commit();
        return (name, warn);
    }

    /// <summary>网格：在画布上拖出第一格，行列数和间距之后在属性面板里改。</summary>
    public string AddGrid(string name, RegionDef firstCell, int cols = 4, int rows = 3, int gapX = 10, int gapY = 10)
    {
        name = Unique(name, Program.Grids.Keys);
        Program.Grids[name] = new GridDef { Label = name, X = firstCell.X, Y = firstCell.Y, W = firstCell.W, H = firstCell.H, Cols = cols, Rows = rows, GapX = gapX, GapY = gapY };
        Commit();
        return name;
    }

    public string AddColor(string name, NormFrame f, int x, int y)
    {
        name = Unique(name, Program.Colors.Keys);
        var c = ColorRatio.FromSample(f, x, y, name);
        Program.Colors[name] = c;
        Commit();
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
        bool Uses(Condition? c) => c switch
        {
            ImageCondition i => (kind == "region" && i.Region == name) || (kind == "template" && i.Template == name),
            ColorCondition k => (kind == "region" && k.Region == name) || (kind == "color" && k.Color == name),
            TextCondition t => kind == "region" && t.Region == name,
            MultiCondition m => m.Items.Any(Uses),
            _ => false
        };
        void C(Condition? c, Block b)
        {
            if (Uses(c)) uses.Add(BlockText.Summary(b, Program));
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
                case ClassifyBlock cl when (kind == "region" && cl.Region == name) || (kind == "template" && cl.Templates.Contains(name)):
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
        Commit();
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
            "classify" => new ClassifyBlock { Region = region, Field = "有效期" },
            // 2026-11-25：默认按第一个文字字段（一般是「名称」）去重，截图字段不算；没有文字字段就不去重
            "recordRow" => new RecordRowBlock { DedupeBy = Runtime.Recorder.FieldsOf(Program).Except(ProgramIO.ImageFields(Program.Program)).Take(1).ToList() },
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
        Commit();
    }

    public void Remove(Block b)
    {
        if (Locate(b) is { } loc) { loc.List.RemoveAt(loc.Index); Commit(); }
    }

    public void Move(Block b, int delta)
    {
        if (Locate(b) is not { } loc) return;
        int j = loc.Index + delta;
        if (j < 0 || j >= loc.List.Count) return;
        loc.List.RemoveAt(loc.Index);
        loc.List.Insert(j, b);
        Commit();
    }

    /// <summary>缩进：放进前一个兄弟容器的末尾。</summary>
    public bool Indent(Block b)
    {
        if (Locate(b) is not { } loc || loc.Index == 0) return false;
        if (loc.List[loc.Index - 1] is not IContainerBlock prev) return false;
        loc.List.RemoveAt(loc.Index);
        prev.Body.Add(b);
        Commit();
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
        Commit();
        return true;
    }

    // ───────────── 复制、拖动、跨作用域上下移（2026-11-23） ─────────────

    /// <summary>深拷贝一块积木（连同里面的积木），走 JSON 往返，和存盘同一套规则。</summary>
    public static Block Clone(Block b) => JsonSerializer.Deserialize<Block>(JsonSerializer.Serialize(b, ProgramIO.Options), ProgramIO.Options)!;

    /// <summary>某个列表属于哪块积木（顶层返回 null），以及是不是「否则」分支。</summary>
    public (Block Owner, bool IsElse)? OwnerOf(List<Block> list)
    {
        foreach (var x in All())
        {
            if (x is IContainerBlock c && ReferenceEquals(c.Body, list)) return (x, false);
            if (x is IfBlock i && ReferenceEquals(i.Else, list)) return (x, true);
        }
        return null;
    }

    /// <summary>list 是不是 b 里面的某个分支（直接或更深）。拖动时不能把积木放进它自己里面。</summary>
    public static bool Contains(Block b, List<Block> list)
    {
        IEnumerable<List<Block>> Lists(Block x)
        {
            if (x is IContainerBlock c) yield return c.Body;
            if (x is IfBlock i) yield return i.Else;
        }
        foreach (var l in Lists(b))
        {
            if (ReferenceEquals(l, list)) return true;
            foreach (var ch in l) if (Contains(ch, list)) return true;
        }
        return false;
    }

    /// <summary>落点：鼠标在哪块积木的什么位置。</summary>
    public enum DropZone { Before, After, IntoFirst, ElseFirst, ElseEnd, BodyEnd, ProgramEnd }

    /// <summary>落点 → 实际插入的列表和下标。anchor 是鼠标下的积木（ElseFirst / ElseEnd 时是那块「如果」）。</summary>
    public (List<Block> List, int Index)? Slot(Block? anchor, DropZone zone) => zone switch
    {
        DropZone.ProgramEnd => (Program.Program, Program.Program.Count),
        DropZone.IntoFirst when anchor is IContainerBlock c => (c.Body, 0),
        DropZone.BodyEnd when anchor is IContainerBlock c => (c.Body, c.Body.Count),
        DropZone.ElseFirst when anchor is IfBlock ib => (ib.Else, 0),
        DropZone.ElseEnd when anchor is IfBlock ib => (ib.Else, ib.Else.Count),
        DropZone.Before when anchor != null && Locate(anchor) is { } l1 => (l1.List, l1.Index),
        DropZone.After when anchor != null && Locate(anchor) is { } l2 => (l2.List, l2.Index + 1),
        _ => null
    };

    /// <summary>把积木挪到 list 的第 index 个位置（index 按挪走之前的列表算）。放进自己里面、或者原地不动，返回 false。</summary>
    public bool MoveTo(Block b, List<Block> list, int index)
    {
        if (Contains(b, list) || Locate(b) is not { } loc) return false;
        if (ReferenceEquals(loc.List, list) && (index == loc.Index || index == loc.Index + 1)) return false;
        loc.List.RemoveAt(loc.Index);
        if (ReferenceEquals(loc.List, list) && loc.Index < index) index--;
        list.Insert(Math.Clamp(index, 0, list.Count), b);
        Commit();
        return true;
    }

    /// <summary>插到 list 的第 index 个位置（粘贴、按住 Ctrl 拖着复制、从积木架拖进来都用它）。</summary>
    public void InsertAt(Block nb, List<Block> list, int index)
    {
        list.Insert(Math.Clamp(index, 0, list.Count), nb);
        Commit();
    }

    /// <summary>
    /// 上移一格，像 IDE 的「移动语句」：上面一块是容器，就进去放到它最后一个分支的末尾（「如果」是否则分支）；
    /// 已经是第一块，就出到容器前面；在否则分支的第一块再上移，进到「那么」的末尾。顶层第一块不动，返回 false。
    /// </summary>
    public bool MoveUp(Block b)
    {
        if (Locate(b) is not { } loc) return false;
        var (l, i) = loc;
        if (i > 0)
        {
            var prev = l[i - 1];
            l.RemoveAt(i);
            if (prev is IfBlock pi) pi.Else.Add(b);
            else if (prev is IContainerBlock pc) pc.Body.Add(b);
            else l.Insert(i - 1, b);
            Commit();
            return true;
        }
        if (OwnerOf(l) is not { } own || Locate(own.Owner) is not { } ol) return false;
        l.RemoveAt(0);
        if (own.IsElse) ((IfBlock)own.Owner).Body.Add(b);
        else ol.List.Insert(ol.Index, b);
        Commit();
        return true;
    }

    /// <summary>下移一格：下面一块是容器，就进去当第一块；已经是最后一块，就出到容器后面（「如果」的「那么」分支先进到否则分支的第一块）。顶层最后一块不动。</summary>
    public bool MoveDown(Block b)
    {
        if (Locate(b) is not { } loc) return false;
        var (l, i) = loc;
        if (i < l.Count - 1)
        {
            var next = l[i + 1];
            l.RemoveAt(i);
            if (next is IContainerBlock nc) nc.Body.Insert(0, b);
            else l.Insert(i + 1, b);
            Commit();
            return true;
        }
        if (OwnerOf(l) is not { } own || Locate(own.Owner) is not { } ol) return false;
        l.RemoveAt(i);
        if (!own.IsElse && own.Owner is IfBlock ib) ib.Else.Insert(0, b);
        else ol.List.Insert(ol.Index + 1, b);
        Commit();
        return true;
    }

    /// <summary>某个列表所在的作用域（从外到里）：每一层是那块容器，以及是不是它的否则分支。顶层是空的（2026-11-23，位置条用）。</summary>
    public List<(Block Owner, bool IsElse)> ScopeOf(List<Block> list)
    {
        var path = new List<(Block, bool)>();
        var cur = list;
        while (OwnerOf(cur) is { } own)
        {
            path.Insert(0, own);
            if (Locate(own.Owner) is not { } l) break;
            cur = l.List;
        }
        return path;
    }

    /// <summary>作用域写成一句话：「程序 › 重复 3 次 › 如果 … › 否则」。每层的摘要超过 maxLen 个字就截断。</summary>
    public string ScopeText(List<Block> list, string root = "程序", string elseLabel = "否则", int maxLen = 22)
    {
        var parts = new List<string> { root };
        foreach (var (owner, isElse) in ScopeOf(list))
        {
            parts.Add(Short(BlockText.Summary(owner, Program), maxLen));
            if (isElse) parts.Add(elseLabel);
        }
        return string.Join(" › ", parts);
    }

    public static string Short(string s, int maxLen) => s.Length <= maxLen ? s : s[..(maxLen - 1)] + "…";

    /// <summary>一块积木（连同里面的）用到的资源：(种类, 名字)。「当前格」不算。</summary>
    public static HashSet<(string Kind, string Name)> ResourcesUsedBy(Block root)
    {
        var set = new HashSet<(string, string)>();
        void R(string? n) { if (!string.IsNullOrEmpty(n) && n != VbProgram.CurrentCell) set.Add(("region", n)); }
        void C(Condition? c)
        {
            switch (c)
            {
                case ImageCondition i: R(i.Region); if (!string.IsNullOrEmpty(i.Template)) set.Add(("template", i.Template)); break;
                case ColorCondition k: R(k.Region); if (!string.IsNullOrEmpty(k.Color)) set.Add(("color", k.Color)); break;
                case TextCondition t: R(t.Region); break;
                case MultiCondition m: foreach (var x in m.Items) C(x); break;
            }
        }
        void B(Block b)
        {
            switch (b)
            {
                case RepeatUntilBlock ru: C(ru.Until); break;
                case IfBlock ib: C(ib.Cond); break;
                case WaitUntilBlock wu: C(wu.Cond); break;
                case ClickBlock cb: R(cb.Region); if (!string.IsNullOrEmpty(cb.Point)) set.Add(("point", cb.Point)); C(cb.ConfirmCond); break;
                case WheelBlock wb: R(wb.Region); if (!string.IsNullOrEmpty(wb.Point)) set.Add(("point", wb.Point)); break;
                case DragBlock db: R(db.Region); break;
                case ReadTextBlock rt: R(rt.Region); break;
                case SaveImageBlock si: R(si.Region); break;
                case ClassifyBlock cl: R(cl.Region); foreach (var t in cl.Templates) if (!string.IsNullOrEmpty(t)) set.Add(("template", t)); break;
                case ForEachCellBlock fe: if (!string.IsNullOrEmpty(fe.Grid)) set.Add(("grid", fe.Grid)); break;
            }
            if (b is IContainerBlock c) foreach (var x in c.Body) B(x);
            if (b is IfBlock i2) foreach (var x in i2.Else) B(x);
        }
        B(root);
        return set;
    }
}
