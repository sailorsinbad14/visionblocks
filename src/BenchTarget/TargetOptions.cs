using System.Globalization;

namespace BenchTarget;

/// <summary>命令行参数。所有随机事件都由 Seed 决定，同一个种子可以复现同一轮。</summary>
public sealed class TargetOptions
{
    public int Seed = 20261116;
    public int Goal = 20;
    public int Width = 1920;
    public int Height = 1080;
    public int Monitor = 1;              // 1 = 主屏，2 = 第二块屏（vbrun 用；序号越界时落回最近的一块）
    public string MonitorDevice = "";    // 2026-11-22：编辑器按设备名选屏（例如 DISPLAY2 的完整设备名），找不到就报错退出（退出码 3），不再悄悄落回主屏
    public string LogDir = "";
    public double DurationSec = 0;       // 0 = 不自动退出
    public double StartDelaySec = 3;
    public double HpDecayPerSec = 5;
    public bool StaticTest = false;      // 探针用：第 4 秒起冻结画面 3 秒，观察 WGC 是否还出帧
    public bool Showcase = false;        // 样张模式：固定摆出所有键帽和红血条，用来裁模板、取颜色
    public string Scene = "patrol";      // patrol = 巡逻小游戏；inventory = 背包（2026-11-20）
    public int OpenItem = -1;            // 背包：启动时直接打开第一个分类里第 N 个物品的详情（截样张用）
    public int ScrollStep = 0;           // 背包：滚轮一格滚多少基准像素；0 = 正好一行（对齐）
    // 背包的按住拖动（2026-11-23）：按下不放、竖着移过门槛就拖列表，没拖动的松开才算点击
    public double DragThreshold = 10;    // 门槛（基准像素）；0 = 关掉拖动，恢复「一按下就点开」的旧行为
    public double DragRatio = 1;         // 拖 1 像素列表动几像素（1 = 跟手；大于 1 模拟拖滚动条）
    public bool DragInertia = false;     // 松手后按松手时的速度再滑一段（每秒剩 13.5%，和常见 UI 库的默认值一样）

    public static TargetOptions Parse(string[] args)
    {
        var o = new TargetOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} 缺少参数值");
            switch (a)
            {
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--goal": o.Goal = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--size":
                    var p = Next().Split('x', 'X');
                    o.Width = int.Parse(p[0], CultureInfo.InvariantCulture);
                    o.Height = int.Parse(p[1], CultureInfo.InvariantCulture);
                    break;
                case "--monitor": o.Monitor = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--monitor-device": o.MonitorDevice = Next(); break;
                case "--log": o.LogDir = Next(); break;
                case "--duration": o.DurationSec = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--start-delay": o.StartDelaySec = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--hp-decay": o.HpDecayPerSec = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--static-test": o.StaticTest = true; break;
                case "--showcase": o.Showcase = true; break;
                case "--scene": o.Scene = Next(); break;
                case "--open-item": o.OpenItem = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--scroll-step": o.ScrollStep = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--drag-threshold": o.DragThreshold = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--drag-ratio": o.DragRatio = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--drag-inertia": o.DragInertia = true; break;
                default: throw new ArgumentException($"未知参数 {a}");
            }
        }
        if (string.IsNullOrEmpty(o.LogDir))
            o.LogDir = Path.Combine(AppContext.BaseDirectory, "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        return o;
    }
}
