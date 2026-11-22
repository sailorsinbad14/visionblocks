"""背包识别对答案：把 vbrun/编辑器输出的 data.csv 和靶子的 truth.jsonl 逐条比对。

用法：py -3 -I -X utf8 tools/inventory_check.py <运行目录>
  运行目录里要有 target/truth.jsonl、target/summary.json，以及 data.csv（vbrun）或 data-*/data.csv（编辑器）。

硬指标：每个物品恰好记一次、没有重复行、没有拦截和点空。
2026-11-22 加一条：工具按下的键和鼠标都松开了（看 bot.jsonl，不看靶子收到的按键，理由见 main 里的注释）。
识别准确率（名称、库存、分类）只如实报告，不预设门槛。
"""
import csv
import json
import sys
from pathlib import Path

# 靶子（WPF Key 名）→ 工具（KeyNames 名），用来判断靶子那边没松开的键是不是工具按的
TARGET_TO_BOT = {"Escape": "Esc", "Return": "Enter", "Back": "Backspace", "Prior": "PageUp", "Next": "PageDown",
                 "LeftShift": "Shift", "RightShift": "Shift", "LeftCtrl": "Ctrl", "RightCtrl": "Ctrl",
                 "LeftAlt": "Alt", "RightAlt": "Alt"}


def bot_keys(bot: Path):
    """工具自己按下、松开的键和鼠标左键（bot.jsonl）。结束或急停时统一松开的只有总数（releaseAll / abort 的 released）。
    返回 (按过的键名集合, 按下后没松开的个数)。"""
    held, pressed, released = {}, set(), 0
    for line in bot.read_text(encoding="utf-8").splitlines():
        rec = json.loads(line)
        ev = rec.get("ev")
        k = "MouseLeft" if ev in ("mousedown", "mouseup") else rec.get("k")
        if ev in ("keydown", "mousedown"):
            held[k] = held.get(k, 0) + 1
            pressed.add(k)
        elif ev in ("keyup", "mouseup"):
            held[k] = held.get(k, 0) - 1
        elif ev in ("releaseAll", "abort"):   # runEnd 里的 released 是同一个数，不重复算
            released += rec.get("released", 0)
    return pressed, max(0, sum(v for v in held.values() if v > 0) - released)


