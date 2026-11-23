using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using VisionBlocks.Core.Runtime;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Core.Input;

/// <summary>
/// 前台守卫：只有前台窗口是「本次绑定的目标窗口」或它弹出的对话框（根所有者是目标窗口）时才允许发输入。
/// 防「焦点被抢」：每次操作前确认前台窗口是目标窗口。
/// 2026-11-20 从「比对进程」改成「比对窗口」：资源管理器同时管着任务栏和桌面，商店应用共用 ApplicationFrameHost，
/// 多窗口程序的其他窗口也属于同一进程，只比对进程会把输入放给不该收的窗口。
/// </summary>
public sealed class InputGuard
{
    public IntPtr TargetHwnd { get; }
    public int Blocked;

    /// <summary>测试用：设置后由它决定是否放行，不再看真实前台。</summary>
    public Func<bool>? TestAllow { get; init; }

    /// <summary>测试用：替换「当前前台窗口」的来源，用来验证按窗口比对的规则本身（selftest-guard）。</summary>
    public Func<IntPtr>? ForegroundProvider { get; init; }

    public InputGuard(IntPtr targetHwnd)
    {
        TargetHwnd = targetHwnd;
    }

    public bool Check(out string reason)
    {
        if (TestAllow != null)
        {
            reason = TestAllow() ? "" : "测试：不放行";
            if (reason != "") Blocked++;
            return reason == "";
        }
        if (!WindowInfo.IsAlive(TargetHwnd)) { reason = "目标窗口已关闭"; Blocked++; return false; }
        if (WindowInfo.IsMinimized(TargetHwnd)) { reason = "目标窗口已最小化"; Blocked++; return false; }
        var fg = ForegroundProvider?.Invoke() ?? WindowInfo.Foreground();
        if (fg != TargetHwnd && WindowInfo.RootOwner(fg) != TargetHwnd)
        {
            reason = $"前台不是目标窗口（前台 0x{fg:X}，进程 {WindowInfo.ProcessIdOf(fg)}；目标 0x{TargetHwnd:X}）";
            Blocked++;
            return false;
        }
        reason = "";
        return true;
    }
}

/// <summary>
/// SendInput 后端：
/// - 键盘一律发扫描码（KEYEVENTF_SCANCODE），扩展键带 EXTENDEDKEY；
/// - 记录每一个按下的键，ReleaseAll 只抬起「确实按下过」的键；
/// - 鼠标用绝对坐标 + MOUSEEVENTF_VIRTUALDESK，按虚拟桌面换算，避免副屏点偏。
/// 另外两道保护（2026-11-19 编辑器验收时发现）：
/// - 急停闸：Abort 和所有发送共用同一把锁，先关闸再抬键。急停之后任何按下类输入一律拒发，
///   正在发的那一次要么已经发完、被 Abort 里的抬键覆盖，要么在闸前被拒，不会漏到急停之后；
/// - 修饰键检查：用户物理按住 Ctrl/Alt/Shift/Win（例如正在按 Ctrl+Alt+F12 急停）时暂缓发送，
///   否则模拟按键会和这些修饰键叠加成组合键（实测出现过 Ctrl+Alt+F）。
/// </summary>
public sealed class SendInputBackend : IDisposable
{
    private readonly InputGuard _guard;
    private readonly BotLog? _log;
    private readonly object _lock = new();
    private readonly HashSet<(ushort Scan, bool Ext)> _down = new();
    private readonly HashSet<ushort> _downVk = new();
    private bool _leftDown;
    private volatile bool _aborted;

    public SendInputBackend(InputGuard guard, BotLog? log) { _guard = guard; _log = log; }

    /// <summary>发键前是否先把目标的输入法切到英文。</summary>
    public bool CloseImeBeforeKeys { get; set; } = true;

    /// <summary>测试用的假后端：设置后不调用 SendInput，只把要发的事件交给它。</summary>
    public Func<string, uint>? TestSender { get; init; }

