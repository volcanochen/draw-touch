"""
高频轮询系统光标，抓「pan 期间光标是否被系统提升到触点、又被宿主钉回」。
宿主日志是 150ms 采一次，任何 <150ms 的偏移都会被漏掉；这里 5ms 采一次。

用法:
  python tests/cursor_jitter_poll.py --seconds 6
同时另开一个进程注入 pan（tests/sim_pan_probe.py），结束后看输出的极值与偏移序列。
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import time


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD),
                ("hCursor", wt.HANDLE), ("ptScreenPos", POINT)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=6.0)
    ap.add_argument("--period", type=float, default=0.005, help="采样周期秒")
    args = ap.parse_args()

    u = ctypes.WinDLL("user32", use_last_error=True)
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

    samples = []
    t0 = time.time()
    while time.time() - t0 < args.seconds:
        p = POINT()
        u.GetCursorPos(ctypes.byref(p))
        ci = CURSORINFO()
        ci.cbSize = ctypes.sizeof(CURSORINFO)
        u.GetCursorInfo(ctypes.byref(ci))
        samples.append((time.time() - t0, p.x, p.y, ci.flags, int(ci.hCursor or 0)))
        time.sleep(args.period)

    xs = [s[1] for s in samples]
    ys = [s[2] for s in samples]
    print(f"采样 {len(samples)} 次 / {args.seconds}s")
    print(f"X 范围 [{min(xs)}, {max(xs)}]  跨度 {max(xs)-min(xs)}px")
    print(f"Y 范围 [{min(ys)}, {max(ys)}]  跨度 {max(ys)-min(ys)}px")

    base = (xs[0], ys[0])
    moved = [s for s in samples if abs(s[1] - base[0]) > 1 or abs(s[2] - base[1]) > 1]
    print(f"相对起点偏移 >1px 的采样: {len(moved)} 次")
    for s in moved[:20]:
        print(f"  t={s[0]:.3f}s  ({s[1]},{s[2]})  flags=0x{s[3]:X} hCursor={s[4]}")

    hidden = [s for s in samples if not (s[3] & 0x1)]
    print(f"不可见（flags 无 SHOWING）的采样: {len(hidden)} 次")
    handles = sorted({s[4] for s in samples})
    print(f"出现过的光标句柄: {handles}")


if __name__ == "__main__":
    main()
