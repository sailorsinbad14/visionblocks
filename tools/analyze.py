"""对齐靶子 truth.jsonl 与 demo bot.jsonl，算延迟、命中率、落点误差。只用标准库。

用法：py -3 -I -X utf8 tools/analyze.py <结果目录>
"""
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path


def load(path):
    out = []
    if not path.exists():
        return out
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line:
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    return out


def pct(values, p):
    if not values:
        return None
    s = sorted(values)
    k = max(0, min(len(s) - 1, int(round(p / 100 * len(s) + 0.5)) - 1))
    return round(s[k], 2)


def stats(values):
    return {"n": len(values), "p50": pct(values, 50), "p95": pct(values, 95),
            "max": round(max(values), 2) if values else None}


def main(d):
    d = Path(d)
    truth = load(d / "target" / "truth.jsonl")
    bot = load(d / "bot.jsonl")
    summary = json.loads((d / "target" / "summary.json").read_text(encoding="utf-8")) if (d / "target" / "summary.json").exists() else {}
    meta = next((r for r in truth if r.get("ev") == "meta"), {})
    freq = meta.get("qpcFreq") or next((r.get("qpcFreq") for r in bot if r.get("ev") == "meta"), 10_000_000)
    ms = lambda a, b: (a - b) * 1000.0 / freq

    tframe = {r["f"]: r["t"] for r in truth if "f" in r and "ev" not in r}
    bframes = [r for r in bot if r.get("ev") == "frame"]
    res = {"dir": str(d), "targetMeta": meta}

    # ① 截图延迟：demo 收到帧的时刻 − 靶子画这一帧的时刻
    cap, capsys, decoded, repeated = [], [], 0, 0
    prev = None
    for r in bframes:
        fid = r.get("fid", -1)
        if fid in tframe:
            decoded += 1
            cap.append(ms(r["arr"], tframe[fid]))
            if r.get("sys"):
                capsys.append(ms(r["sys"], tframe[fid]))
        if prev is not None and fid == prev and fid >= 0:
            repeated += 1
        prev = fid
    arr = [r["arr"] for r in bframes]
    gaps = [ms(b, a) for a, b in zip(arr, arr[1:])]
    res["capture"] = {
        "frames": len(bframes), "decoded": decoded,
        "decodeRate": round(decoded / len(bframes), 4) if bframes else None,
        "latencyArrivalMs": stats(cap), "latencySystemStampMs": stats(capsys),
        "intervalMs": stats(gaps), "repeatedFrameIds": repeated,
        "sizes": dict(Counter(f"{r.get('w')}x{r.get('h')}" for r in bframes)),
    }

    # ② 静止期间是否出帧
    fr = [r for r in truth if r.get("ev") == "freeze"]
    if len(fr) >= 2:
        on, off = fr[0]["t"], fr[1]["t"]
        during = [r for r in bframes if on <= r["arr"] <= off]
        res["freeze"] = {"durationMs": round(ms(off, on), 1), "framesDuring": len(during),
                         "distinctIdsDuring": len({r.get("fid") for r in during}),
                         "maxGapAroundMs": round(max([ms(b, a) for a, b in zip(arr, arr[1:]) if a >= on - freq and b <= off + freq] or [0]), 1)}

    # ③ 端到端：键帽第一次画出来 → 靶子收到 F
    shown = {r["id"]: r["t"] for r in truth if r.get("ev") == "shown"}
    spawns = {r["id"]: r for r in truth if r.get("ev") == "spawn"}
    hits = [r for r in truth if r.get("ev") == "hit"]
    e2e = [ms(h["t"], shown[h["id"]]) for h in hits if h["id"] in shown]
    targets = [s for s in spawns.values() if s["c"] == "F" and not s["gh"]]
    misses = [r for r in truth if r.get("ev") == "miss"]
    wrong = [r for r in truth if r.get("ev") == "wrong"]
    res["t1"] = {
        "targetsSpawned": len(targets), "hits": len(hits), "misses": len(misses),
        "hitRate": round(len(hits) / (len(hits) + len(misses)), 4) if hits or misses else None,
        "wrongPresses": len(wrong), "wrongVisible": dict(Counter(w.get("vis") or "(空场)" for w in wrong)),
        "e2eMs": stats(e2e),
        "potions": sum(1 for r in truth if r.get("ev") == "potion"),
        "potionColors": dict(Counter(r.get("color") for r in truth if r.get("ev") == "potion")),
        "downs": sum(1 for r in truth if r.get("ev") == "down"),
        "done": any(r.get("ev") == "done" for r in truth),
    }

    # ④ 输入送达：demo 调 SendInput → 靶子收到
    bkd = [r["t"] for r in bot if r.get("ev") == "keydown"]
    tkd = [r["t"] for r in truth if r.get("ev") == "kd" and not r.get("rep")]
    deliver = []
    j = 0
    for t in tkd:
        cands = [b for b in bkd if b <= t]
        if cands:
            deliver.append(ms(t, cands[-1]))
    res["inputDeliveryMs"] = stats([x for x in deliver if x < 500])

    # ⑤ 点击落点：demo 想点的基准坐标 vs 靶子实际收到的基准坐标
    want = [(r["bx"], r["by"], r.get("name", "")) for r in bot if r.get("ev") in ("probeClick", "clickConfirm") and r.get("ok", True)]
    got = [(r["x"], r["y"]) for r in truth if r.get("ev") == "click"]
    errs = []
    for (bx, by, name), (gx, gy) in zip(want, got):
        errs.append({"name": name, "want": [bx, by], "got": [gx, gy], "dx": round(gx - bx, 1), "dy": round(gy - by, 1)})
    res["clicks"] = {"wanted": len(want), "received": len(got), "errors": errs[:10],
                     "maxAbsErrPx": max([max(abs(e["dx"]), abs(e["dy"])) for e in errs] or [None]) if errs else None}

    # ⑥ 识别耗时与得分
    conds = defaultdict(list)
    for r in bot:
        if r.get("ev") == "cond":
            conds[r["kind"]].append(r)
    res["recognition"] = {k: {"n": len(v), "ms": stats([x["ms"] for x in v]), "hits": sum(1 for x in v if x["hit"])} for k, v in conds.items()}
    if conds.get("image"):
        sc = [x["score"] for x in conds["image"]]
        res["recognition"]["image"]["scoreHist"] = dict(sorted(Counter(round(s, 1) for s in sc).items()))
    if conds.get("text"):
        res["recognition"]["text"]["samples"] = list(dict.fromkeys(x["norm"] for x in conds["text"]))[:8]

    ov = [r for r in bot if r.get("ev") == "overlay"]
    if ov:
        res["overlay"] = {"samples": len(ov), "visible": sum(1 for r in ov if r["vis"]),
                          "alignedWhenVisible": sum(1 for r in ov if r["vis"] and r["aligned"]),
                          "example": ov[len(ov) // 2]}
    res["guard"] = {"blocked": sum(1 for r in bot if r.get("ev") == "blocked"),
                    "reasons": dict(Counter(r.get("why", "")[:20] for r in bot if r.get("ev") == "blocked"))}
    res["targetSummary"] = summary
    end = next((r for r in bot if r.get("ev") == "runEnd"), None)
    if end:
        res["runEnd"] = end

    (d / "analysis.json").write_text(json.dumps(res, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in res.items() if k != "targetMeta"}, ensure_ascii=False, indent=1))


if __name__ == "__main__":
    main(sys.argv[1])
