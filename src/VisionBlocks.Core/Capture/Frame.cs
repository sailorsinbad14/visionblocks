using OpenCvSharp;

namespace VisionBlocks.Core.Capture;

/// <summary>一帧客户区画面（BGRA，物理像素）。调用方拿到的是副本，用完要 Dispose。</summary>
public sealed class Frame : IDisposable
{
    public required Mat Bgra { get; init; }
    public long Seq { get; init; }
    /// <summary>回调收到这一帧的时刻（QPC）。</summary>
    public long ArrivalQpc { get; init; }
    /// <summary>系统给这一帧标的时间（QPC）；拿不到时为 0。</summary>
    public long SystemQpc { get; init; }
    public void Dispose() => Bgra.Dispose();
}

/// <summary>每来一帧就回调一次（在截图线程上，必须很快返回）。bgraClient 只在回调期间有效。</summary>
public delegate void FrameHook(Mat bgraClient, long seq, long arrivalQpc, long systemQpc);

/// <summary>
/// 截图接口：Start / TryGetLatest / Stop，加上一帧的时间戳。
/// 拿不到帧（最小化、窗口已关）时返回 null，不抛异常，由上层决定怎么办。
/// </summary>
public interface IFrameSource : IDisposable
{
    string Name { get; }
    void Start(IntPtr hwnd);
    Frame? TryGetLatest();
    long LatestSeq { get; }
    event FrameHook? FrameArrived;
}
