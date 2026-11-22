using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BenchTarget;

public sealed class MainWindow : Window
{
    private readonly TargetOptions _opt;
    private readonly TruthLog _log;
    private readonly PatrolGame _game;
    private readonly GameView _view;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Dictionary<string, int> _keyBalance = new();
    private long _frameId;
    private bool _frozenLogged;
    private IntPtr _hwnd;

    public MainWindow(TargetOptions opt)
    {
        _opt = opt;
        _log = new TruthLog(opt.LogDir);
        _game = new PatrolGame(opt, _log);
        if (opt.Showcase) _game.SetupShowcase();
        _view = new GameView(_game, opt);
        Title = "BenchTarget";
        Content = _view;
        Background = Brushes.Black;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 400; Height = 300;

        SourceInitialized += (_, _) => { _hwnd = new WindowInteropHelper(this).Handle; ApplyClientSize(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyClientSize, DispatcherPriority.Background);
        Loaded += (_, _) => { Activate(); WriteMeta(); };
        CompositionTarget.Rendering += OnRendering;
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        _view.MouseLeftButtonDown += (_, e) => OnMouse(e, "md");
        _view.MouseLeftButtonUp += (_, e) => OnMouse(e, "mu");
        Activated += (_, _) => _log.Write(new { ev = "focus", on = true, t = Stopwatch.GetTimestamp() });
        Deactivated += (_, _) => _log.Write(new { ev = "focus", on = false, t = Stopwatch.GetTimestamp() });
        Closed += (_, _) => Shutdown();

        if (opt.DurationSec > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(opt.DurationSec) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }

    /// <summary>把客户区固定为 --size 指定的物理像素，放在 --monitor 指定的显示器工作区左上角。</summary>
    private void ApplyClientSize()
    {
        if (_hwnd == IntPtr.Zero) return;
        var mon = Native.Pick(_opt);
        if (mon == null) { Console.Error.WriteLine($"找不到屏幕 {_opt.MonitorDevice}"); System.Windows.Application.Current.Shutdown(3); return; }
        var rc = new Native.RECT { Left = 0, Top = 0, Right = _opt.Width, Bottom = _opt.Height };
        uint style = (uint)Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);
        uint ex = (uint)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.AdjustWindowRectExForDpi(ref rc, style, false, ex, mon.Dpi);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, mon.Work.Left + 40, mon.Work.Top + 40, rc.Right - rc.Left, rc.Bottom - rc.Top,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void WriteMeta()
    {
        Native.GetClientRect(_hwnd, out var rc);
        _log.Write(new
        {
            ev = "meta", pid = Environment.ProcessId, hwnd = _hwnd.ToInt64(), seed = _opt.Seed, goal = _opt.Goal,
            client = new[] { rc.Right - rc.Left, rc.Bottom - rc.Top }, request = new[] { _opt.Width, _opt.Height },
            dpi = Native.GetDpiForWindow(_hwnd), monitor = _opt.Monitor, qpcFreq = Stopwatch.Frequency,
            startDelayMs = _opt.StartDelaySec * 1000, t = Stopwatch.GetTimestamp()
        });
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _sw.Elapsed.TotalMilliseconds;
        // 探针用：第 4–7 秒不重绘，帧计数条停住，用来观察 WGC 在画面静止时是否还出帧
        if (_opt.StaticTest && now >= 4000 && now < 7000)
        {
            if (!_frozenLogged) { _frozenLogged = true; _log.Write(new { ev = "freeze", on = true, t = Stopwatch.GetTimestamp(), f = _frameId }); }
            return;
        }
        if (_frozenLogged && _opt.StaticTest && now >= 7000)
        {
            _frozenLogged = false; _opt.StaticTest = false;
            _log.Write(new { ev = "freeze", on = false, t = Stopwatch.GetTimestamp(), f = _frameId });
        }

        _game.Update(now);
        _frameId++;
        long qpc = Stopwatch.GetTimestamp();
        _game.MarkShown(_frameId, qpc);
        _log.Write(new
        {
            f = _frameId, t = qpc, hp = Math.Round(_game.Hp, 1), col = _game.HpColorName, sc = _game.Score,
            caps = _game.Caps.Select(c => new object[] { c.Id, c.Ghost ? "g" : c.Letter.ToString(), c.X, c.Y }).ToArray()
        });
        _view.FrameId = _frameId;
        _view.InvalidateVisual();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        long qpc = Stopwatch.GetTimestamp();
        string k = e.Key == Key.System ? e.SystemKey.ToString() : e.Key.ToString();
        _log.Write(new { ev = "kd", k, rep = e.IsRepeat, t = qpc });
        if (!e.IsRepeat) _keyBalance[k] = _keyBalance.GetValueOrDefault(k) + 1;
        if (e.Key == Key.F && !e.IsRepeat) _game.OnKeyF(qpc);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        string k = e.Key == Key.System ? e.SystemKey.ToString() : e.Key.ToString();
        _log.Write(new { ev = "ku", k, t = Stopwatch.GetTimestamp() });
        _keyBalance[k] = _keyBalance.GetValueOrDefault(k) - 1;
        e.Handled = true;
    }

    private void OnMouse(MouseButtonEventArgs e, string ev)
    {
        long qpc = Stopwatch.GetTimestamp();
        var p = e.GetPosition(_view);
        var basePt = new Point(p.X * 1920.0 / _view.ActualWidth, p.Y * 1080.0 / _view.ActualHeight);
        if (ev == "md") _game.OnClick(basePt, qpc);
        else _log.Write(new { ev, x = Math.Round(basePt.X, 1), y = Math.Round(basePt.Y, 1), t = qpc });
        _keyBalance["MouseLeft"] = _keyBalance.GetValueOrDefault("MouseLeft") + (ev == "md" ? 1 : -1);
    }

    private void Shutdown()
    {
        CompositionTarget.Rendering -= OnRendering;
        var summary = new
        {
            hits = _game.Hits, misses = _game.Misses, wrong = _game.Wrong, potions = _game.Potions, downs = _game.Downs,
            score = _game.Score, goal = _opt.Goal, done = _game.Done,
            doneSec = _game.Done ? Math.Round((_game.DoneMs - _opt.StartDelaySec * 1000) / 1000.0, 2) : (double?)null,
            frames = _frameId,
            unbalancedKeys = _keyBalance.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value)
        };
        File.WriteAllText(Path.Combine(_opt.LogDir, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        _log.Dispose();
    }
}
