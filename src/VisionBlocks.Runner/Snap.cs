using System.Globalization;
using OpenCvSharp;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Vision;

namespace VisionBlocks.Runner;

/// <summary>
/// 编辑器出来之前的「手工作业」：
/// vbrun snap --out x.png [--showcase] [--monitor N] —— 启动靶子，用 WGC 截一帧，归一化到 1080p 后存盘；
/// vbrun crop in.png x y w h out.png —— 按基准坐标裁模板；
/// vbrun color in.png x y —— 在某点吸色，打印 HSV 范围。
/// 模板从真实截图里裁，而不是用靶子的源图（避免测试结果偏乐观）。
/// </summary>
internal static class Snap
{
    public static int Run(Dictionary<string, string> o)
    {
        string outPng = Path.GetFullPath(Program.Opt(o, "out", "snap.png"));
        int monitor = int.Parse(Program.Opt(o, "monitor", "1"), CultureInfo.InvariantCulture);
        string extra = (o.ContainsKey("showcase") ? "--showcase " : "") + (o.TryGetValue("size", out var sz) ? "--size " + sz : "") + Program.SceneArgs(o);
        string logDir = Path.Combine(Path.GetTempPath(), "vb-snap-" + Environment.ProcessId);
        var (proc, hwnd) = TargetLauncher.Launch(TargetLauncher.FindTargetExe(), $"--log \"{logDir}\" --duration 10 --monitor {monitor} {extra}");
        using var wgc = new WgcFrameSource();
        wgc.Start(hwnd);
        Thread.Sleep(int.Parse(Program.Opt(o, "delay-ms", "2000"), CultureInfo.InvariantCulture));
        using var f = wgc.TryGetLatest() ?? throw new InvalidOperationException("没截到帧");
        using var n = NormFrame.FromBgra(f.Bgra);
        Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
        Cv2.ImWrite(outPng, n.Bgr);
        Console.WriteLine($"已存 {outPng}（原始 {f.Bgra.Width}×{f.Bgra.Height}，归一化 {n.Bgr.Width}×{n.Bgr.Height}，K={n.K:0.###}）");
        TargetLauncher.CloseGracefully(proc);
        try { Directory.Delete(logDir, true); } catch { }
        return 0;
    }

    public static int Crop(List<string> a)
    {
        if (a.Count < 6) { Console.WriteLine("用法：vbrun crop in.png x y w h out.png"); return 2; }
        using var img = Cv2.ImRead(a[0]);
        int[] v = a.Skip(1).Take(4).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        using var sub = new Mat(img, new Rect(v[0], v[1], v[2], v[3]));
        Cv2.ImWrite(a[5], sub);
        Console.WriteLine($"已裁 {a[5]}（{v[2]}×{v[3]}）");
        return 0;
    }

    /// <summary>
    /// 阈值标定（阈值要用真实截图标定）：在样张上逐个键帽位置跑模板匹配，
    /// 打印得分和对比度，看真目标和干扰项之间有没有足够的间隔。
    /// </summary>
    /// <summary>vbrun ocr in.png x y w h [x y w h …]：离线识别截图里若干区域的文字（基准坐标），用来先试 OCR 准不准。</summary>
    /// <remarks>--scale N --pad P：换一种放大倍数、四周补边再读（2026-11-25，试「读空时再读一次」用）。</remarks>
    public static int Ocr(List<string> a, Dictionary<string, string> o)
    {
        double scale = double.Parse(Program.Opt(o, "scale", "0"), CultureInfo.InvariantCulture);
        int pad = int.Parse(Program.Opt(o, "pad", "0"), CultureInfo.InvariantCulture);
        if (a.Count < 5 || (a.Count - 1) % 4 != 0) { Console.WriteLine("用法：vbrun ocr in.png x y w h [x y w h …]"); return 2; }
        using var img = Cv2.ImRead(a[0]);
        double k = Math.Min(1.0, img.Width / 1920.0);
        using var n = new NormFrame(img.Clone(), k, 1920);
        var ocr = new OcrReader();
        for (int i = 1; i < a.Count; i += 4)
        {
            var roi = new RegionDef { X = int.Parse(a[i]), Y = int.Parse(a[i + 1]), W = int.Parse(a[i + 2]), H = int.Parse(a[i + 3]) };
            var (raw, norm, ms) = ocr.ReadAsync(n, roi, default, scale, pad).GetAwaiter().GetResult();
            Console.WriteLine($"  {roi}：「{norm}」（原文「{raw}」，{ms:0}ms）");
        }
        return 0;
    }

    public static int Calib(List<string> a)
    {
        using var img = Cv2.ImRead(a[0]);
        // 存下来的是归一化后的帧：宽 1920 时 K=1；比基准窄时保持原尺寸，K = 宽/1920（只缩模板，不放大截图）
        double k = Math.Min(1.0, img.Width / 1920.0);
        using var n = new NormFrame(img.Clone(), k, 1920);
        Console.WriteLine($"帧 {img.Width}×{img.Height}，K={k:0.###}");
        using var m = TemplateMatcher.Load(a[1]);
        var spots = new (string Name, int X, int Y)[] { ("真F", 200, 200), ("E", 400, 200), ("R", 600, 200), ("半透明F", 200, 420), ("空地", 600, 420) };
        foreach (var (name, x, y) in spots)
        {
            var roi = new RegionDef { X = x - 20, Y = y - 20, W = 112, H = 112 };
            var r = m.Match(n, roi);
            Console.WriteLine($"{name,-5} 得分 {r.Score:0.0000}  对比度 {r.Contrast:0.000}");
        }
        var red = new ColorDef { Lower = new[] { 170, 114, 146 }, Upper = new[] { 10, 254, 255 } };
        var (ratio, _) = ColorRatio.Measure(n, new RegionDef { X = 1010, Y = 130, W = 840, H = 48 }, red);
        Console.WriteLine($"红血条占比 {ratio:P1}（样张血量 20%，期望约 20%）");
        var (raw, norm, ms) = new OcrReader().ReadAsync(n, new RegionDef { X = 40, Y = 664, W = 900, H = 150 }).GetAwaiter().GetResult();
        Console.WriteLine($"计分 OCR「{norm}」（{ms:0}ms）");
        return 0;
    }

    public static int Color(List<string> a)
    {
        using var img = Cv2.ImRead(a[0]);
        using var n = new NormFrame(img.Clone(), 1.0, 1920);
        var c = ColorRatio.FromSample(n, int.Parse(a[1], CultureInfo.InvariantCulture), int.Parse(a[2], CultureInfo.InvariantCulture), "sample");
        Console.WriteLine($"HSV 范围：lower [{string.Join(",", c.Lower)}] upper [{string.Join(",", c.Upper)}]");
        return 0;
    }
}
