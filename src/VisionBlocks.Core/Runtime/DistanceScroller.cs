using OpenCvSharp;
using VisionBlocks.Core.Vision;

namespace VisionBlocks.Core.Runtime;

/// <summary>
/// 「按距离滚」的闭环（2026-11-22）：滚一下 → 等画面停稳 → 量平移 → 累加，滚够就停。
/// 第一格顺便量出「每格多少像素」；剩下不足一格时发不足 120 的 delta。
/// 目标程序不认不足一格时（攒着不动，或者当成一整格），撤回这一下，并在整个运行期间记住，之后只滚整格。
/// 输入和截图都通过 <see cref="Io"/> 传进来，自测可以换成假的。距离都是基准像素。
/// </summary>
public sealed class DistanceScroller
{
    /// <param name="Send">发 count 个滚轮事件，每个的 delta 为 signedDelta（正 = 向下滚，绝对值 1–120）；返回实际发出的个数。</param>
    /// <param name="Grab">当前这一帧热区的灰度图（画面像素，调用方给一份新的 Mat，用完由这里释放），以及 K = 画面像素 / 基准像素。实现里要先等下一帧（有超时）。</param>
    public sealed record Io(
        Func<int, int, CancellationToken, Task<int>> Send,
        Func<CancellationToken, Task<(Mat Gray, double K)>> Grab,
        Func<int, CancellationToken, Task> Delay,
        Func<long> NowMs);

    /// <param name="Moved">实际滚了多少基准像素（带方向，正 = 向下）。</param>
    /// <param name="Reason">done 到了 / end 到底了 / nearestNotch 只认整格、停在最近的整格 / cantMeasure 量不出 / blocked 输入被拦 / maxSteps。</param>
    public sealed record Result(double Moved, int Steps, double? PerNotch, bool? SubNotch, bool End, string Reason, int MeasureFails, int EventsSent);

    public const int MinWaitMs = 120, QuietMs = 500, SettleMaxMs = 1500, MaxSteps = 40;

    /// <summary>目标程序认不认不足一格的滚动。null = 还没试过。一次运行里只试一次。</summary>
    public bool? SubNotchOk { get; set; }
    /// <summary>上一次在这个热区量到的「每格多少像素」。有了它，下一块第一步就能直接按它算（比一格还短的距离不会先滚一整格）；之后照样边滚边量。</summary>
    public double? PerNotchHint { get; set; }
    /// <summary>每步之后：已滚（带方向）、步数、每格多少像素。</summary>
    public Action<double, int, double?>? Progress { get; set; }
    public Action<object>? Log { get; set; }

