using System.Diagnostics;
using System.Windows;

namespace BenchTarget;

/// <summary>画面上的一个键帽。Letter 为 F 且 Ghost=false 才是真目标；E、R 和半透明 F 都是干扰项。</summary>
public sealed class Keycap
{
    public int Id;
    public char Letter;
    public bool Ghost;
    public double X, Y;               // 基准坐标（1920×1080）左上角
    public double SpawnMs;
    public double LifeMs;
    public long FirstFrame = -1;      // 第一次被画出来的帧号
    public long FirstFrameQpc;
    public bool IsTarget => Letter == 'F' && !Ghost;
}

/// <summary>
/// 「巡逻小游戏」的规则，全部在基准坐标系里计算：
/// - 提示键区随机弹出键帽：真 F 要按 F 消掉（命中），超时算漏掉；对干扰项或空场按 F 算误按。
/// - 血量持续下降，颜色绿 → 黄 → 红；点「喝药」按钮回满；掉到 0 算倒下。
/// - 计分到 Goal 时显示「完成」，停止出题。
/// </summary>
public sealed class PatrolGame
{
    public static readonly Rect KeyZone = new(40, 64, 900, 576);
    public static readonly Rect HpZone = new(980, 64, 900, 300);
    public static readonly Rect HpBar = new(1010, 130, 840, 48);
    public static readonly Rect PotionButton = new(1010, 230, 220, 84);
    public static readonly Rect StatsZone = new(980, 388, 900, 252);
    public static readonly Rect ScoreZone = new(40, 664, 900, 150);
    public static readonly Rect StatusZone = new(980, 664, 900, 150);
    public const double CapSize = 72;

    private readonly TargetOptions _opt;
    private readonly TruthLog _log;
    private readonly Random _rng;
    private double _nextEventMs;
    private double _lastMs;
    private int _nextId = 1;

    public readonly List<Keycap> Caps = new();
    public double Hp = 100;
    public int Score, Hits, Misses, Wrong, Potions, Downs;
    public bool Started, Done;
    public double DoneMs;
    public double NowMs;

    public PatrolGame(TargetOptions opt, TruthLog log)
    {
        _opt = opt;
        _log = log;
        _rng = new Random(opt.Seed);
        _nextEventMs = opt.StartDelaySec * 1000 + Rand(400, 1400);
    }

    private double Rand(double a, double b) => a + _rng.NextDouble() * (b - a);

    public string HpColorName => Hp >= 60 ? "green" : Hp >= 35 ? "yellow" : "red";

    /// <summary>样张模式：真 F、E、R、半透明 F 各摆一个，血量 20%（红），计分 17。</summary>
    public void SetupShowcase()
    {
        Started = true;
        Hp = 20;
        Score = 17;
        Caps.Add(new Keycap { Id = 1, Letter = 'F', X = 200, Y = 200, LifeMs = double.MaxValue });
        Caps.Add(new Keycap { Id = 2, Letter = 'E', X = 400, Y = 200, LifeMs = double.MaxValue });
        Caps.Add(new Keycap { Id = 3, Letter = 'R', X = 600, Y = 200, LifeMs = double.MaxValue });
        Caps.Add(new Keycap { Id = 4, Letter = 'F', Ghost = true, X = 200, Y = 420, LifeMs = double.MaxValue });
    }

