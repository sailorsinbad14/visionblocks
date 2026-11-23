# VisionBlocks run results format (interface spec)

> `formatVersion = 1`, VisionBlocks Studio 0.2 and later. 中文版：同一个文件夹里的 `结果格式说明.md`（仓库里是 `docs\results-format.zh.md`）。

This document is for other programs that read VisionBlocks run results. Follow it to read the table, the image that belongs to each row, the type of each column, and whether the run finished.

## 1. Where the files are

Each click on "▶ Run" in the editor produces a results folder:

```
<data folder>\results\
├─ latest.json                         ← always points to the most recent finished run
├─ 结果格式说明.md / results-format.en.md   ← copies of this document
└─ studio-20261122-084451\             ← one "launch target / run" session
   ├─ bot.jsonl                         ← run log (for debugging, not part of this interface)
   └─ data-084454\                      ← the results of one run (the "results folder" below)
      ├─ manifest.json
      ├─ data.csv
      └─ images\
         ├─ 001_图片.png
         └─ 002_图片.png
```

`<data folder>` depends on how VisionBlocks runs:

| How it runs | Data folder |
|---|---|
| From source (inside the repository) | the repository's `vision-blocks\` |
| Portable folder (`portable.txt` next to the exe) | `data\` next to the exe |
| Single exe | `Documents\VisionBlocks\` |

A `data-HHMMSS\` results folder exists only when the program uses a "Read text" or "Save image" block.

## 2. Recommended way to read

1. Read `results\latest.json` and take `dataDir` (absolute path of the results folder).
2. Read `<dataDir>\manifest.json` and check that `run.status` is **not** `running`. `running` means the run is still going and the table is incomplete; read again later.
3. Read `data.csv` as described in `manifest.table`; column types are in `manifest.table.columns`.
4. Values in `image` columns are paths relative to `dataDir`, separated by `/`. Join them with `dataDir` to get the file.

## 3. data.csv

- UTF-8 **with BOM** (so Excel opens it correctly). Read it with a BOM-aware decoder such as `utf-8-sig`.
- Comma-separated, `\r\n` line endings, first line is the header.
- A value containing a comma, double quote or line break is wrapped in double quotes, with inner quotes doubled (RFC 4180).
- The first column is always `序号` (row number), starting at 1.
- The other column names are the "Into field" names of the "Read text" / "Save image" blocks, in the order they first appear in the program. Users choose these names, so they may be Chinese.
- An **empty string** means nothing was read for that cell: the text was not recognized, or the row's flow did not pass that block. There is no `null` or `N/A` placeholder.
- `number` columns contain only digits `0–9` ("Digits only" was ticked on the Read text block) and may be empty.
- Items skipped as duplicates are not written to the table and leave no image behind.

## 4. manifest.json

```json
{
  "format": "visionblocks.results",
  "formatVersion": 1,
  "program": { "name": "我的背包", "file": "D:\\…\\programs\\20261122-072127\\program.vbp.json" },
  "run": {
    "status": "done",
    "start": "2026-11-22T08:44:54+08:00",
    "end": "2026-11-22T08:45:31+08:00",
    "rows": 46,
    "duplicatesSkipped": 46,
    "emptyKeyRows": 0,
    "dedupeBy": ["名称"]
  },
  "table": {
    "file": "data.csv", "encoding": "utf-8-sig", "delimiter": ",", "quote": "\"", "newline": "\r\n", "header": true,
    "columns": [
      { "name": "序号", "type": "integer" },
      { "name": "分类", "type": "text",   "from": "readText",  "region": "当前格" },
      { "name": "名称", "type": "text",   "from": "readText",  "region": "物品名" },
      { "name": "库存", "type": "number", "from": "readText",  "region": "库存" },
      { "name": "图片", "type": "image",  "from": "saveImage", "region": "图片", "path": "relative" }
    ]
  },
  "images": { "dir": "images", "naming": "{序号:000}_{字段名}.png", "format": "png" }
}
```

| Key | Meaning |
|---|---|
| `format` | always `visionblocks.results` |
| `formatVersion` | format version, see section 7 |
| `program.name` / `program.file` | program name; absolute path of the program file at run time |
| `run.status` | see the table below |
| `run.start` / `run.end` | start and end time, ISO 8601 with offset; `end` is `null` while running |
| `run.rows` | rows written to `data.csv` (header excluded) |
| `run.duplicatesSkipped` | how many times a row was skipped as a duplicate |
| `run.emptyKeyRows` | how many times every dedupe field was empty (duplicates are then judged by the image and other fields) |
| `run.dedupeBy` | dedupe fields (from the first "Record row" block in the program); empty means no dedupe |
| `table.columns[].type` | `integer` (row number), `text`, `number`, `image` |
| `table.columns[].from` / `region` | which kind of block and which region the column comes from (`当前格` = the current cell in a grid loop) |
| `images.naming` | image file names: three-digit row number, underscore, field name; characters not allowed in file names become `_` |

`run.status` values:

| Value | Meaning | Table complete? |
|---|---|---|
| `running` | still running | no, do not read |
| `done` | the program finished | yes |
| `stoppedByBlock` | reached a "Stop" block, a normal end | yes |
| `cancelled` | the user clicked Stop, or the run's time limit was reached | partial |
| `emergencyStop` | the user pressed the e-stop hotkey (Ctrl+Alt+F12 by default) | partial |
| `error` | the run stopped on an error | partial |

"Partial" means every row already in the table is complete and usable; the run just did not cover everything. If the editor exits abnormally, `status` may stay `running`; treat such results as incomplete.

## 5. latest.json

```json
{
  "format": "visionblocks.latest",
  "formatVersion": 1,
  "dataDir": "D:\\…\\results\\studio-20261122-084451\\data-084454",
  "manifest": "D:\\…\\results\\studio-20261122-084451\\data-084454\\manifest.json",
  "status": "done",
  "end": "2026-11-22T08:45:31+08:00",
  "program": "我的背包"
}
```

The editor updates it when a run **ends** (it never points to a run that is still going). Runs that produce no results folder do not update it. The command-line tool `vbrun` writes `manifest.json` but not `latest.json`.

Both `manifest.json` and `latest.json` are written to a temporary file first and then renamed, so a reader never sees a half-written file. Both are UTF-8 without BOM.

## 6. Reading examples

Python:

```python
import csv, json, pathlib

