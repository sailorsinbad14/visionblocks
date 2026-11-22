using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VisionBlocks.Core.Runtime;

/// <summary>demo 这一侧的运行日志（bot.jsonl），时间戳用 QPC，和靶子的 truth.jsonl 同一个时钟。多线程安全。</summary>
public sealed class BotLog : IDisposable
{
    public static long Now => Stopwatch.GetTimestamp();
    private readonly StreamWriter _w;
    private readonly object _lock = new();
    private long _lastFlush = Now;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public event Action<string>? Line;

    public BotLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _w = new StreamWriter(path, false, new UTF8Encoding(false));
    }

    public void Write(object record)
    {
        string s = JsonSerializer.Serialize(record, Json);
        lock (_lock)
        {
            if (_disposed) return;   // 进程退出钩子里的 ReleaseAll 可能晚于日志关闭
            _w.WriteLine(s);
            if (Now - _lastFlush > Stopwatch.Frequency / 2) { _w.Flush(); _lastFlush = Now; }
        }
        Line?.Invoke(s);
    }

    private bool _disposed;
    public void Dispose() { lock (_lock) { if (_disposed) return; _disposed = true; _w.Flush(); _w.Dispose(); } }
}
