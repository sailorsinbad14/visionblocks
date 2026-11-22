using System.Diagnostics;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Runner;

/// <summary>启动靶子并拿到它的主窗口。输入守卫绑定的就是这里启动的那个进程。</summary>
internal static class TargetLauncher
{
    public static string FindTargetExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "src", "BenchTarget", "bin", "Release", "net10.0-windows10.0.22621.0", "BenchTarget.exe");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到 BenchTarget.exe，请先 dotnet build -c Release");
    }

    /// <summary>
    /// 等到窗口出现且客户区尺寸稳定（连续 400ms 不变）才返回。
    /// 靶子被放到另一块 DPI 不同的屏上时，WPF 会先按 DIP 缩放一次窗口，随后靶子再改回物理尺寸，中间有一段瞬态。
    /// </summary>
    public static (Process Proc, IntPtr Hwnd) Launch(string exe, string args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })
                ?? throw new InvalidOperationException("靶子启动失败");
        var sw = Stopwatch.StartNew();
        WindowInfo.PxRect last = default;
        var stable = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15000)
        {
            p.Refresh();
            if (p.MainWindowHandle != IntPtr.Zero && WindowInfo.TryGetClientOnScreen(p.MainWindowHandle, out var r) && r.W > 500)
            {
                if (r != last) { last = r; stable.Restart(); }
                else if (stable.ElapsedMilliseconds >= 400) return (p, p.MainWindowHandle);
            }
            Thread.Sleep(50);
        }
        try { p.Kill(); } catch { }
        throw new TimeoutException("15 秒内没等到靶子窗口");
    }

    /// <summary>尽量让靶子到前台；系统不允许时（前台锁）返回 false，由调用方提示用户点一下靶子。</summary>
    public static bool EnsureForeground(IntPtr hwnd, int waitUserMs)
    {
        uint pid = WindowInfo.ProcessIdOf(hwnd);
        if (WindowInfo.ProcessIdOf(WindowInfo.Foreground()) == pid) return true;
        WindowInfo.TrySetForeground(hwnd);
        Thread.Sleep(200);
        if (WindowInfo.ProcessIdOf(WindowInfo.Foreground()) == pid) return true;
        if (waitUserMs <= 0) return false;
        Console.WriteLine($"  靶子不在前台（系统前台锁）。请在 {waitUserMs / 1000} 秒内点一下靶子窗口……");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitUserMs)
        {
            if (WindowInfo.ProcessIdOf(WindowInfo.Foreground()) == pid) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    public static void CloseGracefully(Process p, int waitMs = 5000)
    {
        try
        {
            if (p.HasExited) return;
            p.CloseMainWindow();
            if (!p.WaitForExit(waitMs)) p.Kill();
        }
        catch { }
    }
}
