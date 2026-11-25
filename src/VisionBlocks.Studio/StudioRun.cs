using System.Diagnostics;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Vision;
using VisionBlocks.Core.Win32;
using VisionBlocks.Overlay;

namespace VisionBlocks.Studio;

/// <summary>
/// 编辑器里的一次运行：WGC 截图 + 前台守卫 + 输入法守卫 + 遮罩 + 全局热键（默认 Ctrl+Alt+F11 暂停/继续，Ctrl+Alt+F12 急停，设置里可改）。
/// 用户在编辑器里点「运行」时编辑器就在前台，所以能把前台交给靶子（由前台进程转交）。
/// </summary>
internal sealed class StudioRun
{
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _paused;
    public string OutDir { get; }
    /// <summary>这次运行的结果目录（data.csv、images\、manifest.json）。程序里没有「读文字」「截图存图」时不会创建。</summary>
    public string DataDir { get; }

    public StudioRun(string outDir) { OutDir = outDir; DataDir = Path.Combine(outDir, $"data-{DateTime.Now:HHmmss}"); }

    private SendInputBackend? _input;

    /// <summary>「■ 停止」按钮：和急停一样先关闸抬键，再取消。</summary>
    public void Stop() { _input?.Abort("停止按钮"); _cts.Cancel(); }
    public bool Paused => _paused;

    // 2026-11-22 改版：编辑器每秒轮询几次这些值来更新运行胶囊、置顶胶囊和任务栏（不在急停路径上加跨线程回调）
    private volatile bool _estopped;
    private int _blockedEvents;
    public bool EmergencyStopped => _estopped;
    public int BlockedEvents => Volatile.Read(ref _blockedEvents);
    public IntPtr Target { get; private set; }
    public bool HotkeysFailed { get; private set; }

    /// <summary>必须在 UI 线程上调用（遮罩是本线程的 WPF 窗口）。</summary>
    public async Task<string> RunAsync(ProgramEditor ed, IntPtr hwnd, Action<string> onLog, Action<string> onStatus)
    {
        Directory.CreateDirectory(OutDir);
        string botPath = Path.Combine(OutDir, "bot.jsonl");
        using var log = new BotLog(botPath);
        Target = hwnd;
        log.Line += line => { if (line.Contains("\"ev\":\"blocked\"", StringComparison.Ordinal)) Interlocked.Increment(ref _blockedEvents); FilterLog(line, onLog); };
        log.Write(new { ev = "meta", program = ed.Program.Name, studio = true, targetPid = WindowInfo.ProcessIdOf(hwnd), qpcFreq = Stopwatch.Frequency, t = BotLog.Now });

        var source = new WgcFrameSource();
        source.FrameArrived += (m, seq, arr, sys) =>
            log.Write(new { ev = "frame", seq, fid = FrameCounter.Decode(m, m.Width / 1920.0), arr, sys, w = m.Width, h = m.Height });
        source.Start(hwnd);

        var guard = new InputGuard(hwnd);
        var input = new SendInputBackend(guard, log) { CloseImeBeforeKeys = ed.Program.Target.CloseImeBeforeKeys };
        _input = input;
        var hs = AppSettings.Current;   // 设置里打开了「鼠标操作随机化」就按它跑；种子记进日志，可以复现
        if (hs.Humanize && (hs.HumanizeTiming || hs.HumanizePath))
        {
            input.Humanizer = new Humanizer(hs.HumanizeTiming, hs.HumanizePath, Random.Shared.Next(1, int.MaxValue), hs.HumanizeOvershoot);
            onLog($"鼠标操作随机化已打开：{(hs.HumanizeTiming ? "位置和时机" : "")}{(hs.HumanizeTiming && hs.HumanizePath ? "、" : "")}{(hs.HumanizePath ? "移动轨迹" : "")}{(hs.HumanizeOvershoot ? "（带过冲）" : "")}，种子 {input.Humanizer.Seed}");
        }
        EventHandler releaseOnExit = (_, _) => input.Abort("进程退出");
        AppDomain.CurrentDomain.ProcessExit += releaseOnExit;
        string pauseKey = AppSettings.KeyOf(Keymap.Pause), stopKey = AppSettings.KeyOf(Keymap.EStop);   // 设置里可以改（2026-11-23）
        var hotkeys = new HotkeyService(pauseKey, stopKey);   // 暂停可以清空（不注册），急停不能
        if (hotkeys.Error != null) { HotkeysFailed = true; onLog("⚠ 热键注册失败：" + hotkeys.Error + "。请用「■ 停止」按钮结束。"); }
        hotkeys.Toggle += () => { _paused = !_paused; onStatus(_paused ? $"已暂停（{hotkeys.PauseKey} 继续）" : "运行中"); };
        hotkeys.EmergencyStop += () => { _estopped = true; log.Write(new { ev = "emergencyStop", t = BotLog.Now }); input.Abort("急停热键"); _cts.Cancel(); onLog("■ 急停"); };   // 先置标志再取消：运行结束时编辑器据此显示「已急停」

        var overlay = new OverlayWindow(hwnd, ed.Program.Base.W);
        overlay.StateSampled += (vis, o, c) => log.Write(new { ev = "overlay", vis, ov = new[] { o.X, o.Y, o.W, o.H }, cl = new[] { c.X, c.Y, c.W, c.H }, aligned = o == c, t = BotLog.Now });
        overlay.Show();
        bool fg = WindowInfo.TrySetForeground(hwnd);
        onLog(fg ? "已把靶子切到前台" : "⚠ 没能把靶子切到前台，请点一下靶子窗口；在此之前所有输入都会被守卫拦下");

        var runner = new ProgramRunner(new RunContext
        {
            Program = ed.Program, ProgramDir = ed.Dir, Source = source, Input = input, Log = log,
            TargetHwnd = hwnd, Overlay = overlay, IsPaused = () => _paused, PauseKey = hotkeys.PauseKey,
            OutputDir = DataDir,   // 每次运行一个结果目录，不覆盖上一次的表
            ProgramPath = ed.ProgramPath
        });
        var sw = Stopwatch.StartNew();
        string result;
        try
        {
            onStatus("运行中");
            result = await Task.Run(() => runner.RunAsync(_cts.Token));
        }
        catch (Exception e) { result = "异常：" + e.Message; }
        finally
        {
            input.ReleaseAll();
            AppDomain.CurrentDomain.ProcessExit -= releaseOnExit;
            hotkeys.Dispose();
            source.Dispose();
            overlay.Close();
            runner.Dispose();
        }
        // 运行器只知道「已取消」，急停要在这里补上；然后更新 results\latest.json
        if (runner.Manifest is { } m)
        {
            try
            {
                if (_estopped) m.Finish("emergencyStop", runner.RowsRecorded, runner.Duplicates, runner.EmptyKeys);
                m.WriteLatest(AppPaths.Results);
            }
            catch (Exception e) { onLog("⚠ 写结果说明文件失败：" + e.Message); }
        }
        string summary = $"运行结束：{result}，用时 {sw.Elapsed.TotalSeconds:0.0}s；按键 {runner.Keys}，点击 {runner.Clicks}，拦截 {guard.Blocked}，点后确认失败 {runner.ConfirmFails}，等待超时 {runner.WaitTimeouts}"
            + (Recorder.FieldsOf(ed.Program).Count > 0 ? $"；记录 {runner.RowsRecorded} 行，重复跳过 {runner.Duplicates} 次" : "")
            + (runner.EmptyKeys > 0 ? $"；去重字段读空 {runner.EmptyKeys} 次（改按图片和其他字段区分，名称框可能太紧）" : "")
            + (runner.Rereads > 0 ? $"；读空后放大再读 {runner.Rereads} 次，读出字 {runner.RereadHits} 次" : "")
            + (runner.Classifies > 0 ? $"；认图案 {runner.Classifies} 次" + (runner.PatternClose + runner.PatternMidway > 0
                ? $"（分不太开 {runner.PatternClose} 次、有点像但没过门槛 {runner.PatternMidway} 次：可能有还没裁模板的新图案，看活动里的提醒）" : "") : "");
        log.Dispose();
        try { File.Copy(botPath, Path.Combine(OutDir, $"bot-{DateTime.Now:HHmmss}.jsonl"), true); } catch { }
        return summary;
    }

