#!/usr/bin/env python3
"""
分析 getevent 采集的 sec_touchscreen 原始触摸数据。

getevent -lt 的行格式：
  [  475482.548236] EV_ABS       ABS_MT_TOUCH_MAJOR   0000000b

输出：
  1. 每个「按下段落」的接触面积（sqrt(major*minor)）随时间的变化
  2. 相对「近期静息水平」的涨幅（v1.4.6 判据真正看的量）
  3. 不同动作段落之间的对比，用于定触发阈值

用法：python tools/analyze_touch.py <log文件>
"""
import math
import re
import sys

LINE = re.compile(
    r"^\[\s*([0-9.]+)\]\s+EV_(\w+)\s+(\S+)\s+([0-9a-fA-F]+)"
)
# BTN_TOUCH 这类 KEY 事件的值是 DOWN/UP 字符串，不是十六进制
KEY_WORD = re.compile(
    r"^\[\s*([0-9.]+)\]\s+EV_KEY\s+(\S+)\s+(DOWN|UP)\s*$"
)

EV_ABS = "ABS"
EV_KEY = "KEY"
EV_SYN = "SYN"

# 事件名 → 是否是我们关心的
TRACKING_ID = "ABS_MT_TRACKING_ID"
MAJOR = "ABS_MT_TOUCH_MAJOR"
MINOR = "ABS_MT_TOUCH_MINOR"
PRESSURE = "ABS_MT_PRESSURE"
POS_X = "ABS_MT_POSITION_X"
POS_Y = "ABS_MT_POSITION_Y"
BTN_TOUCH = "BTN_TOUCH"


def parse(path):
    """→ [{'t':ts,'type':'ABS'/'KEY','name':...,'val':int}]"""
    events = []
    with open(path, "r", errors="replace") as f:
        for line in f:
            line = line.strip()
            m = KEY_WORD.match(line)
            if m:
                ts, name, word = m.groups()
                events.append({"t": float(ts), "type": EV_KEY, "name": name,
                               "val": 1 if word == "DOWN" else 0})
                continue
            m = LINE.match(line)
            if not m:
                continue
            ts, etype, name, val = m.groups()
            if name == "SYN_REPORT":
                continue
            try:
                v = int(val, 16)
            except ValueError:
                continue
            events.append({"t": float(ts), "type": etype, "name": name, "val": v})
    return events


def segments(events):
    """按 BTN_TOUCH DOWN/UP 切段，每段收集 (t, size, major, minor, x, y)"""
    segs = []
    cur = None
    maj = mnr = x = y = None
    for e in events:
        n, t, v = e["name"], e["t"], e["val"]
        if n == BTN_TOUCH:
            if v == 1:
                cur = {"t0": t, "samples": [], "xs": [], "ys": []}
                maj = mnr = x = y = None
            else:
                if cur is not None:
                    cur["t1"] = t
                    segs.append(cur)
                    cur = None
                continue
        if e["type"] == EV_ABS:
            if n == MAJOR:
                maj = v
            elif n == MINOR:
                mnr = v
            elif n == POS_X:
                x = v
            elif n == POS_Y:
                y = v
            if cur is not None and maj is not None and mnr is not None:
                # 同一个 SYN 周期里 major/minor 会分别到达，只在两者都有时记一次
                size = math.sqrt(maj * mnr)
                if not cur["samples"] or cur["samples"][-1][1] != size:
                    cur["samples"].append((t, size, maj, mnr))
                if x is not None:
                    cur["xs"].append(x)
                if y is not None:
                    cur["ys"].append(y)
    if cur is not None:
        cur["t1"] = cur["t0"]
        segs.append(cur)
    return segs


def ema(vals, alpha=0.25):
    out = []
    e = None
    for v in vals:
        e = v if e is None else e + alpha * (v - e)
        out.append(e)
    return out


def pctl(sorted_vals, p):
    if not sorted_vals:
        return 0.0
    if len(sorted_vals) == 1:
        return sorted_vals[0]
    pos = p * (len(sorted_vals) - 1)
    lo = int(pos)
    hi = min(lo + 1, len(sorted_vals) - 1)
    return sorted_vals[lo] + (sorted_vals[hi] - sorted_vals[lo]) * (pos - lo)


def analyze_seg(seg, idx, win=30):
    samples = seg["samples"]
    if not samples:
        return None
    sizes = [s[1] for s in samples]
    smooth = ema(sizes)
    t0 = seg["t0"]
    dur = seg.get("t1", t0) - t0
    xs, ys = seg["xs"], seg["ys"]
    moved = 0.0
    if len(xs) > 1:
        moved = math.hypot(xs[-1] - xs[0], ys[-1] - ys[0])

    # 模拟 v1.4.6：滚动窗口 P20 作为「近期静息水平」
    rest = None
    rises = []
    rest_series = []
    for k, v in enumerate(smooth):
        lo = max(0, k - win + 1)
        w = sorted(smooth[lo:k + 1])
        if len(w) >= win:
            rest = pctl(w, 0.20)
        rest_series.append(rest)
        if rest and rest > 0:
            rises.append((v - rest) / rest * 100.0)

    print(f"--- 段落 {idx}: {len(samples)} 样本, {dur:.2f}s, 位移 {moved:.0f}px ---")
    print(f"    raw  面积: min {min(sizes):.1f}  max {max(sizes):.1f}  "
          f"mean {sum(sizes)/len(sizes):.1f}")
    print(f"    major: min {min(s[2] for s in samples)}  max {max(s[2] for s in samples)}")
    print(f"    minor: min {min(s[3] for s in samples)}  max {max(s[3] for s in samples)}")
    if rises:
        rs = sorted(rises)
        print(f"    涨幅(v1.4.6判据): min {min(rises):+.0f}%  max {max(rises):+.0f}%  "
              f"p50 {pctl(rs,0.5):+.0f}%  p90 {pctl(rs,0.9):+.0f}%")
        print(f"    落下前8帧涨幅: {[f'{v:+.0f}%' for v in rises[:8]]}")
    step = max(1, len(smooth) // 16)
    print(f"    曲线: {' '.join(f'{smooth[k]:.1f}' for k in range(0, len(smooth), step))}")
    if rest_series and rest_series[-1]:
        rs2 = sorted(r for r in rest_series if r)
        print(f"    静息(P20)范围: {min(rs2):.1f} ~ {max(rs2):.1f}")
    print()
    return {"rises": rises, "sizes": sizes, "smooth": smooth, "dur": dur,
            "moved": moved, "rest": rest_series}


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tools/touch_capture.log"
    events = parse(path)
    print(f"共解析 {len(events)} 个事件")
    segs = segments(events)
    print(f"共 {len(segs)} 个按下段落\n")
    if not segs:
        print("没有完整的按下段落")
        return
    results = []
    for i, seg in enumerate(segs):
        r = analyze_seg(seg, i + 1)
        if r:
            results.append(r)

    print("=" * 70)
    print("汇总：每段的峰值涨幅（判「加力」最直接的指标）")
    for i, r in enumerate(results):
        if r["rises"]:
            print(f"  段落{i+1}: 峰值涨幅 {max(r['rises']):+.0f}%  "
                  f"p90 {pctl(sorted(r['rises']),0.9):+.0f}%  "
                  f"时长 {r['dur']:.1f}s  位移 {r['moved']:.0f}px")


if __name__ == "__main__":
    main()
