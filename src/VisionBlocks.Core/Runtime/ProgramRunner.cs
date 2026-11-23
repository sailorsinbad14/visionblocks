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
    /// <summary>「记录一行」「截图存图」的输出目录（data.csv 和 images\）。为空时用不了这两块积木。</summary>
    public string? OutputDir { get; init; }
    /// <summary>程序文件路径，写进 manifest.json。</summary>
    public string? ProgramPath { get; init; }
}

public readonly record struct CondResult(bool Hit, string Detail);

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

    public int Evaluations, Keys, Clicks, ConfirmFails, WaitTimeouts, Wheels, Reads;
    public int RowsRecorded => _rec?.Rows ?? 0;
    public int Duplicates => _rec?.Duplicates ?? 0;
    public int EmptyKeys => _rec?.EmptyKeys ?? 0;
    /// <summary>这次运行的 manifest.json（程序里有「读文字」「截图存图」时才有）。</summary>
    public ResultManifest? Manifest { get; private set; }

    private sealed class StopSignal : Exception { }

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
        finally
        {
            int released = _c.Input.ReleaseAll();
            _c.Log.Write(new { ev = "runEnd", result, released, Evaluations, Keys, Clicks, ConfirmFails, WaitTimeouts, Wheels, Reads, rows = RowsRecorded, dups = Duplicates, emptyKeys = EmptyKeys, blocked = _c.Input.Guard.Blocked, t = BotLog.Now });
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
        _c.Input.ReleaseAll();
        _c.Log.Write(new { ev = "paused", t = BotLog.Now });
        Publish("已暂停（Ctrl+Alt+F11 继续）");
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
                long lastCheck = long.MinValue / 2;
                while (true)
                {
                    if (ru.CheckEveryMs <= 0 || sw.ElapsedMilliseconds - lastCheck >= ru.CheckEveryMs)
                    {
                        lastCheck = sw.ElapsedMilliseconds;
                        if ((await Eval(ru.Until, ct)).Hit) break;
                    }
                    if (ru.TimeoutMs > 0 && sw.ElapsedMilliseconds > ru.TimeoutMs)
                    {
                        _c.Log.Write(new { ev = "loopTimeout", ms = sw.ElapsedMilliseconds, t = BotLog.Now });
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
                var outer = _cell;
                try
                {
                    for (int i = 0; i < g.Cols * g.Rows; i++)
                    {
                        _cell = g.Cell(i);
                        SetBox("cell", new OverlayBox(_cell, $"{Label(g.Label, fe.Grid)} 第 {i + 1} 格", "region"));
                        await ExecList(fe.Body, ct);
                    }
                }
                finally { _cell = outer; }
                break;
            }
            case UntilNoNewRowsBlock un:
            {
                int pass = 0;
                while (true)
                {
                    pass++;
                    int before = RowsRecorded;
                    await ExecList(un.Body, ct);
                    int added = RowsRecorded - before;
                    _c.Log.Write(new { ev = "pass", pass, added, t = BotLog.Now });
                    if (added == 0) break;
                    if (pass >= un.MaxTimes) { _c.Log.Write(new { ev = "loopCap", pass, t = BotLog.Now }); break; }
                }
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
                    end = res.End, reason = res.Reason, fails = res.MeasureFails, events = res.EventsSent, t = BotLog.Now
                });
                Publish(res.Reason switch
                {
                    "end" => $"滚轮：到底了，滚了 {res.Moved:0} / {dist} px",
                    "nearestNotch" => $"滚轮 {res.Moved:0} / {dist} px（这个程序只认整格）",
                    "cantMeasure" => "滚轮：量不出画面移动，停下",
                    _ => $"滚轮 {res.Moved:0} / {dist} px"
                });
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
            case ReadTextBlock rt:
            {
                var f = await CurrentFrame(ct);
                var roi = Region(rt.Region);
                _ocr ??= new OcrReader();
                var (raw, norm, ms) = await _ocr.ReadAsync(f, roi, ct);
                string value = rt.NumbersOnly ? new string(norm.Where(char.IsAsciiDigit).ToArray()) : norm;
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

    /// <summary>遮罩上的滚动箭头：从热区竖直中线、靠右处出发，按实际滚过的距离画（最多画到热区边上），标「目标 · 已滚 · 步 · 每格」。</summary>
    private static OverlayBox ScrollArrow(RegionDef roi, int dist, double moved, int steps, double? perNotch)
    {
        int x = roi.X + roi.W - Math.Min(60, roi.W / 4), cy = roi.Y + roi.H / 2;
        int len = (int)Math.Round(Math.Min(Math.Abs(moved), roi.H / 2.0 - 10));
        var box = moved >= 0
            ? new RegionDef { X = x, Y = cy - len, W = 0, H = len }   // 向下滚：内容往上走，箭头朝上
            : new RegionDef { X = x, Y = cy, W = 0, H = len };
        string label = $"目标 {dist} · 已滚 {moved:0} px · {steps} 步" + (perNotch is double pn ? $" · 每格 {pn:0}" : "");
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

    public void Dispose()
    {
        foreach (var t in _templates.Values) t.Dispose();
        _frame?.Dispose();
    }
}
