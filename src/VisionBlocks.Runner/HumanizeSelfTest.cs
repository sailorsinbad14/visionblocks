using VisionBlocks.Core.Input;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-humanize：鼠标操作随机化。用假后端，不发真实输入。
/// A. 热区内取点：几万次都落在热区里、离边 ≥ 4 基准像素、离中心不超过宽高的 30%；小热区只点中心。
/// B. 时机：按住 ×0.7–1.3 且在 10–2000ms，「等待」×0.8–1.2，点击前多等 30–120ms。
/// C. 轨迹：所有点在绑定窗口的客户区里，终点就是目标（目标在窗口外时夹回窗口内），总时长有上限；过冲时最后一步回到终点。
/// D. 同一个种子结果完全一样，换种子就不一样。
/// E. 和输入后端一起：轨迹走完才按下；移动途中被前台守卫拦下、或已经急停时，不会按下。
/// </summary>
internal static class HumanizeSelfTest
{
    public static int Run()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail)
        {
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}：{detail}");
        }

        // A
        var h = new Humanizer(true, true, 7);
        foreach (var (x, y, w, hh) in new[] { (450, 170, 300, 200), (70, 160, 280, 90), (0, 0, 20, 40), (10, 10, 6, 6) })
        {
            bool inside = true, margin = true, near = true;
            for (int i = 0; i < 20000; i++)
            {
                var (px, py) = h.PointIn(x, y, w, hh);
                inside &= px >= x && px < x + w && py >= y && py < y + hh;
                margin &= w < 8 || hh < 8 || (px >= x + 4 && px <= x + w - 4 && py >= y + 4 && py <= y + hh - 4);
                near &= Math.Abs(px - (x + w / 2)) <= w * 0.3 + 1 && Math.Abs(py - (y + hh / 2)) <= hh * 0.3 + 1;
            }
            Check($"热区 {w}×{hh} 内取点 2 万次", inside && margin && near, $"都在热区里 {inside}，离边 ≥ 4 {margin}，离中心 ≤ 30% {near}");
        }
        bool nearOk = Enumerable.Range(0, 5000).All(_ => { var (px, py) = h.PointNear(100, 100); return Math.Abs(px - 100) <= 3 && Math.Abs(py - 100) <= 3; });
        Check("定点附近 ±3", nearOk, "5000 次");
        var off = new Humanizer(false, false, 7);
        Check("关掉时点中心、时长不变", off.PointIn(450, 170, 300, 200) == (600, 270) && off.Hold(40) == 40 && off.Wait(300) == 300 && off.BeforeAction() == 0, "和以前一样");

        // B
        var holds = Enumerable.Range(0, 5000).Select(_ => h.Hold(40)).ToList();
        var waits = Enumerable.Range(0, 5000).Select(_ => h.Wait(1000)).ToList();
        var pres = Enumerable.Range(0, 5000).Select(_ => h.BeforeAction()).ToList();
        Check("按住 40ms", holds.All(v => v >= 28 && v <= 52), $"{holds.Min()}–{holds.Max()}ms");
        Check("按住夹在 10–2000ms", h.Hold(5) >= 10 && Enumerable.Range(0, 200).All(_ => h.Hold(1900) <= 2000), "下限、上限");
        Check("等待 1000ms", waits.All(v => v >= 800 && v <= 1200), $"{waits.Min()}–{waits.Max()}ms");
        Check("点击前多等", pres.All(v => v >= 30 && v <= 120), $"{pres.Min()}–{pres.Max()}ms");

        // C
        var client = new WindowInfo.PxRect(3200, 200, 1920, 1080);
        var rng = new Random(1);
        bool allIn = true, endOk = true; int maxSteps = 0, maxMs = 0;
        foreach (bool overshoot in new[] { false, true })
        {
            var hp = new Humanizer(true, true, 11, overshoot);
            for (int i = 0; i < 3000; i++)
            {
                (int, int) from = i % 3 == 0 ? (rng.Next(0, 6000), rng.Next(0, 2000)) : (rng.Next(3200, 5120), rng.Next(200, 1280));
                (int, int) to = i % 10 == 0 ? (rng.Next(0, 6000), rng.Next(0, 2000)) : (rng.Next(3200, 5120), rng.Next(200, 1280));
                var pts = hp.PathPoints(from, to, client);
                allIn &= pts.All(p => p.X > client.X && p.X < client.X + client.W - 1 && p.Y > client.Y && p.Y < client.Y + client.H - 1);
                int ex = Math.Clamp(to.Item1, client.X + 1, client.X + client.W - 2), ey = Math.Clamp(to.Item2, client.Y + 1, client.Y + client.H - 2);
                endOk &= pts[^1].X == ex && pts[^1].Y == ey;
                maxSteps = Math.Max(maxSteps, pts.Count);
                maxMs = Math.Max(maxMs, pts.Sum(p => p.DelayMs));
            }
        }
        Check("轨迹 6000 条全在窗口客户区里", allIn, "起点有三分之一在窗口外");
        Check("终点就是目标（窗口外的目标夹回窗口内）", endOk, "含过冲");
        Check("步数和总时长有上限", maxSteps <= 41 && maxMs <= 1100, $"最多 {maxSteps} 步、{maxMs}ms");

        // D
        string Trace(int seed) { var a = new Humanizer(true, true, seed); return string.Join(";", Enumerable.Range(0, 50).Select(_ => $"{a.PointIn(450, 170, 300, 200)}{a.Hold(40)}{a.PathPoints((3300, 300), (4000, 900), client).Count}")); }
        Check("同一个种子可以复现", Trace(42) == Trace(42), "两次 50 组结果一样");
        Check("换种子就不一样", Trace(42) != Trace(43), "种子 42 和 43");

        // E
        List<string> Click(Func<int, bool> allowStep, bool abortFirst)
        {
            var ev = new List<string>();
            int moves = 0;
            var input = new SendInputBackend(new InputGuard(IntPtr.Zero) { TestAllow = () => allowStep(moves) }, null)
            {
                TestSender = d => { ev.Add(d); if (d.StartsWith("mv", StringComparison.Ordinal)) moves++; return 1; },
                IsKeyPhysicallyDown = _ => false, CloseImeBeforeKeys = false,
                CursorPosition = () => (3300, 300), ClientRect = _ => client,
                Humanizer = new Humanizer(true, true, 5),
            };
            if (abortFirst) input.Abort("测试");
            input.ClickAsync(4500, 900, 40, CancellationToken.None).GetAwaiter().GetResult();
            return ev;
        }
        var full = Click(_ => true, false);
        int mvCount = full.Count(e => e.StartsWith("mv", StringComparison.Ordinal));
        Check("走完轨迹才按下", mvCount >= 6 && full.IndexOf("md") > full.FindLastIndex(e => e.StartsWith("mv", StringComparison.Ordinal)) && full.Contains("mu") && full.Last(e => e.StartsWith("mv", StringComparison.Ordinal)) == "mv 4500,900",
            $"{mvCount} 步移动，最后一步 {full.Last(e => e.StartsWith("mv", StringComparison.Ordinal))}，然后 md、mu");
        var cut = Click(m => m < 3, false);
        Check("移动途中被前台守卫拦下就不按", !cut.Contains("md") && cut.Count(e => e.StartsWith("mv", StringComparison.Ordinal)) == 3, $"移动 {cut.Count(e => e.StartsWith("mv", StringComparison.Ordinal))} 步后停下，没有按下");
        var aborted = Click(_ => true, true);
        Check("急停之后不移动也不按", !aborted.Any(e => e.StartsWith("mv", StringComparison.Ordinal) || e == "md"), $"发出 {aborted.Count} 个事件");

        Console.WriteLine(fails == 0 ? "随机化自测全部通过" : $"随机化自测失败 {fails} 项");
        return fails == 0 ? 0 : 1;
    }
}
