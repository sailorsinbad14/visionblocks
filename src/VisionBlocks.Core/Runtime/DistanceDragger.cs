using OpenCvSharp;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Vision;

namespace VisionBlocks.Core.Runtime;

/// <summary>
/// 「拖动」积木的闭环（2026-11-23）：按住拖一笔 → 松开、等画面停稳 → 量内容移了多少 → 差多少再补一笔，直到在容差以内。
/// 方向约定和「按距离滚」一样：正数 = 内容往上走（向下翻），这时鼠标往上拖。距离都是基准像素。
///
/// 目标程序的拖动大致是这样：按下后要先移过一个门槛（几到十几像素）才开始拖；开始以后内容跟着鼠标走，
/// 拖 1 px 内容动 g px（直接拖列表 g = 1，拖滚动条之类 g &gt; 1）。所以一笔「光标总共移 S（正 = 往下）、第一段的方向是 s1」时，
/// 内容往上走 -g·(S - s1·T)。第一笔量出 g 和 T（每个热区记住，下一块接着用），之后按剩下的距离算下一笔。
/// 剩下的太少、一笔短得可能过不了门槛时，在同一次按住里先往反方向拖过门槛、再拖回来（两段），净位移照样按上面的式子算。
/// 输入和截图都通过 <see cref="Io"/> 传进来，自测可以换成假的。
/// </summary>
public sealed class DistanceDragger
{
    /// <param name="Drag">拖一笔：光标在 y 方向依次移这几段（基准像素，正 = 往下）。返回这一笔的结果；除了 Ok 都已经松开了。</param>
    /// <param name="Grab">当前这一帧热区的灰度图（新的 Mat，用完由这里释放）和 K = 画面像素 / 基准像素。实现里要先等下一帧（有超时）。</param>
    /// <param name="WaitWhilePaused">暂停时在这里等到继续（不暂停就马上返回）。</param>
    public sealed record Io(
        Func<double[], CancellationToken, Task<SendInputBackend.DragOutcome>> Drag,
        Func<CancellationToken, Task<(Mat Gray, double K)>> Grab,
        Func<int, CancellationToken, Task> Delay,
        Func<long> NowMs,
        Func<CancellationToken, Task> WaitWhilePaused);

    /// <param name="Moved">内容实际走了多少基准像素（带方向，正 = 往上）。</param>
    /// <param name="Reason">done 到了 / end 到底了 / cantMeasure 量不出 / blocked 输入被拦 / userMouse 用户动了鼠标 / aborted 急停 / maxStrokes。</param>
    /// <param name="CarryIn">这一块开始时加上的、上一块还差的距离。</param>
    /// <param name="CarryOut">这一块停下时还差的，留给同一个热区的下一块。</param>
    public sealed record Result(double Moved, int Strokes, double? Gain, double? Threshold, bool End, string Reason, int MeasureFails, double CarryIn, double CarryOut);

    public const int MaxStrokes = 20;
    /// <summary>一段比这个短，就可能过不了目标程序的拖动门槛，改成「先反向再拖回来」的两段。</summary>
    public const double MinSingleLeg = 30;

    /// <summary>上次在这个热区量到的：拖 1 px 内容动几 px。</summary>
    public double? GainHint { get; set; }
    /// <summary>上次在这个热区量到的：每一笔开头被拖动门槛吃掉多少 px。</summary>
    public double? ThresholdHint { get; set; }
    /// <summary>上一块停下时还差的距离（带方向）。只在「到了」时记下；到底、量不出、被拦时清零。</summary>
    public double Carry { get; set; }
    /// <summary>每笔之后：已走（带方向）、笔数。</summary>
    public Action<double, int>? Progress { get; set; }
    public Action<object>? Log { get; set; }

