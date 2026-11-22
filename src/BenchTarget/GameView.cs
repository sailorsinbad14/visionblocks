using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BenchTarget;

/// <summary>
/// 按基准坐标 1920×1080 作画，再整体缩放到实际客户区。
/// 左上角的「帧计数条」是 24 个黑白方块，编码当前帧号；demo 解码它来对齐 ground truth，只用于测量延迟。
/// </summary>
public sealed class GameView : FrameworkElement
{
    public const int CounterBits = 24;
    public const double CounterX = 16, CounterY = 16, CounterCell = 16, CounterPitch = 18;

    private readonly PatrolGame _game;
    private readonly TargetOptions _opt;
    public long FrameId;

    private static readonly Brush Bg = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x26, 0x30)));
    private static readonly Brush Panel = Frozen(new SolidColorBrush(Color.FromRgb(0x24, 0x31, 0x3C)));
    private static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0x9F, 0xB0, 0xBC)));
    private static readonly Brush CapFill = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)));
    private static readonly Pen CapPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF2)), 4));
    private static readonly Brush BarBg = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)));
    private static readonly Pen BarPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), 2));
    private static readonly Brush Green = Frozen(new SolidColorBrush(Color.FromRgb(60, 179, 113)));
    private static readonly Brush Yellow = Frozen(new SolidColorBrush(Color.FromRgb(224, 176, 0)));
    private static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(216, 60, 60)));
    private static readonly Brush Button = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x60)));
    private static readonly Brush ScoreBg = Frozen(new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF1)));
    private static readonly Brush ScoreInk = Frozen(new SolidColorBrush(Color.FromRgb(0x15, 0x21, 0x2B)));
    private static readonly Brush DoneInk = Frozen(new SolidColorBrush(Color.FromRgb(0x2C, 0x7A, 0x4C)));
    private static readonly Typeface Ui = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiBold = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public GameView(PatrolGame game, TargetOptions opt)
    {
        _game = game;
        _opt = opt;
        SnapsToDevicePixels = true;
    }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, bold ? UiBold : Ui, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 1920.0, ActualHeight / 1080.0));
        dc.DrawRectangle(Bg, null, new Rect(0, 0, 1920, 1080));

        // 帧计数条：1 = 白，0 = 黑
        dc.DrawRectangle(Brushes.Gray, null, new Rect(CounterX - 4, CounterY - 4, CounterBits * CounterPitch + 6, CounterCell + 8));
        for (int i = 0; i < CounterBits; i++)
        {
            bool bit = ((FrameId >> (CounterBits - 1 - i)) & 1) == 1;
            dc.DrawRectangle(bit ? Brushes.White : Brushes.Black, null, new Rect(CounterX + i * CounterPitch, CounterY, CounterCell, CounterCell));
        }
        dc.DrawText(Text($"BenchTarget · 巡逻小游戏 · seed {_opt.Seed}", 20, Label), new Point(470, 12));

        // 提示键区：背景缓慢变化
        double t = _game.NowMs / 1000.0;
        var zoneBrush = new LinearGradientBrush(
            Color.FromRgb(34, (byte)(48 + 10 * Math.Sin(t * 0.7)), 60),
            Color.FromRgb(24, 34, (byte)(44 + 12 * Math.Sin(t * 0.5))), 30);
        dc.DrawRoundedRectangle(zoneBrush, null, PatrolGame.KeyZone, 8, 8);
        dc.DrawText(Text("提示键区", 20, Label), new Point(PatrolGame.KeyZone.X + 16, PatrolGame.KeyZone.Y + 8));
        foreach (var c in _game.Caps)
        {
            if (c.Ghost) dc.PushOpacity(0.35);
            var r = new Rect(c.X, c.Y, PatrolGame.CapSize, PatrolGame.CapSize);
            dc.DrawRoundedRectangle(CapFill, CapPen, r, 10, 10);
            var ft = Text(c.Letter.ToString(), 40, Brushes.White, true);
            dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
            if (c.Ghost) dc.Pop();
        }

        // 血量
        dc.DrawRoundedRectangle(Panel, null, PatrolGame.HpZone, 8, 8);
        dc.DrawText(Text("血量", 20, Label), new Point(PatrolGame.HpZone.X + 16, PatrolGame.HpZone.Y + 8));
        dc.DrawRectangle(BarBg, BarPen, PatrolGame.HpBar);
        var fill = new Rect(PatrolGame.HpBar.X + 2, PatrolGame.HpBar.Y + 2, Math.Max(0, (PatrolGame.HpBar.Width - 4) * _game.Hp / 100.0), PatrolGame.HpBar.Height - 4);
        dc.DrawRectangle(_game.Hp >= 60 ? Green : _game.Hp >= 35 ? Yellow : Red, null, fill);
        dc.DrawRoundedRectangle(Button, null, PatrolGame.PotionButton, 10, 10);
        var pt = Text("喝药", 36, Brushes.White, true);
        dc.DrawText(pt, new Point(PatrolGame.PotionButton.X + (PatrolGame.PotionButton.Width - pt.Width) / 2,
            PatrolGame.PotionButton.Y + (PatrolGame.PotionButton.Height - pt.Height) / 2));

        // 统计
        dc.DrawRoundedRectangle(Panel, null, PatrolGame.StatsZone, 8, 8);
        dc.DrawText(Text("统计", 20, Label), new Point(PatrolGame.StatsZone.X + 16, PatrolGame.StatsZone.Y + 8));
        dc.DrawText(Text($"命中 {_game.Hits}　漏掉 {_game.Misses}　误按 {_game.Wrong}", 28, Brushes.White), new Point(PatrolGame.StatsZone.X + 24, PatrolGame.StatsZone.Y + 56));
        dc.DrawText(Text($"喝药 {_game.Potions}　倒下 {_game.Downs}", 28, Brushes.White), new Point(PatrolGame.StatsZone.X + 24, PatrolGame.StatsZone.Y + 110));
        double secs = _game.Started ? ((_game.Done ? _game.DoneMs : _game.NowMs) - _opt.StartDelaySec * 1000) / 1000.0 : 0;
        dc.DrawText(Text($"用时 {secs:0.0}s", 28, Brushes.White), new Point(PatrolGame.StatsZone.X + 24, PatrolGame.StatsZone.Y + 164));

        // 计分（OCR 读这里）
        dc.DrawRoundedRectangle(ScoreBg, null, PatrolGame.ScoreZone, 8, 8);
        string score = _game.Done ? $"计分 {_game.Score} · 完成" : $"计分 {_game.Score}";
        dc.DrawText(Text(score, 72, _game.Done ? DoneInk : ScoreInk, true), new Point(PatrolGame.ScoreZone.X + 30, PatrolGame.ScoreZone.Y + 22));

        // 状态
        dc.DrawRoundedRectangle(Panel, null, PatrolGame.StatusZone, 8, 8);
        string status = !_game.Started ? $"准备中 {Math.Max(0, _opt.StartDelaySec - _game.NowMs / 1000):0}…" : _game.Done ? "已完成" : "进行中";
        dc.DrawText(Text("状态：" + status, 32, Brushes.White), new Point(PatrolGame.StatusZone.X + 24, PatrolGame.StatusZone.Y + 50));

        dc.DrawText(Text("玩法：按 F 消掉弹出的 F 键帽（E、R 和半透明的 F 是干扰项）；血条变红时点「喝药」。", 24, Label), new Point(40, 860));
        dc.Pop();
    }
}