    private static readonly HashSet<string> Shown = new()
    {
        "runStart", "runEnd", "keydown", "clickConfirm", "wait", "blocked", "imeOff", "waitTimeout", "loopTimeout",
        "paused", "resumed", "emergencyStop", "noFrame", "frameBack", "sendFailed",
        "row", "pass", "loopCap"
    };

    private static void FilterLog(string line, Action<string> onLog)
    {
        // 帧和逐次求值太多，只把关键事件和 OCR 结果显示到界面上，全量都在 bot.jsonl 里
        int i = line.IndexOf("\"ev\":\"", StringComparison.Ordinal);
        if (i < 0) return;
        int j = line.IndexOf('"', i + 6);
        string ev = line[(i + 6)..j];
        if (Shown.Contains(ev) || (ev == "cond" && line.Contains("\"kind\":\"text\"")))
            onLog(line.Length > 220 ? line[..220] + "…" : line);
    }
}

/// <summary>编辑器里启动靶子（与 vbrun 的启动器逻辑相同：等客户区尺寸稳定才返回）。</summary>
internal static class BenchLauncher
{
    /// <summary>
    /// 2026-11-22：靶子做进了编辑器自己的 exe（Program.Main 的 --bench-target 分支），这样单个 exe 发布也能启动靶子，
    /// 源码、便携文件夹、单个 exe 三种运行方式走同一条路。进程名和编辑器相同，编辑器按「自己的 exe + 窗口标题 BenchTarget」认它（见 MainWindow.ProcessNameOf）。
    /// </summary>
    public static async Task<(Process, IntPtr)> LaunchAsync(string args)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("拿不到编辑器自己的路径");
        var p = Process.Start(new ProcessStartInfo(exe, "--bench-target " + args) { UseShellExecute = false }) ?? throw new InvalidOperationException("靶子启动失败");
        var sw = Stopwatch.StartNew();
        WindowInfo.PxRect last = default;
        var stable = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15000)
        {
            // 靶子自己退出了：多半是所选屏幕不存在（--monitor-device 找不到时靶子报错退出，退出码 3）
            if (p.HasExited) throw new InvalidOperationException(p.ExitCode == 3 ? "靶子没启动：找不到所选的屏幕，可能刚拔掉了。点「刷新」重选屏幕" : $"靶子启动后立刻退出了（退出码 {p.ExitCode}）");
            p.Refresh();
            if (p.MainWindowHandle != IntPtr.Zero && WindowInfo.TryGetClientOnScreen(p.MainWindowHandle, out var r) && r.W > 500)
            {
                if (r != last) { last = r; stable.Restart(); }
                else if (stable.ElapsedMilliseconds >= 400) return (p, p.MainWindowHandle);
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("15 秒内没等到靶子窗口");
    }
}
