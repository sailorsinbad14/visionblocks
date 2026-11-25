using System.Globalization;
using System.Text.Json;

namespace VisionBlocks.Core.Runtime;

/// <summary>
/// 编辑器「活动」里每一条（2026-11-25）：运行日志 bot.jsonl 的一行翻成一句人话，前面配一块和积木架同色的小积木。
/// 以前活动里直接显示原始 JSON（截到 220 个字），新手看不懂；「重复跳过」「拖动 / 滚轮结果」这些也不显示。
/// 频繁的事件（每一帧、每次条件判断）照旧不显示，全量还在 bot.jsonl 里。纯函数，自测逐个事件对一遍。
/// </summary>
public static class ActivityText
{
    /// <param name="Kind">flow 流程（橙）/ sense 识别（青）/ act 动作（蓝）/ warn 提醒（黄）/ bad 出错（红）/ sys 编辑器（灰）。</param>
    /// <param name="Block">小积木上的字。</param>
    /// <param name="Sub">第二行的小字（细节），没有为 null。</param>
    public sealed record Entry(string Kind, string Block, string Text, string? Sub = null)
    {
        /// <summary>一整行纯文字（界面自动化读、复制用）。</summary>
        public string Plain => Block + "  " + Text + (Sub != null ? "（" + Sub + "）" : "");
    }

    /// <summary>会翻译的事件（编辑器按它先挑一遍，别的行不用解析）。runEnd 不在里面：编辑器自己写一句带统计的「运行结束」。</summary>
    public static readonly HashSet<string> Events = new()
    {
        "runStart", "keydown", "clickConfirm", "wait", "blocked", "imeOff", "waitTimeout", "loopTimeout",
        "paused", "resumed", "emergencyStop", "noFrame", "frameBack", "sendFailed",
        "row", "dup", "pass", "loopCap", "loopAtEnd", "read", "reread", "classify", "saveImage", "dragPx", "wheelPx",
        "gridShift", "gridShiftSkipped", "cond"
    };

