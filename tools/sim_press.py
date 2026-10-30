#!/usr/bin/env python3
"""
用真实采集数据离线复刻 App 端的「重按」判定算法，逐帧输出触发时间线。

复刻的是 PhoneTablet/app/src/main/java/com/volcano/drawtouch/PressSignal.kt：
  1. size = sqrt(major × minor)，EMA(0.25) 平滑
  2. 近期静息水平 rest = 最近 N 帧的 P20（滚动窗口，跟着手指走）
  3. 通道仲裁：压力不可用（伪压感）→ 退化到面积通道
     面积通道可用条件：restReady && sizeMax > sizeMin × 1.03
  4. 触发：sizeEma ≥ rest × (1 + rise)，连续 confirm 帧
  5. 释放：sizeEma ≤ rest × (1 - release)，连续 release 帧

用法：
    python tools/sim_press.py tools/touch_capture.log
    python tools/sim_press.py tools/touch_capture.log --rise 0.40 --rest-window 20
    python tools/sim_press.py tools/touch_capture.log --sweep
"""
import argparse
import math
import sys

from analyze_touch import (MAJOR, MINOR, POS_X, POS_Y, BTN_TOUCH, ema,
                           parse, pctl, segments)

SIZE_EMA_ALPHA = 0.25
REST_PERCENTILE = 0.20
AREA_MIN_VARIATION = 0.03
PRESSURE_MIN_DELTA = 0.15
PRESSURE_DOWN = 0.55
NO_SAMPLE = float("inf")


def simulate(sizes, rise=0.30, release_ratio=0.4, rest_window=30,
             confirm=3, release_frames=2, probe=4, min_samples=None):
    """完全复刻 PressSignal 的逐帧逻辑，返回事件时间线。"""
    if min_samples is None:
        min_samples = rest_window

    size_ema = 0.0
    have_size = False
    size_min = NO_SAMPLE
    size_max = 0.0
    rest_level = 0.0
    rest_ready = False
    channel = "NONE"          # NONE / AREA
    dragging = False
    above = 0
    below = 0
    frames = 0

    buf = []
    events = []
    rises = []
    rest_series = []

    for raw in sizes:
        frames += 1
        size = raw
        if size > 0:
            size_ema = size if not have_size else size_ema + SIZE_EMA_ALPHA * (size - size_ema)
            have_size = True
            size_min = min(size_min, size_ema)
            size_max = max(size_max, size_ema)
            buf.append(size_ema)
            while len(buf) > rest_window:
                buf.pop(0)
            if len(buf) >= min_samples:
                rest_level = pctl(sorted(buf), REST_PERCENTILE)
                rest_ready = True
        baseline = rest_level

        # 压力通道：伪压感（恒定高压）→ 永远不可用
        if frames >= probe and channel == "NONE":
            if rest_ready and rest_level > 0 and size_max > 0 and size_min < NO_SAMPLE \
                    and size_max > size_min * (1 + AREA_MIN_VARIATION):
                channel = "AREA"
                events.append((frames, "ARM", f"面积通道就绪 rest={rest_level:.1f}"))

        rise_pct = None
        if baseline > 0:
            rise_pct = (size_ema - baseline) / baseline * 100.0
        rises.append(rise_pct)
        rest_series.append(baseline)

        if channel == "AREA":
            if not dragging:
                thr = baseline * (1 + rise)
                if baseline > 0 and size_ema >= thr:
                    above += 1
                else:
                    above = 0
                if above >= confirm:
                    dragging = True
                    above = 0
                    events.append((frames, "DOWN", f"size={size_ema:.1f} thr={thr:.1f} "
                                                  f"rise={rise_pct:+.0f}%"))
            else:
                thr = baseline * (1 - release_ratio * rise)
                if baseline > 0 and size_ema <= thr:
                    below += 1
                else:
                    below = 0
                if below >= release_frames:
                    dragging = False
                    below = 0
                    events.append((frames, "UP", f"size={size_ema:.1f} thr={thr:.1f}"))

    return {"events": events, "rises": rises, "rest": rest_series,
            "smooth": None, "raw": sizes}