    public async Task<Result> RunAsync(int distancePx, int tolerancePx, Io io, CancellationToken ct)
    {
        int dir = Math.Sign(distancePx);
        double goal = Math.Abs(distancePx), tol = Math.Max(1, tolerancePx);
        var (prev, k) = await Settle(io, io.NowMs() - MinWaitMs, ct);
        double maxStep = 0.8 * ScrollMeter.MaxTrack(prev.Rows, dir) / k;
        double moved = 0, pxSum = 0;
        int notchSum = 0, steps = 0, fails = 0, events = 0, learned = 0;
        double? perNotch = null;
        bool end = false;
        string reason = "done";
        try
        {
            if (!ScrollMeter.HasTexture(prev, dir))
            {
                Log?.Invoke(new { ev = "wheelStep", step = 0, ok = false, why = "热区里画面太平，量不出移动，没有滚" });
                return new Result(0, 0, null, SubNotchOk, false, "cantMeasure", 0, 0);
            }
            while (true)
            {
                double remaining = goal - moved;
                if (remaining <= tol)
                {
                    // 滚过头了：只认整格的程序停在最近的整格，其余算滚过头
                    reason = remaining >= -tol ? "done" : SubNotchOk == false ? "nearestNotch" : "overshoot";
                    break;
                }
                if (steps >= MaxSteps) { reason = "maxSteps"; break; }
                int count = 1, unit = 120;
                double? plan = perNotch ?? PerNotchHint;
                if (plan is double pn)
                {
                    double want = remaining / pn;
                    // 一步最多 10 格：第一格要是碰上到底只滚了一点，算出来的「每格像素」会偏小。
                    // 还没在这一块里量过（只有上次记下的值）时一步只滚一格，量准了再放开
                    if (want >= 1) count = perNotch == null ? 1 : Math.Clamp(Math.Min((int)Math.Floor(want + 1e-6), (int)Math.Floor(maxStep / pn)), 1, 10);
                    else if (SubNotchOk == false)
                    {
                        if (remaining > pn / 2) count = 1;
                        else { reason = "nearestNotch"; break; }
                    }
                    else unit = Math.Clamp((int)Math.Round(120 * want), 1, 119);
                }
                long sentAt = io.NowMs();
                int sent = await io.Send(dir * unit, count, ct);
                events += sent;
                if (sent == 0) { reason = "blocked"; break; }
                steps++;
                double? expected = plan is double p2 ? p2 * sent * unit / 120.0 : null;
                var (cur, progress, r) = await MeasureStep(io, prev, sentAt, dir, expected, k, ct);
                if (!r.Ok)
                {
                    fails++;
                    // 量不出时按预期估，只限这一块自己量到过每格像素；只凭上次记下的值估太冒险
                    if (perNotch != null && expected is double ex) progress = ex;
                    else { cur.Dispose(); reason = "cantMeasure"; Log?.Invoke(new { ev = "wheelStep", step = steps, delta = dir * unit, count = sent, ok = false, why = r.Why }); break; }
                }
                string note = "";
                if (unit < 120)
                {
                    double fracExpected = expected!.Value;
                    if (progress < 0.5 && SubNotchOk == true)
                    {
                        // 已知认零头，零头还不动：到底了
                        end = true; note = "noMoveFrac";
                    }
                    else if (progress < 0.5)
                    {
                        // 零头没动：可能是程序攒着没用，也可能正好到底了。发一个反向的零头看看：
                        //   画面往回动了 → 程序认零头，刚才是到底了，再把这一下发回去；
                        //   还是不动 → 程序攒着零头，反向的正好把它抵消掉，免得加到以后的滚动里。
                        long backAt = io.NowMs();
                        int back = await io.Send(-dir * unit, 1, ct);
                        events += back;
                        var (afterBack, backProgress, rb) = await MeasureStep(io, cur, backAt, -dir, fracExpected, k, ct);
                        cur.Dispose();
                        cur = afterBack;
                        if (back > 0 && rb.Ok && backProgress >= 0.5)
                        {
                            bool asWhole = backProgress > fracExpected + Math.Max(0.35 * fracExpected, 6) && backProgress > 0.6 * plan!.Value;
                            SubNotchOk = !asWhole;
                            long fwdAt = io.NowMs();
                            events += await io.Send(dir * (asWhole ? 120 : unit), 1, ct);
                            var (afterFwd, fwdProgress, rf) = await MeasureStep(io, cur, fwdAt, dir, backProgress, k, ct);
                            cur.Dispose();
                            cur = afterFwd;
                            progress = (rf.Ok ? fwdProgress : backProgress) - backProgress;
                            end = true; note = "endAfterFrac";
                        }
                        else { SubNotchOk = false; note = "subNotchIgnored"; }
                    }
                    else if (progress > fracExpected + Math.Max(0.35 * fracExpected, 6) && progress > 0.6 * plan!.Value)
                    {
                        // 零头被当成了一整格。停在这里和退回一格，哪个离目标近选哪个
                        double whole = plan.Value;
                        SubNotchOk = false;
                        note = "subNotchAsWhole";
                        if (Math.Abs(goal - (moved + progress)) > Math.Abs(goal - (moved + progress - whole)))
                        {
                            long backAt = io.NowMs();
                            int back = await io.Send(-dir * 120, 1, ct);
                            events += back;
                            if (back > 0)
                            {
                                var (afterBack, backProgress, rb) = await MeasureStep(io, cur, backAt, -dir, whole, k, ct);
                                cur.Dispose();
                                cur = afterBack;
                                progress -= rb.Ok ? backProgress : whole;
                                note = "subNotchAsWhole+back";
                            }
                        }
                    }
                    else SubNotchOk = true;
                }
                else if (progress < 0.5) { end = true; note = "noMove"; }
                // 「滚得比预期少很多 = 到底」只按这一块自己量到的每格像素判断；上次记下的可能已经过时（比如窗口变了）
                else if (perNotch != null && progress < 0.6 * expected!.Value) { end = true; note = "short"; }
                // 已经量到过每格像素时，明显偏少的一步（可能被底部截住一部分）不拿来更新
                else if (perNotch == null || progress >= 0.9 * expected!.Value) { notchSum += sent; pxSum += progress; perNotch = pxSum / notchSum; learned++; }
                else note = "shortNoLearn";
                moved += progress;
                prev.Dispose();
                prev = cur;
                Log?.Invoke(new { ev = "wheelStep", step = steps, delta = dir * unit, count = sent, progress = Math.Round(progress, 1), moved = Math.Round(moved, 1), perNotch = perNotch is double q ? Math.Round(q, 1) : (double?)null, ok = r.Ok, score = Math.Round(r.Score, 3), second = Math.Round(r.Second, 3), note });
                Progress?.Invoke(moved * dir, steps, perNotch);
                if (end) { reason = "end"; break; }
            }
        }
        finally { prev.Dispose(); }
        // 碰到底的那一块，如果只量过一步，那一步可能就被截短了，不拿去给下一块用
        if (!end || learned >= 2) PerNotchHint = perNotch ?? PerNotchHint;
        return new Result(moved * dir, steps, perNotch, SubNotchOk, end, reason, fails, events);
    }

