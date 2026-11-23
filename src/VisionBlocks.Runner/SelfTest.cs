using System.Diagnostics;
using OpenCvSharp;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Vision;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-editor &lt;样张.png&gt; &lt;输出目录&gt;：
/// 用编辑器的数据层（ProgramEditor，和界面调用的是同一套）按「上手清单」走一遍：
/// 建热区、点定点、裁模板、吸色、拼积木、在样张上逐个条件「试一下」，最后校验并保存。
/// 证明拖拽以外的编辑逻辑和坐标口径是对的；拖拽交互本身只能由用户验收。
/// 2026-11-23 加「编辑操作」一节：深拷贝、各种落点、拖进自己被拒、上下移进出作用域、撤销重做、跨程序粘贴带资源。
///
/// vbrun shot --process 进程名 --out x.png：用 WGC 截某个进程的主窗口（给编辑器界面拍照用，只截不发输入）。
/// </summary>
internal static class SelfTest
{
    public static int Editor(List<string> a)
    {
        string snap = Path.GetFullPath(a[0]);
        string dir = Path.GetFullPath(a[1]);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        using var img = Cv2.ImRead(snap);
        using var f = new NormFrame(img.Clone(), 1.0, 1920);
        var ed = ProgramEditor.CreateNew(dir, "编辑器自测：巡逻小游戏");

        // 第 2 步：画布上的操作（坐标就是用户在画布上拖出来的基准坐标）
        string keyZone = ed.AddRegion("提示键区", new RegionDef { X = 40, Y = 64, W = 900, H = 576 });
        string hpBar = ed.AddRegion("血条", new RegionDef { X = 1010, Y = 130, W = 840, H = 48 });
        string score = ed.AddRegion("计分", new RegionDef { X = 40, Y = 664, W = 900, H = 150 });
        string potion = ed.AddPoint("喝药按钮", 1120, 272);
        var (fTpl, warn) = ed.AddTemplate("F字母", f, new RegionDef { X = 222, Y = 214, W = 30, H = 44 });
        string red = ed.AddColor("红", f, 1060, 152);
        Console.WriteLine($"资源：热区 {ed.Program.Regions.Count}，定点 {ed.Program.Points.Count}，模板 {fTpl}{(warn != null ? "（" + warn + "）" : "")}，颜色 {red} [{string.Join(",", ed.Program.Colors[red].Lower)}]–[{string.Join(",", ed.Program.Colors[red].Upper)}]");

        // 第 3 步：拼积木（和界面上「点积木面板」走的是同一个 Insert 规则）
        var loop = (RepeatUntilBlock)ed.CreateDefault("repeatUntil");
        loop.Until = new TextCondition { Region = score, Contains = "完成" };
        loop.CheckEveryMs = 1000;
        loop.TimeoutMs = 180000;
        ed.Insert(loop, null);
        var ifF = new IfBlock { Cond = new ImageCondition { Template = fTpl, Region = keyZone, Threshold = 0.95, MinContrast = 0.6 } };
        ed.Insert(ifF, loop, into: true);
        ed.Insert(new KeyBlock { Key = "F", HoldMs = 40 }, ifF, into: true);
        var key = ifF.Body[0];
        ed.Insert(new WaitUntilBlock { Cond = new ImageCondition { Template = fTpl, Region = keyZone, Threshold = 0.95, MinContrast = 0.6, Negate = true }, TimeoutMs = 1000 }, key);
        var ifRed = new IfBlock { Cond = new ColorCondition { Color = red, Region = hpBar, Op = ">", Ratio = 0.02 } };
        ed.Insert(ifRed, ifF);
        ed.Insert(new ClickBlock { Point = potion, Confirm = ConfirmMode.Condition, ConfirmCond = new ColorCondition { Color = red, Region = hpBar, Op = "<", Ratio = 0.01 } }, ifRed, into: true);
        ed.Insert(new WaitBlock { Ms = 15 }, ifRed);

        // 缩进 / 取消缩进 / 移动：做一遍再撤回，确认结构不变
        var w = loop.Body[^1];
        ed.Outdent(w); ed.Indent(w);
        ed.Move(ifRed, -1); ed.Move(ifRed, 1);

        Console.WriteLine("程序：");
        void Print(List<Block> l, string ind) { foreach (var b in l) { Console.WriteLine(ind + BlockText.Summary(b, ed.Program)); if (b is IContainerBlock c) Print(c.Body, ind + "    "); } }
        Print(ed.Program.Program, "  ");

        // 「试一下」：在样张上逐个条件求值（样张里：真 F、E、R、半透明 F、红血条、计分 17）
        using var tester = new ConditionTester();
        var checks = new (string Name, Condition C, bool Expect)[]
        {
            ("看到 F（整区，含 E/R/半透明 F）", ifF.Cond, true),
            ("红血条占比 > 2%", ifRed.Cond, true),
            ("计分区包含「完成」", loop.Until, false),
            ("只看 E 所在小区域：看到 F？", new ImageCondition { Template = fTpl, Region = ed.AddRegion("E附近", new RegionDef { X = 380, Y = 180, W = 112, H = 112 }), Threshold = 0.95, MinContrast = 0.6 }, false),
            ("只看半透明 F 小区域：看到 F？", new ImageCondition { Template = fTpl, Region = ed.AddRegion("半透明F附近", new RegionDef { X = 180, Y = 400, W = 112, H = 112 }), Threshold = 0.95, MinContrast = 0.6 }, false),
            ("同上但不设对比度下限", new ImageCondition { Template = fTpl, Region = "半透明F附近", Threshold = 0.95, MinContrast = 0 }, true),
        };
        int bad = 0;
        foreach (var (name, c, expect) in checks)
        {
            var r = tester.TestAsync(ed.Program, ed.Dir, c, f).GetAwaiter().GetResult();
            bool ok = r.Hit == expect;
            if (!ok) bad++;
            Console.WriteLine($"  [{(ok ? "通过" : "不符")}] {name}：{r.Detail}");
        }
        ed.RemoveResource("region", "E附近");
        ed.RemoveResource("region", "半透明F附近");
        bad += EditingOps(ed, dir);
        var errs = ProgramIO.Validate(ed.Program);
        Console.WriteLine(errs.Count == 0 ? "校验：通过" : "校验错误：" + string.Join("；", errs));
        ed.Save();
        // 存盘后重新读回，确认 JSON 往返一致
        var back = ProgramIO.Load(ed.ProgramPath);
        bool roundTrip = System.Text.Json.JsonSerializer.Serialize(back, ProgramIO.Options) == System.Text.Json.JsonSerializer.Serialize(ed.Program, ProgramIO.Options);
        Console.WriteLine($"JSON 往返一致：{roundTrip}；已保存 {ed.ProgramPath}");
        return bad == 0 && errs.Count == 0 && roundTrip ? 0 : 1;
    }