    /// <summary>bot.jsonl 的一行 → 活动里的一条；不显示的返回 null。</summary>
    public static Entry? FromBotLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return null; }
        using (doc)
        {
            var e = doc.RootElement;
            string ev = S(e, "ev");
            if (!Events.Contains(ev)) return null;
            return ev switch
            {
                "runStart" => new("flow", "开始运行", $"程序「{S(e, "program")}」"),
                "runEnd" => new("flow", "运行结束", S(e, "result"),
                    $"记录 {N(e, "rows")} 行，重复跳过 {N(e, "dups")} 次" + (N(e, "blocked") > 0 ? $"，拦下 {N(e, "blocked")} 次输入" : "")),
                "keydown" => new("act", "按键", S(e, "k")),
                "clickConfirm" => S(e, "mode") == "None"
                    ? new("act", "点击", $"({N(e, "bx")},{N(e, "by")})")
                    : B(e, "ok")
                        ? new("act", "点击", $"({N(e, "bx")},{N(e, "by")})", $"点完确认到了，用了 {D(e, "ms") / 1000:0.0} 秒")
                        : new("warn", "点击", $"({N(e, "bx")},{N(e, "by")})", $"点完 {D(e, "ms") / 1000:0.0} 秒还没等到确认，接着往下"),
                "wait" => new("act", "等待", $"{N(e, "actual")} ms" + (N(e, "planned") != N(e, "ms") ? $"（设的 {N(e, "ms")}，随机后 {N(e, "planned")}）" : "")),
                "blocked" => new("bad", "拦下", $"没发出去：{S(e, "what")}", S(e, "why")),
                "imeOff" => new("sys", "输入法", "发键前把靶子的输入法切到了英文", $"原来是 {S(e, "before")}"),
                "waitTimeout" => new("warn", "等到…", $"等了 {N(e, "ms")} ms 还没等到，接着往下"),
                "loopTimeout" => new("warn", "重复直到…", $"转了 {N(e, "ms")} ms 到了超时，跳出循环"),
                "paused" => new("sys", "暂停", "已暂停"),
                "resumed" => new("sys", "继续", "接着运行"),
                "emergencyStop" => new("bad", "急停", "急停，所有按键已松开"),
                "noFrame" => new("warn", "截图", "截不到画面（窗口最小化或关了？）"),
                "frameBack" => new("sys", "截图", $"画面回来了，等了 {N(e, "ms")} ms"),
                "sendFailed" => new("bad", "输入", $"发送失败：{S(e, "what")}"),
                "row" => new("act", "记录一行", $"记下第 {N(e, "rows")} 行", Note(S(e, "key"))),
                "dup" => new("act", "记录一行", "重复，跳过", S(e, "key")),
                "pass" => new("flow", "重复直到没有新记录", $"第 {N(e, "pass")} 遍做完，新记下 {N(e, "added")} 行" + (B(e, "atEnd") ? "，拖到底了" : "")),
                "loopCap" => new("warn", "重复直到没有新记录", $"到了最多 {N(e, "pass")} 遍，停下", "去重可能没起作用：看看去重字段"),
                "loopAtEnd" => new("flow", "重复直到没有新记录", $"拖到底了、画面一点没动：第 {N(e, "pass")} 遍做完就停"),
                "read" => new("sense", "读文字", $"{S(e, "field")}：{Shown(S(e, "value"))}"),
                "reread" => S(e, "value").Length > 0
                    ? new("sense", "读文字", $"{S(e, "field")} 第一次读空，放大再读：{S(e, "value")}", "文字框最好再框大一圈")
                    : new("warn", "读文字", $"{S(e, "field")} 读空，放大再读也是空的", "文字框框大一圈、四周留白再试"),
                "classify" => Classify(e),
                "saveImage" => new("sense", "截图存图", $"{S(e, "field")} → {S(e, "file")}"),
                "dragPx" => Move(e, "拖动"),
                "wheelPx" => Move(e, "滚轮"),
                "gridShift" => new("act", "网格", $"「{S(e, "grid")}」下移 {D(e, "phase"):0} px 对齐", $"少走了 {D(e, "shortfall"):0} px，行距 {N(e, "pitch")}"),
                "gridShiftSkipped" => new("warn", "网格", $"「{S(e, "grid")}」没挪：量不出走了多少", "下一遍格子可能对不齐"),
                "cond" when S(e, "kind") == "text" => new("sense", "文字包含", $"读到「{S(e, "norm")}」", B(e, "hit") ? "成立" : "不成立"),
                _ => null
            };
        }
    }

    private static Entry Classify(JsonElement e)
    {
        string field = S(e, "field"), value = S(e, "value");
        var scores = e.TryGetProperty("scores", out var sc) && sc.ValueKind == JsonValueKind.Array
            ? sc.EnumerateArray().Select(x => (Tpl: S(x, "tpl"), Score: D(x, "score"))).OrderByDescending(x => x.Score).Take(3).ToList()
            : new List<(string Tpl, double Score)>();
        string sub = string.Join(" · ", scores.Select(x => $"{x.Tpl} {x.Score:0.00}"));
        if (B(e, "close")) return new("warn", "认图案", $"{field}：{value}（分不太开）", sub + "；可能是还没裁模板的新图案");
        if (B(e, "midway")) return new("warn", "认图案", $"{field}：{value}（有点像但没过门槛）", sub + "；可能是还没裁模板的新图案");
        return new("sense", "认图案", $"{field}：{value}", sub + (B(e, "byColor") ? "；分数差不多，按颜色定的" : ""));
    }

    private static Entry Move(JsonElement e, string verb)
    {
        string got = D(e, "got").ToString("0", CultureInfo.InvariantCulture), want = N(e, "want").ToString(CultureInfo.InvariantCulture);
        return S(e, "reason") switch
        {
            "end" => new("act", verb, $"到底了，只走了 {got} / {want} px"),
            "cantMeasure" => new("warn", verb, "量不出画面移动，停下", "热区要框在会动的内容里"),
            "userMouse" => new("warn", verb, $"鼠标被移动了，已松开（走了 {got} / {want} px）"),
            "blocked" => new("bad", verb, $"输入被拦，已松开（走了 {got} / {want} px）"),
            "nearestNotch" => new("act", verb, $"{got} / {want} px", "这个程序只认整格，停在最近的整格"),
            _ => new("act", verb, $"{got} / {want} px")
        };
    }

    private static string Shown(string v) => v.Length > 0 ? v : "（读空）";
    private static string? Note(string key) => key.StartsWith('（') ? key.Trim('（', '）') : null;

    private static string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => v.ToString()
    } : "";
    private static double D(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static long N(JsonElement e, string p) => (long)Math.Round(D(e, p));
    private static bool B(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
}
