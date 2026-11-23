namespace VisionBlocks.Core.Model;

/// <summary>积木和条件的中文摘要，编辑器的程序树和日志都用它。</summary>
public static class BlockText
{
    public enum Category { Flow, Sense, Action }

    public static Category CategoryOf(Block b) => b switch
    {
        RepeatUntilBlock or RepeatBlock or IfBlock or WaitUntilBlock or StopBlock or ForEachCellBlock or UntilNoNewRowsBlock => Category.Flow,
        ReadTextBlock or SaveImageBlock => Category.Sense,
        _ => Category.Action
    };

    private static string R(VbProgram p, string? n) => n == null ? "?" : n == VbProgram.CurrentCell ? n : L(p.Regions, n);
    private static string LG(VbProgram p, string n) => p.Grids.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v.Label) ? v.Label : n;

    private static string L(Dictionary<string, RegionDef> d, string n) => d.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v.Label) ? v.Label : n;
    private static string LT(VbProgram p, string n) => p.Templates.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v.Label) ? v.Label : n;
    private static string LC(VbProgram p, string n) => p.Colors.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v.Label) ? v.Label : n;
    private static string LP(VbProgram p, string n) => p.Points.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v.Label) ? v.Label : n;

    public static string Cond(Condition? c, VbProgram p) => c switch
    {
        null => "（未设置条件）",
        ImageCondition i => $"{(i.Negate ? "看不到" : "看到")}图像「{LT(p, i.Template)}」在「{L(p.Regions, i.Region)}」≥ {i.Threshold:0.00}" + (i.MinContrast > 0 ? $"（对比度 ≥ {i.MinContrast:0.0}）" : ""),
        ColorCondition k => $"「{L(p.Regions, k.Region)}」里「{LC(p, k.Color)}」占比 {(k.Negate ? "不" : "")}{k.Op} {k.Ratio * 100:0.#}%",
        TextCondition t => $"「{L(p.Regions, t.Region)}」文字{(t.Negate ? "不" : "")}包含「{t.Contains}」",
        _ => c.GetType().Name
    };

    public static string Summary(Block b, VbProgram p) => b switch
    {
        RepeatUntilBlock r => $"重复直到 {Cond(r.Until, p)}" + (r.CheckEveryMs > 0 ? $"（每 {r.CheckEveryMs}ms 查一次）" : ""),
        RepeatBlock r => $"重复 {r.Times} 次",
        IfBlock i => $"如果 {Cond(i.Cond, p)}",
        WaitUntilBlock w => $"等到 {Cond(w.Cond, p)}（最多 {w.TimeoutMs}ms）",
        WaitBlock w => $"等待 {w.Ms}ms",
        KeyBlock k => $"按键 {k.Key}（按住 {k.HoldMs}ms）",
        ClickBlock c => $"点击「{(c.Point != null ? LP(p, c.Point) : c.Region != null ? L(p.Regions, c.Region) + "中心" : "?")}」" + c.Confirm switch
        {
            ConfirmMode.PixelChange => " · 点完确认画面有变化",
            ConfirmMode.Condition => $" · 点完等到 {Cond(c.ConfirmCond, p)}",
            _ => ""
        },
        TypeTextBlock t => $"输入文字「{t.Text}」",
        StopBlock => "停止",
        ForEachCellBlock f => $"对网格「{LG(p, f.Grid)}」的每一格",
        UntilNoNewRowsBlock u => $"重复直到这一遍没有新记录（最多 {u.MaxTimes} 遍）",
        WheelBlock { DistancePx: int d } w => $"在「{R(p, w.Region)}」滚轮{(d > 0 ? "向下" : "向上")} {Math.Abs(d)} px（边滚边量）",
        WheelBlock w => $"在「{(w.Point != null ? LP(p, w.Point) : R(p, w.Region))}」滚轮{(w.Notches > 0 ? "向下" : "向上")} {Math.Abs(w.Notches)} 格",
        ReadTextBlock r => $"读「{R(p, r.Region)}」的文字 → 字段「{r.Field}」" + (r.NumbersOnly ? "（只要数字）" : ""),
        SaveImageBlock s => $"截「{R(p, s.Region)}」存图 → 字段「{s.Field}」",
        RecordRowBlock rr => "记录一行" + (rr.DedupeBy.Count > 0 ? $"（按「{string.Join("+", rr.DedupeBy)}」去重）" : ""),
        _ => b.GetType().Name
    };
}