    /// <summary>2026-11-23：复制、拖动、上下移、撤销、剪贴板。src 是上面拼好的巡逻程序（有热区、定点、模板、颜色）。返回失败数。</summary>
    private static int EditingOps(ProgramEditor src, string dir)
    {
        int fails = 0;
        void Check(string name, bool ok, string detail) { if (!ok) fails++; Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}：{detail}"); }
        string J(VbProgram p) => System.Text.Json.JsonSerializer.Serialize(p, ProgramIO.Options);
        Console.WriteLine("编辑操作：");

        // 深拷贝：改副本不影响原件
        var loop = src.Program.Program[0];
        var copy = ProgramEditor.Clone(loop);
        string before = BlockText.Summary(((IContainerBlock)loop).Body[0], src.Program);
        ((IfBlock)((IContainerBlock)copy).Body[0]).Cond = new TextCondition { Region = "计分", Contains = "改过" };
        Check("深拷贝互不影响", BlockText.Summary(((IContainerBlock)loop).Body[0], src.Program) == before && !ReferenceEquals(copy, loop) && ((IContainerBlock)copy).Body.Count == ((IContainerBlock)loop).Body.Count,
            $"原件仍是「{before}」");

        // 一个小程序：k1, R{ c1, w1 }, I{ t1 } 否则 { e1 }, k2
        var ed = ProgramEditor.CreateNew(Path.Combine(dir, "ops"), "编辑操作");
        ed.AddRegion("区", new RegionDef { X = 0, Y = 0, W = 100, H = 100 });
        KeyBlock K(string k) => new() { Key = k };
        var k1 = K("A"); var k2 = K("B"); var c1 = K("C"); var w1 = new WaitBlock { Ms = 1 }; var t1 = K("T"); var e1 = K("E");
        var R = new RepeatBlock { Times = 2, Body = { c1, w1 } };
        var I = new IfBlock { Cond = new TextCondition { Region = "区", Contains = "x" }, Body = { t1 }, Else = { e1 } };
        foreach (var b in new Block[] { k1, R, I, k2 }) ed.Insert(b, null);
        string Shape(VbProgram p)
        {
            string W(List<Block> l) => string.Join(",", l.Select(b => b switch
            {
                KeyBlock k => k.Key, WaitBlock => "w",
                IfBlock ib => $"I{{{W(ib.Body)}|{W(ib.Else)}}}",
                IContainerBlock c => $"R{{{W(c.Body)}}}",
                _ => "?"
            }));
            return W(p.Program);
        }
        string start = Shape(ed.Program), startJson = J(ed.Program);
        Check("起点", start == "A,R{C,w},I{T|E},B", start);

        // 落点
        bool Mv(Block b, Block? anchor, ProgramEditor.DropZone z) => ed.Slot(anchor, z) is { } s && ed.MoveTo(b, s.List, s.Index);
        var steps = new (string Name, Func<bool> Op, string Want)[]
        {
            ("B 拖到「重复」顶块下半 = 进去当第一块", () => Mv(k2, R, ProgramEditor.DropZone.IntoFirst), "A,R{B,C,w},I{T|E}"),
            ("B 拖到「重复」底盖 = 出到后面", () => Mv(k2, R, ProgramEditor.DropZone.After), "A,R{C,w},B,I{T|E}"),
            ("A 拖到「否则」行 = 否则分支第一块", () => Mv(k1, I, ProgramEditor.DropZone.ElseFirst), "R{C,w},B,I{T|A,E}"),
            ("A 拖到 C 的上半 = 放在 C 前面", () => Mv(k1, c1, ProgramEditor.DropZone.Before), "R{A,C,w},B,I{T|E}"),
            ("w 拖到 B 的下半 = 放在 B 后面", () => Mv(w1, k2, ProgramEditor.DropZone.After), "R{A,C},B,w,I{T|E}"),
            ("「重复」拖到空白处 = 程序末尾", () => Mv(R, null, ProgramEditor.DropZone.ProgramEnd), "B,w,I{T|E},R{A,C}"),
        };
        foreach (var (name, op, want) in steps)
        {
            bool did = op();
            Check(name, did && Shape(ed.Program) == want, Shape(ed.Program));
        }
        string mid = Shape(ed.Program);
        Check("拖进自己里面被拒", !ed.MoveTo(R, R.Body, 0) && !ed.MoveTo(I, I.Else, 1) && Shape(ed.Program) == mid, Shape(ed.Program));
        Check("拖回原地算没动", !ed.MoveTo(k2, ed.Program.Program, 0) && !ed.MoveTo(k2, ed.Program.Program, 1) && Shape(ed.Program) == mid, Shape(ed.Program));

        // 上下移进出作用域（像 IDE 的移动语句）
        var moves = new (string Name, Func<bool> Op, string Want)[]
        {
            ("w 下移：下面是「如果」→ 进去当第一块", () => ed.MoveDown(w1), "B,I{w,T|E},R{A,C}"),
            ("T 下移：「那么」最后一块 → 进到否则第一块", () => ed.MoveDown(t1), "B,I{w|T,E},R{A,C}"),
            ("T 上移：否则第一块 → 回到「那么」末尾", () => ed.MoveUp(t1), "B,I{w,T|E},R{A,C}"),
            ("E 下移：否则最后一块 → 出到「如果」后面", () => ed.MoveDown(e1), "B,I{w,T|},E,R{A,C}"),
            ("E 上移：上面是「如果」→ 进到否则末尾", () => ed.MoveUp(e1), "B,I{w,T|E},R{A,C}"),
            ("w 上移：第一块 → 出到「如果」前面", () => ed.MoveUp(w1), "B,w,I{T|E},R{A,C}"),
            ("C 下移：「重复」最后一块 → 出到后面", () => ed.MoveDown(c1), "B,w,I{T|E},R{A},C"),
        };
        foreach (var (name, op, want) in moves)
        {
            bool did = op();
            Check(name, did && Shape(ed.Program) == want, Shape(ed.Program));
        }
        Check("顶层第一块不能再上移、最后一块不能再下移", !ed.MoveUp(k2) && !ed.MoveDown(c1), Shape(ed.Program));

        // 位置往返
        var path = ed.PathOf(t1)!;
        Check("位置往返", ReferenceEquals(ed.BlockAt(path), t1) && string.Join(".", path) == "2.0.0", string.Join(".", path));

        // 撤销 / 重做：一路撤回到起点，再一路重做回来
        string end = J(ed.Program);
        int n = 0;
        while (ed.Undo()) n++;
        string undone = Shape(ed.Program);
        int m = 0;
        while (ed.Redo()) m++;
        Check("撤销到头 = 新建时；重做到头 = 改完时", n == m && J(ed.Program) == end && undone == "", $"撤 {n} 步回到「{undone}」（新建的空程序），重做 {m} 步");
        // 属性面板的连续小改动并成一步
        var tb = (KeyBlock)ed.BlockAt(ed.PathOf(ed.Program.Program[0])!)!;
        tb.Key = "X"; ed.Touch(); tb.Key = "Y"; ed.Touch(); tb.Key = "Z"; ed.Touch();
        ed.Undo();
        Check("1 秒内连续三次小改动并成一步撤销", ((KeyBlock)ed.Program.Program[0]).Key == "B", $"撤一步后是 {((KeyBlock)ed.Program.Program[0]).Key}");

        // 剪贴板：同一个程序里贴，不带资源；贴到别的程序，带上热区、模板（连 PNG）、颜色
        var used = ProgramEditor.ResourcesUsedBy(src.Program.Program[0]);
        Check("用到的资源", used.Contains(("template", "F字母")) && used.Contains(("region", "提示键区")) && used.Contains(("color", "红")) && used.Contains(("point", "喝药按钮")),
            string.Join("、", used.Select(u => u.Kind + ":" + u.Name)));
        var clip = BlockClip.Capture(src, src.Program.Program[0]);
        int regionsBefore = src.Program.Regions.Count;
        var (same, sameNotes) = clip.Paste(src);
        Check("同一个程序里粘贴：不加资源、没有提示", sameNotes.Count == 0 && src.Program.Regions.Count == regionsBefore && !ReferenceEquals(same, src.Program.Program[0]), $"提示 {sameNotes.Count} 条");
        var other = ProgramEditor.CreateNew(Path.Combine(dir, "paste-b"), "别的程序");
        other.AddRegion("计分", new RegionDef { X = 1, Y = 1, W = 5, H = 5 });   // 同名不同内容：沿用这里的
        var (pasted, notes) = clip.Paste(other);
        other.Insert(pasted, null);
        string tplFile = Path.Combine(other.Dir, other.Program.Templates.TryGetValue("F字母", out var td) ? td.File : "?");
        Check("贴到别的程序：缺的资源带过来，模板 PNG 也复制了", other.Program.Regions.ContainsKey("提示键区") && other.Program.Colors.ContainsKey("红") && other.Program.Points.ContainsKey("喝药按钮") && File.Exists(tplFile)
              && notes.Any(x => x.Contains("沿用") && x.Contains("计分")) && other.Program.Regions["计分"].W == 5 && ProgramIO.Validate(other.Program).Count == 0,
            string.Join("；", notes));
        var (_, again) = clip.Paste(other);
        Check("再贴一次：资源都在了，只剩同名不同内容的提示", again.All(x => x.Contains("沿用")), string.Join("；", again));
        Check("一次粘贴（带资源 + 插入）一步就能撤销", other.Undo() && other.Program.Program.Count == 0 && !other.Program.Regions.ContainsKey("提示键区"), $"撤销后积木 {other.Program.Program.Count} 块、热区 {other.Program.Regions.Count} 个");
        try { Directory.Delete(other.Dir, true); Directory.Delete(ed.Dir, true); } catch { }
        return fails;
    }

