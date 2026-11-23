using OpenCvSharp;
using VisionBlocks.Core.Input;
using VisionBlocks.Core.Model;
using VisionBlocks.Core.Runtime;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-dragpx（2026-11-23）：「拖动」积木不发真输入、不开窗口的自测。
/// A. 闭环：假列表 + 假时钟，模拟跟手（过门槛后从门槛那一点算起）、过了门槛整段都算、拖滚动条（比例 2）、松手后惯性、到底、画面太平、
///    很短的距离（先反向再拖回来的两段）、拖到一半暂停、拖到一半用户动了鼠标、门槛估差几 px 时最后一小笔少走（不能当成到底）；
///    都和假列表自己的真实位置对答案。
/// B. 余差：同一个热区连拖几块，上一块还差的带进下一块，累计不越差越多；到底以后清零。
/// C. 输入后端：按住拖动在每一种退出路径（正常、被拦、急停、用户动鼠标、暂停、取消、用户按住修饰键）都恰好松开一次左键，而且松开是最后一个事件。
/// D. 积木：校验、摘要、JSON 往返；滚轮按距离的余差带入。
/// </summary>
public static class DragPxSelfTest
{
    private static int _fails;

    private static void Check(string name, bool pass, string detail)
    {
        if (!pass) _fails++;
        Console.WriteLine($"  [{(pass ? "通过" : "失败")}] {name}：{detail}");
    }

    public static int Run()
    {
        _fails = 0;
        Console.WriteLine("A. 闭环（假列表 + 假时钟）");
        Loop();
        Console.WriteLine("B. 余差带到下一块");
        Carry();
        Console.WriteLine("C. 输入后端：每条退出路径都松开左键");
        Backend();
        Console.WriteLine("D. 积木和滚轮余差");
        Blocks();
        WheelCarry();
        Console.WriteLine(_fails == 0 ? "全部通过" : $"{_fails} 项失败");
        return _fails == 0 ? 0 : 1;
    }

    // ── 假列表：按住移过门槛才开始拖；跟手模式从过门槛那一点算起（门槛这一段吃掉），跳跃模式过门槛后整段都算 ──
    private sealed class FakeList : IDisposable
    {
        public double Offset, MaxOffset = 4000, Threshold = 10, Ratio = 1, Inertia;
        public bool Jump;
        public long T = 1000;
        public int Strokes;
        public Func<int, int>? CutAfterSteps;              // 第几笔只走到一半就返回（模拟暂停 / 用户动鼠标）
        public Func<int, double>? Wobble;                  // 第几笔比按门槛和比例算的多走几 px（正 = 内容往上）：真靶子上门槛、取整有几 px 误差
        public SendInputBackend.DragOutcome CutOutcome = SendInputBackend.DragOutcome.Paused;
        private readonly Mat _content;
        private const int H = 870;

        public FakeList(Mat content, double start) { _content = content; Offset = start; }

        public Mat Render() => new Mat(_content, new Rect(0, (int)Math.Round(Offset), _content.Width, H)).Clone();

        private SendInputBackend.DragOutcome Drag(double[] legs)
        {
            Strokes++;
            double pos = 0, anchor = 0, start = Offset;
            bool dragging = false;
            int cut = CutAfterSteps?.Invoke(Strokes) ?? int.MaxValue, step = 0;
            double before = Offset;
            foreach (var leg in legs)
            {
                int n = (int)Math.Ceiling(Math.Abs(leg));
                for (int i = 0; i < n; i++)
                {
                    if (step++ >= cut) { T += 200; return CutOutcome; }   // 后端已经松开（这里没有惯性）
                    pos += leg / n;
                    if (!dragging && Math.Abs(pos) >= Threshold) { dragging = true; anchor = Jump ? 0 : pos; }
                    if (dragging) Offset = Math.Clamp(start + Ratio * (anchor - pos), 0, MaxOffset);   // 内容跟着光标：光标往上（负）内容往上（正）
                }
                T += (long)(Math.Abs(leg) / 0.8);   // 800 px/秒
            }
            // 惯性：松手后顺着最后一段再滑这一笔净位移的 Inertia 倍
            if (Inertia > 0) Offset = Math.Clamp(Offset + Inertia * (Offset - before), 0, MaxOffset);
            if (dragging && Wobble != null) Offset = Math.Clamp(Offset + Wobble(Strokes), 0, MaxOffset);
            T += 230;
            return SendInputBackend.DragOutcome.Ok;
        }

