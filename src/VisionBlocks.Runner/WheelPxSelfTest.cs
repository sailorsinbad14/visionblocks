using OpenCvSharp;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Vision;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-wheelpx [snap-grid.png]（2026-11-22）：「按距离滚」不发真输入、不开窗口的自测。
/// A. 量平移：在真截图（示例程序的 snap-grid.png）上切出两段已知位移的画面，看 ScrollMeter 量得准不准。
/// B. 闭环：假程序 + 假时钟，模拟三种处理「不足一格」的程序（按比例 / 攒够 120 / 只看正负），以及到底、慢半拍才开始动、画面太平、缩小的窗口。
///    每个场景都和假程序自己的真实滚动量对答案。
/// C. 积木：校验规则、摘要文字、JSON 往返；格数模式的 JSON 里不出现新字段。
/// </summary>
public static class WheelPxSelfTest
{
    private static int _fails;

    private static void Check(string name, bool pass, string detail)
    {
        if (!pass) _fails++;
        Console.WriteLine($"  [{(pass ? "通过" : "失败")}] {name}：{detail}");
    }

    public static int Run(List<string> a)
    {
        _fails = 0;
        Console.WriteLine("A. 量平移（真截图上合成已知位移）");
        string snap = a.Count > 0 ? a[0] : FindSnap();
        if (File.Exists(snap)) MeterOnSnap(snap);
        else Check("找到 snap-grid.png", false, snap);

        Console.WriteLine("B. 闭环（假程序 + 假时钟）");
        Loop();

        Console.WriteLine("C. 积木");
        Blocks();

        Console.WriteLine(_fails == 0 ? "全部通过" : $"{_fails} 项失败");
        return _fails == 0 ? 0 : 1;
    }

