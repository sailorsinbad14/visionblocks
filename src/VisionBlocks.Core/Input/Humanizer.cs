using VisionBlocks.Core.Win32;

namespace VisionBlocks.Core.Input;

/// <summary>
/// 鼠标操作随机化。用途：测试自己做的游戏里的人机检测能不能识别出自动操作。
/// 默认不启用；编辑器设置里打开前要确认。急停、前台检查照常生效（移动的每一步都过 SendInputBackend 的闸）。
/// 同一个种子 + 同一串操作 → 同样的随机值；种子写进 bot.jsonl 的 runStart，可以复现。
///
/// 位置和时机（Timing）：
/// - 热区里取随机点：离中心最多为宽、高的 30%，并且离热区边至少 4 个基准像素（太小的热区就只在中心）；
///   定点没有范围，最多偏 ±3 个基准像素；
/// - 按住时长 ×0.7–1.3（夹在 10–2000ms）；点击、滚轮前多等 30–120ms；「等待」积木 ×0.8–1.2。
/// 移动轨迹（Path）：
/// - 从当前鼠标位置沿二次贝塞尔曲线移过去，控制点在连线的法线方向偏开 10%–25% 的距离（方向随机）；
/// - 总时长按距离 120–600ms 再 ×0.75–1.25（最长约 750ms），先快后慢（easeOutCubic），每步约 16ms、步长再 ±30%，路径上加 ±0.6 像素的抖动；
/// - 可选过冲（Overshoot，默认关）：先冲过终点 2–6 像素，再用 40–90ms 回正；
/// - 所有点夹在绑定窗口的客户区里，终点就是要点的位置。
/// </summary>
public sealed class Humanizer
{
    public bool Timing { get; }
    public bool Path { get; }
    public bool Overshoot { get; }
    public int Seed { get; }
    private readonly Random _rng;

    public Humanizer(bool timing, bool path, int seed, bool overshoot = false)
    {
        Timing = timing; Path = path; Overshoot = overshoot; Seed = seed;
        _rng = new Random(seed);
    }

    private double Range(double a, double b) => a + (b - a) * _rng.NextDouble();

    /// <summary>热区（基准坐标）里的点击位置。不随机时就是中心，和以前一样。</summary>
    public (int X, int Y) PointIn(int x, int y, int w, int h)
    {
        int cx = x + w / 2, cy = y + h / 2;
        if (!Timing) return (cx, cy);
        int dx = (int)Math.Max(0, Math.Min(w * 0.3, w / 2.0 - 4));
        int dy = (int)Math.Max(0, Math.Min(h * 0.3, h / 2.0 - 4));
        return (cx + (int)Math.Round(Range(-dx, dx)), cy + (int)Math.Round(Range(-dy, dy)));
    }

    /// <summary>定点（基准坐标）附近的点击位置。</summary>
    public (int X, int Y) PointNear(int x, int y) => Timing ? (x + (int)Math.Round(Range(-3, 3)), y + (int)Math.Round(Range(-3, 3))) : (x, y);

    public int Hold(int ms) => Timing ? Math.Clamp((int)Math.Round(ms * Range(0.7, 1.3)), 10, 2000) : ms;
    public int BeforeAction() => Timing ? (int)Math.Round(Range(30, 120)) : 0;
    public int Wait(int ms) => Timing ? Math.Max(0, (int)Math.Round(ms * Range(0.8, 1.2))) : ms;

    /// <summary>
    /// 从 from 移到 to 的轨迹（屏幕物理像素），每个点带「移到这里之前先等多少毫秒」。最后一个点一定是 to（to 本身也夹在 clamp 里）。
    /// </summary>
    public List<(int X, int Y, int DelayMs)> PathPoints((int X, int Y) from, (int X, int Y) to, WindowInfo.PxRect clamp)
    {
        int minX = clamp.X + 1, maxX = clamp.X + Math.Max(1, clamp.W - 2), minY = clamp.Y + 1, maxY = clamp.Y + Math.Max(1, clamp.H - 2);
        (int, int) Clamp(double x, double y) => ((int)Math.Round(Math.Clamp(x, minX, maxX)), (int)Math.Round(Math.Clamp(y, minY, maxY)));
        var end = Clamp(to.X, to.Y);
        var start = Clamp(from.X, from.Y);
        double ddx = end.Item1 - start.Item1, ddy = end.Item2 - start.Item2, d = Math.Sqrt(ddx * ddx + ddy * ddy);
        var pts = new List<(int, int, int)>();
        if (d < 3) { pts.Add((end.Item1, end.Item2, 0)); return pts; }

        double total = Math.Clamp(120 + d * 0.35, 120, 600) * Range(0.75, 1.25);
        // 每步约 16ms：Windows 的计时器精度约 15.6ms，Task.Delay(8) 实际要等 15ms 左右，步数按 8ms 算会让移动慢一倍（2026-11-22 实测最长 1.2s）
        int n = Math.Clamp((int)Math.Round(total / 16), 6, 40);
        double side = _rng.Next(2) == 0 ? -1 : 1, bend = d * Range(0.10, 0.25) * side;
        double cx = (start.Item1 + end.Item1) / 2.0 - ddy / d * bend, cy = (start.Item2 + end.Item2) / 2.0 + ddx / d * bend;
        // 过冲：曲线先瞄准终点外 2–6 像素，最后一步再回到终点
        double ox = 0, oy = 0;
        if (Overshoot) { double o = Range(2, 6); ox = ddx / d * o; oy = ddy / d * o; }
        double tx = end.Item1 + ox, ty = end.Item2 + oy;
        double step = total / n;
        for (int i = 1; i <= n; i++)
        {
            double u = i / (double)n, t = 1 - Math.Pow(1 - u, 3);
            double x = (1 - t) * (1 - t) * start.Item1 + 2 * (1 - t) * t * cx + t * t * tx;
            double y = (1 - t) * (1 - t) * start.Item2 + 2 * (1 - t) * t * cy + t * t * ty;
            if (i < n) { x += Range(-0.6, 0.6); y += Range(-0.6, 0.6); }
            var (px, py) = Clamp(x, y);
            pts.Add((px, py, (int)Math.Round(step * Range(0.7, 1.3))));
        }
        if (Overshoot) pts.Add((end.Item1, end.Item2, (int)Math.Round(Range(40, 90))));
        else pts[^1] = (end.Item1, end.Item2, pts[^1].Item3);
        return pts;
    }
}
