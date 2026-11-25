using OpenCvSharp;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Vision;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-pattern [programs/inv-twins]：「认图案」积木（2026-11-25）。不截屏、不发输入。
/// 1. 靶子 --twins 详情页的图片区截图（右下角角标：1天 / 3天 / 没有 / 红底「限时」/ 蓝底「限时」/ 没裁模板的 7天）贴回 1920×1080 的画布，
///    用裁好的 4 张角标模板认；再错开 1–2 像素、缩成小窗口（K = 0.75 / 0.5）各认一遍。打出第一、二名分差，看余量。
/// 2. 直接喂分数测判定规则：取最高分、按颜色分开只差底色的、分不开要标出来、没过门槛写「无」、有点像要标出来。
/// 3. 积木和去重字段的校验。
/// </summary>
public static class PatternSelfTest
{
    /// <summary>截图是从基准坐标 (1300,130) 起裁的 380×380；角标在图片区右下角 (1572,442,80,40)。</summary>
    private const int SnapX = 1300, SnapY = 130;
    private static readonly RegionDef Corner = new() { X = 1540, Y = 420, W = 125, H = 75 };
    private static readonly (string Tpl, string File)[] Templates =
        { ("1天", "badge-1d.png"), ("3天", "badge-3d.png"), ("限时红", "badge-limit-red.png"), ("限时蓝", "badge-limit-blue.png") };
    private static readonly (string Snap, string Want)[] Snaps =
        { ("snap-badge-1d.png", "1天"), ("snap-badge-3d.png", "3天"), ("snap-badge-none.png", "无"), ("snap-badge-limit-red.png", "限时红"), ("snap-badge-limit-blue.png", "限时蓝"), ("snap-badge-7d.png", "?") };

