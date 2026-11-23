# VisionBlocks 运行结果格式说明（接口文档）

> 格式版本 `formatVersion = 1`，适用于 VisionBlocks Studio 0.2 起。English version: `results-format.en.md`（同一个文件夹里）。

这份文档写给要读取 VisionBlocks 运行结果的其他程序：照着它可以正确读出表格、每一行对应的图片、每一列的类型，以及这次运行有没有完整结束。

## 1. 文件放在哪

每次在编辑器里点「▶ 运行」，会生成一个结果文件夹：

```
<数据目录>\results\
├─ latest.json                         ← 永远指向最新一次结束的运行
├─ 结果格式说明.md / results-format.en.md   ← 本文档的副本
└─ studio-20261122-084451\             ← 一次「启动靶子 / 运行」会话
   ├─ bot.jsonl                         ← 运行日志（调试用，不属于接口）
   └─ data-084454\                      ← 一次运行的结果（本文档说的「结果文件夹」）
      ├─ manifest.json
      ├─ data.csv
      └─ images\
         ├─ 001_图片.png
         └─ 002_图片.png
```

`<数据目录>` 取决于运行方式：

| 运行方式 | 数据目录 |
|---|---|
| 源码（仓库里运行） | 仓库的 `vision-blocks\` |
| 便携文件夹（旁边有 `portable.txt`） | exe 旁边的 `data\` |
| 单个 exe | `文档\VisionBlocks\` |

只有程序里用了「读文字」或「截图存图」积木时，才会有 `data-时分秒\` 结果文件夹。

## 2. 推荐的读取步骤

1. 读 `results\latest.json`，拿到 `dataDir`（结果文件夹的绝对路径）。
2. 读 `<dataDir>\manifest.json`，确认 `run.status` **不是** `running`。是 `running` 表示还在跑，表格还没写完，过一会儿再读。
3. 按 `manifest.table` 的说明读 `data.csv`；列的类型看 `manifest.table.columns`。
4. 类型为 `image` 的列，值是相对 `dataDir` 的路径（用 `/` 分隔），拼上 `dataDir` 就是图片文件。

## 3. data.csv

- 编码 UTF-8 **带 BOM**（Excel 直接双击打开不乱码）；读取时用 `utf-8-sig` 之类能去掉 BOM 的方式。
- 逗号分隔，换行 `\r\n`，第一行是列名。
- 值里有逗号、双引号或换行时整格用双引号包起来，里面的双引号写两次（RFC 4180）。
- 第一列固定是 `序号`，从 1 开始，每行加 1。
- 其余列的名字就是程序里「读文字 / 截图存图」积木的「存进字段」，顺序按它们在程序里第一次出现的顺序。列名是用户起的，可能是中文。
- **空字符串**表示这一格没读到：文字没认出来，或者这一行的流程没有经过那块积木。不会写 `null`、`N/A` 之类的占位。
- `number` 类型的列只含数字 `0–9`（「读文字」勾了「只要数字」），可能为空。
- 被去重跳过的物品不会写进表，也不会留下图片。

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

| 键 | 说明 |
|---|---|
| `format` | 固定 `visionblocks.results` |
| `formatVersion` | 格式版本，见第 7 节 |
| `program.name` / `program.file` | 程序名；程序文件运行时的绝对路径 |
| `run.status` | 见下表 |
| `run.start` / `run.end` | 开始、结束时间，ISO 8601 带时区；还在跑时 `end` 是 `null` |
| `run.rows` | 写进 `data.csv` 的行数（不含表头） |
| `run.duplicatesSkipped` | 因为去重跳过的次数 |
| `run.emptyKeyRows` | 去重字段全部读空的次数（这时改用图片和其他字段判断是否重复） |
| `run.dedupeBy` | 按哪些字段去重（程序里第一块「记录一行」的设置）；空数组表示不去重 |
| `table.columns[].type` | `integer`（序号）、`text`、`number`、`image` |
| `table.columns[].from` / `region` | 这一列来自哪种积木、哪个热区（「当前格」表示网格循环里的当前格子） |
| `images.naming` | 图片文件名规则：三位序号 + 下划线 + 字段名，字段名里不能用于文件名的字符换成 `_` |

`run.status` 的取值：

| 值 | 含义 | 表格是否完整 |
|---|---|---|
| `running` | 还在运行 | 否，不要读 |
| `done` | 程序正常跑完 | 是 |
| `stoppedByBlock` | 遇到「停止」积木，正常结束 | 是 |
| `cancelled` | 用户点了「停止」，或运行时限到了 | 部分 |
| `emergencyStop` | 用户按了急停热键（默认 Ctrl+Alt+F12） | 部分 |
| `error` | 运行出错中断 | 部分 |

「部分」表示已经写进表的行都是完整、可用的，只是没有跑完全部流程。编辑器异常退出时 `status` 可能停在 `running`，这种结果请当作不完整处理。

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

编辑器在每次运行**结束**时更新它（运行中不会指向正在跑的那次）。没有生成结果文件夹的运行不更新它。命令行工具 `vbrun` 只写 `manifest.json`，不写 `latest.json`。

`manifest.json` 和 `latest.json` 都是先写临时文件再改名，读的时候不会读到写了一半的内容。两者都是 UTF-8，不带 BOM。

## 6. 读取示例

Python：

```python
import csv, json, pathlib

results = pathlib.Path(r"D:\…\results")
latest = json.loads((results / "latest.json").read_text(encoding="utf-8"))
run = pathlib.Path(latest["dataDir"])
m = json.loads((run / "manifest.json").read_text(encoding="utf-8"))
if m["run"]["status"] == "running":
    raise SystemExit("还在运行，稍后再读")

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

C#（.NET 6+）：

```csharp
using System.Text.Json;

var results = @"D:\…\results";
using var latest = JsonDocument.Parse(File.ReadAllText(Path.Combine(results, "latest.json")));
string dataDir = latest.RootElement.GetProperty("dataDir").GetString()!;
using var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "manifest.json")));
if (m.RootElement.GetProperty("run").GetProperty("status").GetString() == "running") return;

var columns = m.RootElement.GetProperty("table").GetProperty("columns").EnumerateArray()
    .Select(c => (Name: c.GetProperty("name").GetString()!, Type: c.GetProperty("type").GetString()!)).ToList();
// 简单情况可以按行拆分；值里可能有逗号、引号时请用 CSV 库（例如 CsvHelper）按 RFC 4180 解析
foreach (var line in File.ReadLines(Path.Combine(dataDir, "data.csv")).Skip(1))
{
    var cells = line.Split(',');
    for (int i = 0; i < columns.Count && i < cells.Length; i++)
        if (columns[i].Type == "image" && cells[i] != "")
            Console.WriteLine(Path.Combine(dataDir, cells[i].Replace('/', Path.DirectorySeparatorChar)));
}
```

## 7. 版本约定

- `formatVersion` 从 1 开始。
- 只**增加**新的键或新的 `status` / `type` 取值时，版本号不变；读取方请忽略不认识的键。
- 改变已有键的含义、改名或删掉时，`formatVersion` 加 1，并在本文档里写明差别。
- `bot.jsonl`（运行日志）和 `target\`（练习靶子的日志）不属于这个接口，格式可能随时变。
