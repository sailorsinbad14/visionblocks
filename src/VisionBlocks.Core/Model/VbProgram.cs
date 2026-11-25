using System.Text.Json.Serialization;

namespace VisionBlocks.Core.Model;

/// <summary>
/// 程序文件（.vbp.json）。按「识别对象声明化」的思路：
/// 热区、定点、模板、颜色都是有名字的数据，积木只按名字引用它们。
/// 所有坐标都是「客户区基准坐标」（Base，默认 1920×1080），和窗口在哪块屏、缩放多少无关。
/// </summary>
public sealed class VbProgram
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "未命名";
    public BaseSize Base { get; set; } = new();
    public TargetSpec Target { get; set; } = new();
    public Dictionary<string, RegionDef> Regions { get; set; } = new();
    public Dictionary<string, PointDef> Points { get; set; } = new();
    public Dictionary<string, TemplateDef> Templates { get; set; } = new();
    public Dictionary<string, ColorDef> Colors { get; set; } = new();
    public Dictionary<string, GridDef> Grids { get; set; } = new();
    public List<Block> Program { get; set; } = new();

    /// <summary>保留的热区名：「对每一格」循环里的当前格子。点击、条件、读文字、存图都可以用它。</summary>
    public const string CurrentCell = "当前格";
}

/// <summary>
/// 网格（2026-11-20，背包场景）：第一格的位置和大小，加上列数、行数、间距，就能算出每一格。
/// 一列多行可以当分类列表用；按整行滚动的物品列表，滚完以后格子位置不变，也能用。
/// </summary>
public sealed class GridDef
{
    public string Label { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 100;
    public int H { get; set; } = 100;
    public int Cols { get; set; } = 4;
    public int Rows { get; set; } = 3;
    public int GapX { get; set; } = 10;
    public int GapY { get; set; } = 10;

    /// <summary>第 i 格（先行后列，从 0 开始）的基准坐标。</summary>
    public RegionDef Cell(int i) => new() { X = X + i % Cols * (W + GapX), Y = Y + i / Cols * (H + GapY), W = W, H = H };

    /// <summary>行距 = 格子高 + 行间距。</summary>
    [JsonIgnore] public int Pitch => Math.Max(1, H + GapY);

    /// <summary>整个网格占的范围（基准坐标）。</summary>
    [JsonIgnore] public RegionDef Bounds => new() { X = X, Y = Y, W = Cols * (W + GapX) - GapX, H = Rows * (H + GapY) - GapY };

    /// <summary>少走的距离（带方向，正 = 内容比预期低）按行距取余，落在 [0, 行距)。错开整行时格子照样压在行上，只看零头。</summary>
    public double Phase(double shortfall) => ((shortfall % Pitch) + Pitch) % Pitch;

    /// <summary>
    /// 网格整体下移 phase 以后，完整露在 bounds 里的格子（2026-11-25，到底走不满）：从原第一行上面一行算到原最后一行下面一行，
    /// 被 bounds 上下边切掉的行跳过（上一页读过，或者下一页才完整露出来），新完全露出来的行算进来。只看竖直方向；tol 像素以内的出界不算。
    /// </summary>
    public List<RegionDef> ShiftedCells(double phase, RegionDef bounds, double tol = 3)
    {
        var list = new List<RegionDef>();
        for (int k = -1; k <= Rows; k++)
        {
            int y = (int)Math.Round(Y + phase + k * (double)Pitch);
            if (y < bounds.Y - tol || y + H > bounds.Y + bounds.H + tol) continue;
            for (int c = 0; c < Cols; c++) list.Add(new RegionDef { X = X + c * (W + GapX), Y = y, W = W, H = H });
        }
        return list;
    }

    /// <summary>和热区重叠面积最大的网格（编辑器的拖动 / 滚轮预览、运行时到底挪网格都用它认「这个热区滚的是哪个网格」）。</summary>
    public static (string Name, GridDef Grid)? InRegion(VbProgram p, RegionDef roi)
    {
        (string, GridDef)? best = null;
        long bestArea = 0;
        foreach (var (name, g) in p.Grids)
        {
            var b = g.Bounds;
            long w = Math.Min(b.X + b.W, roi.X + roi.W) - Math.Max(b.X, roi.X), h = Math.Min(b.Y + b.H, roi.Y + roi.H) - Math.Max(b.Y, roi.Y);
            if (w > 0 && h > 0 && w * h > bestArea) { bestArea = w * h; best = (name, g); }
        }
        return best;
    }
    public override string ToString() => $"{Cols}×{Rows} 格，每格 {W}×{H}，间距 {GapX}/{GapY}";
}

public sealed class BaseSize { public int W { get; set; } = 1920; public int H { get; set; } = 1080; }

public sealed class TargetSpec
{
    /// <summary>只允许向这些进程名的窗口发输入（白名单）。</summary>
    public List<string> AllowedProcesses { get; set; } = new() { "BenchTarget" };
    /// <summary>发键前把目标窗口的输入法切到英文（中文输入法会截走字母键，见 ImeGuard）。</summary>
    public bool CloseImeBeforeKeys { get; set; } = true;
}

public sealed class RegionDef
{
    public string Label { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public override string ToString() => $"x{X} y{Y} w{W} h{H}";
}

public sealed class PointDef
{
    public string Label { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class TemplateDef
{
    public string Label { get; set; } = "";
    /// <summary>相对程序文件所在目录的 PNG 路径，按基准分辨率裁出。</summary>
    public string File { get; set; } = "";
}

/// <summary>HSV 范围（OpenCV 口径：H 0–179，S/V 0–255）。Lower.H &gt; Upper.H 表示色相跨过 0（红色）。</summary>
public sealed class ColorDef
{
    public string Label { get; set; } = "";
    public int[] Lower { get; set; } = new int[3];
    public int[] Upper { get; set; } = new int[3];
}

// ───────────── 积木 ─────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RepeatUntilBlock), "repeatUntil")]
[JsonDerivedType(typeof(RepeatBlock), "repeat")]
[JsonDerivedType(typeof(IfBlock), "if")]
[JsonDerivedType(typeof(WaitUntilBlock), "waitUntil")]
[JsonDerivedType(typeof(WaitBlock), "wait")]
[JsonDerivedType(typeof(KeyBlock), "key")]
[JsonDerivedType(typeof(ClickBlock), "click")]
[JsonDerivedType(typeof(TypeTextBlock), "typeText")]
[JsonDerivedType(typeof(StopBlock), "stop")]
[JsonDerivedType(typeof(ForEachCellBlock), "forEachCell")]
[JsonDerivedType(typeof(UntilNoNewRowsBlock), "untilNoNewRows")]
[JsonDerivedType(typeof(WheelBlock), "wheel")]
[JsonDerivedType(typeof(DragBlock), "drag")]
[JsonDerivedType(typeof(ReadTextBlock), "readText")]
[JsonDerivedType(typeof(SaveImageBlock), "saveImage")]
[JsonDerivedType(typeof(RecordRowBlock), "recordRow")]
[JsonDerivedType(typeof(ClassifyBlock), "classify")]
public abstract class Block
{
    public string? Note { get; set; }
    /// <summary>
    /// 程序树里这块容器积木折起来了（2026-11-25）：只影响编辑器显示，运行照常执行里面所有积木。
    /// 展开的不写进文件；0.3.0 及以前的编辑器读到会跳过（全部展开）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Collapsed { get; set; }
}

public interface IContainerBlock { List<Block> Body { get; } }

public sealed class RepeatUntilBlock : Block, IContainerBlock
{
    public Condition Until { get; set; } = new TextCondition();
    /// <summary>多久检查一次「直到」条件。OCR 这类慢识别不该每一圈都跑，否则拖慢整个循环。</summary>
    public int CheckEveryMs { get; set; } = 0;
    /// <summary>整个循环的超时，0 = 不限。</summary>
    public int TimeoutMs { get; set; } = 0;
    public List<Block> Body { get; set; } = new();
}

public sealed class RepeatBlock : Block, IContainerBlock
{
    public int Times { get; set; } = 3;
    public List<Block> Body { get; set; } = new();
}

public sealed class IfBlock : Block, IContainerBlock
{
    public Condition Cond { get; set; } = new ImageCondition();
    public List<Block> Body { get; set; } = new();
    public List<Block> Else { get; set; } = new();
    /// <summary>「否则」分支在程序树里折起来了（只影响显示）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ElseCollapsed { get; set; }
}

public sealed class WaitUntilBlock : Block
{
    public Condition Cond { get; set; } = new ImageCondition();
    public int TimeoutMs { get; set; } = 3000;
}

public sealed class WaitBlock : Block { public int Ms { get; set; } = 100; }

public sealed class KeyBlock : Block
{
    public string Key { get; set; } = "F";
    /// <summary>按下到抬起的间隔，一般 30–60ms。</summary>
    public int HoldMs { get; set; } = 40;
}

public enum ConfirmMode { None, PixelChange, Condition }

public sealed class ClickBlock : Block
{
    /// <summary>点哪里：定点名，或热区名（点热区中心）。</summary>
    public string? Point { get; set; }
    public string? Region { get; set; }
    public int HoldMs { get; set; } = 40;
    /// <summary>
    /// 点完截图确认（不要连续盲点固定坐标）。
    /// PixelChange：点击处周围画面有变化；Condition：等到 ConfirmCond 成立。
    /// </summary>
    public ConfirmMode Confirm { get; set; } = ConfirmMode.PixelChange;
    public Condition? ConfirmCond { get; set; }
    public int ConfirmTimeoutMs { get; set; } = 800;
}

public sealed class TypeTextBlock : Block { public string Text { get; set; } = ""; }

public sealed class StopBlock : Block { }

// ───────────── 背包场景用的积木（2026-11-20） ─────────────

/// <summary>对网格里的每一格（先行后列）执行一遍；循环体里的「当前格」就是这一格。</summary>
public sealed class ForEachCellBlock : Block, IContainerBlock
{
    public string Grid { get; set; } = "";
    public List<Block> Body { get; set; } = new();
}

/// <summary>
/// 反复执行循环体，直到某一遍没有记下任何新的一行（用来翻页：翻到底以后再看一遍全是重复的，就停）。
/// MaxTimes 是硬上限，防止去重失效时永远翻下去。
/// </summary>
public sealed class UntilNoNewRowsBlock : Block, IContainerBlock
{
    public int MaxTimes { get; set; } = 30;
    /// <summary>
    /// 这一遍里有一块「拖动」或「按距离滚」一点没动（不到 0.5 px）并且报到底，这一遍做完就停（2026-11-25）。
    /// 去重有失误、或者关了去重时也能停。走了一部分才到底的不算：下一遍还要读刚露出来的行。旧程序没写这一项也是开。
    /// </summary>
    public bool StopAtEnd { get; set; } = true;
    public List<Block> Body { get; set; } = new();
}

/// <summary>
/// 把鼠标移到定点或热区中心，滚动滚轮。Notches 为正向下滚，为负向上滚；一格 = WHEEL_DELTA 120。
/// DistancePx 不为空时改成「按距离滚」（2026-11-22）：在 Region 里边滚边量画面，滚够这么多基准像素就停（正数向下 = 内容往上走），
/// 这时 Notches 不用。一格滚多少像素由目标程序决定，所以距离只能靠量画面来保证。
/// </summary>
public sealed class WheelBlock : Block
{
    public string? Point { get; set; }
    public string? Region { get; set; }
    public int Notches { get; set; } = 1;
    public int? DistancePx { get; set; }
    /// <summary>按距离滚时差多少以内算到了（基准像素）；空 = 3。</summary>
    public int? TolerancePx { get; set; }
    public const int DefaultTolerancePx = 3;
}

/// <summary>
/// 按住鼠标拖动（2026-11-23）：在热区里按下、拖、松开，边拖边量画面，直到内容移动了 DistancePx 基准像素。
/// 方向和滚轮「按距离滚」一样：正数 = 向下翻（内容往上走，鼠标往上拖）。距离长时分几笔拖；一笔拖完量一次，差多少再补。
/// </summary>
public sealed class DragBlock : Block
{
    public string Region { get; set; } = "";
    public int DistancePx { get; set; } = 240;
    /// <summary>差多少以内算到了（基准像素）；空 = 3。</summary>
    public int? TolerancePx { get; set; }
    /// <summary>拖动速度（基准像素 / 秒）；空 = 800。</summary>
    public int? SpeedPxPerSec { get; set; }
    /// <summary>拖到终点后停多久再松开（毫秒），让有惯性的列表先停住；空 = 150。</summary>
    public int? HoldBeforeUpMs { get; set; }
    public const int DefaultTolerancePx = 3, DefaultSpeed = 800, DefaultHoldMs = 150;
}

/// <summary>识别热区里的文字，存进当前这一行的某个字段。</summary>
public sealed class ReadTextBlock : Block
{
    public string Region { get; set; } = "";
    public string Field { get; set; } = "";
    /// <summary>只保留数字（「库存 23」→「23」）。</summary>
    public bool NumbersOnly { get; set; }
}

/// <summary>把热区截下来存成 PNG，文件路径存进当前这一行的某个字段。</summary>
public sealed class SaveImageBlock : Block
{
    public string Region { get; set; } = "";
    public string Field { get; set; } = "";
}

/// <summary>
/// 认图案（2026-11-25）：在热区里把几张模板都找一遍，取分最高的那张，把它的名字（模板的标签，没有标签用模板名）写进字段；
/// 都没过门槛写 NoneText。用来把图片角上的角标（有效期 1天 / 3天）变成一个能去重的文字字段。
/// 只差一个数字的两张模板会互相拿到 0.8 多，所以不能「谁先过门槛算谁」，见 PatternClassifier。
/// </summary>
public sealed class ClassifyBlock : Block
{
    public string Region { get; set; } = "";
    public List<string> Templates { get; set; } = new();
    public string Field { get; set; } = "";
    public double Threshold { get; set; } = 0.85;
    public string NoneText { get; set; } = "无";
}

/// <summary>
/// 把当前各字段的值写成结果表（data.csv）的一行。字段的值在写完后保留，下一次读到新值时覆盖（这样「分类」读一次就能跟着后面每一行）。
/// DedupeBy 里列出的字段组合已经写过时跳过这一行，不算新记录。
/// </summary>
public sealed class RecordRowBlock : Block
{
    public List<string> DedupeBy { get; set; } = new();
}

// ───────────── 条件 ─────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ImageCondition), "image")]
[JsonDerivedType(typeof(ColorCondition), "color")]
[JsonDerivedType(typeof(TextCondition), "text")]
public abstract class Condition
{
    /// <summary>取反：「看不到」「不包含」。</summary>
    public bool Negate { get; set; }
}

public sealed class ImageCondition : Condition
{
    public string Template { get; set; } = "";
    public string Region { get; set; } = "";
    /// <summary>灰度 TM_CCOEFF_NORMED 的得分阈值，一般从 0.8 起步。</summary>
    public double Threshold { get; set; } = 0.8;
    /// <summary>
    /// 对比度下限（匹配处灰度标准差 / 模板灰度标准差）。0 = 不检查。
    /// 用来挡住「半透明」「变暗」的同形干扰项：TM_CCOEFF_NORMED 对亮度和对比度的线性变化不敏感。
    /// </summary>
    public double MinContrast { get; set; } = 0;
}

public sealed class ColorCondition : Condition
{
    public string Color { get; set; } = "";
    public string Region { get; set; } = "";
    /// <summary>"&gt;" 或 "&lt;"。</summary>
    public string Op { get; set; } = ">";
    /// <summary>落在颜色范围内的像素占热区的比例（0–1）。用占比，不用单点。</summary>
    public double Ratio { get; set; } = 0.05;
}

public sealed class TextCondition : Condition
{
    public string Region { get; set; } = "";
    public string Contains { get; set; } = "";
}
