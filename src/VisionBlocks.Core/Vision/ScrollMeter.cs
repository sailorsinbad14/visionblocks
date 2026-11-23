using OpenCvSharp;

namespace VisionBlocks.Core.Vision;

/// <summary>
/// 量两帧之间画面在热区里竖直平移了多少（「按距离滚」用，2026-11-22）。
/// 做法：从前一帧切一条带子（左右各让出 15%、离上下边 8%，避开热区边上不动的边框、页脚、滚动条），
/// 放在内容移动方向的尾部（向下滚时内容往上走，带子放底部），到后一帧里竖向找它。
/// 2026-11-22 在练习靶子上实测：测得的平移和靶子自己记录的滚动量相差 ≤ 0.3 像素。
/// </summary>
public static class ScrollMeter
{
    /// <param name="Ok">量得出来（得分够高、带子不是一片平的）。</param>
    /// <param name="ShiftPx">内容往上走了多少画面像素（向上滚时为负）。</param>
    public readonly record struct Result(bool Ok, double ShiftPx, double Score, double Second, string Why);

    /// <summary>带子的位置和高度（画面像素）。dir &gt; 0：向下滚，内容往上走。</summary>
    public static (int Y, int H) Band(int regionH, int dir)
    {
        // 带子要高一点，盖住格子里的内容；太矮时只截到格子上沿的空白和边框，格子一样时会认错周期
        int bh = Math.Clamp((int)(regionH * 0.35), 12, 320);
        int inset = Math.Max(4, (int)(regionH * 0.08));
        return (dir > 0 ? regionH - inset - bh : inset, bh);
    }

    /// <summary>一步最多能跟住多少画面像素的平移：带子从尾部走到另一头为止。</summary>
    public static int MaxTrack(int regionH, int dir)
    {
        var (y, h) = Band(regionH, dir);
        return dir > 0 ? y : regionH - y - h;
    }

    /// <summary>带子里的画面够不够量（灰度标准差）。一片平的画面量不出移动，滚之前先看一眼。</summary>
    public static bool HasTexture(Mat region, int dir)
    {
        int w = region.Width, h = region.Height;
        if (w < 40 || h < 40) return false;
        int bx = (int)(w * 0.15);
        var (by, bh) = Band(h, dir);
        using var band = new Mat(region, new Rect(bx, by, w - 2 * bx, bh));
        Cv2.MeanStdDev(band, out _, out var sd);
        return sd.Val0 >= 4;
    }

    /// <param name="prev">前一帧这个热区的灰度图（画面像素）。</param>
    /// <param name="cur">后一帧同一个热区的灰度图，尺寸要和 prev 一样。</param>
    /// <param name="dir">+1 向下滚（内容往上走），-1 向上滚。只在这个方向上找，允许 3 像素的反向误差。</param>
    /// <param name="expectedPx">预期平移的大小（画面像素，正数）。格子长得一样时，几个候选得分只差一点，取最接近预期的那个。</param>
    public static Result Measure(Mat prev, Mat cur, int dir, double? expectedPx = null)
    {
        int w = prev.Width, h = prev.Height;
        if (w < 40 || h < 40 || cur.Width != w || cur.Height != h) return new(false, 0, 0, 0, "热区太小或两帧尺寸不同");
        int bx = (int)(w * 0.15), bw = w - 2 * bx;
        var (by, bh) = Band(h, dir);
        using var band = new Mat(prev, new Rect(bx, by, bw, bh));
        Cv2.MeanStdDev(band, out _, out var sd);
        if (sd.Val0 < 4) return new(false, 0, 0, 0, "热区里画面太平，量不出移动");
        // 只允许左右偏 4 像素
        int sx0 = Math.Max(0, bx - 4), sx1 = Math.Min(w, bx + bw + 4);
        using var area = new Mat(cur, new Rect(sx0, 0, sx1 - sx0, h));
        using var res = new Mat();
        Cv2.MatchTemplate(area, band, res, TemplateMatchModes.CCoeffNormed);
        int rows = res.Rows, cols = res.Cols;
        var score = new double[rows];
        for (int y = 0; y < rows; y++)
        {
            double m = -1;
            for (int x = 0; x < cols; x++) m = Math.Max(m, res.At<float>(y, x));
            score[y] = m;
        }
        // 平移 = by - y（正 = 内容往上走）
        bool Allowed(int y) => dir > 0 ? by - y >= -3 : by - y <= 3;
        int best = -1;
        for (int y = 0; y < rows; y++)
            if (Allowed(y) && (best < 0 || score[y] > score[best])) best = y;
        if (best < 0) return new(false, 0, 0, 0, "没有可找的范围");
        int chosen = best;
        if (expectedPx is double e && e > 0)
        {
            double pickDist = double.MaxValue;
            for (int y = 0; y < rows; y++)
            {
                if (!Allowed(y) || score[y] < score[best] - 0.02) continue;
                bool localMax = (y == 0 || score[y] >= score[y - 1]) && (y == rows - 1 || score[y] >= score[y + 1]);
                if (!localMax) continue;
                double d = Math.Abs(Math.Abs(by - y) - e);
                if (d < pickDist) { pickDist = d; chosen = y; }
            }
        }
        double second = -1;
        int excl = Math.Max(8, bh / 4);
        for (int y = 0; y < rows; y++)
            if (Allowed(y) && Math.Abs(y - chosen) > excl) second = Math.Max(second, score[y]);
        // 抛物线插值到亚像素
        double sub = 0;
        if (chosen > 0 && chosen < rows - 1)
        {
            double a = score[chosen - 1], b = score[chosen], c = score[chosen + 1], den = a - 2 * b + c;
            if (Math.Abs(den) > 1e-9) sub = Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5);
        }
        double shift = by - (chosen + sub);
        if (score[chosen] < 0.8) return new(false, shift, score[chosen], second, $"匹配得分太低（{score[chosen]:0.00}）");
        // 没有预期可参照时，别处还有几乎一样好的位置：画面在重复（格子一模一样、正好滚了整行），分不清，宁可说量不出
        if (expectedPx == null && second >= score[chosen] - 0.005)
            return new(false, shift, score[chosen], second, $"画面重复，分不清移动了多少（{score[chosen]:0.000} / {second:0.000}）");
        return new(true, shift, score[chosen], second, "");
    }
}
