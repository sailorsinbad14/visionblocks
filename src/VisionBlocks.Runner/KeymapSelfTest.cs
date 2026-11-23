using VisionBlocks.Core.Input;
using VisionBlocks.Core.Runtime;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-keymap（2026-11-23）：编辑器快捷键的数据层。
/// A. 组合键文字解析和写回；B. 键名 ↔ 虚拟键码；C. 默认键表（不重复、用户改过的叠上去、急停不能没有）；
/// D. 规矩（急停不能清空、暂停 / 急停必须带 Ctrl 或 Alt、系统保留、撞上急停）和撞键查询；
/// E. 光标在输入框里时哪些键留给输入框；
/// F. 全局热键：自定义组合能注册；同一个组合被占着时报「已被占用」，Probe 也查得出来；暂停清空时不注册。
///    F 用 Ctrl+Alt+Shift+F7/F8/F9，这台电脑上别的程序要是正好占了它们，F 会误报。
/// </summary>
public static class KeymapSelfTest
{
    private static int _fails;

    private static void Check(string name, bool pass, string detail)
    {
        if (!pass) _fails++;
        Console.WriteLine($"  [{(pass ? "通过" : "失败")}] {name}：{detail}");
    }

    public static int Run()
    {
        _fails = 0;
        Console.WriteLine("A. 组合键文字");
        foreach (var (input, want) in new[] { ("Ctrl+Shift+K", "Ctrl+Shift+K"), ("alt + up", "Alt+Up"), ("control+comma", "Ctrl+Comma"), ("F5", "F5"), ("shift+f5", "Shift+F5"),
                     ("Shift+Ctrl+K", "Ctrl+Shift+K"), ("ctrl+escape", "Ctrl+Esc"), ("Ctrl+Alt+F12", "Ctrl+Alt+F12"), ("delete", "Delete"), ("1", "1") })
        {
            bool ok = Keymap.TryParse(input, out var g) && g.ToString() == want;
            Check($"「{input}」", ok, ok ? g.ToString() : "解析失败或写回不一样");
        }
        var bad = new[] { "", "Ctrl+", "Ctrl+Shift", "A+B", "Ctrl+Foo", "Ctrl++", "  " };
        Check("不合法的都拒绝", bad.All(b => !Keymap.TryParse(b, out _)), string.Join("、", bad.Select(b => $"「{b}」")));

        Console.WriteLine("B. 键名和虚拟键码");
        Check("字母 / 小键盘数字 / 修饰键", Keymap.NameOfVk(0x41) == "A" && Keymap.NameOfVk(0x63) == "3" && Keymap.NameOfVk(0x10) == null && Keymap.NameOfVk(0xA2) == null,
            $"0x41={Keymap.NameOfVk(0x41)}，0x63={Keymap.NameOfVk(0x63)}，0x10={Keymap.NameOfVk(0x10) ?? "无"}");
        Check("F12 / Comma / Up", Keymap.VkOf("F12") == 0x7B && Keymap.VkOf("comma") == 0xBC && Keymap.VkOf("Up") == 0x26, $"{Keymap.VkOf("F12"):X} {Keymap.VkOf("comma"):X} {Keymap.VkOf("Up"):X}");

        Console.WriteLine("C. 默认键表");
        var def = Keymap.Effective(null);
        var dups = def.Where(kv => kv.Value != "").GroupBy(kv => kv.Value).Where(g => g.Count() > 1).Select(g => g.Key + "：" + string.Join("/", g.Select(x => x.Key))).ToList();
        Check("默认键没有重复", dups.Count == 0, dups.Count == 0 ? $"{def.Count(kv => kv.Value != "")} 个动作有默认键，共 {def.Count} 个动作" : string.Join("；", dups));
        Check("积木架 19 块都能配键", Keymap.PaletteTypes.All(t => def.ContainsKey("pal:" + t)) && Keymap.PaletteTypes.Length == 19, $"{Keymap.PaletteTypes.Length} 块");
        Check("几个关键默认值", def["save"] == "Ctrl+S" && def["moveUp"] == "Alt+Up" && def["settings"] == "Ctrl+Comma" && def[Keymap.EStop] == "Ctrl+Alt+F12" && def[Keymap.Pause] == "Ctrl+Alt+F11" && def["toolRegion"] == "2",
            $"保存 {def["save"]}，上移 {def["moveUp"]}，设置 {def["settings"]}，急停 {def[Keymap.EStop]}");
        var ov = Keymap.Effective(new Dictionary<string, string> { ["save"] = "ctrl+shift+s", ["copy"] = "", [Keymap.EStop] = "", ["pal:click"] = "Ctrl+1", ["undo"] = "乱写" });
        Check("改过的叠上去；清空的为空；急停清空了退回默认；写坏的当成没有", ov["save"] == "Ctrl+Shift+S" && ov["copy"] == "" && ov[Keymap.EStop] == "Ctrl+Alt+F12" && ov["pal:click"] == "Ctrl+1" && ov["undo"] == "",
            $"保存 {ov["save"]}，复制「{ov["copy"]}」，急停 {ov[Keymap.EStop]}，点击 {ov["pal:click"]}，撤销「{ov["undo"]}」");

        var hand = Keymap.Effective(new Dictionary<string, string> { [Keymap.EStop] = "F9", [Keymap.Pause] = "Shift+P" });
        Check("设置文件被手改成不带 Ctrl / Alt 的暂停、急停：退回默认", hand[Keymap.EStop] == "Ctrl+Alt+F12" && hand[Keymap.Pause] == "Ctrl+Alt+F11", $"急停 {hand[Keymap.EStop]}，暂停 {hand[Keymap.Pause]}");
        Check("「启动靶子」「刷新」也在表里", def.ContainsKey("launch") && def.ContainsKey("refresh"), "默认没有键");

        Console.WriteLine("D. 规矩和撞键");
        Keymap.Gesture G(string s) { Keymap.TryParse(s, out var g); return g; }
        var rules = new (string Name, string? Got, string? Want)[]
        {
            ("急停不能清空", Keymap.Check(Keymap.EStop, null, def), "estopRequired"),
            ("暂停可以清空", Keymap.Check(Keymap.Pause, null, def), null),
            ("急停不带修饰键", Keymap.Check(Keymap.EStop, G("F9"), def), "globalNeedsMod"),
            ("暂停只带 Shift", Keymap.Check(Keymap.Pause, G("Shift+F9"), def), "globalNeedsMod"),
            ("急停 Ctrl+Alt+F9", Keymap.Check(Keymap.EStop, G("Ctrl+Alt+F9"), def), null),
            ("保存配成 Alt+F4", Keymap.Check("save", G("Alt+F4"), def), "reserved"),
            ("保存撞上急停的键", Keymap.Check("save", G("Ctrl+Alt+F12"), def), "holderIsEStop"),
            ("编辑动作不带修饰键可以（只在编辑器里）", Keymap.Check("save", G("F9"), def), null),
        };
        foreach (var (name, got, want) in rules) Check(name, got == want, got ?? "合规");
        Check("Ctrl+S 现在给了保存", Keymap.HolderOf(def, G("Ctrl+S"), "new") == "save" && Keymap.HolderOf(def, G("Ctrl+S"), "save") == null, Keymap.HolderOf(def, G("Ctrl+S"), "new") ?? "无");

        Console.WriteLine("E. 输入框里留给输入框的键");
        var text = new (string Key, bool Want)[]
        {
            ("A", true), ("1", true), ("Delete", true), ("Backspace", true), ("Shift+Left", true), ("Ctrl+C", true), ("Ctrl+V", true), ("Ctrl+Z", true), ("Ctrl+A", true),
            ("Ctrl+S", false), ("F5", false), ("Shift+F5", false), ("Alt+Up", false), ("Ctrl+D", false), ("Ctrl+Comma", false),
        };
        var wrong = text.Where(t => Keymap.IsTextEditing(G(t.Key)) != t.Want).Select(t => t.Key).ToList();
        Check("15 个组合", wrong.Count == 0, wrong.Count == 0 ? "留给输入框：A、1、Delete、Backspace、Shift+Left、Ctrl+C/V/Z/A；照常生效：Ctrl+S、F5、Shift+F5、Alt+Up、Ctrl+D、Ctrl+Comma" : "不对：" + string.Join("、", wrong));

        Console.WriteLine("F. 全局热键（Ctrl+Alt+Shift+F7/F8/F9）");
        using (var hs = new HotkeyService("Ctrl+Alt+Shift+F9", "Ctrl+Alt+Shift+F8"))
        {
            Check("自定义组合能注册", hs.Error == null, hs.Error ?? "暂停 Ctrl+Alt+Shift+F9，急停 Ctrl+Alt+Shift+F8");
            using var other = new HotkeyService("", "Ctrl+Alt+Shift+F8");
            Check("同一个组合被占着：报已被占用", other.Error != null && other.Error.Contains("Ctrl+Alt+Shift+F8"), other.Error ?? "没报");
            Check("Probe 查得出被占用", !HotkeyService.Probe("Ctrl+Alt+Shift+F8") && HotkeyService.Probe("Ctrl+Alt+Shift+F7"), "F8 占着、F7 空着");
        }
        Check("放开以后 Probe 又能注册", HotkeyService.Probe("Ctrl+Alt+Shift+F8"), "F8");
        using (var noPause = new HotkeyService("", "Ctrl+Alt+Shift+F7"))
            Check("暂停清空时不注册、也不报错", noPause.Error == null, noPause.Error ?? "只注册了急停");

        Console.WriteLine(_fails == 0 ? "全部通过" : $"{_fails} 项失败");
        return _fails == 0 ? 0 : 1;
    }
}
