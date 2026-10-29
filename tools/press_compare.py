#!/usr/bin/env python3
"""
对比两代「重按」判据在真实采集数据 + 合成场景上的行为。

【旧 = v1.4.6 现状】
  参考水平 = 最近 30 帧的 P20（每帧都更新）
  触发 = size ≥ ref × (1+30%)，连续 3 帧
  释放 = size ≤ ref × (1-12%)，连续 2 帧

【新 = 候选方案】
  a) **按住期间冻结参考水平**：dragging=true 时不再更新 ref。
     理由：一次加力持续 0.5~1s，30 帧窗口(0.73s)会被加力本身填满 →
     参考水平追着涨上去 → 「按住」自己把自己解除了。
     冻结后参考水平代表「按下去之前的力度」，长按多久都不会松。
  b) **落指爬坡不计入参考**：手指刚落下时接触面积从「几乎没接触」长到「压实」，
     这段（实测最长约 45 帧）是瞬态，不是有效的静息水平。
     跳过它，参考水平才不会偏低 → 阈值偏高 → 早期加力能立刻被认出来。
  c) **通道就绪只要求够样本**：不必等窗口填满。

用法：python tools/press_compare.py [log]
"""
import math
import sys

from analyze_touch import ema, parse, pctl, segments

SIZE_EMA_ALPHA = 0.25
REST_PCTL = 0.20


def pctl_list(vals, p):
    return pctl(sorted(vals), p)


def run(sizes, *, rise=0.30, rel=0.12, window=30, confirm=3, release_frames=2,
        freeze_ref=False, ramp_skip=0, min_samples=None, pct=REST_PCTL,
        rest_window_cap=None, release_mode="ref",
        trend_delay=0, trend_eps=0.03):
    """返回 (DOWN 帧列表, UP 帧列表, 每次触发时的涨幅, 粘键帧数)。

    release_mode:
      "ref"      → 释放条件 = size ≤ ref × (1 - rel)   （rel<0 表示「回到参考水平之上一点」）
      "trigger"  → 释放条件 = size < 触发阈值 × 0.95   （面积回落到触发线以下 = 这次加力结束了）

    trend_delay > 0 时启用**趋势守卫**：
      只把「相比 trend_delay 帧前没有在上涨」的帧喂给参考水平窗口。
      理由 —— 参考水平要代表「手指搁着不使劲时的水平」，
      而**任何上冲都会污染它**：落指瞬间接触面积从极小长到压实（是上冲），
      加力过程也是上冲。两者都会让参考水平偏低 → 阈值偏低 → 乱触发。
      挡住上冲后，参考水平只会被「稳定/回落」的帧喂 → 阈值永远高于静息水平。
    """
    if min_samples is None:
        min_samples = window
    size_ema, have = 0.0, False
    buf = []
    hist = []          # sizeEma 的历史（取 trend_delay 帧前那个）
    ref = 0.0
    armed = False
    dragging = False
    above = below = 0
    downs, ups, info = [], [], []
    stuck = 0          # 「按着但力度已经回落」的帧数 = 粘键的量化指标
    for k, raw in enumerate(sizes):
        if raw > 0:
            size_ema = raw if not have else size_ema + SIZE_EMA_ALPHA * (raw - size_ema)
            have = True
            hist.append(size_ema)
            rising = True          # 开头几帧无法判断趋势 → 一律当作「上升」不喂参考
            if trend_delay > 0 and len(hist) > trend_delay:
                prev = hist[-1 - trend_delay]
                rising = size_ema > prev * (1 + trend_eps)
            # 参考水平：落指爬坡不计入 + 上冲不计入 + 按住期间只降不升
            if not (freeze_ref and dragging) and k >= ramp_skip and not rising:
                buf.append(size_ema)
                cap = rest_window_cap or window
                while len(buf) > cap:
                    buf.pop(0)
                if len(buf) >= min_samples:
                    cand = pctl_list(buf, pct)
                    if dragging and freeze_ref:
                        # 按住期间：参考水平**只降不升**。
                        # 理由：一次加力可能持续好几秒，若参考水平跟着往上爬，
                        # 「按住」就会被自己解除（长按拖拽中途弹开）。
                        if cand < ref:
                            ref = cand
                    else:
                        ref = cand
                    armed = True
        if not armed or ref <= 0:
            continue
        if not dragging:
            thr = ref * (1 + rise)
            if size_ema >= thr:
                above += 1
            else:
                above = 0
            if above >= confirm:
                dragging = True
                above = 0
                downs.append(k)
                info.append((k, (size_ema - ref) / ref * 100))
        else:
            if release_mode == "trigger":
                thr = ref * (1 + rise) * 0.95
                hit = size_ema < thr
            else:
                thr = ref * (1 - rel)
                hit = size_ema <= thr
            # 粘键指标：已经判成「按住」，但力度已经掉回触发线以下了
            if size_ema < ref * (1 + rise) * 0.9:
                stuck += 1
            if hit:
                below += 1
            else:
                below = 0
            if below >= release_frames:
                dragging = False
                below = 0
                ups.append(k)
    return downs, ups, info, stuck