        public DistanceDragger.Io Io() => new(
            Drag: (legs, ct) => Task.FromResult(Drag(legs)),
            Grab: ct => { T += 16; return Task.FromResult((Render(), 1.0)); },
            Delay: (ms, ct) => { T += ms; return Task.CompletedTask; },
            NowMs: () => T,
            WaitWhilePaused: ct => Task.CompletedTask);

        public void Dispose() { }
    }

    private sealed record Case(string Name, int Goal, double Start = 0, double Threshold = 10, double Ratio = 1, bool Jump = false, double Inertia = 0,
        double MaxOffset = 4000, bool Flat = false, double? WantMoved = null, string WantReason = "done", Func<int, int>? Cut = null,
        SendInputBackend.DragOutcome CutOutcome = SendInputBackend.DragOutcome.Paused, Func<int, double>? Wobble = null);

    private static void Loop()
    {
        using var content = WheelPxSelfTest.Content(5200);
        using var flat = WheelPxSelfTest.Content(5200, flat: true);
        var cases = new[]
        {
            new Case("跟手 门槛10 拖240", 240),
            new Case("跟手 门槛10 拖720（分几笔）", 720),
            new Case("跟手 门槛10 向上拖240", -240, Start: 600),
            new Case("没有门槛 拖720", 720, Threshold: 0),
            new Case("门槛20 拖720", 720, Threshold: 20),
            new Case("过了门槛整段都算 拖240", 240, Jump: true),
            new Case("拖滚动条 比例2 拖720", 720, Ratio: 2),
            new Case("拖滚动条 比例3 门槛0 拖1500", 1500, Ratio: 3, Threshold: 0),
            new Case("松手后惯性 8% 拖720", 720, Inertia: 0.08),
            new Case("到底：最多还能走 250，要拖 720", 720, MaxOffset: 250, WantMoved: 250, WantReason: "end"),
            new Case("第一笔就到底：只剩 40", 720, Start: 760, MaxOffset: 800, WantMoved: 40, WantReason: "end"),
            new Case("画面一片平：一下都不拖", 240, Flat: true, WantReason: "cantMeasure"),
            new Case("很短的距离 5（门槛10，先反向再拖回来）", 5),
            new Case("很短的距离 -8（向上）", -8, Start: 600),
            new Case("拖到一半暂停，继续后接着拖", 720, Cut: s => s == 2 ? 120 : int.MaxValue),
            new Case("拖到一半用户动了鼠标：停下这一块", 720, Cut: s => s == 2 ? 120 : int.MaxValue, CutOutcome: SendInputBackend.DragOutcome.UserMouse, WantReason: "userMouse"),
            new Case("第一笔被前台守卫拦下", 720, Cut: s => s == 1 ? 0 : int.MaxValue, CutOutcome: SendInputBackend.DragOutcome.Blocked, WantReason: "blocked"),
            // 真靶子上门槛和取整有几 px 误差（2026-11-23 真靶子 r1 第 5 块）：最后一小笔只走了一部分、甚至没动，不能就当成到底
            new Case("门槛估差几 px：最后一小笔只走了不到一半，但已在容差里 → 算到了", 100, Wobble: s => s == 2 ? -4 : s == 3 ? -0.5 : 0),
            new Case("门槛估差几 px：最后一小笔少走几 px、还没到 → 修正门槛接着拖", 100, Wobble: s => s == 2 ? -8 : s == 3 ? -1 : 0),
            new Case("到底只差几 px：两段的笔一下没动 → 到底", 100, MaxOffset: 94, WantMoved: 94, WantReason: "end"),
        };
        foreach (var c in cases)
        {
            using var list = new FakeList(c.Flat ? flat : content, c.Start) { Threshold = c.Threshold, Ratio = c.Ratio, Jump = c.Jump, Inertia = c.Inertia, MaxOffset = c.MaxOffset, CutAfterSteps = c.Cut, CutOutcome = c.CutOutcome, Wobble = c.Wobble };
            var dr = new DistanceDragger();
            var res = dr.RunAsync(c.Goal, 3, 870, list.Io(), CancellationToken.None).GetAwaiter().GetResult();
            double truth = list.Offset - c.Start;
            bool ok;
            if (c.WantReason == "cantMeasure") ok = res.Reason == "cantMeasure" && list.Strokes == 0;
            else if (c.WantReason is "userMouse" or "blocked") ok = res.Reason == c.WantReason && Math.Abs(res.Moved - truth) <= 2 && dr.Carry == 0;
            else
            {
                double want = c.WantMoved is double w ? w * Math.Sign(c.Goal) : c.Goal;
                ok = Math.Abs(truth - want) <= 3.5 && Math.Abs(res.Moved - truth) <= 2 && res.Reason == c.WantReason;
            }
            Check(c.Name, ok, $"真实 {truth:0.0}，自测 {res.Moved:0.0}，{res.Strokes} 笔，比例 {res.Gain?.ToString("0.00") ?? "-"}，门槛 {res.Threshold?.ToString("0.0") ?? "-"}，{res.Reason}");
        }

        // 同一个热区第二块：用上次量到的比例和门槛，第一笔就能拖长
        using (var list = new FakeList(content, 0) { Ratio = 2 })
        {
            var dr = new DistanceDragger();
            dr.RunAsync(720, 3, 870, list.Io(), CancellationToken.None).GetAwaiter().GetResult();
            int s0 = list.Strokes; double before = list.Offset;
            var r2 = dr.RunAsync(720, 3, 870, list.Io(), CancellationToken.None).GetAwaiter().GetResult();
            Check("第二块接着用比例 2", Math.Abs(list.Offset - before - 720 - r2.CarryIn) <= 3.5 && list.Strokes - s0 <= 3 && r2.Gain is > 1.8 and < 2.2,
                $"真实 {list.Offset - before:0.0}，{list.Strokes - s0} 笔，比例 {r2.Gain:0.00}");
        }
    }

