namespace VisionBlocks.Core.Input;

/// <summary>
/// 编辑器快捷键（2026-11-23）：组合键文字、默认键表、撞键和规则检查。不依赖界面，vbrun selftest-keymap 直接测。
/// 组合键写成「Ctrl+Alt+Shift+键」，键名用这里的词表（字母、数字、F1–F12、方向键、Delete、Comma 等），大小写不敏感。
/// 编辑动作只在编辑器窗口里生效；暂停、急停是运行时注册的全局热键（RegisterHotKey），规矩更严：
/// 必须带 Ctrl 或 Alt（模拟按键也会触发全局热键，「按键」积木一次只按一个键，带了修饰键就不会被程序自己按到），急停不能清空。
/// </summary>
public static class Keymap
{
    public readonly record struct Gesture(bool Ctrl, bool Alt, bool Shift, string Key)
    {
        public override string ToString() => (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + Key;
        public bool HasCtrlOrAlt => Ctrl || Alt;
    }

    /// <summary>键名 ↔ 虚拟键码。</summary>
    private static readonly (string Name, ushort Vk)[] KeyTable = BuildKeys();

    private static (string, ushort)[] BuildKeys()
    {
        var l = new List<(string, ushort)>();
        for (char c = 'A'; c <= 'Z'; c++) l.Add((c.ToString(), c));
        for (char c = '0'; c <= '9'; c++) l.Add((c.ToString(), c));
        for (int i = 1; i <= 12; i++) l.Add(("F" + i, (ushort)(0x70 + i - 1)));
        l.AddRange(new (string, ushort)[]
        {
            ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Esc", 0x1B), ("Backspace", 0x08),
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("Insert", 0x2D), ("Delete", 0x2E), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
            ("Comma", 0xBC), ("Period", 0xBE), ("Minus", 0xBD), ("Equals", 0xBB), ("Slash", 0xBF), ("Semicolon", 0xBA),
            ("Quote", 0xDE), ("BracketLeft", 0xDB), ("BracketRight", 0xDD), ("Backslash", 0xDC), ("Backquote", 0xC0),
        });
        return l.ToArray();
    }

    /// <summary>虚拟键码 → 键名；小键盘数字当成普通数字。不认识的（修饰键本身、媒体键等）返回 null。</summary>
    public static string? NameOfVk(int vk)
    {
        if (vk is >= 0x60 and <= 0x69) vk = '0' + (vk - 0x60);
        foreach (var (n, v) in KeyTable) if (v == vk) return n;
        return null;
    }

    public static ushort VkOf(string key)
    {
        foreach (var (n, v) in KeyTable) if (string.Equals(n, key, StringComparison.OrdinalIgnoreCase)) return v;
        return 0;
    }

    private static string? Canonical(string key)
    {
        foreach (var (n, _) in KeyTable) if (string.Equals(n, key, StringComparison.OrdinalIgnoreCase)) return n;
        return key.ToLowerInvariant() switch { "escape" => "Esc", "del" => "Delete", "return" => "Enter", "," => "Comma", "." => "Period", "pgup" => "PageUp", "pgdn" => "PageDown", _ => null };
    }

    /// <summary>「Ctrl+Shift+K」「alt + up」「F5」都认；修饰键可以写 Control。必须正好一个非修饰键。</summary>
    public static bool TryParse(string? text, out Gesture g)
    {
        g = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, alt = false, shift = false;
        string? key = null;
        foreach (var raw in text.Split('+'))
        {
            string p = raw.Trim();
            if (p.Length == 0) return false;
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default:
                    if (key != null || Canonical(p) is not { } k) return false;
                    key = k;
                    break;
            }
        }
        if (key == null) return false;
        g = new Gesture(ctrl, alt, shift, key);
        return true;
    }

    public sealed record ActionDef(string Id, string Group, string? Default, bool Global = false);

    /// <summary>积木架上能配快捷键的积木（和编辑器积木架的类型名一致），默认都没有键。</summary>
    public static readonly string[] PaletteTypes =
    {
        "repeatUntil", "repeat", "if", "waitUntil", "stop", "forEachCell", "untilNoNewRows",
        "cond:image", "cond:color", "cond:text", "readText", "saveImage", "classify", "recordRow",
        "click", "key", "typeText", "wait", "wheel", "drag",
    };

    public const string Pause = "pause", EStop = "estop";

