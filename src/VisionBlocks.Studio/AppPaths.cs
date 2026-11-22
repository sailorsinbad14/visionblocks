using System.Reflection;

namespace VisionBlocks.Studio;

/// <summary>
/// 编辑器的文件放哪（2026-11-22）。三种运行方式，按顺序判断：
///   源码：往上找得到 VisionBlocks.sln → 用仓库里的 programs\、results\、studio-settings.json（和以前一样）。
///   便携文件夹：exe 旁边有 portable.txt，且旁边能写 → 全部放在 exe 旁边的 data\ 里，整个文件夹拷走就带走数据。
///   单个 exe（或便携文件夹放在了没写权限的地方）：程序和结果放「文档\VisionBlocks」，设置放 %AppData%\VisionBlocks。
/// 测试用：环境变量 VB_HOME 把「文档」和 AppData 两处换到这个目录下（自动化测试不能往用户真正的文档里写）。
/// 打包后示例程序（巡逻、背包）从 exe 里释放到 programs\示例\。
/// </summary>
internal static class AppPaths
{
    public enum Kind { Source, Portable, User }
    public static Kind Mode { get; }
    /// <summary>数据根目录（标题栏显示相对路径用）。</summary>
    public static string Root { get; }
    public static string Programs { get; }
    public static string Results { get; }
    public static string SettingsFile { get; }
    /// <summary>示例程序所在目录：源码模式就是 programs\，打包后是 programs\示例\。</summary>
    public static string Samples { get; }
    /// <summary>启动时要在活动里提示的话（例如便携版没有写权限、退回到「文档」）。</summary>
    public static string? Warning { get; }

    public static string SamplePatrol => Path.Combine(Samples, "t1", "patrol.vbp.json");

    static AppPaths()
    {
        string exeDir = AppContext.BaseDirectory;
        string? repo = FindRepo(exeDir);
        if (repo != null)
        {
            Mode = Kind.Source; Root = repo;
            Programs = Path.Combine(repo, "programs"); Results = Path.Combine(repo, "results");
            SettingsFile = Path.Combine(repo, "studio-settings.json"); Samples = Programs;
            return;
        }
        bool portable = File.Exists(Path.Combine(exeDir, "portable.txt"));
        string data = Path.Combine(exeDir, "data");
        if (portable && CanWrite(data))
        {
            Mode = Kind.Portable; Root = data;
            Programs = Path.Combine(data, "programs"); Results = Path.Combine(data, "results");
            SettingsFile = Path.Combine(data, "settings.json"); Samples = Path.Combine(Programs, "示例");
            return;
        }
        string? home = Environment.GetEnvironmentVariable("VB_HOME");
        string docs = home != null ? Path.Combine(home, "Documents", "VisionBlocks") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VisionBlocks");
        string appData = home != null ? Path.Combine(home, "AppData", "VisionBlocks") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VisionBlocks");
        Mode = Kind.User; Root = docs;
        Programs = Path.Combine(docs, "programs"); Results = Path.Combine(docs, "results");
        SettingsFile = Path.Combine(appData, "settings.json"); Samples = Path.Combine(Programs, "示例");
        if (portable) Warning = $"便携版所在的文件夹（{exeDir}）没有写权限，数据改存到 {docs}。想让数据跟着文件夹走，请把整个文件夹放到桌面、D 盘这类能写的地方";
    }

    private static string? FindRepo(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VisionBlocks.sln"))) dir = dir.Parent;
        return dir?.FullName;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把结果格式说明（中英两份）放到 Results 文件夹里，和 latest.json 挨着。
    /// 这是我们自己的文档，内容不同就覆盖（升级后说明跟着更新）。三种运行方式都写。
    /// </summary>
    public static void EnsureDocs()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("docs/", StringComparison.Ordinal)) continue;
            using var src = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            string target = Path.Combine(Results, name["docs/".Length..]);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) continue;
            Directory.CreateDirectory(Results);
            File.WriteAllBytes(target, bytes);
        }
    }

    /// <summary>打包后的第一次启动：把内嵌在 exe 里的示例程序释放到 Samples（已存在的文件不覆盖，用户改过的示例不会被冲掉）。</summary>
    public static int EnsureSamples()
    {
        if (Mode == Kind.Source) return 0;
        int written = 0;
        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("samples/", StringComparison.Ordinal)) continue;
            string rel = name["samples/".Length..].Replace('\\', '/');
            string target = Path.Combine(Samples, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(target);
            src.CopyTo(dst);
            written++;
        }
        return written;
    }
}
