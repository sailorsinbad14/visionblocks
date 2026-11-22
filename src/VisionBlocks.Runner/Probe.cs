using System.Diagnostics;
using System.Globalization;
using OpenCvSharp;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Vision;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Runner;

/// <summary>
/// 探针：
/// ① WGC 能否截到靶子客户区、帧计数条能否解码；② 画面静止 3 秒时 WGC 是否还出帧；
/// ③ BitBlt 兜底能否截到 WPF 窗口；④ SendInput 扫描码和绝对坐标点击能否被前台靶子收到、落点误差多少。
/// </summary>
internal static class Probe
{
    public static int Run(Dictionary<string, string> o)
    {
        int monitor = int.Parse(Program.Opt(o, "monitor", "1"), CultureInfo.InvariantCulture);
        string outDir = Program.NewOutDir(o, $"probe-m{monitor}");
        Console.WriteLine($"输出：{outDir}");
        Console.WriteLine($"DPI 感知：{WindowInfo.CurrentDpiAwareness()}（2 = 每显示器）；虚拟桌面 {WindowInfo.VirtualScreen()}");

        var (proc, hwnd) = TargetLauncher.Launch(TargetLauncher.FindTargetExe(),
            $"--log \"{Path.Combine(outDir, "target")}\" --static-test --duration 16 --start-delay 3 --monitor {monitor}");
        var t0 = Stopwatch.StartNew();
        WindowInfo.TryGetClientOnScreen(hwnd, out var client);
        WindowInfo.TryGetVisibleBounds(hwnd, out var vis);
        WindowInfo.TryGetClientInVisibleBounds(hwnd, out var crop);
        Console.WriteLine($"靶子 PID {proc.Id}；客户区(屏幕) {client}；可见边界 {vis}；客户区在帧内 {crop}；窗口 DPI {WindowInfo.DpiOf(hwnd)}");

        using var log = new BotLog(Path.Combine(outDir, "bot.jsonl"));
        log.Write(new { ev = "meta", probe = true, targetPid = proc.Id, client = client.ToString(), vis = vis.ToString(), crop = crop.ToString(), qpcFreq = Stopwatch.Frequency, t = BotLog.Now });
        using var wgc = new WgcFrameSource();
        wgc.FrameArrived += (m, seq, arr, sys) =>
            log.Write(new { ev = "frame", seq, fid = FrameCounter.Decode(m, m.Width / 1920.0), arr, sys, w = m.Width, h = m.Height });
        wgc.Start(hwnd);
        Console.WriteLine("WGC：" + wgc.BorderNote);

        Thread.Sleep(1500);
        using (var f = wgc.TryGetLatest())
        {
            if (f == null) Console.WriteLine("① WGC：1.5 秒内没有拿到帧");
            else
            {
                Cv2.ImWrite(Path.Combine(outDir, "wgc-frame.png"), f.Bgra);
                Console.WriteLine($"① WGC：帧 {f.Bgra.Width}×{f.Bgra.Height}，帧号 {FrameCounter.Decode(f.Bgra, f.Bgra.Width / 1920.0)}，已收 {wgc.LatestSeq} 帧");
            }
        }

        // ③ BitBlt 兜底
        var gdi = new GdiFrameSource();
        gdi.Start(hwnd);
        var gdiMs = new List<double>();
        long gdiId = -1;
        for (int i = 0; i < 10; i++)
        {
            long s = Stopwatch.GetTimestamp();
            using var g = gdi.TryGetLatest();
            gdiMs.Add(Stopwatch.GetElapsedTime(s).TotalMilliseconds);
            if (g != null && i == 9)
            {
                Cv2.ImWrite(Path.Combine(outDir, "gdi-frame.png"), g.Bgra);
                gdiId = FrameCounter.Decode(g.Bgra, g.Bgra.Width / 1920.0);
                Cv2.MeanStdDev(g.Bgra, out _, out Scalar sd);
                Console.WriteLine($"③ BitBlt：帧号 {gdiId}，单次耗时中位数 {gdiMs.OrderBy(x => x).ElementAt(5):0.0}ms，画面标准差 {sd.Val0:0.0}（≈0 说明黑屏）");
            }
        }
        log.Write(new { ev = "gdi", fid = gdiId, ms = gdiMs, t = BotLog.Now });

        // 等过冻结期（靶子第 4–7 秒不重绘）
        while (t0.ElapsedMilliseconds < 9000) Thread.Sleep(50);

        // ④ 输入
        var guard = new InputGuard(hwnd);
        using var input = new SendInputBackend(guard, log);
        bool fg = TargetLauncher.EnsureForeground(hwnd, 0);
        Console.WriteLine($"④ 靶子在前台：{fg}");
        var targets = new[] { (1120, 272, "喝药按钮中心"), (1800, 1000, "右下角"), (100, 900, "左下") };
        if (fg)
        {
            input.PressAsync("F", 40, CancellationToken.None).GetAwaiter().GetResult();
            Thread.Sleep(150);
            foreach (var (bx, by, name) in targets)
            {
                WindowInfo.TryGetClientOnScreen(hwnd, out var c);
                double s = c.W / 1920.0;
                int sx = c.X + (int)Math.Round(bx * s), sy = c.Y + (int)Math.Round(by * s);
                bool ok = input.ClickAsync(sx, sy, 40, CancellationToken.None).GetAwaiter().GetResult();
                log.Write(new { ev = "probeClick", name, bx, by, sx, sy, ok, t = BotLog.Now });
                Thread.Sleep(150);
            }
        }
        else Console.WriteLine("  跳过输入测试：靶子不在前台，守卫拦截了发送（安全行为）。");
        input.ReleaseAll();

        while (!proc.HasExited && t0.ElapsedMilliseconds < 20000) Thread.Sleep(100);
        if (!proc.HasExited) TargetLauncher.CloseGracefully(proc);
        Console.WriteLine($"完成。共收 WGC 帧 {wgc.LatestSeq}；拦截 {guard.Blocked} 次。用 tools/analyze.py 分析：{outDir}");
        return 0;
    }
}
