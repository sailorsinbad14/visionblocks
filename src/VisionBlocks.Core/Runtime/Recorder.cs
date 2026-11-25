using System.Text;
using VisionBlocks.Core.Model;

namespace VisionBlocks.Core.Runtime;

/// <summary>
/// 结果表（2026-11-20，背包场景）：「读文字」「截图存图」把值写进当前字段，「记录一行」把当前字段写成 data.csv 的一行。
/// 列的顺序按程序里第一次出现的字段名排；文件用 UTF-8 带 BOM，Excel 直接打开不乱码。
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly List<string> _columns;
    private readonly Dictionary<string, string> _values = new();
    /// <summary>去重键 → 第一次记下它的行号（跳过时告诉用户和第几行一样）。</summary>
    private readonly Dictionary<string, int> _seen = new();
    private readonly Dictionary<string, byte[]> _thumbs = new();
    private readonly List<(string Text, byte[][] Thumbs, int Row)> _blankKeyed = new();
    /// <summary>这一行还没记下时存的图（相对路径）。这一行被去重跳过时要删掉，否则会留下表里不引用的图（2026-11-22 实测：最后一个重复物品留下一张 047）。</summary>
    private readonly List<string> _pendingImages = new();
    private readonly StreamWriter _w;

    public string Dir { get; }
    public int Rows { get; private set; }
    public int Duplicates { get; private set; }
    /// <summary>去重字段全部读空的次数（文字识别没读出来）。</summary>
    public int EmptyKeys { get; private set; }

    public Recorder(string dir, VbProgram p)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        _columns = FieldsOf(p);
        _w = new StreamWriter(Path.Combine(dir, "data.csv"), false, new UTF8Encoding(true));
        _w.WriteLine(string.Join(",", new[] { "序号" }.Concat(_columns).Select(Csv)));
        _w.Flush();
    }

    /// <summary>按出现顺序列出程序里所有「读文字」「截图存图」「认图案」用到的字段名。</summary>
    public static List<string> FieldsOf(VbProgram p)
    {
        var list = new List<string>();
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                string? f = b switch { ReadTextBlock r => r.Field, SaveImageBlock s => s.Field, ClassifyBlock cl => cl.Field, _ => null };
                if (!string.IsNullOrWhiteSpace(f) && !list.Contains(f)) list.Add(f);
                if (b is IContainerBlock c) Walk(c.Body);
                if (b is IfBlock ib) Walk(ib.Else);
            }
        }
        Walk(p.Program);
        return list;
    }

    public void Set(string field, string value) { _values[field] = value; _fresh.Add(field); }

    /// <summary>上一次「记录一行」以后读到的字段（认图案确认框里告诉用户是哪件物品；上一行留下的值、图片字段的文件路径不算）。</summary>
    public IReadOnlyDictionary<string, string> Current => _fresh.Where(f => !_thumbs.ContainsKey(f) && _values.ContainsKey(f)).ToDictionary(f => f, f => _values[f]);
    private readonly HashSet<string> _fresh = new();

    /// <summary>存图时一并记下缩略图（16×16 彩色，BGR 字节），去重字段读空时用来认物品。</summary>
    public void SetImage(string field, string value, byte[] thumb) { _values[field] = value; _thumbs[field] = thumb; _pendingImages.Add(value); }

    /// <summary>下一行的序号（存图时用来给文件命名）。</summary>
    public int NextRowNo => Rows + 1;

    /// <summary>
    /// 写一行；dedupeBy 里的字段组合写过的话跳过，返回 false。key 是给人看的说明（写进日志和运行信息）。
    /// 去重字段读空时（2026-11-20 实测：名称框贴字太紧，7 个物品名读空，后 6 个被当成重复悄悄丢了），
    /// 空值不能当键，改用「没读空的文字字段 + 存的图片缩略图」认物品：都一样才算重复，否则照记一行。
    /// 只比图片不够——靶子 46 个物品只有 27 种图片。没有图片可比时照记，宁可多一行也不丢。
    /// 2026-11-25 起任何一个去重字段读空就走这条路（以前要全部读空）：按「名称，有效期」去重时有效期总有值，
    /// 名称读空、角标一样的几件会拼成同一个键被并掉。没读空的去重字段（比如有效期）留在比较里。
    /// </summary>
    public bool Record(IReadOnlyList<string> dedupeBy, out string key)
    {
        _fresh.Clear();   // 这一行到此为止，后面读到的算下一件
        string V(string f) => _values.GetValueOrDefault(f, "");
        key = string.Join("|", dedupeBy.Select(V));
        var blanks = dedupeBy.Where(f => string.IsNullOrWhiteSpace(V(f))).ToList();
        if (blanks.Count > 0)
        {
            EmptyKeys++;
            string which = string.Join("、", blanks);
            string text = string.Join("|", _columns.Where(c => !_thumbs.ContainsKey(c) && !blanks.Contains(c)).Select(c => c + "=" + V(c)));
            byte[][] thumbs = _columns.Where(_thumbs.ContainsKey).Select(c => _thumbs[c]).ToArray();
            if (thumbs.Length == 0) key = $"（{which}读空，没有图片可比，照记）";
            else
            {
                var same = _blankKeyed.FirstOrDefault(b => b.Text == text && b.Thumbs.Length == thumbs.Length && b.Thumbs.Zip(thumbs).All(p => Similar(p.First, p.Second)));
                if (same.Thumbs != null) { key = $"（{which}读空）图片和其他字段同第 {same.Row} 行"; Duplicates++; DropPendingImages(); return false; }
                _blankKeyed.Add((text, thumbs, Rows + 1));
                key = $"（{which}读空，按图片和其他字段记为新物品）";
            }
        }
        else if (dedupeBy.Count > 0 && _seen.TryGetValue(key, out int first))
        {
            key = $"{string.Join("、", dedupeBy)}都和第 {first} 行一样（{string.Join(" · ", dedupeBy.Select(V))}）";
            Duplicates++; DropPendingImages(); return false;
        }
        else if (dedupeBy.Count > 0) _seen[key] = Rows + 1;
        Rows++;
        _w.WriteLine(string.Join(",", new[] { Rows.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(_columns.Select(c => _values.GetValueOrDefault(c, ""))).Select(Csv)));
        _w.Flush();
        _pendingImages.Clear();
        return true;
    }

    /// <summary>删掉被跳过的这一行存的图，并清掉指向它们的字段值（文件没了，后面的行不能再引用）。</summary>
    private void DropPendingImages()
    {
        foreach (string rel in _pendingImages)
        {
            try { File.Delete(Path.Combine(Dir, rel)); } catch (IOException) { }   // 删不掉只是多一张图，不能因此中断运行
            foreach (var f in _values.Where(kv => kv.Value == rel).Select(kv => kv.Key).ToList()) { _values.Remove(f); _thumbs.Remove(f); }
        }
        _pendingImages.Clear();
    }

    /// <summary>两张缩略图逐字节平均差 ≤ 8（满分 255）算同一张：同一位置的同一图标几乎逐像素一致，换颜色或换形状差得远。</summary>
    private static bool Similar(byte[] a, byte[] b) =>
        a.Length == b.Length && a.Zip(b).Sum(p => Math.Abs(p.First - p.Second)) <= 8L * a.Length;

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Dispose() { _w.Flush(); _w.Dispose(); }
}
