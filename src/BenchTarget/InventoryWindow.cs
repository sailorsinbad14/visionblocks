using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BenchTarget;

/// <summary>背包场景的窗口。窗口摆放、元数据和巡逻小游戏一致；输入（左键、滚轮、Esc）交给 InventoryScene，事件写进 truth.jsonl。</summary>
public sealed class InventoryWindow : Window
{
    private readonly TargetOptions _opt;
    private readonly TruthLog _log;
    private readonly InventoryScene _scene;
    private readonly InventoryView _view;
    private readonly Dictionary<string, int> _keyBalance = new();
    private IntPtr _hwnd;

    public InventoryWindow(TargetOptions opt)
    {
        _opt = opt;
        _log = new TruthLog(opt.LogDir);
        _scene = new InventoryScene(opt, _log);
        _view = new InventoryView(_scene, opt);
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
        _view.MouseLeftButtonDown += (_, e) => OnMouse(e, true);
        _view.MouseLeftButtonUp += (_, e) => OnMouse(e, false);
        _view.MouseWheel += (_, e) => _scene.OnWheel(e.Delta, Base(e), Stopwatch.GetTimestamp());
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
            ev = "meta", scene = "inventory", pid = Environment.ProcessId, hwnd = _hwnd.ToInt64(), seed = _opt.Seed,
            client = new[] { rc.Right - rc.Left, rc.Bottom - rc.Top }, request = new[] { _opt.Width, _opt.Height },
            dpi = Native.GetDpiForWindow(_hwnd), monitor = _opt.Monitor, qpcFreq = Stopwatch.Frequency, t = Stopwatch.GetTimestamp()
        });
    }

    private void OnRendering(object? sender, EventArgs e) => _view.InvalidateVisual();

    private Point Base(MouseEventArgs e)
    {
        var p = e.GetPosition(_view);
        return new Point(p.X * 1920.0 / _view.ActualWidth, p.Y * 1080.0 / _view.ActualHeight);
    }

    private void OnMouse(MouseButtonEventArgs e, bool down)
    {
        long qpc = Stopwatch.GetTimestamp();
        if (down) _scene.OnClick(Base(e), qpc);
        else { var p = Base(e); _log.Write(new { ev = "mu", x = Math.Round(p.X, 1), y = Math.Round(p.Y, 1), t = qpc }); }
        _keyBalance["MouseLeft"] = _keyBalance.GetValueOrDefault("MouseLeft") + (down ? 1 : -1);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        long qpc = Stopwatch.GetTimestamp();
        string k = e.Key == Key.System ? e.SystemKey.ToString() : e.Key.ToString();
        _log.Write(new { ev = "kd", k, rep = e.IsRepeat, t = qpc });
        if (!e.IsRepeat) _keyBalance[k] = _keyBalance.GetValueOrDefault(k) + 1;
        if (e.Key == Key.Escape && !e.IsRepeat) _scene.OnEsc(qpc);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        string k = e.Key == Key.System ? e.SystemKey.ToString() : e.Key.ToString();
        _log.Write(new { ev = "ku", k, t = Stopwatch.GetTimestamp() });
        _keyBalance[k] = _keyBalance.GetValueOrDefault(k) - 1;
        e.Handled = true;
    }

    private void Shutdown()
    {
        CompositionTarget.Rendering -= OnRendering;
        var summary = new
        {
            scene = "inventory", items = _scene.Items.Count, opened = _scene.OpenedSet.Count, opens = _scene.Opens,
            catsVisited = _scene.CatsVisited.Count, clicks = _scene.Clicks, stray = _scene.Stray, wheels = _scene.Wheels,
            notOpened = _scene.Items.Where(i => !_scene.OpenedSet.Contains((i.Cat, i.Idx))).Select(i => $"{InventoryScene.Categories[i.Cat]}#{i.Idx} {i.Name}").ToArray(),
            unbalancedKeys = _keyBalance.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value)
        };
        File.WriteAllText(Path.Combine(_opt.LogDir, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        _log.Dispose();
    }
}
