using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-multicond：判断式「多个条件」（并且 / 或者，2026-11-25）。不截屏、不发输入。
/// 用真的 ProgramRunner.Eval（画面换成靶子截图贴回 1920×1080 的假画面源，输入换成只记录不发送的假后端）和编辑器的「试一下」各算一遍：
/// 角标是「1天」，「看到 1天」成立，「看到 3天」不成立（门槛 0.95；3天模板认 1天角标只有 0.86）。
/// 另外测存盘、校验、摘要、资源引用。
/// </summary>
public static class MultiCondSelfTest
{
    public static int Run(List<string> a)
    {
        string dir = Path.GetFullPath(a.Count > 0 ? a[0] : Path.Combine(RepoRoot(), "programs", "inv-twins"));
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}{(detail.Length > 0 ? "：" + detail : "")}");
        }
        var p = new VbProgram { Name = "多个条件自测" };
        p.Regions["角"] = new RegionDef { X = 1540, Y = 420, W = 125, H = 75 };
        p.Templates["t1"] = new TemplateDef { Label = "1天", File = "templates/badge-1d.png" };
        p.Templates["t3"] = new TemplateDef { Label = "3天", File = "templates/badge-3d.png" };
        p.Templates["tr"] = new TemplateDef { Label = "限时红", File = "templates/badge-limit-red.png" };
        ImageCondition See(string t, bool not = false) => new() { Template = t, Region = "角", Threshold = 0.95, Negate = not };
        MultiCondition All(params Condition[] c) => new() { Mode = "all", Items = c.ToList() };
        MultiCondition Any(params Condition[] c) => new() { Mode = "any", Items = c.ToList() };

        using var canvas = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(242, 240, 236));
        using (var crop = Cv2.ImRead(Path.Combine(dir, "snap-badge-1d.png")))
        using (var dst = new Mat(canvas, new Rect(1300, 130, crop.Width, crop.Height))) crop.CopyTo(dst);
        using var bgra = canvas.CvtColor(ColorConversionCodes.BGR2BGRA);

        string logPath = Path.Combine(Path.GetTempPath(), $"vb-multicond-{Environment.ProcessId}.jsonl");
        var lines = new List<string>();
        using (var log = new BotLog(logPath))
        {
            log.Line += l => { lock (lines) lines.Add(l); };
            var input = new SendInputBackend(new InputGuard(IntPtr.Zero) { TestAllow = () => true }, null) { TestSender = _ => 1, IsKeyPhysicallyDown = _ => false, CloseImeBeforeKeys = false };
            using var runner = new ProgramRunner(new RunContext { Program = p, ProgramDir = dir, Source = new OneFrame(bgra), Input = input, Log = log, TargetHwnd = GetDesktopWindow() });
            using var tester = new ConditionTester();
            using var nf = new Core.Vision.NormFrame(canvas.Clone(), 1.0, 1920);
            var cases = new (string Name, Condition C, bool Want, int Checked)[]
            {
                ("看到 1天 并且 看不到 3天", All(See("t1"), See("t3", true)), true, 2),
                ("看到 3天 并且 看到 1天：第一个不满足就停，后面的不再判断", All(See("t3"), See("t1")), false, 1),
                ("看到 3天 或者 看到 1天", Any(See("t3"), See("t1")), true, 2),
                ("看到 1天 或者 看到 3天：第一个满足就停", Any(See("t1"), See("t3")), true, 1),
                ("看到 3天 或者 看到限时红", Any(See("t3"), See("tr")), false, 2),
                ("整体取反：不满足（看到 1天 并且 看不到 3天）", new MultiCondition { Mode = "all", Negate = true, Items = { See("t1"), See("t3", true) } }, false, 2),
            };
            Console.WriteLine("1. 运行时（ProgramRunner.Eval）和编辑器「试一下」");
            foreach (var (name, c, want, chk) in cases)
            {
                int before; lock (lines) before = lines.Count(l => l.Contains("\"kind\":\"image\"", StringComparison.Ordinal));
                var r = runner.Eval(c, CancellationToken.None).GetAwaiter().GetResult();
                int after; lock (lines) after = lines.Count(l => l.Contains("\"kind\":\"image\"", StringComparison.Ordinal));
                var t = tester.TestAsync(p, dir, c, nf).GetAwaiter().GetResult();
                Check(name, r.Hit == want && after - before == chk && t.Hit == want,
                    $"运行时 {(r.Hit ? "成立" : "不成立")}，判断了 {after - before} 个；试一下 {(t.Hit ? "成立" : "不成立")}");
            }
        }
        try { File.Delete(logPath); } catch { }

        Console.WriteLine("2. 存盘、校验、摘要、资源引用");
        var multi = All(See("t1"), See("t3", true));
        var ib = new IfBlock { Cond = multi };
        p.Program.Add(ib);
        string json = System.Text.Json.JsonSerializer.Serialize(p, ProgramIO.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<VbProgram>(json, ProgramIO.Options)!;
        Check("JSON 往返（type: multi、mode、items）", json.Contains("\"type\": \"multi\"") && back.Program[0] is IfBlock { Cond: MultiCondition { Mode: "all", Items.Count: 2 } m } && m.Items[1].Negate);
        Check("没有错误", ProgramIO.Validate(p).Count == 0, string.Join("；", ProgramIO.Validate(p)));
        Check("摘要", BlockText.Summary(ib, p) == "如果 （看到图像「1天」在「角」≥ 0.95 并且 看不到图像「3天」在「角」≥ 0.95）", BlockText.Summary(ib, p));
        ib.Cond = new MultiCondition { Mode = "any", Items = { See("t1"), All(See("t3")) } };
        Check("里面再套「多个条件」：报错", ProgramIO.Validate(p).Any(e => e.Contains("不能再套")));
        ib.Cond = new MultiCondition { Mode = "any" };
        Check("一个条件都没有：报错", ProgramIO.Validate(p).Any(e => e.Contains("一个条件都没有")));
        ib.Cond = new MultiCondition { Mode = "xor", Items = { See("t1") } };
        Check("组合方式不认识：报错", ProgramIO.Validate(p).Any(e => e.Contains("组合方式")));
        ib.Cond = Any(See("t1"), new ImageCondition { Template = "t3", Region = "没有这个热区" });
        Check("里面第 2 个条件热区不存在：报错并指出是第几个", ProgramIO.Validate(p).Any(e => e.Contains("条件 2") && e.Contains("没有这个热区")));
        ib.Cond = multi;
        var ed = ProgramEditor.CreateNew(Path.Combine(Path.GetTempPath(), $"vb-multicond-ed-{Environment.ProcessId}"), "引用");
        ed.Program.Regions["角"] = p.Regions["角"];
        foreach (var (k, v) in p.Templates) ed.Program.Templates[k] = v;
        ed.Insert(new IfBlock { Cond = All(See("t1"), See("t3", true)) }, null);
        Check("删模板前查引用：「多个条件」里用到的也算", ed.UsesOf("template", "t3").Count == 1 && ed.UsesOf("template", "tr").Count == 0);
        var used = ProgramEditor.ResourcesUsedBy(ib);
        Check("复制积木带走的资源包括里面每个条件的", used.Contains(("template", "t1")) && used.Contains(("template", "t3")) && used.Contains(("region", "角")), string.Join("、", used.Select(u => u.Kind + ":" + u.Name)));
        var cl = new ClassifyBlock { Region = "角", Field = "有效期", Templates = { "t1", "tr" } };
        ed.Insert(cl, null);
        Check("认图案用到的模板、热区：删之前也查得出、复制时也带走", ed.UsesOf("template", "tr").Count == 1 && ed.UsesOf("region", "角").Count == 2
              && ProgramEditor.ResourcesUsedBy(cl).SetEquals(new[] { ("region", "角"), ("template", "t1"), ("template", "tr") }));
        try { Directory.Delete(ed.Dir, true); } catch { }

        Console.WriteLine(fails == 0 ? "多个条件自测全部通过" : $"多个条件自测失败 {fails} 项");
        return fails == 0 ? 0 : 1;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    /// <summary>一直给同一帧的假画面源。</summary>
    private sealed class OneFrame(Mat bgra) : IFrameSource
    {
        public string Name => "one";
        public void Start(IntPtr hwnd) { }
        public Frame? TryGetLatest() => new() { Bgra = bgra.Clone(), Seq = 1 };
        public long LatestSeq => 1;
        public event FrameHook? FrameArrived { add { } remove { } }
        public void Dispose() { }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "VisionBlocks.sln"))) d = d.Parent;
        return d?.FullName ?? ".";
    }
}
