using VisionBlocks.Core.Runtime;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-activity：编辑器「活动」里运行事件翻成人话（2026-11-25）。每种事件喂一行和运行时写的一样的 JSON，
/// 看颜色（流程 / 识别 / 动作 / 提醒 / 出错）、小积木上的字和那句话；不显示的事件要返回空。
/// </summary>
public static class ActivitySelfTest
{
    public static int Run()
    {
        int fails = 0;
        void Check(string json, string kind, string block, string contains)
        {
            var e = ActivityText.FromBotLine(json);
            bool ok = e != null && e.Kind == kind && e.Block == block && e.Plain.Contains(contains, StringComparison.Ordinal);
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {(e == null ? "（没翻出来）" : $"{e.Kind} · {e.Plain}")}");
        }
        void Hidden(string json, string why)
        {
            bool ok = ActivityText.FromBotLine(json) == null;
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] 不显示：{why}");
        }
        Check("{\"ev\":\"runStart\",\"program\":\"背包\",\"t\":1}", "flow", "开始运行", "背包");
        Check("{\"ev\":\"clickConfirm\",\"mode\":\"Condition\",\"ok\":true,\"ms\":412,\"bx\":600,\"by\":270,\"t\":1}", "act", "点击", "用了 0.4 秒");
        Check("{\"ev\":\"clickConfirm\",\"mode\":\"PixelChange\",\"ok\":false,\"ms\":800,\"bx\":600,\"by\":270,\"t\":1}", "warn", "点击", "还没等到确认");
        Check("{\"ev\":\"wait\",\"ms\":300,\"planned\":300,\"actual\":312,\"t\":1}", "act", "等待", "312 ms");
        Check("{\"ev\":\"read\",\"field\":\"名称\",\"raw\":\"碧 落 战 斧\",\"value\":\"碧落战斧\",\"ms\":80,\"seq\":3,\"t\":1}", "sense", "读文字", "名称：碧落战斧");
        Check("{\"ev\":\"reread\",\"field\":\"名称\",\"raw\":\"稀 有 木 材\",\"value\":\"稀有木材\",\"scale\":4,\"pad\":24,\"t\":1}", "sense", "读文字", "放大再读：稀有木材");
        Check("{\"ev\":\"reread\",\"field\":\"名称\",\"raw\":\"\",\"value\":\"\",\"t\":1}", "warn", "读文字", "也是空的");
        Check("{\"ev\":\"classify\",\"field\":\"有效期\",\"value\":\"3天\",\"best\":1,\"second\":0.863,\"close\":false,\"byColor\":false,\"midway\":false,\"scores\":[{\"tpl\":\"1天\",\"score\":0.863,\"color\":4},{\"tpl\":\"3天\",\"score\":1,\"color\":0}],\"t\":1}",
            "sense", "认图案", "有效期：3天（3天 1.00 · 1天 0.86）");
        Check("{\"ev\":\"classify\",\"field\":\"有效期\",\"value\":\"1天\",\"close\":true,\"scores\":[{\"tpl\":\"1天\",\"score\":0.894},{\"tpl\":\"3天\",\"score\":0.882}],\"t\":1}", "warn", "认图案", "分不太开");
        Check("{\"ev\":\"unsureAsk\",\"field\":\"有效期\",\"close\":true,\"guess\":\"1天?\",\"t\":1}", "warn", "认图案", "停下来等你确认");
        Check("{\"ev\":\"unsureAnswer\",\"field\":\"有效期\",\"how\":\"saved\",\"value\":\"7天\",\"template\":\"7天\",\"ms\":5200,\"t\":1}", "sense", "认图案", "存成了新模板「7天」");
        Check("{\"ev\":\"unsureAnswer\",\"field\":\"有效期\",\"how\":\"picked\",\"value\":\"3天\",\"ms\":900,\"t\":1}", "sense", "认图案", "你确认了：有效期 = 3天");
        Check("{\"ev\":\"unsureAnswer\",\"field\":\"有效期\",\"how\":\"timeout\",\"value\":\"1天?\",\"ms\":120000,\"t\":1}", "warn", "认图案", "等了 2 分钟没人确认，先记成「1天?」");
        Check("{\"ev\":\"unsureAnswer\",\"field\":\"有效期\",\"how\":\"auto\",\"value\":\"未认出\",\"t\":1}", "warn", "认图案", "先记成「未认出」接着跑");
        Check("{\"ev\":\"unsureAnswer\",\"field\":\"有效期\",\"how\":\"stop\",\"ms\":800,\"t\":1}", "bad", "认图案", "停止了运行");
        Check("{\"ev\":\"row\",\"key\":\"碧落战斧|3天\",\"rows\":12,\"t\":1}", "act", "记录一行", "记下第 12 行");
        Check("{\"ev\":\"dup\",\"key\":\"名称、有效期都和第 7 行一样（碧落战斧 · 1天）\",\"rows\":12,\"t\":1}", "act", "记录一行", "都和第 7 行一样");
        Check("{\"ev\":\"pass\",\"pass\":2,\"added\":4,\"atEnd\":false,\"t\":1}", "flow", "重复直到没有新记录", "第 2 遍做完，新记下 4 行");
        Check("{\"ev\":\"loopAtEnd\",\"pass\":3,\"t\":1}", "flow", "重复直到没有新记录", "第 3 遍做完就停");
        Check("{\"ev\":\"loopCap\",\"pass\":10,\"t\":1}", "warn", "重复直到没有新记录", "最多 10 遍");
        Check("{\"ev\":\"dragPx\",\"want\":720,\"got\":330.4,\"strokes\":3,\"end\":true,\"reason\":\"end\",\"t\":1}", "act", "拖动", "只走了 330 / 720 px");
        Check("{\"ev\":\"wheelPx\",\"want\":240,\"got\":0,\"reason\":\"cantMeasure\",\"t\":1}", "warn", "滚轮", "量不出");
        Check("{\"ev\":\"gridShift\",\"grid\":\"items\",\"region\":\"itemsView\",\"reason\":\"end\",\"shortfall\":390,\"total\":390,\"pitch\":240,\"phase\":150,\"t\":1}", "act", "网格", "下移 150 px");
        Check("{\"ev\":\"gridShift\",\"grid\":\"items\",\"reason\":\"end\",\"shortfall\":480,\"total\":480,\"pitch\":240,\"phase\":0,\"t\":1}", "act", "网格", "不用挪");
        Check("{\"ev\":\"blocked\",\"what\":\"keydown Esc\",\"why\":\"前台不是靶子\",\"t\":1}", "bad", "拦下", "前台不是靶子");
        Check("{\"ev\":\"emergencyStop\",\"t\":1}", "bad", "急停", "急停");
        Check("{\"ev\":\"cond\",\"kind\":\"text\",\"raw\":\"完成\",\"norm\":\"完成\",\"want\":\"完成\",\"hit\":true,\"t\":1}", "sense", "文字包含", "成立");
        Hidden("{\"ev\":\"cond\",\"kind\":\"image\",\"score\":0.9,\"hit\":true,\"t\":1}", "每次图像判断（太多，在 bot.jsonl 里）");
        Hidden("{\"ev\":\"frame\",\"seq\":5,\"t\":1}", "每一帧");
        Hidden("{\"ev\":\"saveImage\",\"field\":\"图片\",\"file\":\"images/001.png\",\"t\":1}", "截图存图（每件一条，会把提醒挤出活动）");
        {
            bool ok = !ActivityText.Events.Contains("cond") && !ActivityText.Events.Contains("saveImage");
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] 编辑器先挑的事件里没有 cond、saveImage（每次图像 / 颜色判断、每张存图不送到界面；文字条件另外放行）");
        }
        Hidden("{\"ev\":\"runEnd\",\"result\":\"完成\",\"rows\":46,\"t\":1}", "runEnd（编辑器自己写一句带统计的「运行结束」）");
        Hidden("{\"ev\":\"pass\"", "坏掉的 JSON");
        Console.WriteLine(fails == 0 ? "活动翻译自测全部通过" : $"活动翻译自测失败 {fails} 项");
        return fails == 0 ? 0 : 1;
    }
}
