using VisionBlocks.Core.Win32;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-sizing（2026-11-22）：编辑器窗口尺寸和靶子尺寸的计算，用假的屏幕工作区测。
/// 编辑器要适配各种屏幕，可开发用的电脑只有两块屏，单屏、小屏、超宽屏没法真机测，所以把算法单独拿出来测。
/// 最后再列出本机实际检测到的屏（只读，不改任何东西）。
/// </summary>
public static class SizingSelfTest
{
    public static int Run()
    {
        int fails = 0;
        void Expect(string name, bool ok, string got)
        {
            if (!ok) fails++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}：{got}");
        }
        static string S(WindowSizing.Result r) => $"{r.W:0}×{r.H:0}{(r.Maximize ? "（最大化）" : "")}，最小 {r.MinW:0}×{r.MinH:0}";

        Console.WriteLine("编辑器窗口（输入：所在屏工作区，DIP）");
        var a = WindowSizing.Initial(1600, 952);     // 你的主屏 3200×2000 @200%
        Expect("主屏 1600×952 → 最大化", a.Maximize && a.MinW == 1280 && a.MinH == 672, S(a));
        var b = WindowSizing.Initial(2560, 1392);    // 你的副屏 3840×2160 @150%
        Expect("副屏 2560×1392 → 约 85%×88% 且不小于 1920×1080", !b.Maximize && b.W == 2176 && b.H == 1225, S(b));
        var c = WindowSizing.Initial(1280, 672);     // 1920×1080 笔记本 @150%
        Expect("笔记本 1280×672 → 最大化，正好是最小尺寸", c.Maximize && c.MinW == 1280 && c.MinH == 672, S(c));
        var d = WindowSizing.Initial(1097, 617);     // 1920×1080 @175%：比最小尺寸还小
        Expect("小屏 1097×617 → 最大化，最小尺寸不超过工作区", d.Maximize && d.MinW == 1097 && d.MinH == 617, S(d));
        var e = WindowSizing.Initial(3840, 2088);    // 4K @100%
        Expect("4K 3840×2088 → 居中 3264×1837", !e.Maximize && e.W == 3264 && e.H == 1837, S(e));
        foreach (var r in new[] { a, b, c, d, e })
            if (r.W < r.MinW || r.H < r.MinH) { fails++; Console.WriteLine($"  [失败] 尺寸比最小值还小：{S(r)}"); }

        Console.WriteLine("靶子客户区（输入：所选屏工作区，物理像素）");
        void T(string name, int w, int h, int ew, int eh) { var (tw, th) = WindowSizing.TargetClient(w, h); Expect(name, tw == ew && th == eh, $"{tw}×{th}"); }
        T("主屏工作区 3200×1904 → 1920×1080 不变", 3200, 1904, 1920, 1080);
        T("副屏工作区 3840×2088 → 1920×1080 不变", 3840, 2088, 1920, 1080);
        T("1080p 工作区 1920×1032 → 缩到 1584×891", 1920, 1032, 1584, 891);
        T("1366×728 → 缩到 1040×585", 1366, 728, 1040, 585);

        Console.WriteLine("本机检测到的屏（只读）：");
        foreach (var s in Screens.All())
            Console.WriteLine($"  {s.Device}  {(s.Primary ? "主屏" : s.Position(Screens.All()[0]))}  {s.Bounds.W}×{s.Bounds.H}  缩放 {s.ScalePercent}%  工作区 {s.WorkDipW:0}×{s.WorkDipH:0} DIP");
        Console.WriteLine(fails == 0 ? "全部通过" : $"失败 {fails} 项");
        return fails == 0 ? 0 : 1;
    }
}
