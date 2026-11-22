using System.Diagnostics;
using VisionBlocks.Core.Input;

namespace VisionBlocks.Runner;

/// <summary>
/// vbrun selftest-input：用假后端（不调用 SendInput、不读真实键盘）验证急停闸和修饰键检查。
/// A. 竞态：一个任务不停按 F，另一个线程在随机时刻急停，重复 300 次。
///    旧做法「ReleaseAll + 取消」和新做法「Abort」对比：急停返回之后，还有没有新的按下被发出去。
/// B. 急停时有键按住：Abort 必须把它抬起来。
/// C. 用户按住 Ctrl：发送暂缓；在等待窗口内松开就照常发，一直按住就放弃。
/// </summary>
internal static class InputSelfTest
{
    private sealed class Recorder
    {
        private readonly object _l = new();
        public readonly List<string> Events = new();
        public uint Send(string desc) { lock (_l) Events.Add(desc); return 1; }
        public int Count { get { lock (_l) return Events.Count; } }
        public List<string> Snapshot() { lock (_l) return Events.ToList(); }
    }

    private static SendInputBackend Make(Recorder r, Func<int, bool>? keyDown = null) =>
        new(new InputGuard(IntPtr.Zero) { TestAllow = () => true }, null)
        {
            TestSender = r.Send,
            IsKeyPhysicallyDown = keyDown ?? (_ => false),
            CloseImeBeforeKeys = false,
        };

    /// <summary>返回「急停返回之后又发出的按下」次数，以及结束时未配对的按下数。</summary>
    private static (int LeakedDowns, int Unpaired) Trial(bool useAbort, Random rng)
    {
        var rec = new Recorder();
        var input = Make(rec);
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await input.PressAsync("F", rng.Next(5, 15), cts.Token);
                    if (useAbort && input.Aborted) break;
                }
            }
            catch (OperationCanceledException) { }
        });
        Thread.Sleep(rng.Next(0, 25));
        if (useAbort) input.Abort("测试");
        else input.ReleaseAll();               // 旧做法：先抬键……
        int mark = rec.Count;                  // 急停返回的时刻
        cts.Cancel();                          // ……再取消
        loop.Wait(2000);
        var ev = rec.Snapshot();
        int leaked = ev.Skip(mark).Count(e => e.StartsWith("kd"));
        int downs = ev.Count(e => e.StartsWith("kd")), ups = ev.Count(e => e.StartsWith("ku"));
        return (leaked, downs - ups);
    }

    public static int Run()
    {
        var rng = new Random(20261119);
        int fails = 0;

        foreach (bool useAbort in new[] { false, true })
        {
            int leakedTrials = 0, leakedTotal = 0, unpairedTrials = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 300; i++)
            {
                var (leaked, unpaired) = Trial(useAbort, rng);
                if (leaked > 0) { leakedTrials++; leakedTotal += leaked; }
                if (unpaired != 0) unpairedTrials++;
            }
            string name = useAbort ? "新做法 Abort（先关闸再抬键，同一把锁）" : "旧做法 ReleaseAll + 取消";
            Console.WriteLine($"A. {name}：300 次中 {leakedTrials} 次在急停之后又发出了按下（共 {leakedTotal} 个），{unpairedTrials} 次结束时有未配对的按下（{sw.Elapsed.TotalSeconds:0.0}s）");
            if (useAbort && (leakedTrials > 0 || unpairedTrials > 0)) fails++;
        }

        {
            var rec = new Recorder();
            var input = Make(rec);
            input.KeyDown("F");
            int n = input.Abort("测试");
            bool again = input.KeyDown("F");
            var ev = rec.Snapshot();
            bool ok = n == 1 && ev.Count == 2 && ev[1].StartsWith("ku") && !again && input.RefusedAfterAbort == 1;
            Console.WriteLine($"B. 急停时按住 F：Abort 抬起 {n} 个，事件 [{string.Join(", ", ev)}]，急停后再按被拒 {input.RefusedAfterAbort} 次 → {(ok ? "通过" : "不符")}");
            if (!ok) fails++;
        }

        {
            bool ctrlHeld = true;
            var rec = new Recorder();
            var input = Make(rec, vk => ctrlHeld && vk == 0xA2);
            input.ModifierWaitMs = 100;
            bool sent1 = input.PressAsync("F", 10, CancellationToken.None).GetAwaiter().GetResult();
            int afterHeld = rec.Count;
            var releaser = Task.Run(async () => { await Task.Delay(40); ctrlHeld = false; });
            var sw = Stopwatch.StartNew();
            bool sent2 = input.PressAsync("F", 10, CancellationToken.None).GetAwaiter().GetResult();
            long waited = sw.ElapsedMilliseconds;
            bool ok = !sent1 && afterHeld == 0 && input.DeferredByModifier == 1 && sent2 && waited >= 35;
            Console.WriteLine($"C. 用户一直按住 LCtrl：发送={sent1}（暂缓 {input.DeferredByModifier} 次，事件 {afterHeld} 个）；40ms 后松开：发送={sent2}，等了 {waited}ms → {(ok ? "通过" : "不符")}");
            if (!ok) fails++;
        }

        Console.WriteLine(fails == 0 ? "全部通过" : $"{fails} 项不符");
        return fails == 0 ? 0 : 1;
    }
}