    private static void Carry()
    {
        using var content = WheelPxSelfTest.Content(5200);
        using var list = new FakeList(content, 0) { Threshold = 10 };
        var dr = new DistanceDragger();
        double sum = 0, carry = 0;
        bool chain = true;
        var notes = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var r = dr.RunAsync(240, 3, 870, list.Io(), CancellationToken.None).GetAwaiter().GetResult();
            chain &= Math.Abs(r.CarryIn - carry) < 1e-9;
            carry = r.CarryOut;
            sum += 240;
            notes.Add($"{r.Moved:0.0}（带入 {r.CarryIn:+0.0;-0.0;0}）");
        }
        Check("连拖 5 块 240：累计误差不超过容差", Math.Abs(list.Offset - sum) <= 3.5 && chain, $"累计 {list.Offset:0.0} / {sum}；" + string.Join("、", notes));

        using var shortList = new FakeList(content, 0) { MaxOffset = 500 };
        var d2 = new DistanceDragger();
        d2.RunAsync(240, 3, 870, shortList.Io(), CancellationToken.None).GetAwaiter().GetResult();
        var rEnd = d2.RunAsync(720, 3, 870, shortList.Io(), CancellationToken.None).GetAwaiter().GetResult();
        Check("到底以后余差清零", rEnd.Reason == "end" && d2.Carry == 0 && shortList.Offset == 500, $"{rEnd.Reason}，余差 {d2.Carry}，位置 {shortList.Offset:0}");
    }

    // ── C：输入后端 ──
    private static void Backend()
    {
        var client = new VisionBlocks.Core.Win32.WindowInfo.PxRect(3200, 200, 1920, 1080);
        (List<string> Ev, SendInputBackend.DragOutcome? Out, bool Threw) Run(Func<int, bool>? allow = null, Func<int, bool>? abortAt = null, Func<int, (int, int)>? userMove = null,
            Func<int, bool>? pauseAt = null, int cancelAt = -1, Func<int, bool>? ctrlAt = null)
        {
            var ev = new List<string>();
            int steps = 0;
            (int X, int Y) cursor = (0, 0);
            var cts = new CancellationTokenSource();
            SendInputBackend? input = null;
            input = new SendInputBackend(new InputGuard(IntPtr.Zero) { TestAllow = () => allow?.Invoke(steps) ?? true }, null)
            {
                TestSender = d =>
                {
                    ev.Add(d);
                    if (d == "md") cursor = (4000, 900);
                    if (d.StartsWith("dm ", StringComparison.Ordinal))
                    {
                        steps++;
                        var xy = d[3..].Split(',');
                        cursor = (int.Parse(xy[0]), int.Parse(xy[1]));
                        if (abortAt?.Invoke(steps) == true) input!.Abort("测试");
                        if (steps == cancelAt) cts.Cancel();
                    }
                    return 1;
                },
                IsKeyPhysicallyDown = vk => vk == 0xA2 && ctrlAt?.Invoke(steps) == true, CloseImeBeforeKeys = false, ModifierWaitMs = 0,
                CursorPosition = () => userMove?.Invoke(steps) is { } off ? (cursor.X + off.Item1, cursor.Y + off.Item2) : cursor,
                ClientRect = _ => client,
            };
            var path = Enumerable.Range(1, 12).Select(i => (4000, 900 - i * 20, 1)).ToList();
            try
            {
                var o = input.DragAsync(4000, 900, path, 5, 5, () => pauseAt?.Invoke(steps) == true, cts.Token).GetAwaiter().GetResult();
                return (ev, o, false);
            }
            catch (OperationCanceledException) { return (ev, null, true); }
        }
        void Expect(string name, (List<string> Ev, SendInputBackend.DragOutcome? Out, bool Threw) r, SendInputBackend.DragOutcome? want, bool threw = false)
        {
            int md = r.Ev.Count(e => e == "md"), mu = r.Ev.Count(e => e == "mu"), dm = r.Ev.Count(e => e.StartsWith("dm ", StringComparison.Ordinal));
            bool ok = md == 1 && mu == 1 && r.Ev[^1] == "mu" && r.Out == want && r.Threw == threw;
            Check(name, ok, $"{r.Out?.ToString() ?? "抛出取消"}；按下 {md}、移动 {dm}、松开 {mu}，最后一个事件 {r.Ev[^1]}");
        }
        Expect("正常拖完", Run(), SendInputBackend.DragOutcome.Ok);
        Expect("第 4 步被前台守卫拦下", Run(allow: s => s < 4), SendInputBackend.DragOutcome.Blocked);
        Expect("第 5 步急停（Abort 在锁里抬起左键）", Run(abortAt: s => s == 5), SendInputBackend.DragOutcome.Aborted);
        Expect("第 6 步以后用户把鼠标挪开 10 像素", Run(userMove: s => s >= 6 ? (10, 0) : (0, 0)), SendInputBackend.DragOutcome.UserMouse);
        Expect("第 3 步以后要暂停", Run(pauseAt: s => s >= 3), SendInputBackend.DragOutcome.Paused);
        Expect("第 7 步运行被取消", Run(cancelAt: 7), null, threw: true);
        Expect("第 2 步以后用户按住 Ctrl", Run(ctrlAt: s => s >= 2), SendInputBackend.DragOutcome.Blocked);
        // 急停在按下之前：一个事件都不发
        var sent = new List<string>();
        var pre = new SendInputBackend(new InputGuard(IntPtr.Zero) { TestAllow = () => true }, null) { TestSender = d => { sent.Add(d); return 1; }, IsKeyPhysicallyDown = _ => false, CloseImeBeforeKeys = false, CursorPosition = () => (0, 0), ClientRect = _ => client };
        pre.Abort("测试");
        var o2 = pre.DragAsync(4000, 900, new List<(int, int, int)> { (4000, 800, 1) }, 5, 5, null, CancellationToken.None).GetAwaiter().GetResult();
        Check("急停之后不按下", o2 == SendInputBackend.DragOutcome.Aborted && !sent.Contains("md"), $"{o2}，发出 {sent.Count} 个事件");
    }

    private static void Blocks()
    {
        var p = new VbProgram { Name = "dragpx-selftest" };
        p.Regions["物品区"] = new RegionDef { X = 420, Y = 150, W = 1440, H = 870 };
        var good = new DragBlock { Region = "物品区", DistancePx = 720 };
        p.Program.Add(good);
        Check("校验通过", ProgramIO.Validate(p).Count == 0, string.Join("；", ProgramIO.Validate(p)));
        Check("摘要文字", BlockText.Summary(good, p) == "在「物品区」按住拖动，向下翻 720 px（边拖边量）", BlockText.Summary(good, p));
        Check("算动作类", BlockText.CategoryOf(good) == BlockText.Category.Action, BlockText.CategoryOf(good).ToString());
        p.Program.Clear();
        p.Program.Add(new DragBlock { Region = "没有这个", DistancePx = 240 });
        p.Program.Add(new DragBlock { Region = "物品区", DistancePx = 0 });
        p.Program.Add(new DragBlock { Region = "物品区", DistancePx = 240, TolerancePx = 0, SpeedPxPerSec = 10, HoldBeforeUpMs = 5000 });
        p.Program.Add(new DragBlock { Region = VbProgram.CurrentCell, DistancePx = 240 });
        var errs = ProgramIO.Validate(p);
        Check("拦下：热区不存在 / 距离 0 / 容差、速度、停顿越界 / 当前格在循环外", errs.Count == 6, string.Join("；", errs));
        p.Program.Clear();
        p.Program.Add(new DragBlock { Region = "物品区", DistancePx = -480, SpeedPxPerSec = 400 });
        p.Program.Add(new DragBlock { Region = "物品区", DistancePx = 240 });
        string json = System.Text.Json.JsonSerializer.Serialize(p, ProgramIO.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<VbProgram>(json, ProgramIO.Options)!;
        var d0 = (DragBlock)back.Program[0];
        var d1 = (DragBlock)back.Program[1];
        Check("JSON 往返", d0.DistancePx == -480 && d0.SpeedPxPerSec == 400 && d1.SpeedPxPerSec == null && json.Contains("\"type\": \"drag\""), $"距离 {d0.DistancePx}，速度 {d0.SpeedPxPerSec}，默认的不写：{!json[json.LastIndexOf("\"type\": \"drag\"", StringComparison.Ordinal)..].Contains("speedPxPerSec")}");
        var ed = ProgramEditor.CreateNew(Path.Combine(Path.GetTempPath(), $"vb-dragpx-{Environment.ProcessId}"), "x");
        ed.AddRegion("物品区", new RegionDef { X = 1, Y = 1, W = 100, H = 100 });
        var nb = ed.CreateDefault("drag");
        ed.Insert(nb, null);
        Check("新建的拖动积木默认用第一个热区、240 px，删热区时被拦", nb is DragBlock { Region: "物品区", DistancePx: 240 } && ed.UsesOf("region", "物品区").Count == 1, BlockText.Summary(nb, ed.Program));
        try { Directory.Delete(ed.Dir, true); } catch { }
    }

    /// <summary>滚轮按距离（D3）：只认整格的程序（一格 100），连滚 3 块 240，余差带进下一块，累计离 720 不超过半格；不带的话会是 600。</summary>
    private static void WheelCarry()
    {
        using var content = WheelPxSelfTest.Content(5200);
        double offset = 0;
        long t = 1000;
        int acc = 0;
        var io = new DistanceScroller.Io(
            Send: (delta, count, ct) =>
            {
                for (int i = 0; i < count; i++) { acc += delta; while (Math.Abs(acc) >= 120) { offset = Math.Clamp(offset + Math.Sign(acc) * 100, 0, 4000); acc -= Math.Sign(acc) * 120; } }
                return Task.FromResult(count);
            },
            Grab: ct => { t += 16; return Task.FromResult((new Mat(content, new Rect(0, (int)offset, 1440, 870)).Clone(), 1.0)); },
            Delay: (ms, ct) => { t += ms; return Task.CompletedTask; },
            NowMs: () => t);
        var sc = new DistanceScroller();
        var notes = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var r = sc.RunAsync(240, 3, io, CancellationToken.None).GetAwaiter().GetResult();
            notes.Add($"{r.Moved:0}（带入 {r.CarryIn:+0;-0;0}，留下 {r.CarryOut:+0;-0;0}，{r.Reason}）");
        }
        Check("滚轮只认整格：连滚 3 块 240 累计离 720 不超过半格", Math.Abs(offset - 720) <= 50, $"累计 {offset:0}；" + string.Join("、", notes));
    }
}
