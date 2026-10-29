#!/usr/bin/env python3
"""
细分分析：把一次长时间连续触摸按「力度水平」自动切成若干子段落，
用来区分「轻触 / 普通按 / 用力按 / 压着滑 / 加力」几种状态。

思路：接触面积（EMA 平滑后）做一维聚类 —— 按面积高低分成若干档，
每档统计持续时长、移动量。这样能看出：
  - 各档的面积区间
  - 各档的面积波动幅度（判断「普通按压」和「用力按」能否分开）
  - 「压着滑」和「静止按」的区别（移动量）
"""
import math
import re
import sys

LINE = re.compile(r"^\[\s*([0-9.]+)\]\s+EV_(\w+)\s+(\S+)\s+([0-9a-fA-F]+)")
KEY_WORD = re.compile(r"^\[\s*([0-9.]+)\]\s+EV_KEY\s+(\S+)\s+(DOWN|UP)\s*$")

MAJOR, MINOR = "ABS_MT_TOUCH_MAJOR", "ABS_MT_TOUCH_MINOR"
POS_X, POS_Y = "ABS_MT_POSITION_X", "ABS_MT_POSITION_Y"
BTN_TOUCH = "BTN_TOUCH"


def parse(path):
    events = []
    with open(path, "r", errors="replace") as f:
        for line in f:
            line = line.strip()
            m = KEY_WORD.match(line)
            if m:
                ts, name, word = m.groups()
                events.append((float(ts), "KEY", name, 1 if word == "DOWN" else 0))
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
            events.append((float(ts), etype, name, v))
    return events


def ema(vals, alpha=0.25):
    out, e = [], None
    for v in vals:
        e = v if e is None else e + alpha * (v - e)
        out.append(e)
    return out


def pctl(sv, p):
    if not sv:
        return 0.0
    if len(sv) == 1:
        return sv[0]
    pos = p * (len(sv) - 1)
    lo = int(pos)
    hi = min(lo + 1, len(sv) - 1)
    return sv[lo] + (sv[hi] - sv[lo]) * (pos - lo)


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tools/touch_capture.log"
    events = parse(path)

    # 收集所有接触样本（跨段落，带 DOWN/UP 边界）
    samples = []   # (t, size, down)
    maj = mnr = None
    down = False
    for t, etype, name, v in events:
        if name == BTN_TOUCH:
            down = (v == 1)
            continue
        if name == MAJOR:
            maj = v
        elif name == MINOR:
            mnr = v
        if maj is not None and mnr is not None and down:
            samples.append((t, math.sqrt(maj * mnr)))
            maj = mnr = None

    if not samples:
        print("没有样本")
        return

    # 采样率
    dts = [samples[i + 1][0] - samples[i][0] for i in range(len(samples) - 1)]
    dts = sorted(d for d in dts if d > 0)
    if dts:
        med = dts[len(dts) // 2]
        print(f"样本 {len(samples)} 个, 中位采样间隔 {med*1000:.1f}ms "
              f"(≈{1/med:.0f}Hz)\n")

    sizes = [s[1] for s in samples]
    smooth = ema(sizes)

    # 用滑动窗口 P20 作为「近期静息水平」，算涨幅
    W = 30
    rest = None
    rises = []
    for k, v in enumerate(smooth):
        lo = max(0, k - W + 1)
        win = sorted(smooth[lo:k + 1])
        if len(win) >= W:
            rest = pctl(win, 0.20)
        rises.append(((v - rest) / rest * 100.0) if rest and rest > 0 else None)

    # ---- 自动分层：按涨幅把样本分档 ----
    # 找出涨幅的分位数作为档界
    valid = sorted(r for r in rises if r is not None)
    if not valid:
        print("样本太少")
        return
    print("涨幅分位数:", " ".join(
        f"p{q}={pctl(valid,q/100):+.0f}%" for q in (10, 25, 50, 75, 90, 95, 99)))

    # 逐帧打印：时间、面积、涨幅、是否持续
    print("\n=== 逐帧（每 5 帧抽样，带连续超阈值的计数）===")
    above = 0
    print(f"{'t(s)':>8} {'面积':>7} {'涨幅':>8} {'连续≥30%':>9}")
    for k in range(0, len(samples), 5):
        r = rises[k]
        rs = f"{r:+.0f}%" if r is not None else "  --"
        if r is not None and r >= 30:
            above += 1
        else:
            above = 0
        print(f"{samples[k][0]-samples[0][0]:8.2f} {smooth[k]:7.1f} {rs:>8} {above:9d}")


if __name__ == "__main__":
    main()
