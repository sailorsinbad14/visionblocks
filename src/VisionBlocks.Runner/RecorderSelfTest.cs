using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-recorder：结果表去重规则（2026-11-20），以及被跳过的行不留图（2026-11-22）。不截图、不发输入，只在临时目录里写 data.csv。
/// 起因：实测时名称框太紧，7 个物品名读空，旧规则把空名字当成同一个键，后 6 个被当成重复悄悄丢了。
/// </summary>
public static class RecorderSelfTest
{
    public static int Run()
    {
        int fails = 0;
        void Expect(string name, bool got, bool want)
        {
            bool pass = got == want;
            if (!pass) fails++;
            Console.WriteLine($"  [{(pass ? "通过" : "失败")}] {name}：{(got ? "记下" : "跳过")}，期望{(want ? "记下" : "跳过")}");
        }
        static byte[] Thumb(byte v) => Enumerable.Repeat(v, 16 * 16 * 3).ToArray();
        static VbProgram Prog(bool withImage)
        {
            var p = new VbProgram { Name = "recorder-selftest" };
            p.Program.Add(new ReadTextBlock { Region = "a", Field = "名称" });
            p.Program.Add(new ReadTextBlock { Region = "b", Field = "库存", NumbersOnly = true });
            if (withImage) p.Program.Add(new SaveImageBlock { Region = "c", Field = "图片" });
            return p;
        }
        var dedupe = new List<string> { "名称" };
        string dir = Path.Combine(Path.GetTempPath(), "vb-recorder-selftest-" + Environment.ProcessId);

        using (var r = new Recorder(Path.Combine(dir, "img"), Prog(true)))
        {
            bool Row(string name, string stock, byte img)
            {
                r.Set("名称", name); r.Set("库存", stock); r.SetImage("图片", $"images/{r.NextRowNo:000}.png", Thumb(img));
                return r.Record(dedupe, out _);
            }
            Expect("名称「甲」第一次", Row("甲", "17", 10), true);
            Expect("名称「甲」第二次（真重复）", Row("甲", "17", 10), false);
            Expect("名称读空，库存 17，图片 A", Row("", "17", 50), true);
            Expect("名称读空，库存 17，图片 A（同一个物品再看到）", Row("", "17", 50), false);
            Expect("名称读空，库存 17，图片 A 有 3 级噪声", Row("", "17", 53), false);
            Expect("名称读空，库存 38，图片 A（同图不同物品）", Row("", "38", 50), true);
            Expect("名称读空，库存 17，图片 B（不同图）", Row("", "17", 200), true);
            Console.WriteLine($"  有图片：记下 {r.Rows} 行，跳过 {r.Duplicates} 次，读空 {r.EmptyKeys} 次（期望 4 / 3 / 5）");
            if (r.Rows != 4 || r.Duplicates != 3 || r.EmptyKeys != 5) fails++;
        }
        using (var r = new Recorder(Path.Combine(dir, "noimg"), Prog(false)))
        {
            r.Set("名称", ""); r.Set("库存", "17");
            Expect("没有图片字段，名称读空", r.Record(dedupe, out _), true);
            Expect("没有图片字段，名称读空，再看到一次（照记，不丢）", r.Record(dedupe, out string key), true);
            Console.WriteLine($"  没图片：记下 {r.Rows} 行，读空 {r.EmptyKeys} 次（期望 2 / 2），说明「{key}」");
            if (r.Rows != 2 || r.EmptyKeys != 2) fails++;
        }
        // 2026-11-22：被去重跳过的物品不能留图（「截图存图」按下一行的序号命名，最后一个重复物品曾留下一张表里不引用的图）
        using (var r = new Recorder(Path.Combine(dir, "files"), Prog(true)))
        {
            string Save(string name)
            {
                string rel = $"images/{r.NextRowNo:000}_图片.png";
                Directory.CreateDirectory(Path.Combine(r.Dir, "images"));
                File.WriteAllBytes(Path.Combine(r.Dir, rel), new byte[] { 1 });
                r.Set("名称", name); r.Set("库存", "1"); r.SetImage("图片", rel, Thumb(10));
                return rel;
            }
            bool Exists(string rel) => File.Exists(Path.Combine(r.Dir, rel));
            string a = Save("甲"); r.Record(dedupe, out _);
            string b = Save("甲"); r.Record(dedupe, out _);
            bool ok1 = Exists(a) && !Exists(b);
            Console.WriteLine($"  [{(ok1 ? "通过" : "失败")}] 重复物品的图被删掉：第 1 行的图{(Exists(a) ? "还在" : "没了")}，重复那次的图{(Exists(b) ? "还在" : "已删")}");
            string c = Save("乙"); bool rec = r.Record(dedupe, out _);
            bool ok2 = rec && c == b && Exists(c);
            Console.WriteLine($"  [{(ok2 ? "通过" : "失败")}] 下一个新物品照常用这个序号存图并记下：{c}{(Exists(c) ? " 在" : " 不在")}");
            if (!ok1) fails++;
            if (!ok2) fails++;
        }
        try { Directory.Delete(dir, true); } catch { }
        Console.WriteLine(fails == 0 ? "去重自测全部通过" : $"去重自测失败 {fails} 项");
        return fails == 0 ? 0 : 1;
    }
}
