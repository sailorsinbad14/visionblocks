using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-unsure：认图案分不开时停下来问（2026-11-25）。不截屏、不发输入。
/// 画面是靶子「7天」角标的截图（还没裁 7天 模板：1天 0.89 / 3天 0.88，分不太开），用真的 ProgramRunner 跑，
/// 「问用户」换成假的：不问 / 超时 / 停止 / 填新名字存成模板。看写进表的值、问了几次、模板存没存、「重复直到」的超时扣没扣掉等的时间。
/// </summary>
public static class UnsureSelfTest
{
    public static int Run(List<string> a)
    {
        string src = Path.GetFullPath(a.Count > 0 ? a[0] : Path.Combine(RepoRoot(), "programs", "inv-twins"));
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}{(detail.Length > 0 ? "：" + detail : "")}");
        }

        using var canvas = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(242, 240, 236));
        using (var crop = Cv2.ImRead(Path.Combine(src, "snap-badge-7d.png")))
        using (var dst = new Mat(canvas, new Rect(1300, 130, crop.Width, crop.Height))) crop.CopyTo(dst);
        using var bgra = canvas.CvtColor(ColorConversionCodes.BGR2BGRA);

        string root = Path.Combine(Path.GetTempPath(), $"vb-unsure-{Environment.ProcessId}");
        int n = 0;
        // 每个用例一个新的程序文件夹：「认图案」（1天 / 3天）→「记录一行」按有效期去重，做两遍
        (ProgramEditor Ed, ClassifyBlock Cl) NewProgram(bool loop)
        {
            var ed = ProgramEditor.CreateNew(Path.Combine(root, $"p{++n}"), "分不开自测");
            Directory.CreateDirectory(Path.Combine(ed.Dir, "templates"));
            foreach (var f in new[] { "badge-1d.png", "badge-3d.png" }) File.Copy(Path.Combine(src, "templates", f), Path.Combine(ed.Dir, "templates", f));
            ed.Program.Regions["角"] = new RegionDef { X = 1540, Y = 420, W = 125, H = 75 };
            ed.Program.Templates["t1"] = new TemplateDef { Label = "1天", File = "templates/badge-1d.png" };
            ed.Program.Templates["t3"] = new TemplateDef { Label = "3天", File = "templates/badge-3d.png" };
            var cl = new ClassifyBlock { Region = "角", Field = "有效期", Templates = { "t1", "t3" } };
            var body = new List<Block> { cl, new RecordRowBlock { DedupeBy = { "有效期" } } };
            if (loop)   // 「重复直到」超时 400 ms，直到条件永远不成立（7天角标上找 3天 ≥ 0.99）
                ed.Program.Program.Add(new RepeatUntilBlock { TimeoutMs = 400, Until = new ImageCondition { Template = "t3", Region = "角", Threshold = 0.99 }, Body = body });
            else
            {
                ed.Program.Program.AddRange(body);
                ed.Program.Program.Add(new ClassifyBlock { Region = "角", Field = "有效期", Templates = { "t1", "t3" } });
                ed.Program.Program.Add(new RecordRowBlock { DedupeBy = { "有效期" } });
            }
            return (ed, cl);
        }

        (string Result, ProgramRunner R, List<string> Lines, string Csv) Run(ProgramEditor ed, Func<UnsureRequest, CancellationToken, Task<UnsureAnswer?>>? ask)
        {
            string outDir = Path.Combine(ed.Dir, "out");
            var lines = new List<string>();
            string result;
            ProgramRunner runner;
            using (var log = new BotLog(Path.Combine(ed.Dir, "bot.jsonl")))
            {
                log.Line += l => { lock (lines) lines.Add(l); };
                var input = new SendInputBackend(new InputGuard(IntPtr.Zero) { TestAllow = () => true }, null) { TestSender = _ => 1, IsKeyPhysicallyDown = _ => false, CloseImeBeforeKeys = false };
                runner = new ProgramRunner(new RunContext
                {
                    Program = ed.Program, ProgramDir = ed.Dir, Source = new OneFrame(bgra), Input = input, Log = log, TargetHwnd = GetDesktopWindow(),
                    OutputDir = outDir, AskUnsure = ask
                });
                using (runner) result = runner.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            string csv = File.Exists(Path.Combine(outDir, "data.csv")) ? File.ReadAllText(Path.Combine(outDir, "data.csv")) : "";
            return (result, runner, lines, csv);
        }
        int Ev(List<string> lines, string ev) => lines.Count(l => l.Contains($"\"ev\":\"{ev}\"", StringComparison.Ordinal));
        bool Has(List<string> lines, string s) => lines.Any(l => l.Contains(s, StringComparison.Ordinal));

        Console.WriteLine("1. 没人问（vbrun、设置成「直接记录」）：写「1天?」，不和认出来的「1天」撞");
        {
            var (ed, _) = NewProgram(false);
            var (res, r, lines, csv) = Run(ed, null);
            Check("运行完成", res == "完成", res);
            Check("两次都分不开，都没问", r.Unsures == 2 && Ev(lines, "unsureAsk") == 0 && Has(lines, "\"how\":\"auto\""), $"分不开 {r.Unsures} 次");
            Check("表里写的是「1天?」，第二次按去重跳过", csv.Contains("1天?") && r.RowsRecorded == 1 && r.Duplicates == 1, $"{r.RowsRecorded} 行，跳过 {r.Duplicates}");
        }

        Console.WriteLine("2. 问了没人答（等太久）：照样写「1天?」接着跑");
        {
            var (ed, _) = NewProgram(false);
            UnsureRequest? got = null;
            var (res, r, lines, csv) = Run(ed, (q, ct) => { got = q; return Task.FromResult<UnsureAnswer?>(null); });
            Check("运行完成，问了两次", res == "完成" && Ev(lines, "unsureAsk") == 2 && Ev(lines, "unsureAnswer") == 2 && Has(lines, "\"how\":\"timeout\""));
            Check("表里是「1天?」", csv.Contains("1天?") && r.RowsRecorded == 1);
            Check("问的内容：分数从高到低、带热区截图和角标截图、角标在热区里",
                got != null && got.Close && got.Candidates.Count == 2 && got.Candidates[0].Score >= got.Candidates[1].Score && got.Candidates[0].Value == "1天"
                && got.RegionPng.Length > 100 && got.BadgePng is { Length: > 100 } && got.BadgeInRegion is { } bi && bi.X >= 0 && bi.Y >= 0 && bi.X + bi.W <= 125 && bi.Y + bi.H <= 75,
                got == null ? "没问" : $"{got.Candidates[0].Value} {got.Candidates[0].Score:0.000} / {got.Candidates[1].Value} {got.Candidates[1].Score:0.000}，角标 {got.BadgeInRegion}");
            Check("第二次问时不把上一行留下的值当成这件物品的", got != null && got.Row.Count == 0, got == null ? "" : string.Join("，", got.Row));
            using (var rec = new Recorder(Path.Combine(ed.Dir, "rec"), ed.Program))
            {
                rec.Set("有效期", "3天");
                bool before = rec.Current.GetValueOrDefault("有效期") == "3天";
                rec.Record(new[] { "有效期" }, out _);
                Check("「记录一行」以后才读到的字段才算这件物品的", before && rec.Current.Count == 0);
            }
        }

        Console.WriteLine("3. 点「停止运行」：运行停下，不写这一行");
        {
            var (ed, _) = NewProgram(false);
            var (res, r, lines, _) = Run(ed, (q, ct) => Task.FromResult<UnsureAnswer?>(new UnsureAnswer(null, Stop: true)));
            Check("结果是「在确认框里停止了运行」，一行没记", res.Contains("确认框里停止") && r.RowsRecorded == 0 && Has(lines, "\"how\":\"stop\""), res);
        }

        Console.WriteLine("4. 填新名字「7天」并存成模板：只问一次，以后自动认；「重复直到」的超时不算等用户的时间");
        {
            var (ed, cl) = NewProgram(true);
            int asks = 0;
            var (res, r, lines, csv) = Run(ed, async (q, ct) =>
            {
                asks++;
                await Task.Delay(600, ct);   // 用户想了 0.6 秒，比循环超时 0.4 秒还长
                string name = ed.AddTemplateFromPng("7天", q.BadgePng!, q.Block);
                return new UnsureAnswer("7天", name);
            });
            Check("只问了一次", asks == 1 && Ev(lines, "unsureAsk") == 1 && Has(lines, "\"how\":\"saved\""), $"问了 {asks} 次");
            Check("模板存进程序文件夹，标签「7天」，加进了这块认图案", ed.Program.Templates.TryGetValue("7天", out var td) && td.Label == "7天"
                && File.Exists(Path.Combine(ed.Dir, td.File)) && cl.Templates.Contains("7天"));
            Check("后面几遍认出「7天」，没再标分不开", r.Classifies >= 2 && r.Unsures == 1 && lines.Count(l => l.Contains("\"ev\":\"classify\"") && l.Contains("\"value\":\"7天\"") && l.Contains("\"close\":false")) == r.Classifies - 1,
                $"认图案 {r.Classifies} 次，分不开 {r.Unsures} 次");
            Check("表里「7天」一行，后面的按去重跳过", csv.Contains("7天") && !csv.Contains("1天") && r.RowsRecorded == 1 && r.Duplicates == r.Classifies - 1);
            var to = lines.FirstOrDefault(l => l.Contains("\"ev\":\"loopTimeout\""));
            long held = to == null ? 0 : System.Text.Json.JsonDocument.Parse(to).RootElement.GetProperty("held").GetInt64();
            Check("循环还是到了超时，但等用户的 0.6 秒没算进去（确认完又转了几圈）", to != null && held >= 600 && res == "完成", to ?? "没超时");
            Check("存了模板算一步改动（关窗会问保存），存盘后读得回来", ed.Dirty && Reload(ed).Templates.ContainsKey("7天"));
        }

        Console.WriteLine("5. 选了已有的值（不存模板）：写那个值");
        {
            var (ed, _) = NewProgram(false);
            var (res, r, lines, csv) = Run(ed, (q, ct) => Task.FromResult<UnsureAnswer?>(new UnsureAnswer("3天")));
            Check("表里是「3天」，记作「选的」", res == "完成" && csv.Contains("3天") && r.RowsRecorded == 1 && Has(lines, "\"how\":\"picked\"") && r.UnsureAnswered == 2);
        }
        try { Directory.Delete(root, true); } catch { }

        Console.WriteLine(fails == 0 ? "认图案分不开自测全部通过" : $"认图案分不开自测失败 {fails} 项");
        return fails == 0 ? 0 : 1;
    }

    private static VbProgram Reload(ProgramEditor ed)
    {
        ed.Save();
        return ProgramIO.Load(ed.ProgramPath);
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
