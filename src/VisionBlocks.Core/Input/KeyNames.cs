namespace VisionBlocks.Core.Input;

/// <summary>按键名 → 虚拟键码，以及是否扩展键（方向键等要带 KEYEVENTF_EXTENDEDKEY）。</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, (ushort Vk, bool Ext)> Map = Build();

    private static Dictionary<string, (ushort, bool)> Build()
    {
        var m = new Dictionary<string, (ushort, bool)>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++) m[c.ToString()] = (c, false);
        for (char c = '0'; c <= '9'; c++) m[c.ToString()] = (c, false);
        for (int i = 1; i <= 12; i++) m["F" + i] = ((ushort)(0x70 + i - 1), false);
        m["Space"] = (0x20, false); m["Enter"] = (0x0D, false); m["Tab"] = (0x09, false);
        m["Esc"] = (0x1B, false); m["Backspace"] = (0x08, false);
        m["Shift"] = (0x10, false); m["Ctrl"] = (0x11, false); m["Alt"] = (0x12, false);
        m["Left"] = (0x25, true); m["Up"] = (0x26, true); m["Right"] = (0x27, true); m["Down"] = (0x28, true);
        m["Insert"] = (0x2D, true); m["Delete"] = (0x2E, true); m["Home"] = (0x24, true); m["End"] = (0x23, true);
        m["PageUp"] = (0x21, true); m["PageDown"] = (0x22, true);
        return m;
    }

    public static bool TryGet(string name, out ushort vk, out bool extended)
    {
        if (Map.TryGetValue(name.Trim(), out var v)) { vk = v.Vk; extended = v.Ext; return true; }
        vk = 0; extended = false;
        return false;
    }

    public static IEnumerable<string> All => Map.Keys;
}
