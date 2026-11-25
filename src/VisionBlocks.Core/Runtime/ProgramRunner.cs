using System.Diagnostics;
using OpenCvSharp;
using VisionBlocks.Core.Capture;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Vision;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Core.Runtime;

public sealed class RunContext
{
    public required VbProgram Program { get; init; }
    public required string ProgramDir { get; init; }
    public required IFrameSource Source { get; init; }
    public required SendInputBackend Input { get; init; }
    public required BotLog Log { get; init; }
    public required IntPtr TargetHwnd { get; init; }
    public IOverlaySink? Overlay { get; init; }
    /// <summary>热键暂停时返回 true；暂停期间先抬起所有键，再原地等待。</summary>
    public Func<bool>? IsPaused { get; init; }
    /// <summary>暂停 / 继续的热键文字，显示在遮罩上（2026-11-23 起可以改）。</summary>
    public string PauseKey { get; init; } = "Ctrl+Alt+F11";
    /// <summary>「记录一行」「截图存图」的输出目录（data.csv 和 images\）。为空时用不了这两块积木。</summary>
    public string? OutputDir { get; init; }
    /// <summary>程序文件路径，写进 manifest.json。</summary>
    public string? ProgramPath { get; init; }
    /// <summary>
    /// 认图案分不开（第一、二名分数太近，或者有点像但没过门槛）时问用户（2026-11-25）。编辑器弹确认框：运行停在这里，按键都松开，靶子停在这一页。
    /// 返回 null（设置成不问、等太久没人答）就写一个不会和认出来的值撞的值（「1天?」「未认出」）接着跑，这一行留在表里。vbrun 不设，等于不问。
    /// </summary>
    public Func<UnsureRequest, CancellationToken, Task<UnsureAnswer?>>? AskUnsure { get; init; }
}

public readonly record struct CondResult(bool Hit, string Detail);

/// <summary>认图案分不开时问用户的内容（2026-11-25）。</summary>
/// <param name="Candidates">每张模板：模板名、写进字段的值、分数（从高到低）。</param>
/// <param name="Fallback">没人确认时写的值（「1天?」「未认出」）。</param>
/// <param name="Row">这一行到现在为止读到的各字段（让用户知道是哪件物品）。</param>
/// <param name="RegionPng">认图案的热区截图（PNG）。</param>
/// <param name="BadgePng">分数最高那张模板匹配到的那一块（PNG，基准尺寸）：用户要「存成新模板」时存的就是它。</param>
/// <param name="BadgeInRegion">那一块在热区截图里的位置（截图像素）。</param>
public sealed record UnsureRequest(ClassifyBlock Block, IReadOnlyList<(string Template, string Value, double Score)> Candidates, bool Close, string Fallback,
    IReadOnlyDictionary<string, string> Row, byte[] RegionPng, byte[]? BadgePng, (int X, int Y, int W, int H)? BadgeInRegion);

/// <param name="Value">写进字段的值；null = 没人答（超时），按「不问」处理。</param>
/// <param name="NewTemplate">用户存成了新模板：模板名（已经加进程序和这块认图案，运行器把它载进来）。</param>
/// <param name="Stop">用户点了「停止运行」。</param>
/// <param name="Skipped">用户点了「先记下接着跑」或者关掉了确认框（不是没人答）。</param>
public sealed record UnsureAnswer(string? Value, string? NewTemplate = null, bool Stop = false, bool Skipped = false);

/// <summary>
/// 积木解释器。按「独立任务」模型来跑：
/// 整个程序是一个可取消的任务，所有等待都可取消，退出（正常、取消、异常、急停）时一律 ReleaseAll。
/// </summary>
public sealed class ProgramRunner : IDisposable
{
    private readonly RunContext _c;
    private readonly Dictionary<string, TemplateMatcher> _templates = new();
    private OcrReader? _ocr;
    private NormFrame? _frame;
    private long _frameSeq = -1;
    private readonly Dictionary<string, OverlayBox> _boxes = new();
    private string _status = "";
    private RegionDef? _cell;             // 「对每一格」循环里的当前格子（基准坐标）
    private Recorder? _rec;
    private readonly Dictionary<string, DistanceScroller> _scrollers = new();
    private readonly Dictionary<string, DistanceDragger> _draggers = new();
    /// <summary>
    /// 到底走不满（2026-11-25）：网格名 → 累计少走了多少（基准像素，正 = 内容比预期低）和拖 / 滚的是哪个热区。
    /// 「对每一格」按它把网格下移「少走的距离按行距取余」；进入「重复直到没有新记录」时清零（换分类后列表回到顶）。
    /// </summary>
    private readonly Dictionary<string, (double Shortfall, string Region)> _gridShift = new();
    /// <summary>这一遍里有没有一块「拖动」「按距离滚」一点没动并报到底（「重复直到没有新记录」按它停，2026-11-25）。</summary>
    private bool _atEndNoMove;

    public int Evaluations, Keys, Clicks, ConfirmFails, WaitTimeouts, Wheels, Drags, Reads;
    /// <summary>认图案的次数、其中分不太开的、有点像但没过门槛的；读文字读空后重读的次数、重读读出字的次数（2026-11-25）。</summary>
    public int Classifies, PatternClose, PatternMidway, Rereads, RereadHits;
    /// <summary>
    /// 认图案分不开的次数（= 分不太开 + 有点像但没过门槛）；其中用户确认了值的、点了「先记下」的、
    /// 和前面问过的是同一张图、直接用上次答案的次数。剩下的是没人确认（vbrun、设置成不问、超时）。
    /// </summary>
    public int Unsures, UnsureAnswered, UnsureSkipped, UnsureRemembered;
    public int RowsRecorded => _rec?.Rows ?? 0;
    public int Duplicates => _rec?.Duplicates ?? 0;
    public int EmptyKeys => _rec?.EmptyKeys ?? 0;
    /// <summary>这次运行的 manifest.json（程序里有「读文字」「截图存图」时才有）。</summary>
    public ResultManifest? Manifest { get; private set; }

    private sealed class StopSignal : Exception { }
    private sealed class UserStopSignal : Exception { }

