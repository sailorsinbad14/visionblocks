using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenCvSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using VisionBlocks.Core.Model;

namespace VisionBlocks.Core.Vision;

public readonly record struct MatchResult(double Score, RegionDef? Box, double Contrast, double Ms);

/// <summary>ROI 内灰度 TM_CCOEFF_NORMED 模板匹配。</summary>
public sealed class TemplateMatcher : IDisposable
{
    private readonly Mat _tmplGray;             // 基准分辨率下的灰度模板
    private readonly double _tmplStd;
    private readonly Dictionary<double, Mat> _scaled = new();
    /// <summary>模板的平均颜色（B、G、R）；灰度模板没有颜色，为 null。</summary>
    private readonly Scalar? _meanBgr;

    public TemplateMatcher(Mat templateBgrOrBgra)
    {
        _tmplGray = templateBgrOrBgra.Channels() switch
        {
            4 => templateBgrOrBgra.CvtColor(ColorConversionCodes.BGRA2GRAY),
            3 => templateBgrOrBgra.CvtColor(ColorConversionCodes.BGR2GRAY),
            _ => templateBgrOrBgra.Clone()
        };
        Cv2.MeanStdDev(_tmplGray, out _, out Scalar sd);
        _tmplStd = Math.Max(1e-6, sd.Val0);
        if (templateBgrOrBgra.Channels() >= 3) _meanBgr = Cv2.Mean(templateBgrOrBgra);
    }

    /// <summary>
    /// 匹配处（基准坐标）和模板的平均颜色差多少（B、G、R 三个差的平方和开方，0–441）。灰度模板返回 NaN。
    /// 「认图案」在两张模板灰度分数差不多时用它分开只差底色的图案（2026-11-25）：灰度匹配不看颜色。
    /// </summary>
    public double ColorDistance(NormFrame f, RegionDef box)
    {
        if (_meanBgr is not Scalar t) return double.NaN;
        var r = f.ToPixels(box);
        if (r.Width <= 0 || r.Height <= 0) return double.NaN;
        using var patch = new Mat(f.Bgr, r);
        var m = Cv2.Mean(patch);
        return Math.Sqrt(Math.Pow(m.Val0 - t.Val0, 2) + Math.Pow(m.Val1 - t.Val1, 2) + Math.Pow(m.Val2 - t.Val2, 2));
    }

    public static TemplateMatcher Load(string pngPath)
    {
        using var m = Cv2.ImRead(pngPath, ImreadModes.Unchanged);
        if (m.Empty()) throw new FileNotFoundException($"模板读不出来：{pngPath}");
        return new TemplateMatcher(m);
    }

    /// <summary>画面比基准小（K&lt;1）时只缩模板，不放大截图。</summary>
    private Mat TemplateFor(double k)
    {
        if (k > 0.9995) return _tmplGray;
        double key = Math.Round(k, 4);
        if (_scaled.TryGetValue(key, out var m)) return m;
        m = _tmplGray.Resize(new Size(Math.Max(4, (int)Math.Round(_tmplGray.Width * k)), Math.Max(4, (int)Math.Round(_tmplGray.Height * k))), 0, 0, InterpolationFlags.Area);
        _scaled[key] = m;
        return m;
    }

    public MatchResult Match(NormFrame f, RegionDef roi)
    {
        long t0 = Stopwatch.GetTimestamp();
        var r = f.ToPixels(roi);
        var tmpl = TemplateFor(f.K);
        if (r.Width < tmpl.Width || r.Height < tmpl.Height) return new MatchResult(0, null, 0, 0);
        using var area = new Mat(f.Gray, r);
        using var res = new Mat();
        Cv2.MatchTemplate(area, tmpl, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double max, out _, out Point loc);
        var px = new Rect(r.X + loc.X, r.Y + loc.Y, tmpl.Width, tmpl.Height);
        using var patch = new Mat(f.Gray, px);
        Cv2.MeanStdDev(patch, out _, out Scalar sd);
        double contrast = sd.Val0 / (_tmplStd * (f.K > 0.9995 ? 1 : 1));
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return new MatchResult(double.IsFinite(max) ? max : 0, f.ToBase(px), contrast, ms);
    }

