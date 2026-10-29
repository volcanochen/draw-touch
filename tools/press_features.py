#!/usr/bin/env python3
"""
对比几种「接触面积」特征在真实采集数据上的抗噪性。

背景：这台设备**没有 ABS_MT_PRESSURE 轴**（采集日志里 0 条），
所以「重按」只能靠接触椭圆的尺寸来判。问题是用哪个尺寸最稳：

  geo   = sqrt(major × minor)   ← 当前实现（几何平均）
  major = 长轴
  minor = 短轴
  mean  = (major + minor) / 2

物理直觉：手指滑动时**会滚转/侧倾**，接触椭圆变「长而扁」——
短轴 minor 对侧倾最敏感 → 最脏；长轴 major 更接近「压得有多实」。

用法：python tools/press_features.py [log]
"""
import math
import sys

from analyze_touch import MAJOR, MINOR, ema, parse, pctl, segments

FEATURES = {
    "geo":   lambda m, n: math.sqrt(m * n),
    "major": lambda m, n: float(m),
    "minor": lambda m, n: float(n),
    "mean":  lambda m, n: (m + n) / 2.0,
}


def build(seg, feat):
    """→ (ema 序列, 每帧的 major/minor)"""
    f = FEATURES[feat]
    raw = [f(s[2], s[3]) for s in seg["samples"]]
    return ema(raw), raw


def rises(smooth, win=30, p=0.20):
    out = []
    for k, v in enumerate(smooth):
        lo = max(0, k - win + 1)
        w = sorted(smooth[lo:k + 1])
        if len(w) < win:
            out.append(None)
            continue
        rest = pctl(w, p)
        out.append((v - rest) / rest * 100.0 if rest > 0 else None)
    return out


def triggers(rs, thr, confirm=3, arm=30):
    """连续 confirm 帧超过 thr 才算一次触发；返回 (次数, 触发帧列表)"""
    downs, above = [], 0
    for k in range(arm, len(rs)):
        r = rs[k]
        if r is not None and r >= thr:
            above += 1
            if above >= confirm:
                downs.append(k)
                above = 0
        else:
            above = 0
    return len(downs), downs


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tools/touch_capture.log"
    events = parse(path)
    segs = segments(events)
    print(f"共 {len(segs)} 段\n")

    print("=" * 92)
    print("表1：各特征在 30% 阈值下的触发次数（越少越抗噪，但漏掉真加力更糟）")
    print("=" * 92)
    header = "特征      " + "".join(f"P{i+1:<6}" for i in range(len(segs)))
    print(header)
    series = {}
    for feat in FEATURES:
        row, rs_all = [], []
        for seg in segs:
            smooth, _ = build(seg, feat)
            rs = rises(smooth)
            rs_all.append(rs)
            n, _d = triggers(rs, 30.0)
            row.append(f"{n:<7}")
        series[feat] = rs_all
        print(f"{feat:<10}" + "".join(row))

    print()
    print("=" * 92)
    print("表2：各特征的涨幅分布（静息噪声 vs 加力信号能否分开）")
    print("=" * 92)
    for feat in FEATURES:
        allv = [r for rs in series[feat] for r in rs if r is not None]
        if not allv:
            continue
        q = {f"p{p}": pctl(sorted(allv), p / 100) for p in (25, 50, 75, 90, 95, 99)}
        print(f"{feat:<10}" + "  ".join(f"{k}={v:+6.0f}%" for k, v in q.items()))

    print()
    print("=" * 92)
    print("表3：各特征的阈值扫描（P1=长滑段，P2/P3=静止按压，P4=滑动中加力）")
    print("=" * 92)
    for feat in FEATURES:
        print(f"-- {feat} --")
        for thr in (25, 30, 35, 40, 45, 50, 60):
            row = []
            for rs in series[feat]:
                n, _ = triggers(rs, float(thr))
                row.append(f"{n}")
            print(f"   阈值{thr:>3}%  触发次数 " + "  ".join(f"P{i+1}={v}" for i, v in enumerate(row)))

    print()
    print("=" * 92)
    print("表4：P1（18 秒长滑）里 geo 特征的触发时刻与当时的涨幅")
    print("=" * 92)
    rs = series["geo"][0]
    smooth, _ = build(segs[0], "geo")
    n, downs = triggers(rs, 30.0)
    for d in downs:
        print(f"  第{d}帧: size={smooth[d]:.1f} 涨幅={rs[d]:+.0f}%")
    # 这几段的原始 major/minor，看看是「真压扁」还是「minor 抖动」
    print("  触发点前后各 3 帧的 major/minor：")
    for d in downs:
        for k in range(max(0, d - 3), min(len(segs[0]["samples"]), d + 4)):
            _t, _s, mj, mn = segs[0]["samples"][k]
            mark = " <<<" if k == d else ""
            print(f"    f{k:4d} major={mj:3d} minor={mn:3d} geo={math.sqrt(mj*mn):5.1f} "
                  f"涨幅={(rs[k] if rs[k] is not None else 0):+5.0f}%{mark}")
        print()


if __name__ == "__main__":
    main()
