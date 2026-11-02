#!/usr/bin/env python3
"""
两项取证（2026-11-01，触摸注入平移）：

  ① 长距离平移（120 帧）时，光标偏移是「固定跳一下」还是「跟着触点持续走」？
     —— 决定要不要在平移期间做「仅当用户没动鼠标时」的复位。

  ② 抬手后光标被系统藏住，哪种方式能真正叫醒它？
     逐种方法单独验证（每种前先确认光标是隐藏的），不靠猜：
       A SendInput 相对 +1 米奇
       B SendInput 相对 +4 米奇
       C SetCursorPos 同点（+1 再回原位）
       D SetCursorPos 到相邻像素再回原位
       E SendInput 绝对定位到当前点

会真实注入触摸并移动光标；脚本自己弹开始/结束横幅。
用法：python tests/pan_cursor_wake_probe.py [host] [port]
"""
import ctypes
import re
import socket
import struct
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tests"))
try:
    from desk_notify import notify
except Exception:
    def notify(*a, **k):
        print("[notify]", a)

LOG_CANDIDATES = [
    ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "DrawTouchHost.log",
    ROOT / "PcHost" / "publish_test" / "DrawTouchHost.log",
    ROOT / "PcHost" / "publish" / "DrawTouchHost.log",
]

T_HELLO, T_MOUSE = 0x05, 0x07
MOUSE_FINGER, MOUSE_PAN = 15, 17
PV = 4
PAN_TOUCH = 1

user32 = ctypes.windll.user32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint), ("flags", ctypes.c_uint),
                ("hCursor", ctypes.c_void_p), ("ptScreenPos", POINT)]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long), ("mouseData", ctypes.c_uint),
                ("dwFlags", ctypes.c_uint), ("time", ctypes.c_uint), ("dwExtraInfo", ctypes.c_void_p)]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT)]

    _anonymous_ = ("u",)
    _fields_ = [("type", ctypes.c_uint), ("u", _U)]


def cursor():
    p = POINT()
    return (p.x, p.y) if user32.GetCursorPos(ctypes.byref(p)) else None


def visible():
    ci = CURSORINFO()
    ci.cbSize = ctypes.sizeof(CURSORINFO)
    if not user32.GetCursorInfo(ctypes.byref(ci)):
        return None
    return bool(ci.flags & 0x1)


def send_move(dx, dy=0):
    inp = INPUT()
    inp.type = 0
    inp.mi.dx, inp.mi.dy = dx, dy
    inp.mi.dwFlags = 0x0001
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def send_abs(x, y):
    """绝对定位到虚拟桌面上的 (x,y)（0..65535 归一化）。"""
    vx, vy = user32.GetSystemMetrics(76), user32.GetSystemMetrics(77)
    vw, vh = user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)
    nx = int(round((x - vx) * 65535.0 / max(1, vw - 1)))
    ny = int(round((y - vy) * 65535.0 / max(1, vh - 1)))
    inp = INPUT()
    inp.type = 0
    inp.mi.dx, inp.mi.dy = nx, ny
    inp.mi.dwFlags = 0x0001 | 0x8000 | 0x4000   # MOVE | ABSOLUTE | VIRTUALDESK
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def mouse_frame(kind, x=0.0, y=0.0):
    payload = struct.pack("<fff", float(kind), float(x), float(y))
    return bytes([0x53, 0x50, 0x01, T_MOUSE]) + struct.pack("<H", len(payload)) + payload


def hello(pan_mode=PAN_TOUCH):
    payload = struct.pack("<9f", 1080.0, 2400.0, 1.0, 1.0, 0.0, 0.0, 1.0, float(PV), float(pan_mode))
    return bytes([0x53, 0x50, 0x01, T_HELLO]) + struct.pack("<H", len(payload)) + payload


def host_log():
    best, best_t = None, -1.0
    for p in LOG_CANDIDATES:
        if p.exists() and p.stat().st_mtime > best_t:
            best, best_t = p, p.stat().st_mtime
    return best


def log_since(p, size):
    with open(p, "rb") as f:
        f.seek(size)
        return f.read().decode("utf-8", errors="ignore")


def pan(s, frames, step_dx=-0.008, interval=0.02):
    s.sendall(mouse_frame(MOUSE_FINGER, 1.0, 0.0))
    time.sleep(0.15)
    for _ in range(frames):
        s.sendall(mouse_frame(MOUSE_PAN, step_dx, 0.0))
        time.sleep(interval)
    s.sendall(mouse_frame(MOUSE_FINGER, 0.0, 0.0))


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 6000
    try:
        socket.create_connection((host, port), timeout=3).close()
    except Exception as e:
        print("[跳过] 宿主没在跑：%s" % e)
        return 0
    logp = host_log()
    if logp is None or cursor() is None:
        print("[跳过] 找不到日志 / 读不到光标")
        return 0

    notify("正在测试：平移光标偏移与叫醒方式探针（会注入触摸、移动光标，约 20 秒）", 40)
    s = socket.create_connection((host, port), timeout=5)
    s.sendall(hello(PAN_TOUCH))
    time.sleep(0.8)

    # ---------- ① 长距离平移：偏移是固定跳变还是持续跟随 ----------
    print("\n[①] 120 帧长距离平移（不做任何鼠标动作）")
    c0 = cursor()
    sz = logp.stat().st_size
    pan(s, 120)
    time.sleep(1.2)
    c1 = cursor()
    dev = re.findall(r"光标净位移最大\s*(\d+)px", log_since(logp, sz))
    print("    起点 %s → 终点 %s   合计位移 (%+d,%+d)   宿主采样最大偏移=%s"
          % (c0, c1, c1[0] - c0[0], c1[1] - c0[1], dev[-1] if dev else "?"))
    print("    触点共平移约 %d px" % int(120 * 0.008 * 360))

    # ---------- ② 抬手后叫醒方式 ----------
    print("\n[②] 抬手后各叫醒方式（每种前先确认光标确实隐藏）")
    methods = [
        ("A 相对+1", lambda: send_move(1, 0)),
        ("B 相对+4", lambda: send_move(4, 0)),
        ("C SetCursorPos 同点", lambda: user32.SetCursorPos(*cursor())),
        ("D 相邻像素往返", lambda: (lambda p: (user32.SetCursorPos(p[0] + 1, p[1]),
                                          user32.SetCursorPos(p[0], p[1])))(cursor())),
        ("E 绝对定位同点", lambda: send_abs(*cursor())),
    ]
    results = []
    for name, fn in methods:
        # 先制造一次「接触后隐藏」：跑一小段平移让系统把光标藏起来
        before = cursor()
        pan(s, 10)
        time.sleep(1.2)          # 等宿主尾巴期结束，避免它替我们叫醒
        hidden = visible()
        if hidden is None:
            print("    读不到光标状态，跳过")
            break
        if hidden:
            print("    %-18s 前：光标仍可见（本轮没藏住，结论不可用）" % name)
            results.append((name, None))
            continue
        fn()
        time.sleep(0.25)
        after = visible()
        after_pos = cursor()
        drift = max(abs(after_pos[0] - before[0]), abs(after_pos[1] - before[1])) if after_pos else -1
        print("    %-18s 前：隐藏 → 后：%s（光标位移 %+dpx）"
              % (name, "可见" if after else "仍隐藏", drift))
        results.append((name, bool(after)))

    s.close()
    ok = [n for n, r in results if r]
    print("\n结论：能叫醒的方式 =", ok if ok else "无（全部失败）")
    notify("探针完成，桌面已交还", 6, "ok" if ok else "fail")
    return 0


if __name__ == "__main__":
    sys.exit(main())