    /// <summary>读某个虚拟键是否被物理按住。默认用 GetAsyncKeyState，测试时可替换。</summary>
    public Func<int, bool> IsKeyPhysicallyDown { get; init; } = vk => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>用户按住修饰键时，最多等多久再放弃这次发送。</summary>
    public int ModifierWaitMs { get; set; } = 600;

    public InputGuard Guard => _guard;
    public bool Aborted => _aborted;

    /// <summary>鼠标操作随机化。为空时和以前完全一样：直接跳到目标点、按住时长不变。</summary>
    public Humanizer? Humanizer { get; set; }

    /// <summary>当前鼠标位置（屏幕物理像素），轨迹的起点。测试时可替换。</summary>
    public Func<(int X, int Y)> CursorPosition { get; init; } = () => PInvoke.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>绑定窗口客户区（屏幕物理像素），轨迹夹在里面。测试时可替换。</summary>
    public Func<IntPtr, WindowInfo.PxRect?> ClientRect { get; init; } = h => WindowInfo.TryGetClientOnScreen(h, out var r) ? r : null;
    public int RefusedAfterAbort, DeferredByModifier;

    private static readonly (int Vk, string Name)[] Modifiers =
    {
        (0xA2, "LCtrl"), (0xA3, "RCtrl"), (0xA4, "LAlt"), (0xA5, "RAlt"),
        (0xA0, "LShift"), (0xA1, "RShift"), (0x5B, "LWin"), (0x5C, "RWin"),
    };

    /// <summary>用户（不是我们自己）按住的修饰键。</summary>
    public List<string> UserModifiersHeld()
    {
        var held = new List<string>();
        foreach (var (vk, name) in Modifiers)
        {
            bool ours = (vk is 0xA2 or 0xA3 && _downVk.Contains(0x11)) || (vk is 0xA4 or 0xA5 && _downVk.Contains(0x12))
                        || (vk is 0xA0 or 0xA1 && _downVk.Contains(0x10)) || _downVk.Contains((ushort)vk);
            if (!ours && IsKeyPhysicallyDown(vk)) held.Add(name);
        }
        return held;
    }