    /// <summary>认图案分不开、又没人确认时写的值：分数太近写「第一名?」，有点像但没过门槛写「未认出」——都不会和认出来的值撞（同名、角标也分不开的两件照样会按去重并成一行）。</summary>
    public static string UnsureValue(PatternClassifier.Result r) => r.Close ? r.Value + "?" : "未认出";

    public ProgramRunner(RunContext c)
    {
        _c = c;
        foreach (var (name, def) in c.Program.Templates)
            _templates[name] = TemplateMatcher.Load(Path.Combine(c.ProgramDir, def.File));
    }

    public async Task<string> RunAsync(CancellationToken ct)
    {
        var errs = ProgramIO.Validate(_c.Program);
        if (errs.Count > 0) throw new InvalidDataException("程序有错误：\n" + string.Join("\n", errs));
        var hz = _c.Input.Humanizer;
        _c.Log.Write(new { ev = "runStart", program = _c.Program.Name, humanize = hz == null ? null : new { timing = hz.Timing, path = hz.Path, overshoot = hz.Overshoot, seed = hz.Seed }, t = BotLog.Now });
        if (_c.OutputDir != null && Recorder.FieldsOf(_c.Program).Count > 0)
        {
            _rec = new Recorder(_c.OutputDir, _c.Program);
            Manifest = ResultManifest.Begin(_c.OutputDir, _c.Program, _c.ProgramPath);
        }
        string result = "完成", status = "error";
        try { await ExecList(_c.Program.Program, ct); status = "done"; }
        catch (OperationCanceledException) { result = "已取消"; status = "cancelled"; }
        catch (StopSignal) { result = "遇到「停止」积木"; status = "stoppedByBlock"; }
        catch (UserStopSignal) { result = "你在认图案确认框里停止了运行"; status = "stoppedByUser"; }
        finally
        {
            int released = _c.Input.ReleaseAll();
            _c.Log.Write(new { ev = "runEnd", result, released, Evaluations, Keys, Clicks, ConfirmFails, WaitTimeouts, Wheels, Drags, Reads, rows = RowsRecorded, dups = Duplicates, emptyKeys = EmptyKeys,
                Classifies, PatternClose, PatternMidway, Unsures, UnsureAnswered, UnsureSkipped, UnsureRemembered, Rereads, RereadHits, blocked = _c.Input.Guard.Blocked, t = BotLog.Now });
            _rec?.Dispose();
            try { Manifest?.Finish(status, RowsRecorded, Duplicates, EmptyKeys); }
            catch (Exception e) { _c.Log.Write(new { ev = "manifestFailed", error = e.Message, t = BotLog.Now }); }
            _c.Overlay?.SetScene(OverlayScene.Empty);
        }
        return result;
    }

    private async Task ExecList(List<Block> list, CancellationToken ct)
    {
        foreach (var b in list) await Exec(b, ct);
    }

    private async Task WaitIfPaused(CancellationToken ct)
    {
        if (_c.IsPaused == null || !_c.IsPaused()) return;
        _c.Input.ReleaseAll();   // 拖到一半暂停时，输入后端已经先松开了左键，这里再兜一次底
        _c.Log.Write(new { ev = "paused", t = BotLog.Now });
        Publish($"已暂停（{_c.PauseKey} 继续）");
        while (_c.IsPaused()) await Task.Delay(50, ct);
        _c.Log.Write(new { ev = "resumed", t = BotLog.Now });
    }

