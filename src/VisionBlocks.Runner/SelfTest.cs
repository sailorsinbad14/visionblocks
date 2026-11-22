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
        var errs = ProgramIO.Validate(ed.Program);
        Console.WriteLine(errs.Count == 0 ? "校验：通过" : "校验错误：" + string.Join("；", errs));
        ed.Save();
        // 存盘后重新读回，确认 JSON 往返一致
        var back = ProgramIO.Load(ed.ProgramPath);
        bool roundTrip = System.Text.Json.JsonSerializer.Serialize(back, ProgramIO.Options) == System.Text.Json.JsonSerializer.Serialize(ed.Program, ProgramIO.Options);
        Console.WriteLine($"JSON 往返一致：{roundTrip}；已保存 {ed.ProgramPath}");
        return bad == 0 && errs.Count == 0 && roundTrip ? 0 : 1;
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
