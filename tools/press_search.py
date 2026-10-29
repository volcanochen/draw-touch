#!/usr/bin/env python3
"""
为「重按」判据做参数搜索：在真实采集数据 + 合成场景集上打分，挑出同时满足所有性质的组合。

评分里的每条性质都来自实测，不是拍脑袋：

  T1 轻触稳态(10)        → 一次都不该触发      （手指只是压着滑，不该乱按左键）
  T2 噪声稳态(10±0.6)    → 一次都不该触发      （压着滑时的自然起伏）
  T3 长按 200 帧          → 触发且**一直保持**    （按住拖拽不能自己松开）
  T4 渐进加力后松回静息   → 触发 + 释放          （松手必须能松开，否则粘键）
  T5 滑动中加力后松开     → 触发 + 释放
  T6 落指压实(4.9→17)    → 触发 + 保持           （落下就压实 = 一次真实的加力）
  T7 压着滑 + 一次加力    → 触发 + 释放
  T8 P2 静止加力（真实）  → 尽早触发（<25 帧；v1.4.6 要 31 帧）
  T9 P4 滑动中加力（真实）→ 尽早触发（<100 帧；v1.4.6 要 138 帧 ≈ 3.4s）
  T10 P1 长滑（真实）     → 触发少 + **不能粘键**（按住但力度已回落的帧数必须为 0）

用法：python tools/press_search.py [log]
"""
import itertools
import sys

from analyze_touch import parse, segments
from press_compare import run, seg_sizes


def ramp(base, top, frames):
    if frames <= 1:
        return [top]
    return [base + (top - base) * i / (frames - 1) for i in range(frames)]


def noise(base, amp, n, seed=7):
    """确定性伪随机起伏（不用 random，保证每次跑分一致）"""
    out = []
    x = 12345
    for i in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        out.append(base + amp * ((x / 0x7FFFFFFF) * 2 - 1))
    return out


SCEN = {
    # 名称: (序列, 期望触发次数, 期望释放次数)
    "T1轻触稳态": ([10.0] * 120, 0, 0),
    "T2噪声稳态": (noise(10.0, 0.6, 120), 0, 0),
    # 稳下来之后加力并一直按住 → 触发且**一直保持**（按住拖拽不能自己松开）
    "T3稳态后长按200帧": ([10.0] * 40 + ramp(10, 17, 25) + [17.0] * 200, 1, 0),
    # 加力后松回静息 → 触发 + 释放（松手必须能松开，否则粘键）
    "T4加力后松回静息": ([10.0] * 40 + ramp(10, 17, 25) + ramp(17, 10, 25), 1, 1),
    "T5滑动加力后松开": ([10.0] * 40 + ramp(11.8, 18.9, 40) + ramp(18.9, 10, 25), 1, 1),
    # 落指压实 = 手指刚落下时压平（**不是**主动加力）。
    # 每次触摸都会经历落指，落指若触发就是灾难 → 必须不触发。
    "T6落指压实": (ramp(4.9, 17.0, 45) + [17.0] * 40, 0, 0),
    "T7滑中一次加力": ([10.0] * 60 + ramp(10, 17, 20) + [17.0] * 20 + ramp(17, 10, 25), 1, 1),
    "T8稳态后加力": ([10.0] * 40 + ramp(10, 17, 25) + ramp(17, 10, 25), 1, 1),
    # **已知取舍**：落下瞬间就使劲 → 与落指瞬态同形，信号上不可区分 → 宁可漏也不错触。
    # （宁可漏：漏了用户再按一次；错触：每次触摸都误按左键，直接不可用）
    "T9落下就使劲(取舍:不触发)": (ramp(4.9, 17.0, 45) + [17.0] * 60, 0, 0),
}