    private async Task Exec(Block b, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await WaitIfPaused(ct);
        switch (b)
        {
            case RepeatUntilBlock ru:
            {
                var sw = Stopwatch.StartNew();
                long lastCheck = long.MinValue / 2, held0 = _heldMs;
                while (true)
                {
                    if (ru.CheckEveryMs <= 0 || sw.ElapsedMilliseconds - lastCheck >= ru.CheckEveryMs)
                    {
                        lastCheck = sw.ElapsedMilliseconds;
                        if ((await Eval(ru.Until, ct)).Hit) break;
                    }
                    if (ru.TimeoutMs > 0 && sw.ElapsedMilliseconds - (_heldMs - held0) > ru.TimeoutMs)
                    {
                        _c.Log.Write(new { ev = "loopTimeout", ms = sw.ElapsedMilliseconds, held = _heldMs - held0, t = BotLog.Now });
                        break;
                    }
                    if (ru.Body.Count == 0) await WaitNextFrame(ct, 50);
                    else await ExecList(ru.Body, ct);
                }
                break;
            }
            case RepeatBlock r:
                for (int i = 0; i < r.Times; i++) await ExecList(r.Body, ct);
                break;
            case IfBlock ib:
                if ((await Eval(ib.Cond, ct)).Hit) await ExecList(ib.Body, ct);
                else await ExecList(ib.Else, ct);
                break;
            case WaitUntilBlock wu:
                if (!await WaitUntil(wu.Cond, wu.TimeoutMs, ct)) WaitTimeouts++;
                break;
            case WaitBlock w:
            {
                // 只等这么久：下一块点击的移动轨迹、移动前的小停顿都在那一块里，另外算（2026-11-23 写进日志，便于核对）
                int planned = Math.Max(0, _c.Input.Humanizer?.Wait(w.Ms) ?? w.Ms);
                var sw = Stopwatch.StartNew();
                await Task.Delay(planned, ct);
                _c.Log.Write(new { ev = "wait", ms = w.Ms, planned, actual = sw.ElapsedMilliseconds, t = BotLog.Now });
                break;
            }
            case KeyBlock k:
                Publish($"按键 {k.Key}");
                if (await _c.Input.PressAsync(k.Key, k.HoldMs, ct)) Keys++;
                break;
            case ClickBlock cb:
                await Click(cb, ct);
                break;
            case TypeTextBlock tt:
                _c.Input.TypeText(tt.Text);
                break;
            case StopBlock:
                throw new StopSignal();
            case ForEachCellBlock fe:
            {
                var g = _c.Program.Grids[fe.Grid];
                var cells = CellsOf(fe.Grid, g, out double phase);
                var outer = _cell;
                try
                {
                    for (int i = 0; i < cells.Count; i++)
                    {
                        _cell = cells[i];
                        SetBox("cell", new OverlayBox(_cell, $"{Label(g.Label, fe.Grid)} 第 {i + 1} 格" + (phase > 0 ? $"（网格下移 {phase:0} px）" : ""), "region"));
                        await ExecList(fe.Body, ct);
                    }
                }
                finally { _cell = outer; }
                break;
            }
            case UntilNoNewRowsBlock un:
            {
                if (_gridShift.Count > 0) _c.Log.Write(new { ev = "gridShiftReset", grids = _gridShift.Keys.ToArray(), t = BotLog.Now });
                _gridShift.Clear();   // 换了分类 / 列表，回到顶，网格回原位
                int pass = 0;
                bool outerAtEnd = _atEndNoMove;   // 里面再套一层时，里层的到底不影响外层
                try
                {
                    while (true)
                    {
                        pass++;
                        int before = RowsRecorded;
                        _atEndNoMove = false;
                        await ExecList(un.Body, ct);
                        int added = RowsRecorded - before;
                        _c.Log.Write(new { ev = "pass", pass, added, atEnd = _atEndNoMove, t = BotLog.Now });
                        if (added == 0) break;
                        if (un.StopAtEnd && _atEndNoMove) { _c.Log.Write(new { ev = "loopAtEnd", pass, t = BotLog.Now }); Publish($"拖到底了，画面一点没动：第 {pass} 遍做完就停"); break; }
                        if (pass >= un.MaxTimes) { _c.Log.Write(new { ev = "loopCap", pass, t = BotLog.Now }); break; }
                    }
                }
                finally { _atEndNoMove = outerAtEnd; }
                break;
            }
            case WheelBlock { DistancePx: int dist } wd:
            {
                var roi = Region(wd.Region!);
                var (bx, by) = Inside(roi);
                if (!ToScreen(bx, by, out int sx, out int sy)) break;
                Publish($"滚轮 {dist} px");
                // 每个热区在整个运行里共用一个：「认不认不足一格」只试一次，「每格多少像素」接着上次的用
                if (!_scrollers.TryGetValue(wd.Region!, out var scroller))
                    _scrollers[wd.Region!] = scroller = new DistanceScroller { Log = o => _c.Log.Write(o) };
                scroller.Progress = (moved, steps, pn) => SetBox("scroll:" + wd.Region, ScrollArrow(roi, dist, moved, steps, pn));
                var io = new DistanceScroller.Io(
                    Send: (delta, count, c) => _c.Input.WheelAsync(sx, sy, Math.Sign(delta) * count, 60, c, Math.Abs(delta)),
                    Grab: async c =>
                    {
                        await WaitNextFrame(c, 100);
                        var f = await CurrentFrame(c);
                        return (new Mat(f.Gray, f.ToPixels(roi)).Clone(), f.K);
                    },
                    Delay: (ms, c) => Task.Delay(ms, c),
                    NowMs: () => Environment.TickCount64);
                var res = await scroller.RunAsync(dist, wd.TolerancePx ?? WheelBlock.DefaultTolerancePx, io, ct);
                if (res.EventsSent > 0) Wheels++;
                _c.Log.Write(new
                {
                    ev = "wheelPx", want = dist, got = Math.Round(res.Moved, 1), steps = res.Steps,
                    perNotch = res.PerNotch is double pn ? Math.Round(pn, 1) : (double?)null, subNotch = res.SubNotch,
                    end = res.End, reason = res.Reason, fails = res.MeasureFails, events = res.EventsSent,
                    carryIn = Math.Round(res.CarryIn, 1), carryOut = Math.Round(res.CarryOut, 1), t = BotLog.Now
                });
                string shiftNote = NoteShortfall(wd.Region!, roi, dist + res.CarryIn, res.Moved, res.Reason);
                if (EndedWithoutMoving(res.Reason, res.Moved)) _atEndNoMove = true;
                Publish(res.Reason switch
                {
                    "end" => $"滚轮：到底了，滚了 {res.Moved:0} / {dist} px",
                    "nearestNotch" => $"滚轮 {res.Moved:0} / {dist} px（这个程序只认整格）",
                    "cantMeasure" => "滚轮：量不出画面移动，停下",
                    _ => $"滚轮 {res.Moved:0} / {dist} px"
                } + shiftNote);
                break;
            }
            case WheelBlock wb:
            {
                var (bx, by) = wb.Point != null ? Near(_c.Program.Points[wb.Point].X, _c.Program.Points[wb.Point].Y) : Inside(Region(wb.Region!));
                if (!ToScreen(bx, by, out int sx, out int sy)) break;
                Publish($"滚轮 {wb.Notches} 格");
                if (await _c.Input.WheelAsync(sx, sy, wb.Notches, 60, ct) > 0) Wheels++;
                break;
            }
            case DragBlock db:
                await Drag(db, ct);
                break;
            case ReadTextBlock rt:
            {
                var f = await CurrentFrame(ct);
                var roi = Region(rt.Region);
                _ocr ??= new OcrReader();
                var (raw, norm, ms) = await _ocr.ReadAsync(f, roi, ct);
                string Keep(string s) => rt.NumbersOnly ? new string(s.Where(char.IsAsciiDigit).ToArray()) : s;
                string value = Keep(norm);
                if (string.IsNullOrWhiteSpace(value))
                {
                    // 读空时放大 4 倍、四周补 24 像素再读一次（2026-11-25：11-20 实测贴字框读空的 7 个名称，这样 7 个都读出字、5 个全对）
                    var (raw2, norm2, ms2) = await _ocr.ReadAsync(f, roi, ct, RereadScale, RereadPad);
                    string v2 = Keep(norm2);
                    Rereads++;
                    if (!string.IsNullOrWhiteSpace(v2)) { RereadHits++; raw = raw2; norm = norm2; value = v2; }
                    ms += ms2;
                    _c.Log.Write(new { ev = "reread", field = rt.Field, raw = raw2, value = v2, scale = RereadScale, pad = RereadPad, ms = Math.Round(ms2, 1), t = BotLog.Now });
                }
                Reads++;
                _rec?.Set(rt.Field, value);
                _c.Log.Write(new { ev = "read", field = rt.Field, raw, value, ms = Math.Round(ms, 1), seq = f.Seq, t = BotLog.Now });
                SetBox("roi:" + rt.Region, new OverlayBox(roi, $"{rt.Field}：{value}", "region"));
                Publish($"{rt.Field}：{value}");
                break;
            }
            case SaveImageBlock si:
            {
                if (_rec == null) throw new InvalidOperationException("没有输出目录，存不了图");
                var f = await CurrentFrame(ct);
                var roi = Region(si.Region);
                string rel = Path.Combine("images", $"{_rec.NextRowNo:000}_{Safe(si.Field)}.png");
                Directory.CreateDirectory(Path.Combine(_rec.Dir, "images"));
                byte[] thumb = new byte[16 * 16 * 3];
                using (var crop = new Mat(f.Bgr, f.ToPixels(roi)))
                using (var small = new Mat())
                {
                    Cv2.ImWrite(Path.Combine(_rec.Dir, rel), crop);
                    Cv2.Resize(crop, small, new Size(16, 16), 0, 0, InterpolationFlags.Area);
                    System.Runtime.InteropServices.Marshal.Copy(small.Data, thumb, 0, thumb.Length);
                }
                _rec.SetImage(si.Field, rel.Replace('\\', '/'), thumb);
                _c.Log.Write(new { ev = "saveImage", field = si.Field, file = rel, seq = f.Seq, t = BotLog.Now });
                break;
            }
            case ClassifyBlock cl:
            {
                var f = await CurrentFrame(ct);
                var roi = Region(cl.Region);
                var res = PatternClassifier.Classify(f, roi, cl.Templates.Select(n => (n, PatternClassifier.ValueOf(_c.Program, n), _templates[n])).ToList(), cl.Threshold, cl.NoneText);
                Classifies++;
                if (res.Close) PatternClose++;
                if (res.Midway) PatternMidway++;
                _c.Log.Write(new
                {
                    ev = "classify", field = cl.Field, value = res.Value, best = Math.Round(res.Best, 3), second = Math.Round(res.Second, 3),
                    close = res.Close, byColor = res.ByColor, midway = res.Midway,
                    scores = res.All.Select(c => new { tpl = c.Template, score = Math.Round(c.Score, 3), color = double.IsNaN(c.ColorDist) ? (double?)null : Math.Round(c.ColorDist, 1) }),
                    seq = f.Seq, t = BotLog.Now
                });
                SetBox("roi:" + cl.Region, new OverlayBox(roi, $"{cl.Field}：{res.Value}", "region"));
                if (res.Winner?.Box is { } wb) SetBox("match:classify", new OverlayBox(wb, $"{res.Winner.Value} {res.Winner.Score:0.00}", "match"));
                Publish($"{cl.Field}：{res.Value}" + (res.Close ? $"（分不太开：{res.Best:0.00} / {res.Second:0.00}）" : res.Midway ? $"（有点像但没过门槛：最高 {res.Best:0.00}，可能是还没裁模板的新图案）" : ""));
                string value = res.Close || res.Midway ? await Unsure(cl, f, roi, res, ct) : res.Value;
                _rec?.Set(cl.Field, value);
                break;
            }
            case RecordRowBlock rr:
            {
                if (_rec == null) throw new InvalidOperationException("没有输出目录，记录不了");
                bool added = _rec.Record(rr.DedupeBy, out string key);
                _c.Log.Write(new { ev = added ? "row" : "dup", key, rows = _rec.Rows, t = BotLog.Now });
                Publish(added ? $"已记录第 {_rec.Rows} 行" : $"重复，跳过：{key}");
                break;
            }
        }
    }