    public async Task<Result> RunAsync(int distancePx, int tolerancePx, int regionHBase, Io io, CancellationToken ct)
    {
        double tol = Math.Max(1, tolerancePx);
        double carryIn = Carry;
        Carry = 0;
        double target = distancePx + carryIn;
        if (Math.Abs(target) <= tol)
        {
            Carry = target;
            return new Result(0, 0, GainHint, ThresholdHint, false, "done", 0, carryIn, Carry);
        }
        await io.WaitWhilePaused(ct);
        var (prev, k) = await DistanceScroller.Settle(io.Grab, io.Delay, io.NowMs, io.NowMs() - DistanceScroller.MinWaitMs, ct);
        double gain = GainHint ?? 1, thr = ThresholdHint ?? 0;
        bool known = GainHint != null;   // 比例知道了：一笔可以拖长，也能按「比预期少很多」判断到底
        bool firstSingle = GainHint == null;
        bool measuredHere = false;   // 这一块自己量到过比例：只有这样才按「比预期少很多」判断到底（上次记下的可能已经过时）
        double moved = 0;
        int strokes = 0, fails = 0, learnedCount = 0;
        bool end = false;
        string reason = "done";
        try
        {
            if (!ScrollMeter.HasTexture(prev, Math.Sign(target)))
            {
                Log?.Invoke(new { ev = "dragStroke", stroke = 0, ok = false, why = "热区里画面太平，量不出移动，没有拖" });
                return new Result(0, 0, GainHint, ThresholdHint, false, "cantMeasure", 0, carryIn, 0);
            }
            while (true)
            {
                double rem = target - moved;
                if (Math.Abs(rem) <= tol) { reason = "done"; break; }
                if (strokes >= MaxStrokes) { reason = "maxStrokes"; break; }
                await io.WaitWhilePaused(ct);
                int dir = Math.Sign(rem);
                // 一笔内容最多走多少：量得出来的范围（带子从尾部走到另一头）的 80%；还不知道比例时先短拖（热区高的 10%），
                // 拖滚动条这类比例大的（拖 1 px 动 5 px）也不会一下拖出量的范围
                double maxContent = 0.8 * ScrollMeter.MaxTrack(prev.Rows, dir) / k;
                if (!known) maxContent = Math.Min(maxContent, 0.1 * regionHBase);
                double maxCursor = 0.8 * regionHBase;
                double want = Math.Min(Math.Abs(rem), maxContent);
                // 单段：光标往内容要去的反方向……内容跟着光标走，所以内容往上（dir = +1）时光标往上（负）
                double single = -dir * (Math.Min(want / gain, maxCursor) + thr);
                double[] legs;
                double predicted;   // 预计内容往上走多少（带方向）
                if (Math.Abs(single) >= MinSingleLeg || Math.Abs(single) > maxCursor * 0.9)
                {
                    single = Math.Clamp(single, -maxCursor, maxCursor);
                    legs = new[] { single };
                    predicted = -gain * (single - Math.Sign(single) * thr);
                }
                else
                {
                    // 两段：先往反方向拖 A（过门槛），再拖回来，净位移 S = -want·dir/g + s1·T，s1 = 第一段方向 = +dir
                    double a = Math.Max(MinSingleLeg, thr + 15);
                    double s = -dir * want / gain + dir * thr;
                    legs = new[] { dir * a, s - dir * a };
                    predicted = -gain * (s - dir * thr);
                }
                var outcome = await io.Drag(legs, ct);
                strokes++;
                var (cur, progress, r) = await Measure(io, prev, predicted, k, ct);
                if (outcome == SendInputBackend.DragOutcome.Paused)
                {
                    // 拖到一半暂停了：已经松开。等继续以后再量一次，按量到的接着拖
                    await io.WaitWhilePaused(ct);
                    cur.Dispose();
                    (cur, progress, r) = await Measure(io, prev, null, k, ct);
                }
                if (!r.Ok)
                {
                    fails++;
                    if (known && outcome == SendInputBackend.DragOutcome.Ok) progress = predicted;   // 比例知道时按预计估，记一次量不出
                    else
                    {
                        cur.Dispose();
                        reason = outcome == SendInputBackend.DragOutcome.Ok || outcome == SendInputBackend.DragOutcome.Paused ? "cantMeasure" : Why(outcome);
                        Log?.Invoke(new { ev = "dragStroke", stroke = strokes, legs = legs.Select(l => Math.Round(l, 1)), outcome = outcome.ToString(), ok = false, why = r.Why });
                        break;
                    }
                }
                string note = "";
                bool isSingle = legs.Length == 1;
                // 到底和学习只看完整拖完的一笔；拖到一半暂停的那一笔走了多少就算多少
                if (outcome == SendInputBackend.DragOutcome.Ok)
                {
                    if (Math.Abs(progress) < 0.5 && isSingle && Math.Abs(legs[0]) >= MinSingleLeg) { end = true; note = "noMove"; }
                    else if (Math.Abs(progress) < 0.5 && !isSingle) { end = true; note = "noMoveTwoLeg"; }
                    else if (measuredHere && Math.Sign(progress) == Math.Sign(predicted) && Math.Abs(progress) < 0.5 * Math.Abs(predicted)) { end = true; note = "short"; }
                    else if (r.Ok && isSingle)
                    {
                        Learn(legs[0], progress, ref gain, ref thr, ref known, ref firstSingle, ref note);
                        if (note is "learned" or "gain" or "threshold") { measuredHere = true; learnedCount++; }
                    }
                    else if (r.Ok)
                    {
                        // 两段的笔只用来修正门槛：净位移 = dir·want + g·dir·(T真 - T估)，所以 T真 = T估 + (走的 - want) / g
                        double t = thr + (progress * dir - want) / gain;
                        if (t > -5 && t < 80) { thr = Math.Clamp(t, 0, 60); note = "thresholdTwoLeg"; learnedCount++; }
                    }
                }
                moved += progress;
                prev.Dispose();
                prev = cur;
                Log?.Invoke(new
                {
                    ev = "dragStroke", stroke = strokes, legs = legs.Select(l => Math.Round(l, 1)), outcome = outcome.ToString(),
                    predicted = Math.Round(predicted, 1), progress = Math.Round(progress, 1), moved = Math.Round(moved, 1),
                    gain = Math.Round(gain, 3), threshold = Math.Round(thr, 1), ok = r.Ok, score = Math.Round(r.Score, 3), note
                });
                Progress?.Invoke(moved, strokes);
                if (outcome is not (SendInputBackend.DragOutcome.Ok or SendInputBackend.DragOutcome.Paused)) { reason = Why(outcome); break; }
                if (end) { reason = "end"; break; }
            }
        }
        finally { prev.Dispose(); }
        // 到底的那一块要是只量过一笔，那一笔可能就被截短了（比例、门槛都会算错），不拿去给下一块用
        if (known && (!end || learnedCount >= 2)) { GainHint = gain; ThresholdHint = thr; }
        // 只有「到了」才把零头带给下一块；到底了、量不出、被拦时位置不可信，清零
        Carry = reason == "done" ? target - moved : 0;
        return new Result(moved, strokes, known ? gain : null, known ? thr : null, end, reason, fails, carryIn, Carry);
    }

