using VisionBlocks.Core.Model;

namespace VisionBlocks.Core.Runtime;

/// <summary>遮罩上要画的东西（基准坐标）。运行器每次整体替换一份场景，遮罩按自己的节拍合并刷新。</summary>
public sealed record OverlayBox(RegionDef Box, string Label, string Kind);   // Kind: region / match / point / miss / scrollUp / scrollDown（竖直箭头，Box 宽 0）

public sealed record OverlayScene(IReadOnlyList<OverlayBox> Boxes, string Status)
{
    public static readonly OverlayScene Empty = new(Array.Empty<OverlayBox>(), "");
}

public interface IOverlaySink
{
    void SetScene(OverlayScene scene);
}