    private static string FindSnap()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "programs", "inv1", "snap-grid.png");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return "programs/inv1/snap-grid.png";
    }

    private static void MeterOnSnap(string path)
    {
        using var img = Cv2.ImRead(path, ImreadModes.Grayscale);
        // 截图 1920×1080；取 y 340 起 500 高、物品区那一段宽当热区，前后两帧错开 s 像素（一步最多跟住 285）
        foreach (int s in new[] { 0, 7, 33, 100, 170, 240 })
        {
            using var prev = new Mat(img, new Rect(440, 340, 1400, 500));
            // 向上滚：内容往下走 s，后一帧的第 y 行 = 前一帧的第 y-s 行
            using var cur = new Mat(img, new Rect(440, 340 - s, 1400, 500));
            // 向下滚：内容往上走 s，后一帧的第 y 行 = 前一帧的第 y+s 行
            using var curUp = new Mat(img, new Rect(440, 340 + s, 1400, 500));
            var r = ScrollMeter.Measure(prev, curUp, +1);
            Check($"向下滚 {s}px", r.Ok && Math.Abs(r.ShiftPx - s) <= 1, $"测得 {r.ShiftPx:0.0}，得分 {r.Score:0.000} / 次佳 {r.Second:0.000}");
            var r2 = ScrollMeter.Measure(prev, cur, -1);
            Check($"向上滚 {s}px", r2.Ok && Math.Abs(r2.ShiftPx + s) <= 1, $"测得 {r2.ShiftPx:0.0}，得分 {r2.Score:0.000}");
        }
    }

    // ── 假程序 ──
    private enum Mode { Linear, Accumulate, SignOnly }

    private sealed class FakeApp : IDisposable
    {
        public Mode Mode; public double Step; public double MaxOffset = 4000; public int LagMs; public int AnimMs;
        public double Target; public int Acc; public long T = 1000; public int Events, FracEvents;
        private readonly Mat _content; private readonly double _k; private readonly int _h;
        private double _from, _to; private long _start;

        public FakeApp(Mat contentBase, double k, int regionHBase, double start)
        {
            _k = k;
            _content = k == 1 ? contentBase.Clone() : contentBase.Resize(new Size(0, 0), k, k, InterpolationFlags.Area);
            _h = (int)Math.Round(regionHBase * k);
            Target = _from = _to = start;
        }

        public void Apply(int delta)
        {
            Events++;
            if (Math.Abs(delta) < 120) FracEvents++;
            switch (Mode)
            {
                case Mode.Linear: Target += delta / 120.0 * Step; break;
                case Mode.Accumulate:
                    Acc += delta;
                    while (Math.Abs(Acc) >= 120) { Target += Math.Sign(Acc) * Step; Acc -= Math.Sign(Acc) * 120; }
                    break;
                case Mode.SignOnly: Target += Math.Sign(delta) * Step; break;
            }
            Target = Math.Clamp(Target, 0, MaxOffset);
            _from = Shown(); _to = Target; _start = T + LagMs;
        }

        private double Shown()
        {
            if (T < _start) return _from;
            double p = AnimMs <= 0 ? 1 : Math.Min(1, (T - _start) / (double)AnimMs);
            return _from + (_to - _from) * p;
        }

        public Mat Render() => new Mat(_content, new Rect(0, (int)Math.Round(Shown() * _k), _content.Width, _h)).Clone();

        public DistanceScroller.Io Io() => new(
            Send: (delta, count, ct) =>
            {
                for (int i = 0; i < count; i++) { if (i > 0) T += 60; Apply(delta); }
                return Task.FromResult(count);
            },
            Grab: ct => { T += 16; return Task.FromResult((Render(), _k)); },
            Delay: (ms, ct) => { T += ms; return Task.CompletedTask; },
            NowMs: () => T);

        public void Dispose() => _content.Dispose();
    }

    /// <summary>假背包内容（基准像素，1440 宽）：每格 300×200、行距 240，格子里画不同的形状和数字；periodic = 每格一模一样。flat = 一片灰。</summary>
    internal static Mat Content(int height, bool periodic = false, bool flat = false)
    {
        var m = new Mat(height, 1440, MatType.CV_8UC1, Scalar.All(flat ? 90 : 34));
        if (flat) return m;
        var rng = new Random(20261122);
        for (int row = 0; row * 240 + 20 < height; row++)
            for (int col = 0; col < 4; col++)
            {
                int x = 30 + col * 360, y = 20 + row * 240;
                Cv2.Rectangle(m, new Rect(x, y, 300, 200), Scalar.All(215), 3);
                int v = periodic ? 150 : rng.Next(80, 250);
                int kind = periodic ? 0 : rng.Next(3);
                var c = new Point(x + 90, y + 90);
                if (kind == 0) Cv2.Circle(m, c, periodic ? 50 : rng.Next(25, 60), Scalar.All(v), -1);
                else if (kind == 1) Cv2.Rectangle(m, new Rect(c.X - 45, c.Y - rng.Next(20, 55), 90, 70), Scalar.All(v), -1);
                else Cv2.FillConvexPoly(m, new[] { new Point(c.X, c.Y - 55), new Point(c.X - 50, c.Y + 40), new Point(c.X + 50, c.Y + 40) }, Scalar.All(v));
                string label = periodic ? "Item" : $"No.{row * 4 + col:00} {rng.Next(10, 99)}";
                Cv2.PutText(m, label, new Point(x + 160, y + 170), HersheyFonts.HersheySimplex, 1.0, Scalar.All(235), 2);
            }
        return m;
    }

    private sealed record Case(string Name, Mode Mode, double Step, int Goal, double Start = 0, double MaxOffset = 4000,
        int LagMs = 0, int AnimMs = 0, double K = 1, bool Periodic = false, bool Flat = false,
        double? WantMoved = null, string? WantReason = "done", bool? WantSub = null, int Tol = 3);

    private static void Loop()
    {
        using var content = Content(5200);
        using var periodic = Content(5200, periodic: true);
        using var flat = Content(5200, flat: true);
        var cases = new[]
        {
            new Case("按比例 一格100 滚240", Mode.Linear, 100, 240, WantSub: true),
            new Case("按比例 一格100 滚720", Mode.Linear, 100, 720),
            new Case("按比例 一格100 向上滚240", Mode.Linear, 100, -240, Start: 600),
            new Case("按比例 一格170 滚240", Mode.Linear, 170, 240),
            new Case("格子一模一样（一格170）：第一格分不清 170 还是 410，滚一格就停", Mode.Linear, 170, 720, Periodic: true, WantReason: "cantMeasure"),
            new Case("格子一模一样且一格正好一行：分不清，滚一格就停", Mode.Linear, 240, 720, Periodic: true, WantReason: "cantMeasure"),
            new Case("按比例 一格40 滚1000（一步多格）", Mode.Linear, 40, 1000),
            new Case("按比例 慢半拍（300ms 后才动、动画 200ms）", Mode.Linear, 100, 240, LagMs: 300, AnimMs: 200),
            new Case("按比例 窗口缩到 0.5 倍", Mode.Linear, 100, 240, K: 0.5),
            new Case("攒够 120 才动 一格100 滚240", Mode.Accumulate, 100, 240, WantMoved: 200, WantReason: "nearestNotch", WantSub: false),
            new Case("只看正负 一格100 滚240", Mode.SignOnly, 100, 240, WantMoved: 200, WantReason: "nearestNotch", WantSub: false),
            new Case("只看正负 一格100 滚270（剩 70 > 半格，补一整格）", Mode.SignOnly, 100, 270, WantMoved: 300, WantReason: "nearestNotch", WantSub: false),
            new Case("到底：最多还能滚 250，要滚 720", Mode.Linear, 100, 720, MaxOffset: 250, WantMoved: 250, WantReason: "end"),
            new Case("画面一片平：一下都不滚", Mode.Linear, 100, 240, Flat: true, WantReason: "cantMeasure"),
            new Case("练习靶子默认：一格240 滚720", Mode.Linear, 240, 720),
            new Case("练习靶子默认：一格240 滚240", Mode.Linear, 240, 240),
            new Case("按比例 到底时零头不动：停在 200，不当成不认零头", Mode.Linear, 100, 240, MaxOffset: 200, WantMoved: 200, WantReason: "end", WantSub: true),
            new Case("攒够 120 才动 到底前：零头被抵消", Mode.Accumulate, 100, 240, MaxOffset: 200, WantMoved: 200, WantReason: "nearestNotch", WantSub: false),
        };
        foreach (var c in cases)
        {
            var src = c.Flat ? flat : c.Periodic ? periodic : content;
            using var app = new FakeApp(src, c.K, 870, c.Start) { Mode = c.Mode, Step = c.Step, MaxOffset = c.MaxOffset, LagMs = c.LagMs, AnimMs = c.AnimMs };
            var sc = new DistanceScroller();
            var res = sc.RunAsync(c.Goal, c.Tol, app.Io(), CancellationToken.None).GetAwaiter().GetResult();
            double truth = app.Target - c.Start;
            double want = c.WantMoved is double w ? w * Math.Sign(c.Goal) : c.Goal;
            bool ok = c.WantReason == "cantMeasure"
                ? res.Reason == "cantMeasure" && app.Events <= (c.Flat ? 0 : 1)   // 量不出时：画面平的一下都不滚，重复画面滚一格就停
                : Math.Abs(truth - want) <= c.Tol + 0.5            // 真的滚到了该到的地方
                  && Math.Abs(res.Moved - truth) <= 2               // 自己量的和真实的一致
                  && (c.WantReason == null || res.Reason == c.WantReason)
                  && (c.WantSub == null || res.SubNotch == c.WantSub)
                  && app.Acc == 0;                                  // 没有留下攒着的零头
            Check(c.Name, ok, $"真实 {truth:0.0}，自测 {res.Moved:0.0}，{res.Steps} 步，事件 {app.Events}（零头 {app.FracEvents}），每格 {res.PerNotch:0.0}，认零头 {res.SubNotch?.ToString() ?? "-"}，{res.Reason}");
        }

        // 记住「不认零头」：同一个 DistanceScroller 再滚一次，不再发零头。
        // 2026-11-23 起上一块还差的 40 会带进来：第二次目标 280，只认整格时停在最近的 300
        using (var app1 = new FakeApp(content, 1, 870, 0) { Mode = Mode.Accumulate, Step = 100 })
        {
            var sc = new DistanceScroller();
            sc.RunAsync(240, 3, app1.Io(), CancellationToken.None).GetAwaiter().GetResult();
            using var app2 = new FakeApp(content, 1, 870, 0) { Mode = Mode.Accumulate, Step = 100 };
            var res = sc.RunAsync(240, 3, app2.Io(), CancellationToken.None).GetAwaiter().GetResult();
            Check("第二次滚不再试零头", app2.FracEvents == 0 && Math.Abs(res.Moved - app2.Target) <= 2 && app2.Target == 300 && Math.Abs(res.CarryIn - 40) <= 2, $"零头事件 {app2.FracEvents}，带入 {res.CarryIn:0}，真实 {app2.Target:0}，自测 {res.Moved:0}，{res.Reason}");
        }

        // 记住「每格多少像素」：第二块比一格还短（-100，一格 170），第一步就直接发零头，不先滚一整格（2026-11-22 真靶子上发现过冲到 -170）
        using (var app = new FakeApp(content, 1, 870, 0) { Mode = Mode.Linear, Step = 170 })
        {
            var sc = new DistanceScroller();
            sc.RunAsync(240, 3, app.Io(), CancellationToken.None).GetAwaiter().GetResult();
            double before = app.Target; int ev = app.Events;
            var res = sc.RunAsync(-100, 3, app.Io(), CancellationToken.None).GetAwaiter().GetResult();
            double truth = app.Target - before;
            Check("比一格短的距离用上次的每格像素", Math.Abs(truth + 100) <= 3.5 && Math.Abs(res.Moved - truth) <= 2 && app.Events - ev == 1 && res.Reason == "done",
                $"真实 {truth:0.0}，自测 {res.Moved:0.0}，事件 {app.Events - ev}，{res.Reason}");
        }

        // 翻页循环：列表在 800 到底。同一个热区连滚 720、720（第二次到底），再换一个长列表滚 720：到底那次量到的偏小的每格像素不能带进下一块
        using (var app = new FakeApp(content, 1, 870, 0) { Mode = Mode.Linear, Step = 100, MaxOffset = 800 })
        {
            var sc = new DistanceScroller();
            var r1 = sc.RunAsync(720, 3, app.Io(), CancellationToken.None).GetAwaiter().GetResult();
            double t1 = app.Target;
            var r2 = sc.RunAsync(720, 3, app.Io(), CancellationToken.None).GetAwaiter().GetResult();
            double t2 = app.Target;
            using var app2 = new FakeApp(content, 1, 870, 0) { Mode = Mode.Linear, Step = 100 };
            var r3 = sc.RunAsync(720, 3, app2.Io(), CancellationToken.None).GetAwaiter().GetResult();
            Check("翻页：到底以后再滚新列表", Math.Abs(t1 - 720) <= 3.5 && t2 == 800 && r2.Reason == "end" && Math.Abs(app2.Target - 720) <= 3.5 && Math.Abs(r3.Moved - app2.Target) <= 2,
                $"第一次 {t1:0}，第二次到 {t2:0}（{r2.Reason}），新列表 真实 {app2.Target:0.0} 自测 {r3.Moved:0.0}（{r3.Reason}），记下每格 {sc.PerNotchHint:0.0}");
        }
        // 到底那块第一步就被截短（只剩 30 px）：不把 30 当成每格像素记下来
        using (var app = new FakeApp(content, 1, 870, 770) { Mode = Mode.Linear, Step = 100, MaxOffset = 800 })
        {
            var sc = new DistanceScroller();
            var r1 = sc.RunAsync(720, 3, app.Io(), CancellationToken.None).GetAwaiter().GetResult();
            using var app2 = new FakeApp(content, 1, 870, 0) { Mode = Mode.Linear, Step = 100 };
            var r2 = sc.RunAsync(720, 3, app2.Io(), CancellationToken.None).GetAwaiter().GetResult();
            Check("第一步就到底：不记下截短的每格像素", r1.Reason == "end" && sc.PerNotchHint is null or > 90 && Math.Abs(app2.Target - 720) <= 3.5,
                $"第一块 {r1.Moved:0}（{r1.Reason}），记下每格 {sc.PerNotchHint?.ToString("0.0") ?? "无"}，新列表 真实 {app2.Target:0.0}（{r2.Reason}）");
        }

        // 记下的每格像素过时了（以前 170，现在 85）：不能因为「比预期少」就当成到底
        using (var app = new FakeApp(content, 1, 870, 0) { Mode = Mode.Linear, Step = 85 })
        {
            var sc = new DistanceScroller { PerNotchHint = 170, SubNotchOk = true };
            var res = sc.RunAsync(720, 3, app.Io(), CancellationToken.None).GetAwaiter().GetResult();
            Check("记下的每格像素过时", Math.Abs(app.Target - 720) <= 3.5 && !res.End && res.Reason == "done" && sc.PerNotchHint is double h && Math.Abs(h - 85) < 1,
                $"真实 {app.Target:0.0}，自测 {res.Moved:0.0}，{res.Steps} 步，现在每格 {sc.PerNotchHint:0.0}，{res.Reason}");
        }
    }

    private static void Blocks()
    {
        var p = new VbProgram { Name = "wheelpx-selftest" };
        p.Regions["物品区"] = new RegionDef { X = 420, Y = 150, W = 1440, H = 870 };
        p.Points["中间"] = new PointDef { X = 900, Y = 500 };
        var good = new WheelBlock { Region = "物品区", DistancePx = 720 };
        p.Program.Add(good);
        Check("距离模式校验通过", ProgramIO.Validate(p).Count == 0, string.Join("；", ProgramIO.Validate(p)));
        Check("摘要文字", BlockText.Summary(good, p) == "在「物品区」滚轮向下 720 px（边滚边量）", BlockText.Summary(good, p));

        p.Program.Clear();
        p.Program.Add(new WheelBlock { Point = "中间", DistancePx = 240 });
        p.Program.Add(new WheelBlock { Region = "物品区", DistancePx = 0 });
        p.Program.Add(new WheelBlock { Region = "物品区", DistancePx = -240, TolerancePx = 0 });
        var errs = ProgramIO.Validate(p);
        Check("拦下：定点 / 距离 0 / 容差 0", errs.Count == 3 && errs.Any(e => e.Contains("不能用定点")) && errs.Any(e => e.Contains("不能是 0")) && errs.Any(e => e.Contains("容差")), string.Join("；", errs));

        p.Program.Clear();
        p.Program.Add(new WheelBlock { Region = "物品区", Notches = 3 });
        p.Program.Add(new WheelBlock { Region = "物品区", DistancePx = -480, TolerancePx = 5 });
        string json = System.Text.Json.JsonSerializer.Serialize(p, ProgramIO.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<VbProgram>(json, ProgramIO.Options)!;
        var w0 = (WheelBlock)back.Program[0];
        var w1 = (WheelBlock)back.Program[1];
        string first = json[json.IndexOf("\"type\": \"wheel\"", StringComparison.Ordinal)..json.IndexOf('}', json.IndexOf("\"type\": \"wheel\"", StringComparison.Ordinal))];
        Check("JSON 往返", w0.DistancePx == null && w0.Notches == 3 && w1.DistancePx == -480 && w1.TolerancePx == 5, $"格数 {w0.Notches}，距离 {w1.DistancePx}，容差 {w1.TolerancePx}");
        Check("格数模式的 JSON 不带新字段", !first.Contains("distancePx") && !first.Contains("tolerancePx"), first.Replace("\n", " ").Replace("  ", ""));
    }
}
