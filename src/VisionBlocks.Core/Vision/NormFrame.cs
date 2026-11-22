using OpenCvSharp;
using VisionBlocks.Core.Model;

namespace VisionBlocks.Core.Vision;

/// <summary>
/// 归一化后的画面。规则：客户区比基准宽就缩到基准宽（只缩不放）；
/// 比基准窄就保持原样，改为把模板和 ROI 按比例缩小（K &lt; 1）。
/// 所有对外的坐标都是「客户区基准坐标」（默认 1920×1080）。
/// </summary>
public sealed class NormFrame : IDisposable
{
    public Mat Bgr { get; }
    /// <summary>本画面像素 / 基准像素。=1 表示画面和基准一样大。</summary>
    public double K { get; }
    public int BaseW { get; }
    public long Seq { get; init; }
    public long ArrivalQpc { get; init; }
    private Mat? _gray, _hsv;

    public NormFrame(Mat bgr, double k, int baseW) { Bgr = bgr; K = k; BaseW = baseW; }

    public Mat Gray => _gray ??= Bgr.CvtColor(ColorConversionCodes.BGR2GRAY);
    public Mat Hsv => _hsv ??= Bgr.CvtColor(ColorConversionCodes.BGR2HSV);

    public static NormFrame FromBgra(Mat bgraClient, int baseW = 1920, long seq = 0, long arrivalQpc = 0)
    {
        var bgr = bgraClient.CvtColor(ColorConversionCodes.BGRA2BGR);
        double k = bgr.Width / (double)baseW;
        if (k > 1.0005)
        {
            var small = bgr.Resize(new Size(baseW, (int)Math.Round(bgr.Height / k)), 0, 0, InterpolationFlags.Area);
            bgr.Dispose();
            return new NormFrame(small, 1.0, baseW) { Seq = seq, ArrivalQpc = arrivalQpc };
        }
        return new NormFrame(bgr, k, baseW) { Seq = seq, ArrivalQpc = arrivalQpc };
    }

    /// <summary>基准坐标矩形 → 本画面像素矩形（裁到画面内）。</summary>
    public Rect ToPixels(RegionDef r)
    {
        int x = (int)Math.Round(r.X * K), y = (int)Math.Round(r.Y * K);
        int w = (int)Math.Round(r.W * K), h = (int)Math.Round(r.H * K);
        x = Math.Clamp(x, 0, Bgr.Width - 1); y = Math.Clamp(y, 0, Bgr.Height - 1);
        w = Math.Clamp(w, 1, Bgr.Width - x); h = Math.Clamp(h, 1, Bgr.Height - y);
        return new Rect(x, y, w, h);
    }

    public RegionDef ToBase(Rect px) => new()
    {
        X = (int)Math.Round(px.X / K), Y = (int)Math.Round(px.Y / K),
        W = (int)Math.Round(px.Width / K), H = (int)Math.Round(px.Height / K)
    };

    public void Dispose() { _gray?.Dispose(); _hsv?.Dispose(); Bgr.Dispose(); }
}
