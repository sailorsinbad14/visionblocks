using System.Text.Json;

namespace VisionBlocks.Studio;

/// <summary>
/// 编辑器设置，存在 studio-settings.json（位置见 AppPaths.SettingsFile）。
/// 包括语言、主题、显示练习靶子、鼠标操作随机化。
/// 旧文件只有 {"Lang":"zh"}，缺的项用默认值。
/// </summary>
internal sealed class AppSettings
{
    public string Lang { get; set; } = "zh";
    /// <summary>system（跟随 Windows）/ light / dark。</summary>
    public string Theme { get; set; } = "system";
    /// <summary>在 ① 里显示「场景」「启动靶子」。默认关：正式使用时不需要练习靶子。</summary>
    public bool ShowPracticeTarget { get; set; }
    /// <summary>鼠标操作随机化的总开关，默认关，打开前要确认（用来测试自己做的游戏能不能识别出自动操作）。</summary>
    public bool Humanize { get; set; }
    /// <summary>位置和时机：点在热区内的随机位置，按住、等待时长随机。</summary>
    public bool HumanizeTiming { get; set; } = true;
    /// <summary>移动轨迹：沿弯曲路径、先快后慢移到目标点。</summary>
    public bool HumanizePath { get; set; } = true;
    /// <summary>轨迹终点前轻微过冲再回正（默认关）。</summary>
    public bool HumanizeOvershoot { get; set; }
    /// <summary>程序树滚动时把外层作用域钉在顶部（2026-11-23，默认开）。</summary>
    public bool StickyScope { get; set; } = true;

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { Current = new(); }   // 设置文件坏了就全用默认值
        if (Current.Lang is not ("zh" or "en")) Current.Lang = "zh";
        if (Current.Theme is not ("system" or "light" or "dark")) Current.Theme = "system";
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }   // 写不进去（只读目录）时这次照常用，只是下次启动恢复默认
    }
}
