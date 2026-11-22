using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using VisionBlocks.Core.Model;

namespace VisionBlocks.Core.Runtime;

/// <summary>
/// 结果接口（别的程序要能照着说明正确读出表格和对应的图片）。规则见 docs\results-format.zh.md。
/// 每次运行的结果目录里写 manifest.json：开始时 status = running，结束时改成 done / cancelled / emergencyStop / stoppedByBlock / error。
/// 外部程序只读 status 不是 running 的结果。写文件先写临时文件再改名，读的一方不会读到半个 JSON。
/// </summary>
public sealed class ResultManifest
{
    public const int FormatVersion = 1;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Dir { get; }
    private readonly Dictionary<string, object?> _doc;
    private readonly Dictionary<string, object?> _run;

    private ResultManifest(string dir, Dictionary<string, object?> doc, Dictionary<string, object?> run) { Dir = dir; _doc = doc; _run = run; }

    public static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>运行开始、结果表建好之后调用。列的顺序和 data.csv 一致（序号 + Recorder.FieldsOf）。</summary>
    public static ResultManifest Begin(string dir, VbProgram p, string? programPath)
    {
        var columns = new List<object> { new Dictionary<string, object?> { ["name"] = "序号", ["type"] = "integer" } };
        foreach (string field in Recorder.FieldsOf(p))
        {
            var src = FirstSource(p.Program, field);
            var c = new Dictionary<string, object?> { ["name"] = field };
            switch (src)
            {
                case SaveImageBlock s: c["type"] = "image"; c["from"] = "saveImage"; c["region"] = s.Region; c["path"] = "relative"; break;
                case ReadTextBlock r: c["type"] = r.NumbersOnly ? "number" : "text"; c["from"] = "readText"; c["region"] = r.Region; break;
            }
            columns.Add(c);
        }
        var dedupe = FirstRecord(p.Program)?.DedupeBy ?? new List<string>();
        var run = new Dictionary<string, object?>
        {
            ["status"] = "running", ["start"] = Now(), ["end"] = null,
            ["rows"] = 0, ["duplicatesSkipped"] = 0, ["emptyKeyRows"] = 0, ["dedupeBy"] = dedupe,
        };
        var doc = new Dictionary<string, object?>
        {
            ["format"] = "visionblocks.results", ["formatVersion"] = FormatVersion,
            ["program"] = new Dictionary<string, object?> { ["name"] = p.Name, ["file"] = programPath },
            ["run"] = run,
            ["table"] = new Dictionary<string, object?>
            {
                ["file"] = "data.csv", ["encoding"] = "utf-8-sig", ["delimiter"] = ",", ["quote"] = "\"", ["newline"] = "\r\n", ["header"] = true,
                ["columns"] = columns,
            },
            ["images"] = new Dictionary<string, object?> { ["dir"] = "images", ["naming"] = "{序号:000}_{字段名}.png", ["format"] = "png" },
        };
        var m = new ResultManifest(dir, doc, run);
        m.Save();
        return m;
    }

    /// <summary>运行结束时调用；可以再调一次改状态（编辑器急停时把 cancelled 改成 emergencyStop）。</summary>
    public void Finish(string status, int rows, int duplicates, int emptyKeys)
    {
        _run["status"] = status;
        _run["end"] = Now();
        _run["rows"] = rows;
        _run["duplicatesSkipped"] = duplicates;
        _run["emptyKeyRows"] = emptyKeys;
        Save();
    }

    public string Status => (string)_run["status"]!;
    public string? End => _run["end"] as string;
    public string ProgramName => (string)((Dictionary<string, object?>)_doc["program"]!)["name"]!;

    private void Save() => WriteAtomic(Path.Combine(Dir, "manifest.json"), JsonSerializer.Serialize(_doc, Json));

    /// <summary>results\latest.json：永远指向最新一次结束的结果（编辑器在每次运行结束时写）。</summary>
    public void WriteLatest(string resultsRoot)
    {
        var latest = new Dictionary<string, object?>
        {
            ["format"] = "visionblocks.latest", ["formatVersion"] = FormatVersion,
            ["dataDir"] = Path.GetFullPath(Dir), ["manifest"] = Path.GetFullPath(Path.Combine(Dir, "manifest.json")),
            ["status"] = Status, ["end"] = End, ["program"] = ProgramName,
        };
        Directory.CreateDirectory(resultsRoot);
        WriteAtomic(Path.Combine(resultsRoot, "latest.json"), JsonSerializer.Serialize(latest, Json));
    }

    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }

    private static Block? FirstSource(IEnumerable<Block> blocks, string field)
    {
        foreach (var b in blocks)
        {
            if ((b is ReadTextBlock r && r.Field == field) || (b is SaveImageBlock s && s.Field == field)) return b;
            var inner = b is IContainerBlock c ? FirstSource(c.Body, field) : null;
            inner ??= b is IfBlock ib ? FirstSource(ib.Else, field) : null;
            if (inner != null) return inner;
        }
        return null;
    }

    private static RecordRowBlock? FirstRecord(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is RecordRowBlock rr) return rr;
            var inner = b is IContainerBlock c ? FirstRecord(c.Body) : null;
            inner ??= b is IfBlock ib ? FirstRecord(ib.Else) : null;
            if (inner != null) return inner;
        }
        return null;
    }
}
