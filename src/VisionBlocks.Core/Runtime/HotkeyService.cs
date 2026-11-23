using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using VisionBlocks.Core.Input;

namespace VisionBlocks.Core.Runtime;

/// <summary>
/// 全局热键（启停这类关键热键用 RegisterHotKey，不用低级钩子）。
/// 在独立线程上跑一个消息循环；注册失败时 Error 写明原因，调用方要提示用户换键。
/// 默认 Ctrl+Alt+F11 = 暂停/继续，Ctrl+Alt+F12 = 急停；2026-11-23 起可以在编辑器设置里改（规矩见 Keymap.Check）。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public event Action? Toggle;
    public event Action? EmergencyStop;
    public string? Error { get; private set; }

    private readonly Thread _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new();
    public string PauseKey { get; }
    public string StopKey { get; }

    public HotkeyService() : this("Ctrl+Alt+F11", "Ctrl+Alt+F12") { }

    /// <param name="pause">暂停 / 继续的组合键文字，例如「Ctrl+Alt+F11」。</param>
    /// <param name="stop">急停的组合键文字。</param>
    public HotkeyService(string pause, string stop)
    {
        PauseKey = pause; StopKey = stop;
        _thread = new Thread(Loop) { IsBackground = true, Name = "hotkeys" };
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Loop()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        var errs = new List<string>();
        if (PauseKey != "" && !Register(1, PauseKey)) errs.Add($"{PauseKey} 已被占用");   // 暂停可以在设置里清空：那就不注册
        if (!Register(2, StopKey)) errs.Add($"{StopKey} 已被占用");
        Error = errs.Count > 0 ? string.Join("；", errs) : null;
        _ready.Set();
        while (PInvoke.GetMessage(out MSG msg, HWND.Null, 0, 0) > 0)
        {
            if (msg.message != PInvoke.WM_HOTKEY) continue;
            if (msg.wParam.Value == 1) Toggle?.Invoke();
            else if (msg.wParam.Value == 2) EmergencyStop?.Invoke();
        }
        PInvoke.UnregisterHotKey(HWND.Null, 1);
        PInvoke.UnregisterHotKey(HWND.Null, 2);
    }

    private static bool Register(int id, string gesture)
    {
        if (!Keymap.TryParse(gesture, out var g) || Keymap.VkOf(g.Key) == 0) return false;
        var mods = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (g.Ctrl) mods |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (g.Alt) mods |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (g.Shift) mods |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        return PInvoke.RegisterHotKey(HWND.Null, id, mods, Keymap.VkOf(g.Key));
    }

    /// <summary>试注册一下再马上注销：这个组合现在有没有被别的程序占着（编辑器设置里保存暂停 / 急停之前用）。</summary>
    public static bool Probe(string gesture)
    {
        const int id = 0xB00F;
        if (!Register(id, gesture)) return false;
        PInvoke.UnregisterHotKey(HWND.Null, id);
        return true;
    }

    public void Dispose()
    {
        if (_threadId != 0) PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, 0, 0);
        _thread.Join(1000);
    }
}
