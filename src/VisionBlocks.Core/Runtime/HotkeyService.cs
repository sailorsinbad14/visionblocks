using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace VisionBlocks.Core.Runtime;

/// <summary>
/// 全局热键（启停这类关键热键用 RegisterHotKey，不用低级钩子）。
/// 在独立线程上跑一个消息循环；注册失败时 Error 写明原因，调用方要提示用户换键。
/// Ctrl+Alt+F11 = 暂停/继续，Ctrl+Alt+F12 = 急停。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public event Action? Toggle;
    public event Action? EmergencyStop;
    public string? Error { get; private set; }

    private readonly Thread _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new();

    public HotkeyService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "hotkeys" };
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Loop()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        var mods = HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        var errs = new List<string>();
        if (!PInvoke.RegisterHotKey(HWND.Null, 1, mods, (uint)VIRTUAL_KEY.VK_F11)) errs.Add("Ctrl+Alt+F11 已被占用");
        if (!PInvoke.RegisterHotKey(HWND.Null, 2, mods, (uint)VIRTUAL_KEY.VK_F12)) errs.Add("Ctrl+Alt+F12 已被占用");
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

    public void Dispose()
    {
        if (_threadId != 0) PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, 0, 0);
        _thread.Join(1000);
    }
}
