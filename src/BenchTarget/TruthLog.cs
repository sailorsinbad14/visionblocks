using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BenchTarget;

/// <summary>
/// ground truth 日志（truth.jsonl）。只给评测脚本读，demo 程序不许读它。
/// 时间戳一律用 QPC（Stopwatch.GetTimestamp），和 demo 进程是同一个时钟，可以直接相减。
/// </summary>
public sealed class TruthLog : IDisposable
{
    private readonly StreamWriter _w;
    private long _lastFlush = Stopwatch.GetTimestamp();
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public TruthLog(string dir)
    {
        Directory.CreateDirectory(dir);
        _w = new StreamWriter(Path.Combine(dir, "truth.jsonl"), false, new UTF8Encoding(false));
    }

    public void Write(object record)
    {
        _w.WriteLine(JsonSerializer.Serialize(record, Json));
        long now = Stopwatch.GetTimestamp();
        if (now - _lastFlush > Stopwatch.Frequency / 2) { _w.Flush(); _lastFlush = now; }
    }

    public void Dispose() { _w.Flush(); _w.Dispose(); }
}
