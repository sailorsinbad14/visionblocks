using OpenCvSharp;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Vision;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-inventory grid.png detail.png outDir（2026-11-20）：
/// 用编辑器同一套操作（ProgramEditor）拼一遍背包程序：建网格、用积木面板的默认积木、改参数、在「当前格」上试一下、试读，
/// 最后校验并检查 JSON 往返。和 programs\inv1\inventory.vbp.json 结构一致，但全部走编辑器的 API。
/// </summary>
public static class InventorySelfTest
{
    public static int Run(List<string> a)
    {
        if (a.Count < 3) { Console.WriteLine("用法：vbrun selftest-inventory grid.png detail.png outDir"); return 2; }
        string dir = Path.GetFullPath(a[2]);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        using var gridImg = Cv2.ImRead(Path.GetFullPath(a[0]));
        using var detailImg = Cv2.ImRead(Path.GetFullPath(a[1]));
        using var gridF = new NormFrame(gridImg.Clone(), 1.0, 1920);
        using var detailF = new NormFrame(detailImg.Clone(), 1.0, 1920);
        var ed = ProgramEditor.CreateNew(dir, "编辑器自测：背包");

        // 资源：画布上拖出来的热区、两张网格（拖第一格，再在属性面板改行列和间距）、模板、颜色
        string items = ed.AddRegion("物品区", new RegionDef { X = 420, Y = 150, W = 1440, H = 870 });
        string title = ed.AddRegion("详情标题区", new RegionDef { X = 600, Y = 90, W = 220, H = 80 });
        string name = ed.AddRegion("物品名", new RegionDef { X = 1290, Y = 500, W = 400, H = 64 });
        string stock = ed.AddRegion("库存", new RegionDef { X = 1290, Y = 580, W = 400, H = 56 });
        string pic = ed.AddRegion("图片", new RegionDef { X = 1322, Y = 152, W = 336, H = 336 });
        string cats = ed.AddGrid("分类", new RegionDef { X = 70, Y = 160, W = 280, H = 90 });
        ed.Program.Grids[cats].Cols = 1; ed.Program.Grids[cats].Rows = 4; ed.Program.Grids[cats].GapX = 0; ed.Program.Grids[cats].GapY = 30;
        string cells = ed.AddGrid("物品格", new RegionDef { X = 450, Y = 170, W = 300, H = 200 });
        ed.Program.Grids[cells].GapX = 60; ed.Program.Grids[cells].GapY = 40;
        var (tpl, warn) = ed.AddTemplate("详情页标题", detailF, new RegionDef { X = 634, Y = 104, W = 132, H = 46 });
        string border = ed.AddColor("格子边框", gridF, 451, 260);
        // 吸色只取到一个像素附近，边框只有 3 像素宽，取到的范围不对：和用户一样，在属性面板里手动改
        ed.Program.Colors[border].Lower = new[] { 0, 0, 185 };
        ed.Program.Colors[border].Upper = new[] { 179, 50, 255 };
        Console.WriteLine($"资源：热区 {ed.Program.Regions.Count}，网格 {ed.Program.Grids.Count}（{ed.Program.Grids[cells]}），模板 {tpl}{(warn != null ? "（" + warn + "）" : "")}，颜色 {border}");

        // 积木：都从 CreateDefault（积木面板）出来，再改参数；Insert 规则和界面一致
        var catLoop = (ForEachCellBlock)ed.CreateDefault("forEachCell");
        bool defaultGridOk = catLoop.Grid == cats;
        ed.Insert(catLoop, null);
        var clickCat = (ClickBlock)ed.CreateDefault("click");
        clickCat.Region = VbProgram.CurrentCell; clickCat.Confirm = ConfirmMode.None;
        ed.Insert(clickCat, catLoop, into: true);
        ed.Insert(new WaitBlock { Ms = 300 }, clickCat);
        var readCat = (ReadTextBlock)ed.CreateDefault("readText");
        readCat.Region = VbProgram.CurrentCell; readCat.Field = "分类";
        ed.Insert(readCat, catLoop, into: true);
        var pages = (UntilNoNewRowsBlock)ed.CreateDefault("untilNoNewRows");
        ed.Insert(pages, catLoop, into: true);
        var cellLoop = (ForEachCellBlock)ed.CreateDefault("forEachCell");
        cellLoop.Grid = cells;
        ed.Insert(cellLoop, pages, into: true);
        var notEmpty = new IfBlock { Cond = new ColorCondition { Color = border, Region = VbProgram.CurrentCell, Op = ">", Ratio = 0.008 } };
        ed.Insert(notEmpty, cellLoop, into: true);
        ed.Insert(new ClickBlock { Region = VbProgram.CurrentCell, Confirm = ConfirmMode.Condition, ConfirmTimeoutMs = 2000,
            ConfirmCond = new ImageCondition { Template = tpl, Region = title, Threshold = 0.9 } }, notEmpty, into: true);
        var opened = new IfBlock { Cond = new ImageCondition { Template = tpl, Region = title, Threshold = 0.9 } };
        ed.Insert(opened, notEmpty, into: true);
        var readName = (ReadTextBlock)ed.CreateDefault("readText");
        readName.Region = name; readName.Field = "名称";
        ed.Insert(readName, opened, into: true);
        ed.Insert(new ReadTextBlock { Region = stock, Field = "库存", NumbersOnly = true }, readName);
        var save = (SaveImageBlock)ed.CreateDefault("saveImage");
        save.Region = pic;
        ed.Insert(save, opened, into: true);
        var rec = (RecordRowBlock)ed.CreateDefault("recordRow");
        ed.Insert(rec, save);
        // 2026-11-25：新放的「记录一行」默认按同一层里它上面第一个读出来的字段去重（这里是「名称」，不是外层先读的「分类」）
        ed.DefaultDedupe(rec);
        var defaultDedupe = string.Join("+", rec.DedupeBy);
        rec.DedupeBy = new() { "名称" };
        ed.Insert(new KeyBlock { Key = "Esc" }, rec);
        ed.Insert(new WaitUntilBlock { TimeoutMs = 2000, Cond = new ImageCondition { Template = tpl, Region = title, Threshold = 0.9, Negate = true } }, opened, into: true);
        var wheel = (WheelBlock)ed.CreateDefault("wheel");
        wheel.Region = items; wheel.Notches = 3;
        ed.Insert(wheel, cellLoop);
        ed.Insert(new WaitBlock { Ms = 300 }, wheel);

        Console.WriteLine("程序：");
        void Print(List<Block> l, string ind) { foreach (var b in l) { Console.WriteLine(ind + BlockText.Summary(b, ed.Program)); if (b is IContainerBlock c) Print(c.Body, ind + "    "); } }
        Print(ed.Program.Program, "  ");

        // 「试一下」「试读」：「当前格」用所在网格的第一格（编辑器里也是这么做的）
        using var tester = new ConditionTester();
        int bad = 0;
        void Check(string label, bool ok, string detail) { if (!ok) bad++; Console.WriteLine($"  [{(ok ? "通过" : "不符")}] {label}：{detail}"); }
        Check("新建「对每一格」默认用第一个网格", defaultGridOk, catLoop.Grid);
        Check("新放的「记录一行」默认按「名称」去重（同一层里它上面第一个读出来的字段，不是外层的「分类」）", defaultDedupe == "名称", defaultDedupe);
        var cell0 = ed.Program.Grids[cells].Cell(0);
        var r1 = tester.TestAsync(ed.Program, ed.Dir, notEmpty.Cond, gridF, cell0).GetAwaiter().GetResult();
        Check("网格页第一格不是空格子", r1.Hit, r1.Detail);
        // 武器有 13 个：前 3 行满，第 4 行只有第 1 格，所以第 4 行第 2 格的位置是空的（只露出上半截，够判断）
        var r2 = tester.TestAsync(ed.Program, ed.Dir, notEmpty.Cond, gridF, new RegionDef { X = 810, Y = 890, W = 300, H = 130 }).GetAwaiter().GetResult();
        Check("第 4 行第 2 格（空）判为空", !r2.Hit, r2.Detail);
        var r3 = tester.TestAsync(ed.Program, ed.Dir, opened.Cond, detailF).GetAwaiter().GetResult();
        Check("详情页上看到标题", r3.Hit, r3.Detail);
        var r4 = tester.TestAsync(ed.Program, ed.Dir, opened.Cond, gridF).GetAwaiter().GetResult();
        Check("网格页上看不到标题", !r4.Hit, r4.Detail);
        var (_, nm, _) = tester.ReadAsync(detailF, ed.Program.Regions[name]).GetAwaiter().GetResult();
        Check("试读物品名", nm == "精钢弯刀", nm);
        var (_, st, _) = tester.ReadAsync(detailF, ed.Program.Regions[stock]).GetAwaiter().GetResult();
        string stDigits = new(st.Where(char.IsAsciiDigit).ToArray());
        Check("试读库存（只要数字）", stDigits == "23", stDigits);
        var (_, cat, _) = tester.ReadAsync(gridF, ed.Program.Grids[cats].Cell(0)).GetAwaiter().GetResult();
        Check("试读第一个分类（当前格）", cat == "武器", cat);

        // 资源删除保护：网格在用就删不掉
        var uses = ed.RemoveResource("grid", cells);
        Check("在用的网格不能删", uses.Count > 0, string.Join("；", uses));
        Check("字段顺序", string.Join(",", Recorder.FieldsOf(ed.Program)) == "分类,名称,库存,图片", string.Join(",", Recorder.FieldsOf(ed.Program)));

        // 校验：「当前格」放在循环外要报错
        ed.Program.Program.Add(new ReadTextBlock { Region = VbProgram.CurrentCell, Field = "x" });
        var errOutside = ProgramIO.Validate(ed.Program);
        Check("「当前格」放在循环外会报错", errOutside.Any(e => e.Contains("只能放在「对每一格」循环里面")), string.Join("；", errOutside));
        ed.Program.Program.RemoveAt(ed.Program.Program.Count - 1);
        var errs = ProgramIO.Validate(ed.Program);
        Console.WriteLine(errs.Count == 0 ? "校验：通过" : "校验错误：" + string.Join("；", errs));
        ed.Save();
        var back = ProgramIO.Load(ed.ProgramPath);
        bool roundTrip = System.Text.Json.JsonSerializer.Serialize(back, ProgramIO.Options) == System.Text.Json.JsonSerializer.Serialize(ed.Program, ProgramIO.Options);
        Console.WriteLine($"JSON 往返一致：{roundTrip}；已保存 {ed.ProgramPath}");
        Console.WriteLine(bad == 0 && errs.Count == 0 && roundTrip ? "全部通过" : $"{bad} 项不符");
        return bad == 0 && errs.Count == 0 && roundTrip ? 0 : 1;
    }
}