    /// <summary>等画面停稳再量。量出来几乎没动、而离发送还不到 QuietMs 时，再多等一会儿重量一次（慢的、带动画的程序会晚一点才开始动）。</summary>
    private static async Task<(Mat Cur, double Progress, ScrollMeter.Result R)> MeasureStep(Io io, Mat prev, long sentAt, int dir, double? expectedBase, double k, CancellationToken ct)
    {
        var (cur, _) = await Settle(io, sentAt, ct);
        var r = ScrollMeter.Measure(prev, cur, dir, expectedBase * k);
        double progress = r.Ok ? r.ShiftPx * dir / k : 0;
        long waited = io.NowMs() - sentAt;
        if ((!r.Ok || progress < 0.5) && waited < QuietMs)
        {
            await io.Delay((int)(QuietMs - waited), ct);
            cur.Dispose();
            (cur, _) = await Settle(io, sentAt, ct);
            r = ScrollMeter.Measure(prev, cur, dir, expectedBase * k);
            progress = r.Ok ? r.ShiftPx * dir / k : 0;
        }
        return (cur, progress, r);
    }

    /// <summary>连续两帧几乎一样、且离发送至少 MinWaitMs，算停稳；最多等 SettleMaxMs。</summary>
    private static async Task<(Mat Gray, double K)> Settle(Io io, long sentAt, CancellationToken ct)
    {
        Mat? last = null;
        while (true)
        {
            var (g, k) = await io.Grab(ct);
            long since = io.NowMs() - sentAt;
            if (last != null && since >= MinWaitMs && Same(last, g)) { last.Dispose(); return (g, k); }
            last?.Dispose();
            last = g;
            if (since >= SettleMaxMs) return (g, k);
            await io.Delay(40, ct);
        }
    }

    private static bool Same(Mat a, Mat b)
    {
        if (a.Size() != b.Size()) return false;
        using var d = new Mat();
        Cv2.Absdiff(a, b, d);
        return Cv2.Mean(d).Val0 < 0.6;
    }
}