results = pathlib.Path(r"D:\…\results")
latest = json.loads((results / "latest.json").read_text(encoding="utf-8"))
run = pathlib.Path(latest["dataDir"])
m = json.loads((run / "manifest.json").read_text(encoding="utf-8"))
if m["run"]["status"] == "running":
    raise SystemExit("still running, read again later")

types = {c["name"]: c["type"] for c in m["table"]["columns"]}
with open(run / m["table"]["file"], encoding="utf-8-sig", newline="") as f:
    for row in csv.DictReader(f):
        item = {}
        for name, value in row.items():
            if types.get(name) == "number":
                item[name] = int(value) if value else None
            elif types.get(name) == "image":
                item[name] = run / value if value else None
            else:
                item[name] = value or None
        print(item)
```

C# (.NET 6+):

```csharp
using System.Text.Json;

var results = @"D:\…\results";
using var latest = JsonDocument.Parse(File.ReadAllText(Path.Combine(results, "latest.json")));
string dataDir = latest.RootElement.GetProperty("dataDir").GetString()!;
using var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "manifest.json")));
if (m.RootElement.GetProperty("run").GetProperty("status").GetString() == "running") return;

var columns = m.RootElement.GetProperty("table").GetProperty("columns").EnumerateArray()
    .Select(c => (Name: c.GetProperty("name").GetString()!, Type: c.GetProperty("type").GetString()!)).ToList();
// Splitting on commas is fine for simple data; use a CSV library (e.g. CsvHelper) when values may contain commas or quotes
foreach (var line in File.ReadLines(Path.Combine(dataDir, "data.csv")).Skip(1))
{
    var cells = line.Split(',');
    for (int i = 0; i < columns.Count && i < cells.Length; i++)
        if (columns[i].Type == "image" && cells[i] != "")
            Console.WriteLine(Path.Combine(dataDir, cells[i].Replace('/', Path.DirectorySeparatorChar)));
}
```

## 7. Versioning

- `formatVersion` starts at 1.
- Adding new keys, or new `status` / `type` values, keeps the version number; readers should ignore keys they do not know.
- Changing the meaning of an existing key, renaming it or removing it bumps `formatVersion` by 1, and this document will describe the difference.
- `bot.jsonl` (run log) and `target\` (practice target logs) are not part of this interface and may change at any time.