def main(run: Path) -> int:
    truth = None
    for line in (run / "target" / "truth.jsonl").read_text(encoding="utf-8").splitlines():
        rec = json.loads(line)
        if rec.get("ev") == "items":
            truth = rec
            break
    if truth is None:
        print("truth.jsonl 里没有 items 记录（不是背包场景？）")
        return 2
    items = truth["items"]
    by_name = {it["name"]: it for it in items}

    csvs = sorted(run.glob("data.csv")) + sorted(run.glob("data-*/data.csv"))
    if not csvs:
        print("没找到 data.csv")
        return 2
    rows = list(csv.DictReader(csvs[-1].open(encoding="utf-8-sig")))
    summary = json.loads((run / "target" / "summary.json").read_text(encoding="utf-8"))

    # 按顺序配对：靶子每打开一次详情（open），工具就恰好记一行（row）或跳过一次（dup）。
    # 这样能知道每一行实际来自哪个物品，「名字读错」和「漏记」就不会混在一起。
    opens = [json.loads(l) for l in (run / "target" / "truth.jsonl").read_text(encoding="utf-8").splitlines() if '"ev":"open"' in l]
    bot = run / "bot.jsonl" if (run / "bot.jsonl").exists() else None
    # 没有样张模式了，取材时用户会在同一个靶子里手动点开物品。
    # 靶子和工具用同一台机器的 QPC 计时，只配对「这次运行开始以后」的打开
    start = None
    if bot:
        start = next((json.loads(l)["t"] for l in bot.read_text(encoding="utf-8").splitlines() if '"ev":"runStart"' in l), None)
        if start is not None:
            manual = sum(1 for o in opens if o["t"] < start)
            opens = [o for o in opens if o["t"] >= start]
            if manual:
                print(f"（运行开始前手动打开过 {manual} 次详情，不参与配对）")
    marks = [json.loads(l)["ev"] for l in bot.read_text(encoding="utf-8").splitlines() if '"ev":"row"' in l or '"ev":"dup"' in l] if bot else []
    paired = len(opens) == len(marks)
    src = [o["name"] for o, m in zip(opens, marks) if m == "row"] if paired else []
    recorded = sorted(set(src))
    missing = [it for it in items if it["name"] not in src] if paired else []
    twice = sorted({n for n in src if src.count(n) > 1})

    names = [r.get("名称", "") for r in rows]
    name_ok = sum(1 for r, n in zip(rows, src) if r.get("名称") == n)
    stock_ok = sum(1 for r, n in zip(rows, src) if r.get("库存") == str(by_name[n]["stock"]))
    cat_ok = sum(1 for r, n in zip(rows, src) if r.get("分类") == by_name[n]["cat"])
    imgs_ok = sum(1 for r in rows if r.get("图片") and (csvs[-1].parent / r["图片"]).exists())

    print(f"结果表：{csvs[-1]}")
    print(f"靶子物品 {len(items)} 个；结果表 {len(rows)} 行；打开详情 {len(opens)} 次，工具记录/跳过 {len(marks)} 次（{'能逐次配对' if paired else '对不上，无法配对'}）")
    once = paired and not missing and not twice and len(rows) == len(src)
    print(f"[硬指标] 每个物品恰好记一次：{'通过' if once else '不通过'}（漏记 {len(missing)}，同一物品记了两次 {len(twice)}）")
    # 卡键看工具自己的日志：靶子那边的「按下没收到松开」也可能是用户自己按的键（比如急停热键的 Ctrl、Alt），
    # 或者工具发了松开、但那一刻前台已经换成别的窗口（2026-11-22 实测两种都出现过），都不是工具卡键
    pressed, left = bot_keys(bot) if bot else (set(), None)
    # 点空也只算运行开始以后的：取材时用户在靶子里手滑点到空处，不算工具点空（summary.json 的 stray 是靶子整个生命周期的）
    stray = summary.get("stray")
    if start is not None:
        stray = sum(1 for l in (run / "target" / "truth.jsonl").read_text(encoding="utf-8").splitlines()
                    if '"ev":"md"' in l and (e := json.loads(l))["t"] >= start and e.get("hit") == "none")
    print(f"[硬指标] 点空 {stray} 次，没打开过 {len(summary.get('notOpened', []))} 个，"
          f"工具按下没松开 {'无法判断（没有 bot.jsonl）' if left is None else str(left) + ' 个'}")
    target_held = {k: v for k, v in (summary.get("unbalancedKeys") or {}).items() if v > 0}
    if target_held:
        print(f"[参考] 靶子那边按下后没收到松开：{target_held}")
        mine = [k for k in target_held if TARGET_TO_BOT.get(k, k) in pressed]
        theirs = [k for k in target_held if k not in mine]
        if theirs:
            print(f"  {'、'.join(theirs)}：工具没按过，是人按的（比如急停热键 Ctrl+Alt+F12 的修饰键），松手前前台已经换成别的窗口")
        if mine and left == 0:
            print(f"  {'、'.join(mine)}：工具按过，也发了松开；松开时前台已经不是靶子，消息送到了别的窗口，系统里这个键已经松开")
    print(f"[识别] 名称完全一致 {name_ok}/{len(src)}；库存一致 {stock_ok}/{len(src)}；分类一致 {cat_ok}/{len(src)}；图片文件存在 {imgs_ok}/{len(rows)}")
    for r, n in zip(rows, src):
        if r.get("名称") != n:
            print(f"  名称读错：「{n}」读成「{r.get('名称')}」")
    for r, n in zip(rows, src):
        if r.get("库存") != str(by_name[n]["stock"]):
            print(f"  库存读错：{n} 读成「{r.get('库存')}」，应为 {by_name[n]['stock']}")
    wrong_cat = sorted({(r.get("分类"), by_name[n]["cat"]) for r, n in zip(rows, src) if r.get("分类") != by_name[n]["cat"]})
    for got, want in wrong_cat:
        print(f"  分类读错：「{want}」读成「{got}」")
    for it in missing:
        print(f"  漏记：{it['cat']}#{it['idx']} {it['name']}")
    hard_ok = once and stray == 0 and not left
    return 0 if hard_ok else 1


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))
