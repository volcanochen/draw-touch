#!/usr/bin/env python3
"""
最终候选参数逐项确认 —— 这份输出就是 Kotlin 单测要复刻的「回归基线」。

用法：python tools/press_final.py [log]
"""
import sys

from analyze_touch import parse, segments
from press_compare import run, seg_sizes
from press_search import SCEN, noise, ramp


def show(tag, kw, segs):
    print(f"── {tag}")
    print(f"   参数 {kw}")
    allok = True
    for name, (sizes, wd, wu) in SCEN.items():
        d, u, _i, st = run(sizes, **kw)
        flag = "✅" if (len(d) == wd and len(u) == wu and st == 0) else "❌"
        if flag == "❌":
            allok = False
        print(f"   {flag} {name:<20} 下{len(d)} 上{len(u)} 粘键{st}帧  (期望 下{wd} 上{wu})")
    for i, seg in enumerate(segs):
        d, u, info, st = run(seg_sizes(seg), **kw)
        first = f"首次 f{info[0][0]} (+{info[0][1]:.0f}%)" if info else "无触发"
        print(f"   {'  '} P{i+1:<19} 下{len(d)} 上{len(u)} 粘键{st}帧  {first}")
    print(f"   → 场景全过：{'是' if allok else '否'}")
    print()


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tools/touch_capture.log"
    events = parse(path)
    segs = segments(events)

    base = dict(window=30, min_samples=8, ramp_skip=0, pct=0.20, freeze_ref=True,
                release_mode="trigger", rel=-0.05, confirm=3)
    cands = [
        ("A 窗口30 阈值25%", dict(base, rise=0.25)),
        ("B 窗口30 阈值30%", dict(base, rise=0.30)),
        ("C 窗口45 阈值30%", dict(base, window=45, rise=0.30)),
        ("D 窗口45 阈值25%", dict(base, window=45, rise=0.25)),
        ("E 窗口30 阈值30% 跳落指14帧", dict(base, ramp_skip=14, rise=0.30)),
        ("F 窗口45 阈值30% 跳落指14帧", dict(base, window=45, ramp_skip=14, rise=0.30)),
        ("G 窗口45 阈值35% 跳落指14帧", dict(base, window=45, ramp_skip=14, rise=0.35)),
        ("H 窗口45 阈值40% 跳落指14帧", dict(base, window=45, ramp_skip=14, rise=0.40)),
    ]
    for tag, kw in cands:
        show(tag, kw, segs)

    old = dict(window=30, min_samples=30, ramp_skip=0, pct=0.20, freeze_ref=False,
               release_mode="ref", rel=0.12, confirm=3, rise=0.30)
    show("线上 v1.4.6（对照）", old, segs)


if __name__ == "__main__":
    main()