def max_consecutive_run(rises, thr_pct, arm_frame):
    """从 arm_frame 起，最长连续「涨幅 ≥ thr」的帧数（决定会不会触发）。"""
    best = 0
    cur = 0
    for k in range(arm_frame, len(rises)):
        r = rises[k]
        if r is not None and r >= thr_pct:
            cur += 1
            best = max(best, cur)
        else:
            cur = 0
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log", nargs="?", default="tools/touch_capture.log")
    ap.add_argument("--rise", type=float, default=0.30)
    ap.add_argument("--rest-window", type=int, default=30)
    ap.add_argument("--min-samples", type=int, default=None)
    ap.add_argument("--confirm", type=int, default=3)
    ap.add_argument("--release-frames", type=int, default=2)
    ap.add_argument("--timeline", action="store_true", help="打印逐帧涨幅")
    ap.add_argument("--sweep", action="store_true", help="扫描阈值 → 每段触发次数")
    ap.add_argument("--windows", action="store_true", help="扫描静息窗口长度 → 每段触发次数")
    args = ap.parse_args()

    events = parse(args.log)
    segs = segments(events)
    print(f"共 {len(segs)} 个按下段落  (rise={args.rise:.0%}, window={args.rest_window}, "
          f"confirm={args.confirm}, min_samples={args.min_samples})")
    print()

    seg_results = []
    for i, seg in enumerate(segs):
        sizes = [s[1] for s in seg["samples"]]
        if not sizes:
            continue
        res = simulate(sizes, rise=args.rise, rest_window=args.rest_window,
                       confirm=args.confirm, release_frames=args.release_frames,
                       min_samples=args.min_samples)
        xs, ys = seg["xs"], seg["ys"]
        moved = math.hypot(xs[-1] - xs[0], ys[-1] - ys[0]) if len(xs) > 1 else 0
        dur = seg.get("t1", seg["t0"]) - seg["t0"]
        arm = next((e[0] for e in res["events"] if e[1] == "ARM"), None)
        downs = [e for e in res["events"] if e[1] == "DOWN"]
        ups = [e for e in res["events"] if e[1] == "UP"]
        print(f"--- 段落 {i+1}: {len(sizes)} 帧, {dur:.2f}s, 位移 {moved:.0f}px ---")
        print(f"    raw 面积 {min(sizes):.1f}~{max(sizes):.1f}  "
              f"就绪帧 {arm if arm else '从未就绪'}")
        print(f"    触发 DOWN × {len(downs)}   松开 UP × {len(ups)}")
        for f, kind, msg in res["events"]:
            print(f"      第{f:4d}帧 {kind:4s} {msg}")
        # 最长连续超阈帧数
        if arm:
            run = max_consecutive_run(res["rises"], args.rise * 100, arm - 1)
            print(f"    最长连续超阈帧数 = {run} (需 ≥{args.confirm} 才触发)")
        if args.timeline:
            print("    逐帧涨幅:", " ".join(
                ("--" if r is None else f"{r:+.0f}") for r in res["rises"]))
        print()
        seg_results.append((i + 1, len(sizes), dur, moved, downs, ups, arm))

    if args.sweep:
        print("=" * 78)
        print("阈值扫描：每个段落触发几次")
        for rise in (0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.60):
            row = []
            for i, seg in enumerate(segs):
                sizes = [s[1] for s in seg["samples"]]
                if not sizes:
                    continue
                res = simulate(sizes, rise=rise, rest_window=args.rest_window,
                               confirm=args.confirm, release_frames=args.release_frames,
                               min_samples=args.min_samples)
                d = len([e for e in res["events"] if e[1] == "DOWN"])
                row.append(f"P{i+1}:{d}")
            print(f"  rise={rise:.0%}  " + "  ".join(row))

    if args.windows:
        print()
        print("=" * 78)
        print("静息窗口长度扫描（rise 固定，沿用滞回锁存 = App 真实行为）")
        print("  窗口越长 → 参考水平越稳 → 自然起伏越不像『加力』，")
        print("  但真加力也会被窗口吸收（窗口 > 加力时长就漏）")
        print()
        print(f"  {'窗口':>6} {'帧数@41Hz':>9}   " + "  ".join(f"P{i+1}" for i in range(len(segs))))
        for win in (15, 20, 30, 40, 50, 60, 80, 100, 120, 160):
            row = []
            for seg in segs:
                sizes = [s[1] for s in seg["samples"]]
                if not sizes:
                    continue
                res = simulate(sizes, rise=args.rise, rest_window=win,
                               confirm=args.confirm, release_frames=args.release_frames,
                               min_samples=args.min_samples)
                d = len([e for e in res["events"] if e[1] == "DOWN"])
                row.append(str(d))
            print(f"  {win:>6} {win/41.0:>8.2f}s   " + "  ".join(f"{v:>3}" for v in row))


if __name__ == "__main__":
    main()
