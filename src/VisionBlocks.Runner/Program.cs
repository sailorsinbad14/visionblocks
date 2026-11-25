using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenCvSharp;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Vision;
using VisionBlocks.Core.Win32;
using VisionBlocks.Overlay;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun probe [--monitor N] [--out DIR]
/// vbrun run &lt;program.vbp.json&gt; [--monitor N] [--seed N] [--duration 秒] [--capture wgc|gdi] [--overlay] [--out DIR] [--wait-user 秒]
///           [--humanize timing,path[,overshoot]] [--humanize-seed N]
/// 所有输入只发给本命令自己启动的 BenchTarget 进程，并且发之前检查它在前台。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) { Console.WriteLine("用法：vbrun probe | vbrun run <program.vbp.json> [选项]"); return 2; }
        var opts = ParseOpts(args.Skip(1).ToArray(), out var positional);
        try
        {
            return args[0] switch
            {
                "probe" => Probe.Run(opts),
                "snap" => Snap.Run(opts),
                "crop" => Snap.Crop(positional),
                "color" => Snap.Color(positional),
                "calib" => Snap.Calib(positional),
                "ocr" => Snap.Ocr(positional, opts),
                "selftest-editor" => SelfTest.Editor(positional),
                "shot" => SelfTest.Shot(opts),
                "selftest-input" => InputSelfTest.Run(),
                "selftest-guard" => GuardSelfTest.Run(),
                "selftest-inventory" => InventorySelfTest.Run(positional),
                "selftest-recorder" => RecorderSelfTest.Run(),
                "selftest-sizing" => SizingSelfTest.Run(),
                "selftest-humanize" => HumanizeSelfTest.Run(),
                "selftest-wheelpx" => WheelPxSelfTest.Run(positional),
                "selftest-dragpx" => DragPxSelfTest.Run(),
                "selftest-keymap" => KeymapSelfTest.Run(),
                "selftest-pattern" => PatternSelfTest.Run(positional),
                "selftest-gridshift" => GridShiftSelfTest.Run(),
                "selftest-activity" => ActivitySelfTest.Run(),
                "run" => RunProgram(positional.FirstOrDefault() ?? throw new ArgumentException("缺少程序文件路径"), opts),
                _ => throw new ArgumentException("未知子命令 " + args[0])
            };
        }
        catch (Exception e)
        {
            Console.WriteLine("出错：" + e.Message);
            return 1;
        }
    }

    /// <summary>把 --scene / --open-item / --scroll-step 原样转给靶子（背包场景用）。</summary>
    internal static string SceneArgs(Dictionary<string, string> o)
    {
        string s = "";
        foreach (var k in new[] { "scene", "open-item", "open-cat", "scroll-step", "drag-threshold", "drag-ratio" })
            if (o.TryGetValue(k, out var v)) s += $" --{k} {v}";
        if (o.ContainsKey("drag-inertia")) s += " --drag-inertia";   // 背包的按住拖动（2026-11-23）：松手后惯性
        foreach (var k in new[] { "twins", "natural-end" })          // 背包的同名物品、真实到底位置（2026-11-25）
            if (o.ContainsKey(k)) s += " --" + k;
        return s;
    }

    internal static Dictionary<string, string> ParseOpts(string[] a, out List<string> positional)
    {
        var d = new Dictionary<string, string>();
        positional = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--"))
            {
                string key = a[i][2..];
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) d[key] = a[++i];
                else d[key] = "true";
            }
            else positional.Add(a[i]);
        }
        return d;
    }

    internal static string Opt(Dictionary<string, string> o, string k, string def) => o.TryGetValue(k, out var v) ? v : def;

    internal static string NewOutDir(Dictionary<string, string> o, string prefix)
    {
        string dir = o.TryGetValue("out", out var v) ? v
            : Path.Combine(FindResultsRoot(), prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(dir);
    }

    private static string FindResultsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VisionBlocks.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? ".", "results");
    }

    private static int RunProgram(string programPath, Dictionary<string, string> o)
    {
        programPath = Path.GetFullPath(programPath);
        var program = ProgramIO.Load(programPath);
        var errs = ProgramIO.Validate(program);
        if (errs.Count > 0) { Console.WriteLine("程序有错误：\n  " + string.Join("\n  ", errs)); return 1; }

        int monitor = int.Parse(Opt(o, "monitor", "1"), CultureInfo.InvariantCulture);
        int seed = int.Parse(Opt(o, "seed", "20261116"), CultureInfo.InvariantCulture);
        double duration = double.Parse(Opt(o, "duration", "120"), CultureInfo.InvariantCulture);
        string size = Opt(o, "size", "1920x1080");
        string outDir = NewOutDir(o, "run");
        Console.WriteLine($"程序：{program.Name}（{programPath}）");
        Console.WriteLine($"输出：{outDir}");
        Console.WriteLine($"DPI 感知：{WindowInfo.CurrentDpiAwareness()}（2 = 每显示器）");

        string exe = TargetLauncher.FindTargetExe();
        var (proc, hwnd) = TargetLauncher.Launch(exe,
            $"--log \"{Path.Combine(outDir, "target")}\" --seed {seed} --monitor {monitor} --size {size} --duration {duration + 15}" + SceneArgs(o));
        if (!program.Target.AllowedProcesses.Contains(proc.ProcessName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"进程 {proc.ProcessName} 不在程序的白名单里");
        Console.WriteLine($"靶子 PID {proc.Id}，客户区 {(WindowInfo.TryGetClientOnScreen(hwnd, out var cr) ? cr.ToString() : "?")}，DPI {WindowInfo.DpiOf(hwnd)}");

        using var log = new BotLog(Path.Combine(outDir, "bot.jsonl"));
        log.Write(new { ev = "meta", program = program.Name, targetPid = proc.Id, monitor, seed, size, qpcFreq = Stopwatch.Frequency, t = BotLog.Now });
        IFrameSource source = Opt(o, "capture", "wgc") == "gdi" ? new GdiFrameSource() : new WgcFrameSource();
        source.FrameArrived += (m, seq, arr, sys) =>
            log.Write(new { ev = "frame", seq, fid = FrameCounter.Decode(m, m.Width / 1920.0), arr, sys, w = m.Width, h = m.Height });
        source.Start(hwnd);
        if (source is WgcFrameSource wgc) Console.WriteLine("WGC：" + wgc.BorderNote);

        var guard = new InputGuard(hwnd);
        using var input = new SendInputBackend(guard, log) { CloseImeBeforeKeys = program.Target.CloseImeBeforeKeys };
        // --humanize timing,path[,overshoot] [--humanize-seed N]，和编辑器设置里的「鼠标操作随机化」同一套
        if (o.TryGetValue("humanize", out var hz))
        {
            var parts = hz.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            int hseed = int.Parse(Opt(o, "humanize-seed", Random.Shared.Next(1, int.MaxValue).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            input.Humanizer = new Humanizer(parts.Contains("timing"), parts.Contains("path"), hseed, parts.Contains("overshoot"));
            Console.WriteLine($"鼠标操作随机化：{hz}，种子 {hseed}");
        }
        if (o.ContainsKey("ime-test")) Console.WriteLine($"输入法测试：已把靶子切到中文 → {ImeGuard.ForceChinese(hwnd)}");
        var ime = ImeGuard.Get(hwnd);
        Console.WriteLine($"靶子输入法：{ime}{(ime.WillEatLetters ? "（会截走字母键）" : "")}；发键前切英文：{program.Target.CloseImeBeforeKeys}");
        log.Write(new { ev = "imeAtStart", state = ime.ToString(), eats = ime.WillEatLetters, t = BotLog.Now });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
        bool paused = false;
        using var hotkeys = new HotkeyService();
        if (hotkeys.Error != null) Console.WriteLine("热键注册失败：" + hotkeys.Error + "（急停改用 Ctrl+C）");
        hotkeys.Toggle += () => { paused = !paused; Console.WriteLine(paused ? "已暂停" : "继续"); };
        bool estopped = false;
        hotkeys.EmergencyStop += () => { estopped = true; log.Write(new { ev = "emergencyStop", t = BotLog.Now }); input.Abort("急停热键"); cts.Cancel(); Console.WriteLine("急停！"); };
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; input.Abort("Ctrl+C"); cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => input.Abort("进程退出");

        OverlayWindow? overlay = o.ContainsKey("overlay") ? OverlayWindow.StartOnOwnThread(hwnd, program.Base.W) : null;
        if (overlay != null) overlay.StateSampled += (vis, o, c) => log.Write(new { ev = "overlay", vis, ov = new[] { o.X, o.Y, o.W, o.H }, cl = new[] { c.X, c.Y, c.W, c.H }, aligned = o == c, t = BotLog.Now });
        int waitUser = (int)(double.Parse(Opt(o, "wait-user", "20"), CultureInfo.InvariantCulture) * 1000);
        if (!TargetLauncher.EnsureForeground(hwnd, waitUser)) Console.WriteLine("  靶子仍不在前台：输入会被守卫拦下（这是预期的安全行为）。");

        using var runner = new ProgramRunner(new RunContext
        {
            Program = program, ProgramDir = Path.GetDirectoryName(programPath)!, Source = source, Input = input, Log = log,
            TargetHwnd = hwnd, Overlay = overlay, IsPaused = () => paused, OutputDir = outDir, ProgramPath = programPath
        });
        var sw = Stopwatch.StartNew();
        string result;
        try { result = runner.RunAsync(cts.Token).GetAwaiter().GetResult(); }
        catch (Exception e) { result = "异常：" + e.Message; input.ReleaseAll(); }
        if (estopped) runner.Manifest?.Finish("emergencyStop", runner.RowsRecorded, runner.Duplicates, runner.EmptyKeys);
        Console.WriteLine($"运行结束：{result}，用时 {sw.Elapsed.TotalSeconds:0.0}s；求值 {runner.Evaluations} 次，按键 {runner.Keys}，点击 {runner.Clicks}，拦截 {guard.Blocked}，点后确认失败 {runner.ConfirmFails}，等待超时 {runner.WaitTimeouts}"
            + (Recorder.FieldsOf(program).Count > 0 ? $"；滚轮 {runner.Wheels}，读文字 {runner.Reads}，记录 {runner.RowsRecorded} 行，重复跳过 {runner.Duplicates} 次" : "")
                + (runner.EmptyKeys > 0 ? $"，去重字段读空 {runner.EmptyKeys} 次" : "")
                + (runner.Rereads > 0 ? $"，读空后放大再读 {runner.Rereads} 次（读出 {runner.RereadHits}）" : "")
                + (runner.Classifies > 0 ? $"，认图案 {runner.Classifies} 次（分不太开 {runner.PatternClose}，有点像但没过门槛 {runner.PatternMidway}）" : ""));

        overlay?.CloseFromAnyThread();
        source.Dispose();
        Thread.Sleep(500);
        TargetLauncher.CloseGracefully(proc);
        string summary = Path.Combine(outDir, "target", "summary.json");
        if (File.Exists(summary)) Console.WriteLine("靶子统计：" + File.ReadAllText(summary).Replace("\n", " ").Replace("  ", ""));
        return 0;
    }
}
