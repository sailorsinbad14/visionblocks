using VisionBlocks.Core.Model;
using VisionBlocks.Core.Vision;

namespace VisionBlocks.Core.Runtime;

/// <summary>判定规则只写在这里，运行器和编辑器的「试一下」共用，保证试出来的结果和真跑时一致。</summary>
public static class ConditionRules
{
    public static bool ImageHit(MatchResult m, ImageCondition c) =>
        m.Score >= c.Threshold && (c.MinContrast <= 0 || m.Contrast >= c.MinContrast);

    public static bool ColorHit(double ratio, ColorCondition c) => c.Op == "<" ? ratio < c.Ratio : ratio > c.Ratio;

    public static bool TextHit(string normalized, TextCondition c) =>
        normalized.Contains(OcrReader.Normalize(c.Contains), StringComparison.OrdinalIgnoreCase);
}

/// <summary>编辑器「试一下」：在冻结帧上求一个条件的值，并给出匹配框，方便画在画布上。</summary>
public sealed class ConditionTester : IDisposable
{
    private readonly Dictionary<string, (string File, TemplateMatcher M)> _cache = new();
    private OcrReader? _ocr;

    public sealed record Result(bool Hit, string Detail, RegionDef? Roi, RegionDef? Box);

    /// <param name="currentCell">条件用的是「当前格」时，拿哪一格来试（编辑器传所在网格的第一格）。</param>
    public async Task<Result> TestAsync(VbProgram p, string dir, Condition c, NormFrame f, RegionDef? currentCell = null)
    {
        bool TryRegion(string name, out RegionDef r)
        {
            if (name == VbProgram.CurrentCell && currentCell != null) { r = currentCell; return true; }
            return p.Regions.TryGetValue(name, out r!);
        }
        bool hit;
        string detail;
        RegionDef? roi = null, box = null;
        switch (c)
        {
            case ImageCondition ic:
            {
                if (!p.Templates.TryGetValue(ic.Template, out var td)) return new Result(false, $"模板「{ic.Template}」不存在", null, null);
                if (!TryRegion(ic.Region, out roi)) return new Result(false, $"热区「{ic.Region}」不存在", null, null);
                var m = Matcher(ic.Template, Path.Combine(dir, td.File)).Match(f, roi);
                hit = ConditionRules.ImageHit(m, ic);
                box = m.Box;
                detail = $"得分 {m.Score:0.000}（阈值 {ic.Threshold:0.00}），对比度 {m.Contrast:0.00}（下限 {ic.MinContrast:0.0}），用时 {m.Ms:0.0}ms";
                break;
            }
            case ColorCondition cc:
            {
                if (!p.Colors.TryGetValue(cc.Color, out var cd)) return new Result(false, $"颜色「{cc.Color}」不存在", null, null);
                if (!TryRegion(cc.Region, out roi)) return new Result(false, $"热区「{cc.Region}」不存在", null, null);
                var (ratio, ms) = ColorRatio.Measure(f, roi, cd);
                hit = ConditionRules.ColorHit(ratio, cc);
                detail = $"占比 {ratio:P1}（条件 {cc.Op} {cc.Ratio:P1}），用时 {ms:0.0}ms";
                break;
            }
            case TextCondition tc:
            {
                if (!TryRegion(tc.Region, out roi)) return new Result(false, $"热区「{tc.Region}」不存在", null, null);
                _ocr ??= new OcrReader();
                var (raw, norm, ms) = await _ocr.ReadAsync(f, roi);
                hit = ConditionRules.TextHit(norm, tc);
                detail = $"识别到「{norm}」（原文「{raw}」），用时 {ms:0.0}ms";
                break;
            }
            default: return new Result(false, "不支持的条件", null, null);
        }
        if (c.Negate) hit = !hit;
        return new Result(hit, (hit ? "✓ 成立：" : "✗ 不成立：") + detail, roi, box);
    }

    private TemplateMatcher Matcher(string name, string file)
    {
        if (!_cache.TryGetValue(name, out var e) || e.File != file)
        {
            e.M?.Dispose();
            e = (file, TemplateMatcher.Load(file));
            _cache[name] = e;
        }
        return e.M;
    }

    /// <summary>编辑器「读文字」积木的「试读」：和运行时一样的 OCR（scale、padPx 给「读空时重读」用）。</summary>
    public Task<(string Raw, string Normalized, double Ms)> ReadAsync(NormFrame f, RegionDef roi, double scale = 0, int padPx = 0)
    {
        _ocr ??= new OcrReader();
        return _ocr.ReadAsync(f, roi, default, scale, padPx);
    }

    /// <summary>编辑器「认图案」积木的「试一下」（2026-11-25）：和运行时同一套判定。</summary>
    public PatternClassifier.Result Classify(VbProgram p, string dir, ClassifyBlock cl, NormFrame f, RegionDef roi)
    {
        var list = new List<(string, string, TemplateMatcher)>();
        foreach (var n in cl.Templates)
            if (p.Templates.TryGetValue(n, out var td)) list.Add((n, PatternClassifier.ValueOf(p, n), Matcher(n, Path.Combine(dir, td.File))));
        return PatternClassifier.Classify(f, roi, list, cl.Threshold, cl.NoneText);
    }

    public void Dispose()
    {
        foreach (var e in _cache.Values) e.M.Dispose();
    }
}