    public void Update(double nowMs)
    {
        if (_opt.Showcase) { NowMs = nowMs; return; }
        double dt = Math.Max(0, nowMs - _lastMs) / 1000.0;
        _lastMs = nowMs;
        NowMs = nowMs;
        if (!Started)
        {
            if (nowMs < _opt.StartDelaySec * 1000) return;
            Started = true;
            _log.Write(new { ev = "start", t = Stopwatch.GetTimestamp(), ms = nowMs });
        }
        if (Done) return;

        // 过期
        for (int i = Caps.Count - 1; i >= 0; i--)
        {
            var c = Caps[i];
            if (nowMs - c.SpawnMs < c.LifeMs) continue;
            Caps.RemoveAt(i);
            if (c.IsTarget)
            {
                Misses++;
                _log.Write(new { ev = "miss", id = c.Id, t = Stopwatch.GetTimestamp() });
            }
            else _log.Write(new { ev = "expire", id = c.Id, t = Stopwatch.GetTimestamp() });
        }

        // 出题
        if (nowMs >= _nextEventMs)
        {
            double r = _rng.NextDouble();
            bool hasTarget = Caps.Any(c => c.IsTarget);
            if (r < 0.55 && !hasTarget) Spawn('F', false, 2500);
            else if (r < 0.85) Spawn(_rng.NextDouble() < 0.5 ? 'E' : 'R', false, Rand(1200, 1800));
            else if (!Caps.Any(c => c.Ghost)) Spawn('F', true, 1500);
            _nextEventMs = nowMs + Rand(400, 1400);
        }

        // 血量
        Hp -= _opt.HpDecayPerSec * dt;
        if (Hp <= 0)
        {
            Downs++;
            Hp = 100;
            _log.Write(new { ev = "down", t = Stopwatch.GetTimestamp() });
        }
    }

    private void Spawn(char letter, bool ghost, double life)
    {
        double x = 0, y = 0;
        for (int tries = 0; tries < 20; tries++)
        {
            x = Math.Round(Rand(KeyZone.X + 20, KeyZone.Right - 20 - CapSize));
            y = Math.Round(Rand(KeyZone.Y + 48, KeyZone.Bottom - 20 - CapSize));
            var r = new Rect(x - 12, y - 12, CapSize + 24, CapSize + 24);
            if (!Caps.Any(c => r.IntersectsWith(new Rect(c.X, c.Y, CapSize, CapSize)))) break;
        }
        var cap = new Keycap { Id = _nextId++, Letter = letter, Ghost = ghost, X = x, Y = y, SpawnMs = NowMs, LifeMs = life };
        Caps.Add(cap);
        _log.Write(new { ev = "spawn", id = cap.Id, c = letter.ToString(), gh = ghost, x, y, w = CapSize, h = CapSize, t = Stopwatch.GetTimestamp() });
    }

    /// <summary>本帧画出来的键帽，第一次出现时记录帧号和时刻，用于算「出现 → 按键」的端到端延迟。</summary>
    public void MarkShown(long frameId, long qpc)
    {
        foreach (var c in Caps)
        {
            if (c.FirstFrame >= 0) continue;
            c.FirstFrame = frameId;
            c.FirstFrameQpc = qpc;
            _log.Write(new { ev = "shown", id = c.Id, f = frameId, t = qpc });
        }
    }

    public void OnKeyF(long qpc)
    {
        if (!Started || Done) return;
        var target = Caps.FirstOrDefault(c => c.IsTarget && c.FirstFrame >= 0);
        if (target != null)
        {
            Caps.Remove(target);
            Hits++;
            Score++;
            double lat = (qpc - target.FirstFrameQpc) * 1000.0 / Stopwatch.Frequency;
            _log.Write(new { ev = "hit", id = target.Id, t = qpc, lat = Math.Round(lat, 2), score = Score });
            if (Score >= _opt.Goal)
            {
                Done = true;
                DoneMs = NowMs;
                Caps.Clear();
                _log.Write(new { ev = "done", t = qpc, ms = NowMs });
            }
        }
        else
        {
            Wrong++;
            string vis = string.Join(",", Caps.Select(c => c.Ghost ? "ghostF" : c.Letter.ToString()));
            _log.Write(new { ev = "wrong", t = qpc, vis });
        }
    }

    public void OnClick(Point basePt, long qpc)
    {
        bool onPotion = PotionButton.Contains(basePt);
        _log.Write(new { ev = "click", x = Math.Round(basePt.X, 1), y = Math.Round(basePt.Y, 1), onPotion, hp = Math.Round(Hp, 1), color = HpColorName, t = qpc });
        if (!onPotion || !Started || Done) return;
        Potions++;
        _log.Write(new { ev = "potion", hpBefore = Math.Round(Hp, 1), color = HpColorName, t = qpc });
        Hp = 100;
    }
}
