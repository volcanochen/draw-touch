#!/usr/bin/env python3
"""
区分「真加力」与「手指滚转造成的接触形状变化」。

物理判据（互相独立的两条）：

1. **长短轴同向**：手指压得更重 → 接触椭圆整体变大，major 和 minor **同时**涨。
   手指在玻璃上滚转/侧倾 → 椭圆变「长而扁」，major 涨、minor 跌（或反之）。
   → 算「major 涨幅」和「minor 涨幅」：同号同向 = 压扁；反号 = 滚转。

2. **运动学**：人主动加力时手指多半会**顿一下/减速**（手臂施力时动作变僵硬）；
   纯滚转不会改变运动节奏。→ 算触发点前后的瞬时速度。

用法：python tools/press_kinematics.py [log]
"""
import math
import sys

from analyze_touch import ema, parse, pctl, segments


def analyze(seg, name):
    samples = seg["samples"]
    n = len(samples)
    major = [float(s[2]) for s in samples]
    minor = [float(s[3]) for s in samples]
    geo = [math.sqrt(s[2] * s[3]) for s in samples]
    xs, ys = seg["xs"], seg["ys"]

    # 每帧位移（用原始坐标，不做滤波）
    step = []
    for i in range(1, min(n, len(xs))):
        step.append(math.hypot(xs[i] - xs[i - 1], ys[i] - ys[i - 1]))
    step_ema = ema(step)

    # 复刻 App 的涨幅序列（geo + EMA + 滚动 P20）
    smooth = ema(geo)
    W = 30
    rest = None
    rises = []
    for k, v in enumerate(smooth):
        lo = max(0, k - W + 1)
        w = sorted(smooth[lo:k + 1])
        if len(w) >= W:
            rest = pctl(w, 0.20)
        rises.append((v - rest) / rest * 100.0 if rest else None)

    print(f"--- {name}: {n} 帧, 位移 {sum(step):.0f}px ---")
    print("   触发点判读：")
    print(f"   {'帧':>5} {'涨幅':>7} {'major':>7} {'minor':>7} "
          f"{'major涨幅':>10} {'minor涨幅':>10}  {'判定':>6}  {'速度px/帧':>9}")
    # 找连续超阈段
    k = 30
    while k < n:
        if rises[k] is not None and rises[k] >= 30:
            j = k
            while j + 1 < n and rises[j + 1] is not None and rises[j + 1] >= 30:
                j += 1
            peak = max(range(k, j + 1), key=lambda t: rises[t])
            # 以峰值帧前后 8 帧的均值做基线
            lo = max(0, peak - 12)
            hi = max(lo + 1, peak - 4)
            mj0 = sum(major[lo:hi]) / (hi - lo)
            mn0 = sum(minor[lo:hi]) / (hi - lo)
            mj1 = sum(major[peak:min(n, peak + 4)]) / max(1, min(4, n - peak))
            mn1 = sum(minor[peak:min(n, peak + 4)]) / max(1, min(4, n - peak))
            dmj = (mj1 - mj0) / max(mj0, 1e-6) * 100
            dmn = (mn1 - mn0) / max(mn0, 1e-6) * 100
            same = (dmj > 0) == (dmn > 0)
            spd = step_ema[min(peak - 1, len(step_ema) - 1)] if step_ema else 0
            avg_spd = sum(step_ema) / len(step_ema) if step_ema else 0
            kind = "压扁(真加力)" if same and dmj > 0 else ("滚转(形状变)" if not same else "变小")
            print(f"   {peak:>5} {rises[peak]:>+6.0f}% {major[peak]:>7.0f} {minor[peak]:>7.0f} "
                  f"{dmj:>+9.0f}% {dmn:>+9.0f}%  {kind:>10}  {spd:>6.2f}(均{avg_spd:.2f})")
            k = j + 1
        else:
            k += 1
    print()


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tools/touch_capture.log"
    events = parse(path)
    segs = segments(events)
    for i, seg in enumerate(segs):
        analyze(seg, f"段落{i+1}")


if __name__ == "__main__":
    main()