    /// <summary>热区名 → 基准坐标；「当前格」取循环里的当前格子。</summary>
    private RegionDef Region(string name) =>
        name == VbProgram.CurrentCell ? _cell ?? throw new InvalidOperationException("「当前格」只能在「对每一格」循环里用") : _c.Program.Regions[name];

    /// <summary>点击、滚轮落在热区里的哪一点（基准坐标）：平时是中心；开了随机化时是热区内的随机点。</summary>
    private (int, int) Inside(RegionDef r) => _c.Input.Humanizer?.PointIn(r.X, r.Y, r.W, r.H) ?? (r.X + r.W / 2, r.Y + r.H / 2);
    private (int, int) Near(int x, int y) => _c.Input.Humanizer?.PointNear(x, y) ?? (x, y);

    private static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>读空后重读：放大几倍、四周补几像素（11-20 那批读空的名称上试出来的）。</summary>
    public const double RereadScale = 4;
    public const int RereadPad = 24;

    /// <summary>基准坐标 → 客户区像素（按宽度比例，和 NormFrame 一致）→ 屏幕物理像素。</summary>
    private bool ToScreen(int bx, int by, out int sx, out int sy)
    {
        sx = sy = 0;
        if (!WindowInfo.TryGetClientOnScreen(_c.TargetHwnd, out var client)) return false;
        double s = client.W / (double)_c.Program.Base.W;
        sx = client.X + (int)Math.Round(bx * s);
        sy = client.Y + (int)Math.Round(by * s);
        return true;
    }

