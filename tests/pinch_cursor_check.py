#!/usr/bin/env python3
"""
回归测试：捏合缩放**不能动到光标**（2026-10-30 用户报「副屏捏合 → 指针消失、飞到右边主屏的右侧」）。

背景（实测结论，别再犯）：
  · 合成触摸（TouchZoom）的触点坐标是按**目标窗口所在显示器的局部坐标**解释的，
    旧代码把「虚拟桌面坐标」当局部坐标注入 → 副屏上注入 -1200 会被解释成 -1200-1920
    = -3120 → 越界 → Windows 把它夹到主屏右沿（物理 3072）→ 光标被系统拖走。
  · 触摸接触点活动期间，Windows 会**把光标藏起来**（CURSOR_SHOWING 变 false），
    抬起后也不自己恢复 → 用户看到「指针消失」。

现在 PC 端的做法：
  · 光标或前台窗口不在【主显示器】上 → 一律不注入触摸，改走 Ctrl+滚轮（缩放中心不变）；
  · 主显示器上注入时，每帧把光标钉回原位 + 强制把它叫回来（可见）。

测试做法：伪装手机连上宿主，发真实 MOUSE_ZOOM 帧，在【副屏】和【主屏】各捏一次，
断言「光标位置不变」且「光标始终可见」。

会碰桌面（真实移动鼠标 / 向前台窗口投递捏合手势），按规矩先弹横幅、结束再弹一次。
用法：python tests/pinch_cursor_check.py [host] [port]
"""
import ctypes
import ctypes.wintypes as wt
import socket
import struct
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tests"))
try:
    from desk_notify import notify
except Exception:  # 提示失败绝不能把测试搞挂
    def notify(*a, **k):
        print("[notify]", a)

u = ctypes.WinDLL("user32", use_last_error=True)


class TZPoint(ctypes.Structure):
    _fields_ = [("X", wt.LONG), ("Y", wt.LONG)]


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.LONG), ("hCursor", wt.HANDLE),
                ("ptScreenPos", TZPoint)]


u.GetCursorInfo.argtypes = [ctypes.POINTER(CURSORINFO)]
u.GetCursorPos.argtypes = [ctypes.POINTER(TZPoint)]
u.SetCursorPos.argtypes = [wt.LONG, wt.LONG]

TYPE_MOUSE = 0x07
MOUSE_ZOOM, MOUSE_FINGER = 6, 15


def cursor_pos():
    p = TZPoint()
    if not u.GetCursorPos(ctypes.byref(p)):
        return None
    return p.X, p.Y


def cursor_visible():
    ci = CURSORINFO()
    ci.cbSize = ctypes.sizeof(CURSORINFO)
    if not u.GetCursorInfo(ctypes.byref(ci)):
        return None
    return bool(ci.flags & 1)          # CURSOR_SHOWING


def frame(kind, x=0.0, y=0.0):
    payload = struct.pack("<fff", float(kind), float(x), float(y))
    return bytes([0x53, 0x50, 0x01, TYPE_MOUSE]) + struct.pack("<H", len(payload)) + payload


def pinch(host, port, seconds=1.5, factor=1.02):
    """发一轮真实捏合，返回 (期间最大偏移, 期间是否出现过隐藏)。"""
    s = socket.create_connection((host, port), timeout=5)
    try:
        s.sendall(frame(MOUSE_FINGER, 1.0, 0.0))     # 手指落在触摸板上
        time.sleep(0.12)
        base = cursor_pos()
        worst = 0
        hidden = False
        t0 = time.time()
        while time.time() - t0 < seconds:
            s.sendall(frame(MOUSE_ZOOM, factor, 0.0))
            time.sleep(0.05)
            pos = cursor_pos()
            if pos is None:                          # 桌面被锁/切换 → 本轮作废
                return None, None
            if base:
                worst = max(worst, abs(pos[0] - base[0]), abs(pos[1] - base[1]))
            if cursor_visible() is False:
                hidden = True
        s.sendall(frame(MOUSE_FINGER, 0.0, 0.0))     # 手指离开 → 结束缩放会话
        time.sleep(0.35)
        after = cursor_pos()
        if base and after:
            worst = max(worst, abs(after[0] - base[0]), abs(after[1] - base[1]))
        return worst, hidden
    finally:
        s.close()


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 6000
    try:
        socket.create_connection((host, port), timeout=3).close()
    except Exception as e:
        print("[跳过] 宿主没在跑（%s:%s 连不上：%s）" % (host, port, e))
        return 0

    orig = cursor_pos()
    if orig is None:
        # 屏幕被锁定 / 输入桌面不可用（GetCursorPos 返回 ACCESS_DENIED）：
        # 这时任何桌面交互都测不了 —— 必须**跳过**，绝不能报「通过」。
        print("[跳过] 输入桌面不可用（屏幕已锁定？），无法测捏合对光标的影响")
        return 0
    print("原始光标:", orig, "可见:", cursor_visible())
    notify("正在测试：捏合缩放是否会动到光标（会短暂移动鼠标、前台窗口可能缩放）", 40)
    failures = []
    skipped = False
    try:
        # ① 副屏（左侧 1920×1080 屏，坐标 x<0）
        u.SetCursorPos(-1200, 900)
        time.sleep(0.3)
        print("\n[副屏] 捏合前 光标=%s 可见=%s" % (cursor_pos(), cursor_visible()))
        worst, hidden = pinch(host, port)
        if worst is None:
            print("[副屏] 桌面中途不可用（锁屏？），本项作废")
            skipped = True
        else:
            print("[副屏] 最大偏移=%s px，期间隐藏过=%s" % (worst, hidden))
            if worst > 5:
                failures.append("副屏捏合把光标挪动了 %s px（应原地不动）" % worst)
            if hidden:
                failures.append("副屏捏合期间光标被系统藏起来了（应始终可见）")

        # ② 主屏（右侧主显示器，175% 缩放 → 逻辑坐标 1500 ≈ 物理 2625）
        u.SetCursorPos(1500, 900)
        time.sleep(0.3)
        print("\n[主屏] 捏合前 光标=%s 可见=%s" % (cursor_pos(), cursor_visible()))
        worst, hidden = pinch(host, port)
        if worst is None:
            print("[主屏] 桌面中途不可用（锁屏？），本项作废")
            skipped = True
        else:
            print("[主屏] 最大偏移=%s px，期间隐藏过=%s" % (worst, hidden))
            if worst > 5:
                failures.append("主屏捏合把光标挪动了 %s px（应原地不动）" % worst)
            if hidden:
                failures.append("主屏捏合期间光标被系统藏起来了（应始终可见）")
    finally:
        if orig:
            u.SetCursorPos(orig[0], orig[1])
        time.sleep(0.2)
        print("\n已恢复光标:", cursor_pos(), "可见:", cursor_visible())
        notify("测试完成，桌面已交还", 6, "ok" if not failures else "fail")

    if failures:
        print("\n=== 失败 ===")
        for f in failures:
            print("  ✗", f)
        return 1
    if skipped:
        print("\n=== 跳过：桌面中途不可用（锁屏），本轮不算通过 ===")
        return 0
    print("\n=== 通过：两种屏幕下捏合都没有动到光标，光标全程可见 ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
