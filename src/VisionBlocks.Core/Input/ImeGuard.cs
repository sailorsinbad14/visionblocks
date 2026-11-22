using System.Runtime.InteropServices;

namespace VisionBlocks.Core.Input;

/// <summary>
/// 输入法守卫。实测：目标窗口开着中文输入法时，SendInput 发出的字母键（哪怕是扫描码）
/// 会被输入法截走变成组字（WPF 里表现为 Key.ImeProcessed），游戏逻辑收不到。
/// 这是「按了没反应」一个容易漏掉的原因。
/// 做法：发键前通过目标窗口的默认 IME 窗口发 WM_IME_CONTROL，把输入法切到关闭/英文状态。只作用于绑定的目标窗口。
/// </summary>
public static class ImeGuard
{
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001, IMC_SETCONVERSIONMODE = 0x0002;
    private const int IMC_GETOPENSTATUS = 0x0005, IMC_SETOPENSTATUS = 0x0006;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    public readonly record struct ImeState(bool HasIme, bool Open, int ConversionMode)
    {
        /// <summary>开着且处于「本地语言」转换模式（IME_CMODE_NATIVE = 1），字母键会被截走。</summary>
        public bool WillEatLetters => HasIme && Open && (ConversionMode & 1) != 0;
        public override string ToString() => HasIme ? $"open={Open} mode=0x{ConversionMode:X}" : "无输入法窗口";
    }

    private static int Query(IntPtr ime, int cmd)
    {
        SendMessageTimeout(ime, WM_IME_CONTROL, new IntPtr(cmd), IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out var r);
        return r.ToInt32();
    }

    public static ImeState Get(IntPtr target)
    {
        var ime = ImmGetDefaultIMEWnd(target);
        if (ime == IntPtr.Zero) return new ImeState(false, false, 0);
        return new ImeState(true, Query(ime, IMC_GETOPENSTATUS) != 0, Query(ime, IMC_GETCONVERSIONMODE));
    }

    /// <summary>测试用：把目标窗口的输入法切到「开启 + 中文」，模拟用户开着中文输入法的情况。</summary>
    public static ImeState ForceChinese(IntPtr target)
    {
        var ime = ImmGetDefaultIMEWnd(target);
        if (ime == IntPtr.Zero) return Get(target);
        SendMessageTimeout(ime, WM_IME_CONTROL, new IntPtr(IMC_SETOPENSTATUS), new IntPtr(1), SMTO_ABORTIFHUNG, 200, out _);
        SendMessageTimeout(ime, WM_IME_CONTROL, new IntPtr(IMC_SETCONVERSIONMODE), new IntPtr(1), SMTO_ABORTIFHUNG, 200, out _);
        return Get(target);
    }

    /// <summary>把目标窗口的输入法切到英文（关闭 + 字母数字模式）。返回切换前的状态。</summary>
    public static ImeState EnsureOff(IntPtr target)
    {
        var before = Get(target);
        if (!before.WillEatLetters) return before;
        var ime = ImmGetDefaultIMEWnd(target);
        SendMessageTimeout(ime, WM_IME_CONTROL, new IntPtr(IMC_SETCONVERSIONMODE), IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out _);
        SendMessageTimeout(ime, WM_IME_CONTROL, new IntPtr(IMC_SETOPENSTATUS), IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out _);
        return before;
    }
}