    private async Task<bool> WaitUntil(Condition cond, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if ((await Eval(cond, ct)).Hit) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                _c.Log.Write(new { ev = "waitTimeout", ms = sw.ElapsedMilliseconds, t = BotLog.Now });
                return false;
            }
            await WaitNextFrame(ct, 100);
        }
    }

    /// <summary>等到有新的一帧（或超时）。画面静止时 WGC 可能不再出帧，所以要有超时。</summary>
    private async Task WaitNextFrame(CancellationToken ct, int timeoutMs)
    {
        long seq = _c.Source.LatestSeq;
        var sw = Stopwatch.StartNew();
        while (_c.Source.LatestSeq == seq && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(4, ct);
    }

    /// <summary>拿当前帧（同一帧内多次求值复用同一个归一化结果）。拿不到帧时等待，最长 10 秒。</summary>
    private async Task<NormFrame> CurrentFrame(CancellationToken ct)
    {
        if (_frame != null && _c.Source.LatestSeq == _frameSeq) return _frame;
        var sw = Stopwatch.StartNew();
        bool logged = false;
        while (true)
        {
            using var raw = _c.Source.TryGetLatest();
            if (raw != null)
            {
                _frame?.Dispose();
                _frame = NormFrame.FromBgra(raw.Bgra, _c.Program.Base.W, raw.Seq, raw.ArrivalQpc);
                _frameSeq = raw.Seq;
                if (logged) _c.Log.Write(new { ev = "frameBack", ms = sw.ElapsedMilliseconds, t = BotLog.Now });
                return _frame;
            }
            if (!logged) { logged = true; _c.Log.Write(new { ev = "noFrame", t = BotLog.Now }); Publish("截不到画面（窗口最小化或已关闭？）"); }
            if (!WindowInfo.IsAlive(_c.TargetHwnd)) throw new OperationCanceledException("目标窗口已关闭");
            if (sw.ElapsedMilliseconds > 10_000) throw new TimeoutException("10 秒内截不到画面");
            await Task.Delay(50, ct);
        }
    }

    public async Task<CondResult> Eval(Condition cond, CancellationToken ct)
    {
        var f = await CurrentFrame(ct);
        Evaluations++;
        bool hit;
        string detail;
        switch (cond)
        {
            case ImageCondition ic:
            {
                var roi = Region(ic.Region);
                var m = _templates[ic.Template].Match(f, roi);
                hit = ConditionRules.ImageHit(m, ic);
                detail = $"{Label(_c.Program.Templates[ic.Template].Label, ic.Template)} 得分 {m.Score:0.00} 对比度 {m.Contrast:0.00}";
                _c.Log.Write(new { ev = "cond", kind = "image", tpl = ic.Template, score = Math.Round(m.Score, 4), contrast = Math.Round(m.Contrast, 3), hit, ms = Math.Round(m.Ms, 2), seq = f.Seq, arr = f.ArrivalQpc, t = BotLog.Now });
                SetBox("roi:" + ic.Region, new OverlayBox(roi, Label(roi.Label, ic.Region), "region"));
                if (m.Box != null) SetBox("match:" + ic.Template, new OverlayBox(m.Box, $"{m.Score:0.00}", hit ? "match" : "miss"));
                break;
            }
            case ColorCondition cc:
            {
                var roi = Region(cc.Region);
                var (ratio, ms) = ColorRatio.Measure(f, roi, _c.Program.Colors[cc.Color]);
                hit = ConditionRules.ColorHit(ratio, cc);
                detail = $"{Label(_c.Program.Colors[cc.Color].Label, cc.Color)} 占比 {ratio:P1}";
                _c.Log.Write(new { ev = "cond", kind = "color", color = cc.Color, ratio = Math.Round(ratio, 4), hit, ms = Math.Round(ms, 2), seq = f.Seq, t = BotLog.Now });
                SetBox("roi:" + cc.Region, new OverlayBox(roi, $"{Label(roi.Label, cc.Region)} {ratio:P0}", hit ? "match" : "region"));
                break;
            }
            case TextCondition tc:
            {
                var roi = Region(tc.Region);
                _ocr ??= new OcrReader();
                var (raw, norm, ms) = await _ocr.ReadAsync(f, roi, ct);
                hit = ConditionRules.TextHit(norm, tc);
                detail = $"文字「{norm}」";
                _c.Log.Write(new { ev = "cond", kind = "text", raw, norm, want = tc.Contains, hit, ms = Math.Round(ms, 2), seq = f.Seq, t = BotLog.Now });
                SetBox("roi:" + tc.Region, new OverlayBox(roi, $"OCR：{norm}", "region"));
                break;
            }
            case MultiCondition mc:
            {
                // 从上往下判断：全部满足遇到一个不满足就停，任一满足遇到一个满足就停（后面的不再截图识别，慢的文字条件放最后能省时间）
                hit = !mc.Any;
                var parts = new List<string>();
                foreach (var sub in mc.Items)
                {
                    var r = await Eval(sub, ct);
                    parts.Add((r.Hit ? "✓ " : "✗ ") + r.Detail);
                    if (mc.Any && r.Hit) { hit = true; break; }
                    if (!mc.Any && !r.Hit) { hit = false; break; }
                }
                detail = string.Join(mc.Any ? " 或者 " : " 并且 ", parts);
                _c.Log.Write(new { ev = "cond", kind = "multi", mode = mc.Mode, hit, checkedItems = parts.Count, items = mc.Items.Count, t = BotLog.Now });
                break;
            }
            default: throw new NotSupportedException(cond.GetType().Name);
        }
        if (cond.Negate) hit = !hit;
        Publish(detail);
        return new CondResult(hit, detail);
    }

    private static string Label(string label, string name) => string.IsNullOrWhiteSpace(label) ? name : label;

    private (int X, int Y) BasePointOf(ClickBlock cb)
    {
        if (cb.Point != null) { var p = _c.Program.Points[cb.Point]; return Near(p.X, p.Y); }
        return Inside(Region(cb.Region!));
    }

    private async Task Click(ClickBlock cb, CancellationToken ct)
    {
        var (bx, by) = BasePointOf(cb);
        if (!WindowInfo.TryGetClientOnScreen(_c.TargetHwnd, out var client)) return;
        // 基准坐标 → 客户区像素（按宽度比例，和 NormFrame 一致）→ 屏幕物理像素
        double s = client.W / (double)_c.Program.Base.W;
        int sx = client.X + (int)Math.Round(bx * s), sy = client.Y + (int)Math.Round(by * s);

        Mat? before = null;
        var probe = new RegionDef { X = bx - 32, Y = by - 32, W = 64, H = 64 };
        if (cb.Confirm == ConfirmMode.PixelChange)
        {
            var f0 = await CurrentFrame(ct);
            before = new Mat(f0.Gray, f0.ToPixels(probe)).Clone();
        }
        Publish($"点击 ({bx},{by})");
        SetBox("click", new OverlayBox(new RegionDef { X = bx - 6, Y = by - 6, W = 12, H = 12 }, "点击", "point"));
        if (!await _c.Input.ClickAsync(sx, sy, cb.HoldMs, ct)) { before?.Dispose(); return; }
        Clicks++;

        var sw = Stopwatch.StartNew();
        bool ok = true;
        if (cb.Confirm == ConfirmMode.Condition && cb.ConfirmCond != null)
            ok = await WaitUntil(cb.ConfirmCond, cb.ConfirmTimeoutMs, ct);
        else if (cb.Confirm == ConfirmMode.PixelChange && before != null)
        {
            ok = false;
            while (sw.ElapsedMilliseconds < cb.ConfirmTimeoutMs)
            {
                await WaitNextFrame(ct, 100);
                var f1 = await CurrentFrame(ct);
                using var after = new Mat(f1.Gray, f1.ToPixels(probe));
                if (after.Size() != before.Size()) break;
                using var diff = new Mat();
                Cv2.Absdiff(before, after, diff);
                if (Cv2.Mean(diff).Val0 > 6) { ok = true; break; }
            }
            before.Dispose();
        }
        if (!ok) ConfirmFails++;
        _c.Log.Write(new { ev = "clickConfirm", mode = cb.Confirm.ToString(), ok, ms = sw.ElapsedMilliseconds, bx, by, sx, sy, t = BotLog.Now });
    }

    /// <summary>
    /// 「拖动」积木（2026-11-23）：每个热区一个 DistanceDragger（记住比例、门槛和上一块还差的距离）。
    /// 一笔的光标路径在基准坐标里算好，再换成屏幕物理像素交给输入后端；暂停在两笔之间等，拖到一半暂停时后端先松开。
    /// </summary>
    private async Task Drag(DragBlock db, CancellationToken ct)
    {
        var roi = Region(db.Region);
        int dist = db.DistancePx;
        Publish($"拖动 {dist} px");
        if (!_draggers.TryGetValue(db.Region, out var dragger))
            _draggers[db.Region] = dragger = new DistanceDragger { Log = o => _c.Log.Write(o) };
        dragger.Progress = (moved, strokes) => SetBox("drag:" + db.Region, ScrollArrow(roi, dist, moved, strokes, null, "拖动"));
        int speed = db.SpeedPxPerSec ?? DragBlock.DefaultSpeed, hold = db.HoldBeforeUpMs ?? DragBlock.DefaultHoldMs;
        var h = _c.Input.Humanizer;
        var io = new DistanceDragger.Io(
            Drag: async (legs, holdMs, c) =>
            {
                var (bx, _) = Inside(roi);
                var (x0, y0, pts) = DistanceDragger.PathFor(legs, roi.X, roi.Y, roi.H, bx, speed, h);
                if (!ToScreen((int)Math.Round(x0), (int)Math.Round(y0), out int sx, out int sy)) return SendInputBackend.DragOutcome.Blocked;
                var path = new List<(int, int, int)>(pts.Count);
                foreach (var (x, y, d) in pts)
                {
                    if (!ToScreenD(x, y, out int px, out int py)) return SendInputBackend.DragOutcome.Blocked;
                    path.Add((px, py, d));
                }
                int pressHold = h?.Hold(80) ?? 80, upHold = h != null ? h.Hold(Math.Max(10, holdMs)) : holdMs;
                return await _c.Input.DragAsync(sx, sy, path, pressHold, upHold, () => _c.IsPaused?.Invoke() == true, c);
            },
            Grab: async c =>
            {
                await WaitNextFrame(c, 100);
                var f = await CurrentFrame(c);
                return (new Mat(f.Gray, f.ToPixels(roi)).Clone(), f.K);
            },
            Delay: (ms, c) => Task.Delay(ms, c),
            NowMs: () => Environment.TickCount64,
            WaitWhilePaused: WaitIfPaused);
        var res = await dragger.RunAsync(dist, db.TolerancePx ?? DragBlock.DefaultTolerancePx, roi.H, io, ct, hold);
        if (res.Strokes > 0) Drags++;
        _c.Log.Write(new
        {
            ev = "dragPx", want = dist, got = Math.Round(res.Moved, 1), strokes = res.Strokes,
            gain = res.Gain is double g ? Math.Round(g, 3) : (double?)null, threshold = res.Threshold is double t ? Math.Round(t, 1) : (double?)null,
            end = res.End, reason = res.Reason, fails = res.MeasureFails, carryIn = Math.Round(res.CarryIn, 1), carryOut = Math.Round(res.CarryOut, 1), hold = res.HoldMs, t = BotLog.Now
        });
        string shiftNote = NoteShortfall(db.Region, roi, dist + res.CarryIn, res.Moved, res.Reason);
        // 只认「到底而且一点没动」：走了一部分才到底的，下一遍还要读刚露出来的行；量不出、被拦、动了鼠标都不算到底
        if (EndedWithoutMoving(res.Reason, res.Moved)) _atEndNoMove = true;
        Publish(res.Reason switch
        {
            "end" => $"拖动：到底了，拖了 {res.Moved:0} / {dist} px",
            "cantMeasure" => "拖动：量不出画面移动，停下",
            "userMouse" => "拖动：鼠标被移动了，已松开并停下这一块",
            "blocked" => "拖动：输入被拦，已松开",
            _ => $"拖动 {res.Moved:0} / {dist} px"
        } + (res.HoldMs > hold ? $"（松手后列表还在滑，松手前停顿已加长到 {res.HoldMs} ms）" : "") + shiftNote);
    }

    /// <summary>「拖到底就停」认的到底：积木自己判断到底了，而且这一块一点没动（不到 0.5 px）。量不出、被拦、动了鼠标都不算。</summary>
    public static bool EndedWithoutMoving(string reason, double moved) => reason == "end" && Math.Abs(moved) < 0.5;

    /// <summary>
    /// 拖动 / 按距离滚停下以后，把少走的距离记到这个热区里的网格上（2026-11-25）。返回一句附在运行信息后面的话。
    /// 停在容差里的（done、只认整格、滚过头）零头已经带给下一块，不算；量不出的那一块走了多少不可信，不挪，只提醒。
    /// 到底、被拦、动了鼠标、笔数用完：内容停在哪就是哪，少走的那段让网格跟上。
    /// </summary>
    private string NoteShortfall(string regionName, RegionDef roi, double target, double moved, string reason)
    {
        if (reason is "done" or "nearestNotch" or "overshoot") return "";
        // 内容一点没动（到底以后再拖、被拦在第一笔）：格子和行的相对位置没变，不挪。不然拖动距离不是整行时会把网格挪错
        if (Math.Abs(moved) < 0.5) return "";
        if (GridDef.InRegion(_c.Program, roi) is not { } found) return "";
        var (gname, g) = found;
        if (reason == "cantMeasure")
        {
            _c.Log.Write(new { ev = "gridShiftSkipped", grid = gname, region = regionName, reason, t = BotLog.Now });
            return $"；量不出走了多少，网格「{Label(g.Label, gname)}」没挪，下一遍格子可能对不齐";
        }
        double sf = target - moved;
        if (Math.Abs(sf) < 0.5) return "";
        double total = _gridShift.GetValueOrDefault(gname).Shortfall + sf;
        _gridShift[gname] = (total, regionName);
        double phase = g.Phase(total);
        _c.Log.Write(new { ev = "gridShift", grid = gname, region = regionName, reason, shortfall = Math.Round(sf, 1), total = Math.Round(total, 1), pitch = g.Pitch, phase = Math.Round(phase, 1), t = BotLog.Now });
        return phase < 0.5 || phase > g.Pitch - 0.5
            ? $"；少走 {sf:0} px 正好是整行，网格「{Label(g.Label, gname)}」不用挪"
            : $"；少走 {sf:0} px，网格「{Label(g.Label, gname)}」下移 {phase:0} px 对齐";
    }

    /// <summary>「对每一格」这一次要走的格子：没少走过就是原来的格子；少走过就按取余下移，只留完整露在列表里的。</summary>
    private List<RegionDef> CellsOf(string gname, GridDef g, out double phase)
    {
        phase = 0;
        if (_gridShift.TryGetValue(gname, out var s))
        {
            double ph = g.Phase(s.Shortfall);
            if (ph >= 0.5 && ph <= g.Pitch - 0.5)
            {
                phase = ph;
                // 可见范围：网格原来的范围和拖动 / 滚轮热区合起来的外框
                var gb = g.Bounds;
                var rb = s.Region == VbProgram.CurrentCell || !_c.Program.Regions.TryGetValue(s.Region, out var r) ? gb : r;
                int top = Math.Min(gb.Y, rb.Y), bottom = Math.Max(gb.Y + gb.H, rb.Y + rb.H);
                var cells = g.ShiftedCells(ph, new RegionDef { X = gb.X, Y = top, W = gb.W, H = bottom - top });
                _c.Log.Write(new { ev = "gridCells", grid = gname, phase = Math.Round(ph, 1), cells = cells.Count, t = BotLog.Now });
                return cells;
            }
        }
        return Enumerable.Range(0, g.Cols * g.Rows).Select(g.Cell).ToList();
    }

    /// <summary>基准坐标（可带小数）→ 屏幕物理像素。</summary>
    private bool ToScreenD(double bx, double by, out int sx, out int sy)
    {
        sx = sy = 0;
        if (!WindowInfo.TryGetClientOnScreen(_c.TargetHwnd, out var client)) return false;
        double s = client.W / (double)_c.Program.Base.W;
        sx = client.X + (int)Math.Round(bx * s);
        sy = client.Y + (int)Math.Round(by * s);
        return true;
    }

    /// <summary>遮罩上的滚动箭头：从热区竖直中线、靠右处出发，按实际滚过的距离画（最多画到热区边上），标「目标 · 已滚 · 步 · 每格」。</summary>
    private static OverlayBox ScrollArrow(RegionDef roi, int dist, double moved, int steps, double? perNotch, string? verb = null)
    {
        int x = roi.X + roi.W - Math.Min(60, roi.W / 4), cy = roi.Y + roi.H / 2;
        int len = (int)Math.Round(Math.Min(Math.Abs(moved), roi.H / 2.0 - 10));
        var box = moved >= 0
            ? new RegionDef { X = x, Y = cy - len, W = 0, H = len }   // 向下滚：内容往上走，箭头朝上
            : new RegionDef { X = x, Y = cy, W = 0, H = len };
        string label = verb == "拖动"
            ? $"拖动 目标 {dist} · 已拖 {moved:0} px · 第 {steps} 笔"
            : $"目标 {dist} · 已滚 {moved:0} px · {steps} 步" + (perNotch is double pn ? $" · 每格 {pn:0}" : "");
        return new OverlayBox(box, label, moved >= 0 ? "scrollUp" : "scrollDown");
    }

    private void SetBox(string key, OverlayBox box)
    {
        _boxes[key] = box;
        _c.Overlay?.SetScene(new OverlayScene(_boxes.Values.ToList(), _status));
    }

    private void Publish(string status)
    {
        _status = status;
        _c.Overlay?.SetScene(new OverlayScene(_boxes.Values.ToList(), _status));
    }

    /// <summary>等用户确认花掉的时间（毫秒）。「重复直到…」的超时不算这段，不然确认完一回来循环就超时跳出了。</summary>
    private long _heldMs;

    /// <summary>
    /// 认图案分不开（2026-11-25）：问用户（编辑器弹确认框，运行停在这里，按键都松开，靶子停在这一页）。
    /// 没人问（vbrun、设置成「直接记录」）、等太久没人答、用户点了「先记下」，就写 <see cref="UnsureValue"/> 接着跑：不和认出来的值撞，回头在表里找得到。
    /// 用户填了新名字、勾了存成模板：编辑器已经把模板加进程序和这块认图案，这里载进来，以后同样的角标自动认。
    /// 同一次运行里又遇到同一张图（「重复直到没有新记录」会回头再看一遍同一件物品），直接用上次的结果、不再问：
    /// 两次结果不一样（比如第一次选了 1天、第二次超时写了 1天?），同一件物品会被记成两行。
    /// </summary>
    private async Task<string> Unsure(ClassifyBlock cl, NormFrame f, RegionDef roi, PatternClassifier.Result res, CancellationToken ct)
    {
        Unsures++;
        string fallback = UnsureValue(res);
        var rpx = f.ToPixels(roi);
        var top = res.All.Where(c => c.Box != null).OrderByDescending(c => c.Score).FirstOrDefault();
        var keyPx = top?.Box is { } kb ? f.ToPixels(kb) : rpx;   // 认的是最像的那一块（没有就用整个热区）
        var key = new Mat(f.Gray, keyPx).Clone();
        var seen = _unsureSeen.FirstOrDefault(x => x.Field == cl.Field && SamePicture(x.Gray, key));
        if (seen.Gray != null)
        {
            key.Dispose();
            UnsureRemembered++;
            _c.Log.Write(new { ev = "unsureAnswer", field = cl.Field, how = "remembered", value = seen.Value, t = BotLog.Now });
            Publish($"{cl.Field}：{seen.Value}（和前面问过的同一张图，用上次的结果）");
            return seen.Value;
        }
        string Remember(string v) { _unsureSeen.Add((cl.Field, key, v)); return v; }
        if (_c.AskUnsure == null)
        {
            key.Dispose();   // 不问时结果每次都一样，不用记
            _c.Log.Write(new { ev = "unsureAnswer", field = cl.Field, how = "auto", value = fallback, t = BotLog.Now });
            return fallback;
        }
        _c.Input.ReleaseAll();
        byte[] regionPng;
        using (var rc = new Mat(f.Bgr, rpx)) Cv2.ImEncode(".png", rc, out regionPng);
        byte[]? badgePng = null;
        (int, int, int, int)? inRegion = null;
        if (top?.Box is { } b)
        {
            var bpx = f.ToPixels(b);
            using var sub = new Mat(f.Bgr, bpx);
            // 和裁模板一样：窗口比基准小时放大到基准尺寸再存
            using var save = f.K < 0.999 ? sub.Resize(new Size(b.W, b.H), 0, 0, InterpolationFlags.Cubic) : sub.Clone();
            Cv2.ImEncode(".png", save, out byte[] png);
            badgePng = png;
            inRegion = (bpx.X - rpx.X, bpx.Y - rpx.Y, bpx.Width, bpx.Height);
        }
        var cands = res.All.OrderByDescending(c => c.Score).Select(c => (c.Template, c.Value, c.Score)).ToList();
        var req = new UnsureRequest(cl, cands, res.Close, fallback, _rec?.Current ?? new Dictionary<string, string>(), regionPng, badgePng, inRegion);
        _c.Log.Write(new { ev = "unsureAsk", field = cl.Field, close = res.Close, guess = fallback, best = Math.Round(res.Best, 3), second = Math.Round(res.Second, 3), t = BotLog.Now });
        Publish($"「{cl.Field}」分不太开：在视搭里确认后继续（运行已暂停，按键都已松开）");
        var sw = Stopwatch.StartNew();
        UnsureAnswer? ans;
        try { ans = await _c.AskUnsure(req, ct); }
        catch { key.Dispose(); throw; }
        finally { _heldMs += sw.ElapsedMilliseconds; }
        long ms = sw.ElapsedMilliseconds;
        if (ans is { Stop: true })
        {
            key.Dispose();
            _c.Log.Write(new { ev = "unsureAnswer", field = cl.Field, how = "stop", ms, t = BotLog.Now });
            throw new UserStopSignal();
        }
        if (ans == null || ans.Skipped || string.IsNullOrWhiteSpace(ans.Value))
        {
            bool skipped = ans != null;
            if (skipped) UnsureSkipped++;
            _c.Log.Write(new { ev = "unsureAnswer", field = cl.Field, how = skipped ? "skip" : "timeout", value = fallback, ms, t = BotLog.Now });
            Publish($"{cl.Field}：{fallback}（{(skipped ? "你选了先这样记" : "没人确认，先这样记")}，接着跑）");
            return Remember(fallback);
        }
        UnsureAnswered++;
        string v = ans.Value.Trim();
        if (ans.NewTemplate is { } nt && _c.Program.Templates.TryGetValue(nt, out var def))
        {
            if (_templates.Remove(nt, out var old)) old.Dispose();
            _templates[nt] = TemplateMatcher.Load(Path.Combine(_c.ProgramDir, def.File));
            if (!cl.Templates.Contains(nt)) cl.Templates.Add(nt);
        }
        string how = ans.NewTemplate != null ? "saved" : v == cl.NoneText || cands.Any(c => c.Value == v) ? "picked" : "typed";
        _c.Log.Write(new { ev = "unsureAnswer", field = cl.Field, how, value = v, template = ans.NewTemplate, ms, t = BotLog.Now });
        Publish($"{cl.Field}：{v}（你确认的）");
        return Remember(v);
    }

    /// <summary>这次运行里问过的图（灰度，最像的那一块）和结果，按写进的字段分开（同一个字段、同一张图 = 同一个值）。</summary>
    private readonly List<(string Field, Mat Gray, string Value)> _unsureSeen = new();

    /// <summary>同一张图：尺寸一样，灰度差超过 40 的像素不到 1%。同一件物品再看一次几乎逐像素一样；只差一个数字的两种角标差得远。</summary>
    public static bool SamePicture(Mat a, Mat b)
    {
        if (a.Size() != b.Size()) return false;
        using var d = new Mat();
        Cv2.Absdiff(a, b, d);
        using var m = new Mat();
        Cv2.Threshold(d, m, 40, 255, ThresholdTypes.Binary);
        return Cv2.CountNonZero(m) <= 0.01 * a.Rows * a.Cols;
    }

    public void Dispose()
    {
        foreach (var t in _templates.Values) t.Dispose();
        foreach (var x in _unsureSeen) x.Gray.Dispose();
        _frame?.Dispose();
    }
}