def seg_sizes(seg):
    return [s[1] for s in seg["samples"]]


def fmt(lst):
    return ",".join(str(x) for x in lst) if lst else "-"


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tools/touch_capture.log"
    events = parse(path)
    segs = segments(events)
    print(f"真实数据 {len(segs)} 段\n")

    variants = [
        ("旧 v1.4.6 (窗口30, 每帧更新)", dict()),
        ("新: 冻结参考", dict(freeze_ref=True)),
        ("新: 冻结参考 + 跳过落指8帧 + 样本门槛16", dict(freeze_ref=True, ramp_skip=8, min_samples=16)),
        ("新: 冻结参考 + 跳过落指12帧 + 样本门槛16", dict(freeze_ref=True, ramp_skip=12, min_samples=16)),
        ("新: 冻结参考 + 跳过落指12帧 + 样本门槛12", dict(freeze_ref=True, ramp_skip=12, min_samples=12)),
        ("新: 冻结参考 + 跳过落指12帧 + 样本门槛16 + 窗口40", dict(freeze_ref=True, ramp_skip=12, min_samples=16, window=40)),
        ("新: 冻结参考 + 跳过落指12帧 + 样本门槛16 + P30", dict(freeze_ref=True, ramp_skip=12, min_samples=16, pct=0.30)),
    ]

    print("=" * 100)
    print("表A：真实数据上的触发/释放（越早触发越跟手；误触发看 P1）")
    print("=" * 100)
    for name, kw in variants:
        row = []
        for seg in segs:
            d, u, info = run(seg_sizes(seg), **kw)
            first = f"{info[0][0]}(+{info[0][1]:.0f}%)" if info else "-"
            row.append(f"下{len(d)}[{first}] 上{len(u)}")
        print(f"{name:<44} " + " | ".join(f"{x:<18}" for x in row))

    print()
    print("=" * 100)
    print("表B：合成场景（检验『长按会不会自己松开』『早期加力能不能认出来』）")
    print("=" * 100)

    def ramp(base, top, frames):
        """从 base 线性爬到 top"""
        if frames <= 1:
            return [top]
        return [base + (top - base) * i / (frames - 1) for i in range(frames)]

    scenarios = {
        # 轻触落指后稳定在 10，模拟「压着滑」：不该触发
        "轻触稳态(10,100帧)": [10.0] * 100,
        # 落下即缓慢加力到 17 并保持 200 帧：应该触发一次，且**一直保持**
        "落指后加力并长按": ramp(10, 17, 20) + [17.0] * 200,
        # 落指后渐进加力（0.46s）再松开：应该触发 + 释放
        "渐进加力0.46s后松开": ramp(9.3, 16.8, 20) + ramp(16.8, 12.0, 10),
        # 滑动中加力：稳态 10 → 涨到 18.9 → 回落
        "滑动中加力": [10.0] * 40 + ramp(11.8, 18.9, 40) + [10.0] * 30,
        # 强落指瞬态：落下就一路涨到 17（手指压实）——这是落指瞬态，不该误判成加力
        "强落指瞬态(4.9→17)": ramp(4.9, 17.0, 45) + [17.0] * 40,
    }

    for name, kw in variants:
        row = []
        for sname, sizes in scenarios.items():
            d, u, info = run(sizes, **kw)
            row.append(f"下{len(d)}上{len(u)}")
        print(f"{name:<44} " + " | ".join(f"{x:<10}" for x in row))
    print()
    print("场景顺序：" + " | ".join(scenarios.keys()))


if __name__ == "__main__":
    main()
