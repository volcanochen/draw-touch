#!/usr/bin/env python3
"""
扫描「参考窗口长度 × 就绪样本门槛 × 释放规则」的组合，
在真实采集数据 + 合成场景上比行为。

修正上一轮的方法错误：**就绪门槛不能等于窗口长度**。
否则窗口 > 段落总帧数时通道永远不就绪 → 该段「0 次触发」是假的
（P2 只有 36 帧，窗口 40 以上就永远不就绪）。

用法：python tools/press_grid.py [log]
"""
import sys

from analyze_touch import parse, pctl, segments
from press_compare import run, seg_sizes


def report(name, kw, segs, scenarios):
    row = []
    for seg in segs:
        d, u, info, _st = run(seg_sizes(seg), **kw)
        first = f"{info[0][0]}" if info else "-"
        row.append(f"下{len(d)}@{first}上{len(u)}")
    srow = []
    for sizes in scenarios.values():
        d, u, _i, _st = run(sizes, **kw)
        srow.append(f"{len(d)}/{len(u)}")
    return row, srow


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tools/touch_capture.log"
    events = parse(path)
    segs = segments(events)

    def ramp(base, top, frames):
        if frames <= 1:
            return [top]
        return [base + (top - base) * i / (frames - 1) for i in range(frames)]

    scenarios = {
        "轻触稳态": [10.0] * 120,
        "长按200帧": ramp(10, 17, 20) + [17.0] * 200,
        "渐进加力后松开": ramp(9.3, 16.8, 20) + ramp(16.8, 11.0, 12),
        "滑动中加力": [10.0] * 40 + ramp(11.8, 18.9, 40) + [10.0] * 30,
        "强落指瞬态": ramp(4.9, 17.0, 45) + [17.0] * 40,
        "松回静息": ramp(10, 17, 20) + [10.0] * 40,
    }

    print("真实段：" + " | ".join(f"P{i+1}" for i in range(len(segs))))
    print("场景  ：" + " | ".join(f"{k:>8}" for k in scenarios))
    print()

    print("=" * 104)
    print("表：窗口长度 × 就绪门槛（释放规则固定为『回到参考水平』= 阈值 ×1.05）")
    print("=" * 104)
    for window in (30, 45, 60, 90, 120):
        for ms in (8, 12, 16, 24):
            kw = dict(window=window, min_samples=ms, rel=-0.05, confirm=3)
            row, srow = report("", kw, segs, scenarios)
            print(f"  窗口{window:>4} 就绪{ms:>3}  " +
                  " | ".join(f"{x:<12}" for x in row) + "  ||  " +
                  " | ".join(f"{x:<8}" for x in srow))

    print()
    print("=" * 104)
    print("对照：释放规则的影响（窗口30/就绪12）—— rel>0 表示要跌破静息才释放")
    print("=" * 104)
    for rel, tag in ((0.12, "跌破12%(旧)"), (-0.05, "回到参考±5%(新)"), (0.0, "回到参考(严格)")):
        kw = dict(window=30, min_samples=12, rel=rel, confirm=3)
        row, srow = report("", kw, segs, scenarios)
        print(f"  {tag:<16} " + " | ".join(f"{x:<12}" for x in row) + "  ||  " +
              " | ".join(f"{x:<8}" for x in srow))

    print()
    print("=" * 104)
    print("对照：确认帧数的影响（窗口30/就绪12/释放=参考±5%）")
    print("=" * 104)
    for cf in (2, 3, 4, 5):
        kw = dict(window=30, min_samples=12, rel=-0.05, confirm=cf)
        row, srow = report("", kw, segs, scenarios)
        print(f"  确认{cf}帧            " + " | ".join(f"{x:<12}" for x in row) + "  ||  " +
              " | ".join(f"{x:<8}" for x in srow))


if __name__ == "__main__":
    main()