    public void Dispose()
    {
        _tmplGray.Dispose();
        foreach (var m in _scaled.Values) m.Dispose();
    }
}

/// <summary>颜色占比：热区内落在 HSV 范围里的像素比例（用占比，不用单点）。</summary>
public static class ColorRatio
{
    public static (double Ratio, double Ms) Measure(NormFrame f, RegionDef roi, ColorDef c)
    {
        long t0 = Stopwatch.GetTimestamp();
        var r = f.ToPixels(roi);
        using var area = new Mat(f.Hsv, r);
        using var mask = InRange(area, c);
        double ratio = Cv2.CountNonZero(mask) / (double)(r.Width * r.Height);
        return (ratio, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }

    /// <summary>HSV 图里落在范围内的像素（255）。编辑器的调色盘用同一个函数在画布上标洋红，和运行时的判断一致。</summary>
    public static Mat InRange(Mat hsv, ColorDef c)
    {
        var mask = new Mat();
        if (c.Lower[0] <= c.Upper[0])
        {
            Cv2.InRange(hsv, new Scalar(c.Lower[0], c.Lower[1], c.Lower[2]), new Scalar(c.Upper[0], c.Upper[1], c.Upper[2]), mask);
        }
        else
        {
            // 色相跨过 0（红色）：拆成 [Lower.H, 179] ∪ [0, Upper.H]
            using var m1 = new Mat();
            using var m2 = new Mat();
            Cv2.InRange(hsv, new Scalar(c.Lower[0], c.Lower[1], c.Lower[2]), new Scalar(179, c.Upper[1], c.Upper[2]), m1);
            Cv2.InRange(hsv, new Scalar(0, c.Lower[1], c.Lower[2]), new Scalar(c.Upper[0], c.Upper[1], c.Upper[2]), m2);
            Cv2.BitwiseOr(m1, m2, mask);
        }
        return mask;
    }

    /// <summary>编辑器「吸色」：以取样点的 HSV 为中心生成一个范围。</summary>
    public static ColorDef FromSample(NormFrame f, int baseX, int baseY, string label)
    {
        int x = Math.Clamp((int)Math.Round(baseX * f.K), 0, f.Bgr.Width - 1);
        int y = Math.Clamp((int)Math.Round(baseY * f.K), 0, f.Bgr.Height - 1);
        // 取 5×5 邻域的中位数，避免抗锯齿边缘
        var roi = new Rect(Math.Max(0, x - 2), Math.Max(0, y - 2), 5, 5) & new Rect(0, 0, f.Bgr.Width, f.Bgr.Height);
        using var patch = new Mat(f.Hsv, roi);
        var hs = new List<int>(); var ss = new List<int>(); var vs = new List<int>();
        for (int yy = 0; yy < patch.Rows; yy++)
            for (int xx = 0; xx < patch.Cols; xx++)
            {
                var v = patch.At<Vec3b>(yy, xx);
                hs.Add(v.Item0); ss.Add(v.Item1); vs.Add(v.Item2);
            }
        int Med(List<int> l) { l.Sort(); return l[l.Count / 2]; }
        int h = Med(hs), s = Med(ss), val = Med(vs);
        int hl = h - 10, hu = h + 10;
        if (hl < 0) hl += 180;
        if (hu > 179) hu -= 180;
        return new ColorDef
        {
            Label = label,
            Lower = new[] { hl, Math.Max(0, s - 70), Math.Max(0, val - 70) },
            Upper = new[] { hu, Math.Min(255, s + 70), Math.Min(255, val + 70) }
        };
    }
}

/// <summary>
/// Windows 自带 OCR（不用另装模型）。文字位置已知，只做识别。
/// 同一个引擎实例串行调用（同一个识别引擎不并发调用）。
/// </summary>
public sealed class OcrReader
{
    private readonly OcrEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Language => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>这台电脑装没装某种 OCR 语言（例如 zh-Hans）。没装中文时构造函数会退回用户语言，中文读出来是乱的。</summary>
    public static bool HasLanguage(string prefix) =>
        OcrEngine.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public OcrReader(string languageTag = "zh-Hans-CN")
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(languageTag))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? throw new InvalidOperationException("系统没有可用的 OCR 语言包");
    }

    /// <param name="scale">放大倍数；0 = 平时的做法（区域高不到 100 像素放大 2 倍，否则不放大）。</param>
    /// <param name="padPx">放大以后四周补几像素的边（复制最外一圈的颜色），给贴着字的框留点白。</param>
    public async Task<(string Raw, string Normalized, double Ms)> ReadAsync(NormFrame f, RegionDef roi, CancellationToken ct = default, double scale = 0, int padPx = 0)
    {
        long t0 = Stopwatch.GetTimestamp();
        var r = f.ToPixels(roi);
        using var area = new Mat(f.Bgr, r);
        // 字太小时放大到约 64px 高再识别
        double s = scale > 0 ? scale : area.Height < 100 ? 2 : 1;
        // 指定了倍数（重读）时不超过 OCR 能收的最大边长，大热区放大 4 倍会超
        if (scale > 0) s = Math.Max(1, Math.Min(s, (OcrEngine.MaxImageDimension - 2.0 * padPx) / Math.Max(area.Width, area.Height)));
        using var resized = s != 1 ? area.Resize(new Size(0, 0), s, s, InterpolationFlags.Cubic) : area.Clone();
        using var scaled = padPx > 0 ? resized.CopyMakeBorder(padPx, padPx, padPx, padPx, BorderTypes.Replicate) : resized.Clone();
        using var bgra = scaled.CvtColor(ColorConversionCodes.BGR2BGRA);
        var bytes = new byte[bgra.Width * bgra.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
        using var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
        sb.CopyFromBuffer(bytes.AsBuffer());
        await _gate.WaitAsync(ct);
        try
        {
            var result = await _engine.RecognizeAsync(sb);
            string raw = result.Text ?? "";
            return (raw, Normalize(raw), Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Windows OCR 识别中文时常在字与字之间插空格，比较前去掉所有空白，并把全角符号转半角。</summary>
    public static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;
            char c = ch;
            if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// 解码靶子左上角的「帧计数条」（24 个黑白方块）。这是测量用的仪表，不是游戏状态：
/// 只用它把 demo 的记录和靶子的记录按帧号对齐，算截图延迟。
/// </summary>
public static class FrameCounter
{
    public const int Bits = 24;
    public static long Decode(Mat bgraOrBgr, double k)
    {
        if (bgraOrBgr.Empty()) return -1;
        int ch = bgraOrBgr.Channels();
        long id = 0;
        for (int i = 0; i < Bits; i++)
        {
            int x = (int)Math.Round((16 + i * 18 + 8) * k), y = (int)Math.Round((16 + 8) * k);
            if (x >= bgraOrBgr.Width || y >= bgraOrBgr.Height) return -1;
            int lum;
            if (ch == 4) { var v = bgraOrBgr.At<Vec4b>(y, x); lum = (v.Item0 + v.Item1 + v.Item2) / 3; }
            else { var v = bgraOrBgr.At<Vec3b>(y, x); lum = (v.Item0 + v.Item1 + v.Item2) / 3; }
            if (lum is > 70 and < 185) return -1;      // 既不黑也不白：不是计数条，或正在过渡
            id = (id << 1) | (lum >= 185 ? 1L : 0L);
        }
        return id;
    }
}