    private static string Why(SendInputBackend.DragOutcome o) => o switch
    {
        SendInputBackend.DragOutcome.Blocked => "blocked",
        SendInputBackend.DragOutcome.UserMouse => "userMouse",
        SendInputBackend.DragOutcome.Aborted => "aborted",
        _ => "done"
    };

    /// <summary>
    /// 从一笔单段的结果学「比例 g」和「门槛 T」。第一笔：动得比光标多 30% 以上 = 拖的是滚动条之类，g = 动的 / 拖的、T = 0；
    /// 否则当作直接拖列表，g = 1、T = 拖的 - 动的。之后：长的一笔（≥ 100 px）修正 g，短的修正 T。
    /// </summary>
    private static void Learn(double leg, double progress, ref double gain, ref double thr, ref bool known, ref bool firstSingle, ref string note)
    {
        double s = Math.Abs(leg), m = Math.Abs(progress);
        if (Math.Sign(progress) != -Math.Sign(leg)) { note = "wrongWay"; return; }
        if (firstSingle)
        {
            if (m > 1.3 * s) { gain = m / s; thr = 0; }
            else { gain = 1; thr = Math.Clamp(s - m, 0, 60); }
            known = true;
            firstSingle = false;
            note = "learned";
            return;
        }
        if (s - thr >= 100)
        {
            double g = m / (s - thr);
            if (g > 0.2 && g < 20) gain = 0.5 * (gain + g);
            note = "gain";
        }
        else
        {
            double t = s - m / gain;
            if (t > -5 && t < 80) thr = Math.Clamp(0.5 * (thr + t), 0, 60);
            note = "threshold";
        }
    }