    /// <summary>所有动作，按设置里的分组排好。Default 为 null = 默认没有快捷键。</summary>
    public static readonly IReadOnlyList<ActionDef> Actions = new List<ActionDef>
    {
        new("launch", "target", null), new("refresh", "target", null),
        new("new", "file", "Ctrl+N"), new("open", "file", "Ctrl+O"), new("save", "file", "Ctrl+S"), new("sample", "file", null),
        new("undo", "file", "Ctrl+Z"), new("redo", "file", "Ctrl+Y"), new("settings", "file", "Ctrl+Comma"),
        new("run", "run", "F5"), new("stop", "run", "Shift+F5"), new("openResults", "run", null),
        new("freeze", "capture", "F6"),
        new("toolSelect", "capture", "1"), new("toolRegion", "capture", "2"), new("toolPoint", "capture", "3"), new("toolTemplate", "capture", "4"),
        new("toolColor", "capture", "5"), new("toolOcr", "capture", "6"), new("toolGrid", "capture", "7"),
        new("copy", "blocks", "Ctrl+C"), new("cut", "blocks", "Ctrl+X"), new("paste", "blocks", "Ctrl+V"), new("duplicate", "blocks", "Ctrl+D"),
        new("delete", "blocks", "Delete"), new("moveUp", "blocks", "Alt+Up"), new("moveDown", "blocks", "Alt+Down"),
        new("indent", "blocks", "Alt+Right"), new("outdent", "blocks", "Alt+Left"),
    }.Concat(PaletteTypes.Select(t => new ActionDef("pal:" + t, "palette", null)))
     .Concat(new[] { new ActionDef(Pause, "global", "Ctrl+Alt+F11", true), new ActionDef(EStop, "global", "Ctrl+Alt+F12", true) })
     .ToList();

    public static ActionDef? Find(string id) => Actions.FirstOrDefault(a => a.Id == id);

    /// <summary>默认键表叠上用户改过的（overrides：动作 → 组合键文字，"" = 用户清空了）。返回每个动作现在的键（"" = 没有）。</summary>
    public static Dictionary<string, string> Effective(IReadOnlyDictionary<string, string>? overrides)
    {
        var map = new Dictionary<string, string>();
        foreach (var a in Actions)
        {
            string v = overrides != null && overrides.TryGetValue(a.Id, out var o) ? o : a.Default ?? "";
            map[a.Id] = TryParse(v, out var g) ? g.ToString() : "";
            // 设置文件被手改成不合规矩的全局热键（不带 Ctrl / Alt）：退回默认
            if (a.Global && map[a.Id] != "" && !(TryParse(map[a.Id], out var gg) && gg.HasCtrlOrAlt)) map[a.Id] = a.Default ?? "";
        }
        // 急停不能没有：设置文件被改坏了也退回默认
        if (map[EStop] == "") map[EStop] = Find(EStop)!.Default!;
        return map;
    }

    /// <summary>这个组合键现在给了哪个动作（除了 exceptId 自己）。</summary>
    public static string? HolderOf(IReadOnlyDictionary<string, string> map, Gesture g, string exceptId)
    {
        string s = g.ToString();
        foreach (var (id, v) in map) if (id != exceptId && v == s) return id;
        return null;
    }

    /// <summary>系统自己要用、程序拿不到的组合。</summary>
    private static readonly string[] Reserved = { "Alt+F4", "Alt+Tab", "Alt+Esc", "Ctrl+Esc", "Ctrl+Alt+Delete", "Alt+Space" };

    /// <summary>
    /// 给 actionId 配 g（null = 清空）合不合规矩，不合规时返回原因代号：
    /// estopRequired 急停不能清空；globalNeedsMod 暂停 / 急停必须带 Ctrl 或 Alt；reserved 系统保留；holderIsEStop 撞上了急停（急停不能被替换成空）。
    /// 和其他动作撞键不算错：界面问用户要不要替换。
    /// </summary>
    public static string? Check(string actionId, Gesture? g, IReadOnlyDictionary<string, string> map)
    {
        var def = Find(actionId);
        if (g is not { } x) return actionId == EStop ? "estopRequired" : null;
        if (Reserved.Contains(x.ToString(), StringComparer.OrdinalIgnoreCase)) return "reserved";
        if (def is { Global: true } && !x.HasCtrlOrAlt) return "globalNeedsMod";
        if (HolderOf(map, x, actionId) == EStop) return "holderIsEStop";
        return null;
    }

    /// <summary>
    /// 光标在文本框里时，这个组合是不是留给文本框的（打字、移动光标、复制粘贴、撤销、删字）。是的话编辑器不当快捷键处理。
    /// </summary>
    public static bool IsTextEditing(Gesture g)
    {
        if (g.Alt) return false;
        if (!g.Ctrl) return !g.Key.StartsWith("F", StringComparison.Ordinal) || g.Key.Length == 1;   // 不带 Ctrl/Alt 的都留给文本框，F1–F12 除外
        return g.Key is "A" or "C" or "V" or "X" or "Z" or "Y" or "Left" or "Right" or "Up" or "Down" or "Home" or "End" or "Backspace" or "Delete" or "Insert";
    }
}