    public static int Run(List<string> a)
    {
        string dir = Path.GetFullPath(a.Count > 0 ? a[0] : Path.Combine(RepoRoot(), "programs", "inv-twins"));
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}{(detail.Length > 0 ? "：" + detail : "")}");
        }

        var matchers = Templates.Select(t => (t.Tpl, t.Tpl, TemplateMatcher.Load(Path.Combine(dir, "templates", t.File)))).ToList();
        Console.WriteLine("1. 靶子截图上认角标（门槛 0.85）");
        double worstMargin = double.MaxValue, bestWrong = 0, worstRight = 1, noBadge = 0;
        foreach (var (k, dx, dy) in new[] { (1.0, 0, 0), (1.0, 1, 0), (1.0, 2, 1), (0.75, 0, 0), (0.5, 0, 0) })
        {
            foreach (var (snap, want) in Snaps)
            {
                using var f = Frame(Path.Combine(dir, snap), k, dx, dy);
                var r = PatternClassifier.Classify(f, Corner, matchers, 0.85, "无");
                string scores = string.Join(" · ", r.All.OrderByDescending(c => c.Score).Select(c => $"{c.Template} {c.Score:0.000}/色差 {c.ColorDist:0}"));
                string tag = $"K={k} 错开 ({dx},{dy}) {want}";
                if (want == "?")
                {
                    // 没裁模板的 7天：不能悄悄当成 1天 / 3天，要么写「无」并标「有点像」，要么标「分不太开」
                    bool flagged = r.Midway || r.Close || r.Value == "无";
                    Check($"{tag} → {r.Value}{(r.Close ? "（分不太开）" : r.Midway ? "（有点像但没过门槛）" : "")}", flagged, scores);
                    continue;
                }
                bool ok = r.Value == want && !r.Close;
                Check($"{tag} → {r.Value}{(r.ByColor ? "（按颜色定）" : "")}", ok, scores);
                if (want != "无")
                {
                    var right = r.All.First(c => c.Value == want);
                    worstRight = Math.Min(worstRight, right.Score);
                    // 只差底色的两张灰度分本来就分不开，余量只算字不一样的
                    var others = r.All.Where(c => c.Value != want && !(want.StartsWith("限时") && c.Value.StartsWith("限时"))).ToList();
                    double wrong = others.Count > 0 ? others.Max(c => c.Score) : 0;
                    bestWrong = Math.Max(bestWrong, wrong);
                    worstMargin = Math.Min(worstMargin, right.Score - wrong);
                }
                else noBadge = Math.Max(noBadge, r.Best);
            }
        }
        // 2026-11-25 实测：1天模板认 3天角标能到 0.86–0.88，比门槛还高——所以只能「取最高分」，不能「谁先过门槛算谁」
        Console.WriteLine($"  余量：认对的最低 {worstRight:0.000}，字不同的认错最高 {bestWrong:0.000}（高过门槛也没关系，取的是最高分），" +
                          $"同一张图上对的比错的最少高 {worstMargin:0.000}；没角标时最高 {noBadge:0.000}");
        Check("认对的都过门槛 0.85", worstRight >= 0.85);
        Check("没角标的都在门槛下（写「无」）", noBadge < 0.85);
        Check($"对的比字不同的错的至少高 {2 * PatternClassifier.CloseGap:0.00}（是「分不太开」线的两倍）", worstMargin >= 2 * PatternClassifier.CloseGap);
        foreach (var m in matchers) m.Item3.Dispose();

        Console.WriteLine("2. 判定规则（直接喂分数）");
        PatternClassifier.Candidate C(string v, double s, double col) => new(v, v, s, col, null);
        var d1 = PatternClassifier.Decide(new[] { C("1天", 0.83, 5), C("3天", 0.91, 5) }, 0.85, "无");
        Check("取最高分，不是第一个过门槛的", d1.Value == "3天" && !d1.Close, d1.Value);
        var d2 = PatternClassifier.Decide(new[] { C("限时红", 0.990, 70), C("限时蓝", 0.985, 4) }, 0.85, "无");
        Check("分数差不多、颜色差得明显 → 按颜色定", d2.Value == "限时蓝" && d2.ByColor && !d2.Close, d2.Value);
        var d3 = PatternClassifier.Decide(new[] { C("1天", 0.88, 5), C("3天", 0.87, 8) }, 0.85, "无");
        Check("分数差不多、颜色也差不多 → 照写第一名并标「分不太开」", d3.Value == "1天" && d3.Close, d3.Value);
        var d4 = PatternClassifier.Decide(new[] { C("1天", 0.70, 5), C("3天", 0.66, 5) }, 0.85, "无");
        Check("都没过门槛 → 写「无」，最高分 0.5 以上标「有点像」", d4.Value == "无" && d4.Midway && d4.Winner == null, d4.Value);
        var d5 = PatternClassifier.Decide(new[] { C("1天", 0.20, 5), C("3天", 0.15, 5) }, 0.85, "无");
        Check("完全不像 → 写「无」，不标", d5.Value == "无" && !d5.Midway, d5.Value);
        var d6 = PatternClassifier.Decide(new[] { C("限时红", 0.86, 70), C("限时蓝", 0.84, 4) }, 0.85, "无");
        Check("按颜色换成第二名时，用第一名的分数过门槛", d6.Value == "限时蓝", d6.Value);
        var d7 = PatternClassifier.Decide(new[] { C("1天", 0.93, double.NaN) }, 0.85, "无");
        Check("只有一张模板、灰度模板（没有颜色）也照常认", d7.Value == "1天" && !d7.Close, d7.Value);

        Console.WriteLine("3. 校验");
        var p = new VbProgram();
        p.Regions["角"] = new RegionDef { X = 0, Y = 0, W = 100, H = 100 };
        p.Templates["t1"] = new TemplateDef { File = "t1.png" };
        var cl = new ClassifyBlock { Region = "角", Field = "有效期", Templates = { "t1" } };
        p.Program.Add(new ReadTextBlock { Region = "角", Field = "名称" });
        p.Program.Add(new SaveImageBlock { Region = "角", Field = "图片" });
        p.Program.Add(cl);
        var rr = new RecordRowBlock { DedupeBy = { "名称", "有效期" } };
        p.Program.Add(rr);
        Check("名称 + 认图案的字段去重：没有错误", ProgramIO.Validate(p).Count == 0, string.Join("；", ProgramIO.Validate(p)));
        Check("认图案的字段进结果表的列", Core.Runtime.Recorder.FieldsOf(p).SequenceEqual(new[] { "名称", "图片", "有效期" }));
        rr.DedupeBy = new() { "名称", "图片" };
        Check("去重里填截图字段：报错", ProgramIO.Validate(p).Any(e => e.Contains("比的是文件名")));
        rr.DedupeBy = new() { "名字" };
        Check("去重字段名写错：报错", ProgramIO.Validate(p).Any(e => e.Contains("在程序里没有")));
        rr.DedupeBy = new() { "名称" };
        cl.Templates = new() { "t9" };
        Check("认图案选了不存在的模板：报错", ProgramIO.Validate(p).Any(e => e.Contains("模板「t9」不存在")));
        cl.Templates = new();
        Check("认图案没选模板：报错", ProgramIO.Validate(p).Any(e => e.Contains("还没选模板")));
        cl.Templates = new() { "t1" };
        cl.NoneText = " ";
        Check("「都不像时写」为空：报错", ProgramIO.Validate(p).Any(e => e.Contains("不能为空")));
        cl.NoneText = "无";
        string json = System.Text.Json.JsonSerializer.Serialize(p, ProgramIO.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<VbProgram>(json, ProgramIO.Options)!;
        Check("JSON 往返", back.Program.OfType<ClassifyBlock>().Single() is { Field: "有效期", Threshold: 0.85, NoneText: "无" } c2 && c2.Templates.SequenceEqual(new[] { "t1" }) && json.Contains("\"type\": \"classify\""));

        foreach (var file in Directory.GetFiles(dir, "*.vbp.json"))
        {
            var errs = ProgramIO.Validate(ProgramIO.Load(file));
            Check($"示例程序 {Path.GetFileName(file)} 没有错误", errs.Count == 0, string.Join("；", errs));
        }

        Console.WriteLine(fails == 0 ? "认图案自测全部通过" : $"认图案自测失败 {fails} 项");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>把截图贴回详情页底色的 1920×1080 画布（可错开几像素），再按 K 缩成小窗口。</summary>
    private static NormFrame Frame(string snap, double k, int dx, int dy)
    {
        using var crop = Cv2.ImRead(snap);
        using var canvas = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(242, 240, 236));
        using (var dst = new Mat(canvas, new Rect(SnapX + dx, SnapY + dy, crop.Width, crop.Height))) crop.CopyTo(dst);
        if (k >= 0.9995) return new NormFrame(canvas.Clone(), 1.0, 1920);
        var small = canvas.Resize(new Size((int)Math.Round(1920 * k), (int)Math.Round(1080 * k)), 0, 0, InterpolationFlags.Area);
        return new NormFrame(small, k, 1920);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "VisionBlocks.sln"))) d = d.Parent;
        return d?.FullName ?? ".";
    }
}