    public static int Shot(Dictionary<string, string> o)
    {
        string pname = Program.Opt(o, "process", "VisionBlocksStudio");
        string outPng = Path.GetFullPath(Program.Opt(o, "out", "shot.png"));
        // --pid：只截指定进程（2026-11-22：同名的编辑器可能不止一个，比如用户自己开着的那个，测试只截自己启动的）
        var p = o.TryGetValue("pid", out var pid)
            ? Process.GetProcessById(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture))
            : Process.GetProcessesByName(pname).FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero && x.MainWindowTitle != "BenchTarget")   // 靶子也用编辑器的 exe 启动，跳过它
              ?? throw new InvalidOperationException($"没有找到进程 {pname} 的窗口");
        if (p.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException($"进程 {p.Id} 没有主窗口");
        using var wgc = new WgcFrameSource();
        wgc.Start(p.MainWindowHandle);
        var sw = Stopwatch.StartNew();
        Frame? fr = null;
        while (fr == null && sw.ElapsedMilliseconds < 3000) { fr = wgc.TryGetLatest(); Thread.Sleep(30); }
        if (fr == null) throw new InvalidOperationException("没截到");
        using (fr)
        {
            using var small = fr.Bgra.Width > 1600 ? fr.Bgra.Resize(new Size(1600, (int)(fr.Bgra.Height * 1600.0 / fr.Bgra.Width)), 0, 0, InterpolationFlags.Area) : fr.Bgra.Clone();
            Cv2.ImWrite(outPng, small);
            Console.WriteLine($"已截 {outPng}（原始 {fr.Bgra.Width}×{fr.Bgra.Height}）");
        }
        return 0;
    }
}