def evaluate(kw, segs):
    """返回 (总分, 明细列表)"""
    s, detail = 0, []
    for name, (sizes, wd, wu) in SCEN.items():
        d, u, _i, st = run(sizes, **kw)
        ok = (len(d) == wd and len(u) == wu and st == 0)
        s += 12 if ok else 0
        detail.append((name, len(d), len(u), st, wd, wu, ok))
    # ---- 真实数据 ----
    d1, u1, i1, st1 = run(seg_sizes(segs[0]), **kw)
    d2, u2, i2, st2 = run(seg_sizes(segs[1]), **kw)
    d3, u3, i3, st3 = run(seg_sizes(segs[2]), **kw)
    d4, u4, i4, st4 = run(seg_sizes(segs[3]), **kw)
    ok8 = len(d2) >= 1 and i2 and i2[0][0] <= 25
    s += 12 if ok8 else 0
    detail.append(("T8P2尽早", len(d2), len(u2), st2, ">=1", "<=25帧", ok8))
    ok9 = len(d4) >= 1 and i4 and i4[0][0] <= 100
    s += 12 if ok9 else 0
    detail.append(("T9P4尽早", len(d4), len(u4), st4, ">=1", "<=100帧", ok9))
    # T10：P1 触发少（**软指标**：P1 的起伏与真实加力在面积信号上完全同形，
    #      物理上不可分 —— 所以只作偏好，不作硬性要求）+ 不粘键
    ok10 = len(d1) <= 3 and st1 == 0
    s += 8 if (len(d1) <= 2 and st1 == 0) else (4 if ok10 else 0)
    detail.append(("T10P1少且不粘", len(d1), len(u1), st1, "<=2(软<=3)", "粘键0帧", ok10))
    ok11 = len(d3) >= 1 and st3 == 0
    s += 8 if ok11 else 0
    detail.append(("T11P3触发不粘", len(d3), len(u3), st3, ">=1", "粘键0帧", ok11))
    return s, detail


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tools/touch_capture.log"
    events = parse(path)
    segs = segments(events)

    space = {
        "window": [30, 45, 60],
        "min_samples": [8, 12, 16, 24],
        "ramp_skip": [0, 8, 14, 20],
        "pct": [0.20, 0.30, 0.50],
        "freeze_ref": [False, True],
        "release_mode": ["ref", "trigger"],
        "rel": [-0.05, 0.0, 0.12],
        "confirm": [2, 3, 4],
        "rise": [0.25, 0.30, 0.35],
    }
    keys = list(space)
    best = []
    for combo in itertools.product(*[space[k] for k in keys]):
        kw = dict(zip(keys, combo))
        if kw["release_mode"] == "trigger" and kw["rel"] != -0.05:
            continue          # trigger 模式不看 rel，跳过重复组合
        s, _ = evaluate(kw, segs)
        best.append((s, kw))
    best.sort(key=lambda x: -x[0])
    maxs = 12 * 7 + 12 * 4 + 8
    print(f"共搜索 {len(best)} 组参数组合，满分 {maxs}")
    print()
    print("=" * 118)
    print("前 15 名（附真实段的触发数 P1/P2/P3/P4 与粘键帧）")
    print("=" * 118)
    for s, kw in best[:15]:
        rs = []
        for seg in segs:
            d, u, _i, st = run(seg_sizes(seg), **kw)
            rs.append(f"P{segs.index(seg)+1}:{len(d)}下{len(u)}上粘{st}")
        print(f"  {s:>4}/{maxs}  " + "  ".join(f"{k}={kw[k]}" for k in keys))
        print(f"          {'  '.join(rs)}")

    print()
    print("=" * 118)
    print("冠军组合逐项明细")
    print("=" * 118)
    s, kw = best[0]
    print(f"  参数：{kw}")
    _s, detail = evaluate(kw, segs)
    for name, d, u, st, wd, wu, ok in detail:
        print(f"    {'✅' if ok else '❌'} {name:<20} 下{d} 上{u} 粘键{st}帧   期望 下{wd} 上{wu}")

    print()
    print("=" * 118)
    print("当前线上 v1.4.6（窗口30/就绪30/P20/不冻结/跌破12%/确认3/阈值30%）")
    print("=" * 118)
    old = dict(window=30, min_samples=30, pct=0.20, freeze_ref=False,
               release_mode="ref", rel=0.12, confirm=3, rise=0.30)
    s2, detail2 = evaluate(old, segs)
    print(f"  分数 {s2} / {maxs}")
    for name, d, u, st, wd, wu, ok in detail2:
        print(f"    {'✅' if ok else '❌'} {name:<20} 下{d} 上{u} 粘键{st}帧   期望 下{wd} 上{wu}")

    print()
    print("=" * 118)
    print("差异最大的几项（冠军 vs 线上）")
    print("=" * 118)
    for (n1, d1_, u1_, st1_, wd1_, wu1_, ok1), (n2, d2_, u2_, st2_, wd2_, wu2_, ok2) in zip(detail, detail2):
        if ok1 != ok2:
            print(f"    {n1:<20} 线上 下{d2_}/上{u2_}/粘{st2_}  →  冠军 下{d1_}/上{u1_}/粘{st1_}")


if __name__ == "__main__":
    main()
