using VisionBlocks.Core.Model;

namespace VisionBlocks.Core.Vision;

/// <summary>
/// 「认图案」积木的判定（2026-11-25）：在热区里把几张模板逐个找一遍，取分最高的那张。运行器和编辑器的「试一下」共用。
/// 不能「谁先过门槛算谁」：只差一个数字的两张角标模板会互相拿到 0.8 多（离线合成实验：认对 0.90–0.92，认错 0.82–0.84）。
/// 匹配只看灰度，只差底色的两张图分数几乎一样，所以第一、二名分差不到 <see cref="CloseGap"/> 时再比匹配处和模板的平均颜色：
/// 颜色差得明显（差距至少 <see cref="ColorDecides"/>）就按颜色定，否则照写第一名并标「分不太开」。
/// </summary>
public static class PatternClassifier
{
    public const double CloseGap = 0.03, ColorDecides = 25, MidwayFloor = 0.5;

    public sealed record Candidate(string Template, string Value, double Score, double ColorDist, RegionDef? Box);

    /// <summary>认出这张模板时写进字段的值：模板的标签，没有标签用模板名。</summary>
    public static string ValueOf(VbProgram p, string template) =>
        p.Templates.TryGetValue(template, out var t) && !string.IsNullOrWhiteSpace(t.Label) ? t.Label : template;

    /// <param name="Value">写进字段的值：认出来的那张模板的名字，或者「都不像时写」的值。</param>
    /// <param name="Winner">认出来的那张；都没过门槛时为 null。</param>
    /// <param name="Close">认出来了，但第一、二名分数差不到 CloseGap，颜色也没分开。</param>
    /// <param name="ByColor">分数差不多，按平均颜色定的。</param>
    /// <param name="Midway">都没过门槛，但最高分有 0.5 以上：有点像，多半是还没裁模板的新图案。</param>
    public sealed record Result(string Value, Candidate? Winner, double Best, double Second, bool Close, bool ByColor, bool Midway, IReadOnlyList<Candidate> All);

    public static Result Classify(NormFrame f, RegionDef roi, IReadOnlyList<(string Name, string Value, TemplateMatcher M)> templates, double threshold, string noneText)
    {
        var all = new List<Candidate>();
        foreach (var (name, value, m) in templates)
        {
            var r = m.Match(f, roi);
            all.Add(new Candidate(name, value, r.Score, r.Box != null ? m.ColorDistance(f, r.Box) : double.NaN, r.Box));
        }
        return Decide(all, threshold, noneText);
    }

    /// <summary>按分数和颜色定输赢（单独拿出来，自测可以直接喂分数）。</summary>
    public static Result Decide(IReadOnlyList<Candidate> all, double threshold, string noneText)
    {
        var sorted = all.OrderByDescending(c => c.Score).ToList();
        if (sorted.Count == 0) return new Result(noneText, null, 0, 0, false, false, false, all);
        var top = sorted[0];
        double second = sorted.Count > 1 ? sorted[1].Score : 0;
        var near = sorted.Where(c => c.Score >= top.Score - CloseGap).ToList();
        var win = top;
        bool close = false, byColor = false;
        if (near.Count > 1)
        {
            var byC = near.OrderBy(c => double.IsNaN(c.ColorDist) ? double.MaxValue : c.ColorDist).ToList();
            if (!double.IsNaN(byC[0].ColorDist) && !double.IsNaN(byC[1].ColorDist) && byC[1].ColorDist - byC[0].ColorDist >= ColorDecides)
            {
                win = byC[0];
                byColor = true;
            }
            else close = true;
        }
        // 按颜色换了人时用第一名的分数过门槛：两张只差底色，像不像看的是图案本身
        if ((byColor ? top.Score : win.Score) < threshold)
            return new Result(noneText, null, top.Score, second, false, false, top.Score >= MidwayFloor, all);
        return new Result(win.Value, win, top.Score, second, close, byColor, false, all);
    }
}
