using VisionBlocks.Core.Model;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-gridshift：到底走不满时网格跟着挪（2026-11-25）。只测纯函数，不截屏、不发输入；接进运行器以后的效果在真靶子上看。
/// 尺寸照背包示例：物品格 4×3、每格 300×200、行距 240，从 (450,170) 起；拖动热区「物品区」(420,150) 1440×870。
/// </summary>
public static class GridShiftSelfTest
{
    public static int Run()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}{(detail.Length > 0 ? "：" + detail : "")}");
        }
        var p = new VbProgram();
        var items = new GridDef { X = 450, Y = 170, W = 300, H = 200, Cols = 4, Rows = 3, GapX = 60, GapY = 40 };
        p.Grids["cats"] = new GridDef { X = 70, Y = 160, W = 280, H = 90, Cols = 1, Rows = 4, GapX = 0, GapY = 30 };
        p.Grids["items"] = items;
        var view = new RegionDef { X = 420, Y = 150, W = 1440, H = 870 };
        string Ys(List<RegionDef> cells) => string.Join(",", cells.Select(c => c.Y).Distinct()) + $"（{cells.Count} 格）";

        Console.WriteLine("1. 少走的距离按行距取余");
        Check("拖 720 只走 330，少走 390 → 下移 150", items.Phase(390) == 150);
        Check("拖 720 只走 90，少走 630 → 还是 150（错开整行不影响）", items.Phase(630) == 150);
        Check("往上拖到顶少走 390（-390）→ 下移 90（= 上移 150）", items.Phase(-390) == 90);
        Check("少走正好 3 行 720 → 不挪", items.Phase(720) == 0);
        Check("行距 = 格子高 + 行间距 = 240", items.Pitch == 240);

        Console.WriteLine("2. 挪过以后哪些格子完整露在列表里");
        var c0 = items.ShiftedCells(0, view);
        Check("不挪：和原来的 12 格一样", c0.Count == 12 && c0.Select((c, i) => (c, i)).All(t => t.c.X == items.Cell(t.i).X && t.c.Y == items.Cell(t.i).Y), Ys(c0));
        var c150 = items.ShiftedCells(150, view);
        Check("下移 150：行在 320 / 560 / 800，最后一行底 1000 还在列表里", Ys(c150) == "320,560,800（12 格）", Ys(c150));
        var c238 = items.ShiftedCells(238, view);
        Check("下移 238（= 上移 2）：第一行在 168，还在列表里（出界不到 3 px 不算）", Ys(c238) == "168,408,648（12 格）", Ys(c238));
        var c2 = items.ShiftedCells(2, view);
        Check("下移 2：三行都在", Ys(c2) == "172,412,652（12 格）", Ys(c2));
        var c90 = items.ShiftedCells(90, view);
        Check("下移 90：第一行 260、第三行底 940，都在；下面那行 980 露一半，跳过", Ys(c90) == "260,500,740（12 格）", Ys(c90));
        var tight = items.ShiftedCells(150, items.Bounds);
        Check("可见范围只有网格自己那么大：下移 150 后最后一行出界，只剩两行", Ys(tight) == "320,560（8 格）", Ys(tight));

        Console.WriteLine("3. 和靶子 --natural-end 的真实到底位置对一下");
        // 武器 4 行：内容高 = 20 + 4×240 − 40 + 20 = 960，可见 870 → 最多滚 90。拖 720 只走 90，行在 170 + r×240 − 90 = 80 / 320 / 560 / 800
        double phase = items.Phase(720 - 90);
        var rowsOnScreen = Enumerable.Range(0, 4).Select(r => 170 + r * 240 - 90).Where(y => y >= 150 - 3 && y + 200 <= 1020 + 3).ToList();
        var cells = items.ShiftedCells(phase, view);
        Check("格子正好压在完整露出来的第 2–4 行上，露一半的第 1 行跳过", cells.Select(c => c.Y).Distinct().SequenceEqual(rowsOnScreen), $"行 {string.Join(",", rowsOnScreen)}，格子 {Ys(cells)}");

        Console.WriteLine("4. 拖动热区对应哪个网格");
        Check("物品区 → 物品格（不是分类）", GridDef.InRegion(p, view)?.Name == "items");
        Check("一个网格都不挨着 → 没有", GridDef.InRegion(p, new RegionDef { X = 0, Y = 0, W = 50, H = 50 }) == null);

        Console.WriteLine("5. 程序文件里不多出计算出来的项");
        string json = System.Text.Json.JsonSerializer.Serialize(items, ProgramIO.Options);
        Check("网格存盘没有 pitch / bounds", !json.Contains("pitch") && !json.Contains("bounds"), json.Replace("\n", " ").Replace("  ", ""));

        Console.WriteLine(fails == 0 ? "网格跟着挪自测全部通过" : $"网格跟着挪自测失败 {fails} 项");
        return fails == 0 ? 0 : 1;
    }
}
