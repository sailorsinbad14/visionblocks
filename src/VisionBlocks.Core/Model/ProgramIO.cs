using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionBlocks.Core.Model;

public static class ProgramIO
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // 小数一律按「.」解析，与系统区域设置无关（有的区域设置把「,」当小数点，0.85 会被读错）
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static VbProgram Load(string path) =>
        JsonSerializer.Deserialize<VbProgram>(File.ReadAllText(path, Encoding.UTF8), Options)
        ?? throw new InvalidDataException($"程序文件为空：{path}");

    public static void Save(VbProgram p, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(p, Options), new UTF8Encoding(false));

    /// <summary>检查积木引用的名字是否都存在，返回中文错误列表（空 = 没问题）。</summary>
    public static List<string> Validate(VbProgram p)
    {
        var errs = new List<string>();
        if (p.Regions.ContainsKey(VbProgram.CurrentCell)) errs.Add($"热区不能叫「{VbProgram.CurrentCell}」，这个名字留给「对每一格」循环里的当前格子");
        // 「当前格」只能在「对每一格」循环里面用
        bool RegionOk(string? r, bool inCell) => r != null && (p.Regions.ContainsKey(r) || (inCell && r == VbProgram.CurrentCell));
        string RegionErr(string? r, bool inCell) => r == VbProgram.CurrentCell && !inCell ? $"「{VbProgram.CurrentCell}」只能放在「对每一格」循环里面" : $"热区「{r}」不存在";
        void Cond(Condition? c, string where, bool inCell)
        {
            switch (c)
            {
                case null: return;
                case ImageCondition ic:
                    if (!p.Templates.ContainsKey(ic.Template)) errs.Add($"{where}：模板「{ic.Template}」不存在");
                    if (!RegionOk(ic.Region, inCell)) errs.Add($"{where}：{RegionErr(ic.Region, inCell)}");
                    if (ic.Threshold is <= 0 or > 1) errs.Add($"{where}：阈值应在 0–1 之间");
                    break;
                case ColorCondition cc:
                    if (!p.Colors.ContainsKey(cc.Color)) errs.Add($"{where}：颜色「{cc.Color}」不存在");
                    if (!RegionOk(cc.Region, inCell)) errs.Add($"{where}：{RegionErr(cc.Region, inCell)}");
                    if (cc.Op is not (">" or "<")) errs.Add($"{where}：比较符只能是 > 或 <");
                    break;
                case TextCondition tc:
                    if (!RegionOk(tc.Region, inCell)) errs.Add($"{where}：{RegionErr(tc.Region, inCell)}");
                    if (string.IsNullOrWhiteSpace(tc.Contains)) errs.Add($"{where}：要匹配的文字为空");
                    break;
            }
        }
        void Walk(List<Block> blocks, string path, bool inCell)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                string w = $"{path}第 {i + 1} 块";
                switch (blocks[i])
                {
                    case RepeatUntilBlock ru: Cond(ru.Until, w, inCell); Walk(ru.Body, w + " › ", inCell); break;
                    case RepeatBlock r: Walk(r.Body, w + " › ", inCell); break;
                    case IfBlock ib: Cond(ib.Cond, w, inCell); Walk(ib.Body, w + " › ", inCell); Walk(ib.Else, w + "（否则）› ", inCell); break;
                    case WaitUntilBlock wu: Cond(wu.Cond, w, inCell); break;
                    case ClickBlock cb:
                        if (cb.Point == null && cb.Region == null) errs.Add($"{w}：点击没有指定定点或热区");
                        if (cb.Point != null && !p.Points.ContainsKey(cb.Point)) errs.Add($"{w}：定点「{cb.Point}」不存在");
                        if (cb.Region != null && !RegionOk(cb.Region, inCell)) errs.Add($"{w}：{RegionErr(cb.Region, inCell)}");
                        if (cb.Confirm == ConfirmMode.Condition) Cond(cb.ConfirmCond, w + "（点后确认）", inCell);
                        break;
                    case KeyBlock kb:
                        if (!Input.KeyNames.TryGet(kb.Key, out _, out _)) errs.Add($"{w}：不认识的按键「{kb.Key}」");
                        break;
                    case ForEachCellBlock fe:
                        if (!p.Grids.TryGetValue(fe.Grid, out var g)) errs.Add($"{w}：网格「{fe.Grid}」不存在");
                        else if (g.Cols < 1 || g.Rows < 1 || g.W < 1 || g.H < 1) errs.Add($"{w}：网格「{fe.Grid}」的行列数和格子大小都要大于 0");
                        Walk(fe.Body, w + " › ", true);
                        break;
                    case UntilNoNewRowsBlock un:
                        if (un.MaxTimes < 1) errs.Add($"{w}：最多次数要大于 0");
                        Walk(un.Body, w + " › ", inCell);
                        break;
                    case WheelBlock wb:
                        if (wb.Point == null && wb.Region == null) errs.Add($"{w}：滚轮没有指定定点或热区");
                        if (wb.Point != null && !p.Points.ContainsKey(wb.Point)) errs.Add($"{w}：定点「{wb.Point}」不存在");
                        if (wb.Region != null && !RegionOk(wb.Region, inCell)) errs.Add($"{w}：{RegionErr(wb.Region, inCell)}");
                        if (wb.Notches == 0) errs.Add($"{w}：滚动格数不能是 0");
                        break;
                    case ReadTextBlock rt:
                        if (!RegionOk(rt.Region, inCell)) errs.Add($"{w}：{RegionErr(rt.Region, inCell)}");
                        if (string.IsNullOrWhiteSpace(rt.Field)) errs.Add($"{w}：没有填字段名");
                        break;
                    case SaveImageBlock si:
                        if (!RegionOk(si.Region, inCell)) errs.Add($"{w}：{RegionErr(si.Region, inCell)}");
                        if (string.IsNullOrWhiteSpace(si.Field)) errs.Add($"{w}：没有填字段名");
                        break;
                }
            }
        }
        Walk(p.Program, "", false);
        return errs;
    }
}