    /// <summary>松开以后等画面停稳再量；按预计的方向找，找不到再按反方向找一次（两段的笔、或者门槛估错时，可能往反方向动了）。</summary>
    private static async Task<(Mat Cur, double Progress, ScrollMeter.Result R)> Measure(Io io, Mat prev, double? predicted, double k, CancellationToken ct)
    {
        var (cur, _) = await DistanceScroller.Settle(io.Grab, io.Delay, io.NowMs, io.NowMs(), ct);
        int dir = predicted is double p && Math.Abs(p) > 0.5 ? Math.Sign(p) : 1;
        var r = ScrollMeter.Measure(prev, cur, dir, predicted is double e ? Math.Abs(e) * k : null);
        if (!r.Ok || Math.Abs(r.ShiftPx) < 0.5)
        {
            var r2 = ScrollMeter.Measure(prev, cur, -dir, null);
            if (r2.Ok && Math.Abs(r2.ShiftPx) >= 0.5) r = r2;
        }
        return (cur, r.Ok ? r.ShiftPx / k : 0, r);
    }

    /// <summary>
    /// 一笔在热区里的光标路径（基准坐标）：按下点、每一步的 y 和「移之前先等多少毫秒」。
    /// 起点按这一笔的总走向放在热区下部（往上拖）或上部（往下拖），整笔都留在热区高度的 10%–90% 之间；
    /// 每段先慢后快再慢（smoothstep），约 16ms 一步，横向不动（开了移动轨迹时有 ±0.5 像素抖动）。
    /// </summary>
    public static (double X0, double Y0, List<(double X, double Y, int DelayMs)> Points) PathFor(double[] legs, double regionX, double regionY, double regionH, double x, int speedPxPerSec, Humanizer? h)
    {
        var offs = new List<double> { 0 };
        foreach (var l in legs) offs.Add(offs[^1] + l);
        double lo = regionY + 0.1 * regionH - offs.Min(), hi = regionY + 0.9 * regionH - offs.Max();
        double y0 = offs[^1] < 0 ? hi : lo;
        if (lo > hi) y0 = (lo + hi) / 2;
        y0 = Math.Clamp(y0, regionY + 2, regionY + regionH - 2);
        int speed = Math.Max(20, h?.DragSpeed(speedPxPerSec) ?? speedPxPerSec);
        var pts = new List<(double, double, int)>();
        double y = y0;
        for (int li = 0; li < legs.Length; li++)
        {
            double leg = legs[li];
            double secs = Math.Clamp(Math.Abs(leg) / speed, 0.06, 2.0);
            int n = Math.Max(3, (int)Math.Round(secs * 1000 / 16));
            int step = (int)Math.Round(secs * 1000 / n);
            for (int i = 1; i <= n; i++)
            {
                double u = i / (double)n, t = u * u * (3 - 2 * u);
                double jx = i < n ? h?.DragJitter() ?? 0 : 0;
                pts.Add((x + jx, y + leg * t, i == 1 && li > 0 ? step + 40 : step));
            }
            y += leg;
        }
        return (x, y0, pts);
    }
}
