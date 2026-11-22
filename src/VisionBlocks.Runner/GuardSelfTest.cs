using System.Runtime.InteropServices;
using VisionBlocks.Core.Input;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-guard：验证前台守卫「按窗口比对」的规则（2026-11-20）。不发任何真实输入。
/// 在本进程里建三个不可见的顶层窗口：A（绑定目标）、B（同进程、不归 A 所有）、C（A 拥有的弹出窗口，相当于对话框）。
/// 期望：前台是 A 或 C 时放行，前台是 B 时拦截。旧规则只比对进程，B 也会被放行。
/// </summary>
public static class GuardSelfTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);

    private const uint WS_OVERLAPPED = 0x00000000, WS_POPUP = 0x80000000;

    public static int Run()
    {
        IntPtr a = CreateWindowEx(0, "STATIC", "guard-A", WS_OVERLAPPED, 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        IntPtr b = CreateWindowEx(0, "STATIC", "guard-B", WS_OVERLAPPED, 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        IntPtr c = CreateWindowEx(0, "STATIC", "guard-C", WS_POPUP, 0, 0, 100, 100, a, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (a == IntPtr.Zero || b == IntPtr.Zero || c == IntPtr.Zero) { Console.WriteLine("建窗口失败"); return 1; }
            int fails = 0;
            void Expect(string name, IntPtr fg, bool want)
            {
                var g = new InputGuard(a) { ForegroundProvider = () => fg };
                bool ok = g.Check(out string reason);
                bool pass = ok == want;
                if (!pass) fails++;
                Console.WriteLine($"  [{(pass ? "通过" : "失败")}] 前台={name}：{(ok ? "放行" : "拦截（" + reason + "）")}，期望{(want ? "放行" : "拦截")}");
            }
            Console.WriteLine($"A=0x{a:X}（目标） B=0x{b:X}（同进程的另一个窗口） C=0x{c:X}（A 拥有的弹出窗口）");
            Expect("A", a, true);
            Expect("C", c, true);
            Expect("B", b, false);
            Expect("无前台", IntPtr.Zero, false);
            Console.WriteLine(fails == 0 ? "全部通过" : $"{fails} 项失败");
            return fails == 0 ? 0 : 1;
        }
        finally
        {
            if (c != IntPtr.Zero) DestroyWindow(c);
            if (b != IntPtr.Zero) DestroyWindow(b);
            if (a != IntPtr.Zero) DestroyWindow(a);
        }
    }
}