    private async Task WaitModifiersReleased(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_aborted && sw.ElapsedMilliseconds < ModifierWaitMs && UserModifiersHeld().Count > 0)
            await Task.Delay(10, ct);
    }

    /// <summary>在锁内调用：急停闸、修饰键、前台守卫三道检查。</summary>
    private bool Gate(string what)
    {
        if (_aborted)
        {
            RefusedAfterAbort++;
            _log?.Write(new { ev = "refusedAfterAbort", what, t = BotLog.Now });
            return false;
        }
        var held = UserModifiersHeld();
        if (held.Count > 0)
        {
            DeferredByModifier++;
            _log?.Write(new { ev = "deferredByModifier", what, held = string.Join("+", held), t = BotLog.Now });
            return false;
        }
        if (_guard.Check(out var why)) return true;
        _log?.Write(new { ev = "blocked", what, why, t = BotLog.Now });
        return false;
    }

    private unsafe uint Send(INPUT[] inputs, string desc)
    {
        if (TestSender != null) return TestSender(desc);
        fixed (INPUT* p = inputs) return PInvoke.SendInput((uint)inputs.Length, p, sizeof(INPUT));
    }

    private static INPUT KeyInput(ushort scan, bool ext, bool up)
    {
        var flags = KEYBD_EVENT_FLAGS.KEYEVENTF_SCANCODE;
        if (ext) flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        if (up) flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        return new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = new() { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } } };
    }

    public bool KeyDown(string key)
    {
        if (!KeyNames.TryGet(key, out ushort vk, out bool ext)) throw new ArgumentException($"不认识的按键 {key}");
        ushort scan = (ushort)PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        lock (_lock)
        {
            if (!Gate("keydown " + key)) return false;
            if (CloseImeBeforeKeys && TestSender == null)
            {
                var before = ImeGuard.EnsureOff(_guard.TargetHwnd);
                if (before.WillEatLetters) _log?.Write(new { ev = "imeOff", before = before.ToString(), after = ImeGuard.Get(_guard.TargetHwnd).ToString(), t = BotLog.Now });
            }
            uint n = Send(new[] { KeyInput(scan, ext, false) }, $"kd {key}");
            if (n != 1) { _log?.Write(new { ev = "sendFailed", what = "keydown " + key, t = BotLog.Now }); return false; }
            _down.Add((scan, ext));
            _downVk.Add(vk);
            _log?.Write(new { ev = "keydown", k = key, scan, t = BotLog.Now });
            return true;
        }
    }

    /// <summary>抬起不经过任何检查：抬起只会让状态更安全，而且必须保证不卡键。</summary>
    public void KeyUp(string key)
    {
        if (!KeyNames.TryGet(key, out ushort vk, out bool ext)) return;
        ushort scan = (ushort)PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        lock (_lock)
        {
            if (!_down.Remove((scan, ext))) return;
            _downVk.Remove(vk);
            Send(new[] { KeyInput(scan, ext, true) }, $"ku {key}");
            _log?.Write(new { ev = "keyup", k = key, t = BotLog.Now });
        }
    }

    public async Task<bool> PressAsync(string key, int holdMs, CancellationToken ct)
    {
        await WaitModifiersReleased(ct);
        if (Humanizer != null) holdMs = Humanizer.Hold(holdMs);
        if (!KeyDown(key)) return false;
        try { await Task.Delay(Math.Clamp(holdMs, 10, 2000), ct); }
        finally { KeyUp(key); }
        return true;
    }

    private static INPUT MouseInput(int dx, int dy, MOUSE_EVENT_FLAGS flags) =>
        new() { type = INPUT_TYPE.INPUT_MOUSE, Anonymous = new() { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } } };

    /// <summary>屏幕物理像素 → 虚拟桌面归一化坐标（0–65535）。</summary>
    private static (int, int) ToAbsolute(int x, int y)
    {
        var v = WindowInfo.VirtualScreen();
        int ax = (int)Math.Round((x - v.X) * 65535.0 / Math.Max(1, v.W - 1));
        int ay = (int)Math.Round((y - v.Y) * 65535.0 / Math.Max(1, v.H - 1));
        return (ax, ay);
    }

    /// <summary>
    /// 先等一小会儿，再沿 Humanizer 给的轨迹把鼠标移到 (sx, sy)。移动的每一步都过闸（急停、修饰键、前台守卫）；
    /// 有一步被拦就停下并返回 false，调用方不会接着点下去或滚动。没开随机化时什么也不做。
    /// </summary>
    private async Task<bool> ApproachAsync(int sx, int sy, CancellationToken ct)
    {
        var h = Humanizer;
        if (h == null) return true;
        int pre = h.BeforeAction();
        if (pre > 0) await Task.Delay(pre, ct);
        if (!h.Path || ClientRect(_guard.TargetHwnd) is not { } client) return true;
        var from = CursorPosition();
        var pts = h.PathPoints(from, (sx, sy), client);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var flags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK;
        foreach (var (x, y, delay) in pts)
        {
            if (delay > 0) await Task.Delay(delay, ct);
            lock (_lock)
            {
                if (!Gate($"move {x},{y}")) return false;
                var (ax, ay) = ToAbsolute(x, y);
                Send(new[] { MouseInput(ax, ay, flags) }, $"mv {x},{y}");
            }
        }
        _log?.Write(new { ev = "move", from = new[] { from.X, from.Y }, to = new[] { sx, sy }, steps = pts.Count, ms = sw.ElapsedMilliseconds, t = BotLog.Now });
        return true;
    }

    public async Task<bool> ClickAsync(int screenX, int screenY, int holdMs, CancellationToken ct)
    {
        var (ax, ay) = ToAbsolute(screenX, screenY);
        var abs = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK;
        await WaitModifiersReleased(ct);
        if (!await ApproachAsync(screenX, screenY, ct)) return false;
        if (Humanizer != null) holdMs = Humanizer.Hold(holdMs);
        lock (_lock)
        {
            if (!Gate($"click {screenX},{screenY}")) return false;
            Send(new[] { MouseInput(ax, ay, abs) }, "move");
            Send(new[] { MouseInput(ax, ay, abs | MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN) }, "md");
            _leftDown = true;
            _log?.Write(new { ev = "mousedown", x = screenX, y = screenY, t = BotLog.Now });
        }
        try { await Task.Delay(Math.Clamp(holdMs, 10, 2000), ct); }
        finally
        {
            lock (_lock)
            {
                if (_leftDown)
                {
                    Send(new[] { MouseInput(ax, ay, abs | MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP) }, "mu");
                    _leftDown = false;
                    _log?.Write(new { ev = "mouseup", t = BotLog.Now });
                }
            }
        }
        return true;
    }

    /// <summary>
    /// 把鼠标移到 (screenX, screenY) 再滚动滚轮（2026-11-20，背包翻页）。notches 为正向下滚。
    /// 每格单独发一次 WHEEL_DELTA=120，格与格之间隔 betweenMs，让程序逐格处理；每格都过一次闸和前台守卫。
    /// unit：每个事件的 delta 大小，默认 120 一整格；「按距离滚」收尾时会发不足一格的（2026-11-22）。
    /// </summary>
    public async Task<int> WheelAsync(int screenX, int screenY, int notches, int betweenMs, CancellationToken ct, int unit = 120)
    {
        unit = Math.Clamp(unit, 1, 120);
        var (ax, ay) = ToAbsolute(screenX, screenY);
        var abs = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK;
        int sent = 0;
        await WaitModifiersReleased(ct);
        if (!await ApproachAsync(screenX, screenY, ct)) return 0;
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            if (i > 0) await Task.Delay(Math.Max(0, betweenMs), ct);
            await WaitModifiersReleased(ct);
            lock (_lock)
            {
                if (!Gate($"wheel {screenX},{screenY}")) return sent;
                Send(new[] { MouseInput(ax, ay, abs) }, "move");
                var w = MouseInput(0, 0, MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL);
                w.Anonymous.mi.mouseData = unchecked((uint)(notches > 0 ? -unit : unit));
                Send(new[] { w }, "wheel");
                sent++;
            }
        }
        if (unit == 120) _log?.Write(new { ev = "wheel", x = screenX, y = screenY, notches, sent, t = BotLog.Now });
        else _log?.Write(new { ev = "wheel", x = screenX, y = screenY, notches, unit, sent, t = BotLog.Now });
        return sent;
    }

    /// <summary>拖动一笔的结果。除了 Ok，其余都已经松开了左键。</summary>
    public enum DragOutcome { Ok, Blocked, UserMouse, Paused, Aborted }

    /// <summary>
    /// 按住左键拖一笔（2026-11-23，「拖动」积木）：先移到 (sx, sy)（开了移动轨迹就沿轨迹过去），按下，停 pressHoldMs，
    /// 沿 path 一步步移（屏幕物理像素，每步带「移之前先等多少毫秒」），停 releaseHoldMs，松开。
    /// 每一步都过闸（急停、修饰键、前台守卫），还要看光标是不是还在我们上一次发的位置：差了 3 像素以上 = 用户动了鼠标。
    /// 有一步不行、要暂停（isPaused）、被取消或出错，都立刻松开左键；急停时 Abort 已经在锁里抬起了左键，不会再发第二次。
    /// </summary>
    public async Task<DragOutcome> DragAsync(int sx, int sy, IReadOnlyList<(int X, int Y, int DelayMs)> path, int pressHoldMs, int releaseHoldMs, Func<bool>? isPaused, CancellationToken ct)
    {
        var (ax, ay) = ToAbsolute(sx, sy);
        var abs = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK;
        await WaitModifiersReleased(ct);
        if (!await ApproachAsync(sx, sy, ct)) return _aborted ? DragOutcome.Aborted : DragOutcome.Blocked;
        lock (_lock)
        {
            if (!Gate($"dragdown {sx},{sy}")) return _aborted ? DragOutcome.Aborted : DragOutcome.Blocked;
            Send(new[] { MouseInput(ax, ay, abs) }, "move");
            Send(new[] { MouseInput(ax, ay, abs | MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN) }, "md");
            _leftDown = true;
            _log?.Write(new { ev = "mousedown", x = sx, y = sy, drag = true, t = BotLog.Now });
        }
        (int X, int Y) last = (sx, sy);
        bool Moved() { var c = CursorPosition(); return Math.Abs(c.X - last.X) > 3 || Math.Abs(c.Y - last.Y) > 3; }
        var outcome = DragOutcome.Ok;
        try
        {
            await Task.Delay(Math.Clamp(pressHoldMs, 0, 2000), ct);
            foreach (var (x, y, delay) in path)
            {
                if (delay > 0) await Task.Delay(delay, ct);
                if (isPaused?.Invoke() == true) { outcome = DragOutcome.Paused; break; }
                if (Moved()) { outcome = DragOutcome.UserMouse; break; }
                lock (_lock)
                {
                    if (!Gate($"drag {x},{y}")) { outcome = _aborted ? DragOutcome.Aborted : DragOutcome.Blocked; break; }
                    var (mx, my) = ToAbsolute(x, y);
                    Send(new[] { MouseInput(mx, my, abs) }, $"dm {x},{y}");
                    last = (x, y);
                }
            }
            if (outcome == DragOutcome.Ok)
            {
                await Task.Delay(Math.Clamp(releaseHoldMs, 0, 2000), ct);
                if (Moved()) outcome = DragOutcome.UserMouse;
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_leftDown)
                {
                    Send(new[] { MouseInput(0, 0, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP) }, "mu");
                    _leftDown = false;
                    _log?.Write(new { ev = "mouseup", drag = true, outcome = outcome.ToString(), t = BotLog.Now });
                }
            }
        }
        if (outcome != DragOutcome.Ok) _log?.Write(new { ev = "dragStopped", outcome = outcome.ToString(), at = new[] { last.X, last.Y }, t = BotLog.Now });
        return outcome;
    }

    /// <summary>Unicode 方式输入文字（T2 用；游戏里通常不认，桌面软件可以）。</summary>
    public bool TypeText(string text)
    {
        lock (_lock)
        {
            if (!Gate("type")) return false;
            foreach (char ch in text)
            {
                var down = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = new() { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE } } };
                var up = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = new() { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP } } };
                Send(new[] { down, up }, $"type {ch}");
            }
            _log?.Write(new { ev = "type", len = text.Length, t = BotLog.Now });
            return true;
        }
    }

    private int ReleaseAllCore()
    {
        int n = 0;
        foreach (var (scan, ext) in _down) { Send(new[] { KeyInput(scan, ext, true) }, $"ku scan{scan}"); n++; }
        _down.Clear();
        _downVk.Clear();
        if (_leftDown)
        {
            Send(new[] { MouseInput(0, 0, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP) }, "mu");
            _leftDown = false;
            n++;
        }
        return n;
    }

    /// <summary>抬起所有按下过的键和鼠标左键。任务结束、取消、异常时都要调用。</summary>
    public int ReleaseAll()
    {
        lock (_lock)
        {
            int n = ReleaseAllCore();
            _log?.Write(new { ev = "releaseAll", released = n, t = BotLog.Now });
            return n;
        }
    }

    /// <summary>
    /// 急停：在同一把锁里先关闸、再抬起所有键。返回抬起的数量。之后的按下类输入一律拒发，抬起照常允许。
    /// </summary>
    public int Abort(string reason)
    {
        lock (_lock)
        {
            _aborted = true;
            int n = ReleaseAllCore();
            _log?.Write(new { ev = "abort", reason, released = n, t = BotLog.Now });
            return n;
        }
    }

    public void Dispose() => ReleaseAll();
}
