using System.Text.Json;

namespace VisionBlocks.Core.Model;

/// <summary>
/// 编辑器里的剪贴板（2026-11-23）：复制时把积木（连同里面的）存成 JSON，顺便记下它用到的资源。
/// 粘到同一个程序里，资源都在，原样贴；粘到别的程序里，目标程序没有的资源（热区、定点、颜色、网格、模板连 PNG）一起带过去，
/// 同名的已经有了就沿用目标程序里的，并在 Notes 里说明。复制时就把模板的 PNG 读进内存：源程序之后被改了、换了也不影响。
/// </summary>
public sealed class BlockClip
{
    public required string BlockJson { get; init; }
    public required string SourcePath { get; init; }
    public string Summary { get; init; } = "";
    public Dictionary<string, RegionDef> Regions { get; } = new();
    public Dictionary<string, PointDef> Points { get; } = new();
    public Dictionary<string, ColorDef> Colors { get; } = new();
    public Dictionary<string, GridDef> Grids { get; } = new();
    /// <summary>模板：定义和 PNG 的内容（找不到文件时为 null）。</summary>
    public Dictionary<string, (TemplateDef Def, byte[]? Png)> Templates { get; } = new();

    private static T Copy<T>(T v) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, ProgramIO.Options), ProgramIO.Options)!;

    public static BlockClip Capture(ProgramEditor ed, Block b)
    {
        var p = ed.Program;
        var clip = new BlockClip { BlockJson = JsonSerializer.Serialize(b, ProgramIO.Options), SourcePath = ed.ProgramPath, Summary = BlockText.Summary(b, p) };
        foreach (var (kind, name) in ProgramEditor.ResourcesUsedBy(b))
        {
            switch (kind)
            {
                case "region" when p.Regions.TryGetValue(name, out var r): clip.Regions[name] = Copy(r); break;
                case "point" when p.Points.TryGetValue(name, out var pt): clip.Points[name] = Copy(pt); break;
                case "color" when p.Colors.TryGetValue(name, out var c): clip.Colors[name] = Copy(c); break;
                case "grid" when p.Grids.TryGetValue(name, out var g): clip.Grids[name] = Copy(g); break;
                case "template" when p.Templates.TryGetValue(name, out var t):
                    string file = Path.Combine(ed.Dir, t.File);
                    clip.Templates[name] = (Copy(t), File.Exists(file) ? File.ReadAllBytes(file) : null);
                    break;
            }
        }
        return clip;
    }

    /// <summary>
    /// 准备贴进 target：返回一份新的积木（每次都是新对象），并把 target 里没有的资源加进去（不单独算一步撤销，
    /// 和随后插入积木的那一步一起撤）。Notes 说明哪些资源是带过来的、哪些同名的沿用了目标程序里的。
    /// </summary>
    public (Block Block, List<string> Notes) Paste(ProgramEditor target)
    {
        var notes = new List<string>();
        var p = target.Program;
        bool sameProgram = string.Equals(Path.GetFullPath(SourcePath), Path.GetFullPath(target.ProgramPath), StringComparison.OrdinalIgnoreCase);
        void Merge<T>(Dictionary<string, T> from, Dictionary<string, T> into, string what)
        {
            foreach (var (name, def) in from)
            {
                if (into.TryGetValue(name, out var have))
                {
                    if (!sameProgram && JsonSerializer.Serialize(have, ProgramIO.Options) != JsonSerializer.Serialize(def, ProgramIO.Options))
                        notes.Add($"{what}「{name}」这个程序里已经有了，和复制来的不一样，沿用这个程序里的");
                }
                else
                {
                    into[name] = Copy(def);
                    notes.Add($"带过来了{what}「{name}」");
                }
            }
        }
        Merge(Regions, p.Regions, "热区");
        Merge(Points, p.Points, "定点");
        Merge(Colors, p.Colors, "颜色");
        Merge(Grids, p.Grids, "网格");
        foreach (var (name, (def, png)) in Templates)
        {
            if (p.Templates.ContainsKey(name))
            {
                if (!sameProgram) notes.Add($"模板「{name}」这个程序里已经有了，沿用这个程序里的");
                continue;
            }
            if (png == null) { notes.Add($"模板「{name}」的图片在原来的程序里找不到，没带过来（运行前要重新裁一个）"); continue; }
            string safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            string rel = Path.Combine("templates", safe + ".png");
            for (int i = 2; File.Exists(Path.Combine(target.Dir, rel)); i++) rel = Path.Combine("templates", $"{safe}{i}.png");
            Directory.CreateDirectory(Path.Combine(target.Dir, "templates"));
            File.WriteAllBytes(Path.Combine(target.Dir, rel), png);
            p.Templates[name] = new TemplateDef { Label = def.Label, File = rel.Replace('\\', '/') };
            notes.Add($"带过来了模板「{name}」（{rel.Replace('\\', '/')}）");
        }
        var block = JsonSerializer.Deserialize<Block>(BlockJson, ProgramIO.Options)!;
        return (block, notes);
    }
}
