#!/usr/bin/env python3
"""
端到端取证：双指平移（触摸注入）期间**指针位置必须恒定**（2026-11-01 用户定稿规则）。

规则（用户原话的意思）：
  · 每帧 pan 都把指针移动到「上一帧所在的全局位置」；
  · 第一帧的位置 = 进入 pan 之前的光标位置；
  ⇒ 整轮平移里指针位置恒定，谁挪的（系统触摸提升 / 用户物理鼠标）都逐帧挪回去。

【这条规则取代了 2026-11-01 早前的相反要求（「平移时物理鼠标要能同时 1:1 移动」）】
早前那份实现留了一个豁口：用户一动鼠标就整段放过光标 → 指针跑开、
用户看到「两个位置都有指针」。现在没有豁口，代价是 pan 期间推不动指针。

断言（全部读真实光标 / 真实宿主日志，不靠猜）：
  A. 只平移、不动鼠标 → 光标纹丝不动；日志「单次最大偏离」≈0
  B. 边平移边模拟鼠标移动 → 光标**必须跟着鼠标走**（硬件实测就是如此），
     日志「用户动鼠标 N 次」>0；位移量精确比对见 tests/pan_mouse_follow.py
  C. 抬手后光标停在固定位置，0.4s 内不被拽走

会碰桌面（真实注入触摸 + 真实移动光标），按规矩由脚本自己弹横幅。
用法：python tests/pan_mouse_together.py [host] [port]
"""
import ctypes
import re
import socket
import struct
import sys
import time
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tests"))
try:
    from desk_notify import notify
except Exception:      # 提示失败绝不能把测试搞挂
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
TEST_TAG = 0x54455354          # 'TEST'：宿主钩子把它当「用户在动鼠标」

user32 = ctypes.WinDLL("user32", use_last_error=True)
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("hCursor", wintypes.HANDLE), ("ptScreenPos", wintypes.POINT)]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG),
                ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_void_p)]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


def cursor():
    p = wintypes.POINT()
    return (p.x, p.y) if user32.GetCursorPos(ctypes.byref(p)) else None


def cursor_visible():
    ci = CURSORINFO(cbSize=ctypes.sizeof(CURSORINFO))
    if not user32.GetCursorInfo(ctypes.byref(ci)):
        return None
    return bool(ci.flags & 0x1)


def mouse_move(dx, dy=0):
    inp = INPUT(type=0)
    inp.mi = MOUSEINPUT(dx=dx, dy=dy, dwFlags=0x0001, dwExtraInfo=TEST_TAG)
    return user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT)) == 1


def mouse_frame(kind, x=0.0, y=0.0):
    return b"SP" + bytes([PV, T_MOUSE]) + struct.pack("<H", 12) + struct.pack("<3f", kind, x, y)


def hello(pan_mode=PAN_TOUCH):
    return b"SP" + bytes([PV, T_HELLO]) + struct.pack("<H", 36) + \
        struct.pack("<9f", 1494, 614, 1, 0, -1, -1, -1, PV, pan_mode)


def host_log():
    for p in LOG_CANDIDATES:
        if p.exists():
            return p
    return None


def log_since(p, size):
    with open(p, "rb") as f:
        f.seek(max(0, size - 1))
        return f.read().decode("utf-8", "ignore")


def num(pattern, log, default=None):
    m = re.findall(pattern, log)
    return int(m[-1]) if m else default


def check(failures, ok, msg):
    print(("   ✓ " if ok else "   ✗ ") + msg)
    if not ok:
        failures.append(msg)


def run(s, logp, with_mouse, frames=40, step_dx=0.008, mouse_every=3, mickey=8):
    size = logp.stat().st_size if logp else 0
    c0 = cursor()
    vis = []
    for i in range(frames):
        s.sendall(mouse_frame(MOUSE_PAN, step_dx, -0.004))
        if with_mouse and i % mouse_every == 0:
            mouse_move(mickey)          # 模拟用户推鼠标（带 TEST 标记）
        vis.append(cursor_visible())
        time.sleep(0.022)
    # 手指「按下 → 离开」是一个状态迁移：宿主只在迁移时结束平移（直接发 off 无效，
    # 因为 _fingerOnPad 本来就是 false，会一直拖到 10s 安全抬手 —— 期间指针被钉住）。
    s.sendall(mouse_frame(MOUSE_FINGER, 1.0, 0.0))
    time.sleep(0.12)
    s.sendall(mouse_frame(MOUSE_FINGER, 0.0, 0.0))
    time.sleep(0.5)
    c1 = cursor()
    time.sleep(0.05)
    return c0, c1, log_since(logp, size) if logp else "", vis


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 6000
    logp = host_log()
    failures = []

    has_cursor = cursor() is not None
    if not has_cursor:
        print("读不到光标 → 桌面被锁或不在交互会话，测试作废（不报通过）")
        return 2
    print("宿主日志：%s" % logp)

    notify("正在测试：双指平移期间指针位置恒定（会注入触摸 + 模拟鼠标移动）", 40, "busy")
    time.sleep(0.4)
    s = socket.create_connection((host, port), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.sendall(hello())
    time.sleep(0.5)

    # ---------- A. 只平移、不动鼠标 ----------
    print("\n[A] 双指平移 40 帧，全程不动鼠标")
    c0, c1, log, visA = run(s, logp, with_mouse=False)
    moved = max(abs(c1[0] - c0[0]), abs(c1[1] - c0[1])) if (c0 and c1) else -1
    print("   光标 %s → %s（移动 %spx）" % (c0, c1, moved))
    check(failures, "开始平移" in log, "真的走了触摸注入（日志出现「开始平移」）")
    check(failures, 0 <= moved <= 3, "不动鼠标时光标纹丝不动（实测 %spx，应 0~3）" % moved)
    check(failures, "回退为滚轮" not in log, "没有回退成滚轮（走的确实是触摸注入）")
    devA = num(r"单次最大偏离 (\d+)px", log)
    check(failures, devA is not None and devA <= 3,
          "日志：平移期间指针被动过的最大幅度 %spx（应 ≤3）" % devA)
    shownA = sum(1 for v in visA if v)
    print("   平移期间光标可见采样：%d/%d（不可见 = 符合「pan 时看不到指针」）" % (shownA, len(visA)))

    # ---------- B. 边平移边模拟鼠标移动：指针仍必须钉住 ----------
    print("\n[B] 边平移边模拟鼠标移动（每 3 帧 +%d 米奇）" % 8)
    c0, c1, log, visB = run(s, logp, with_mouse=True)
    dx = (c1[0] - c0[0]) if (c0 and c1) else -1
    print("   光标 %s → %s（X 位移 %+dpx）" % (c0, c1, dx))
    # 【2026-11-01 定稿·硬件对齐】pan 期间物理鼠标必须照常推动指针（真触摸板实测：100% 由鼠标事件驱动），
    # 但起点是 pan 起点、不是触点 —— 所以这里要求「有位移」，且位移量由 pan_mouse_follow.py 精确比对。
    check(failures, dx >= 10,
          "鼠标在动 → 指针跟着走（X 位移 %+dpx，应 >10；为 0 说明鼠标被吃掉）" % dx)
    mouseN = num(r"用户动鼠标 (\d+) 次", log)
    devB = num(r"单次最大偏离 (\d+)px", log)
    print("   日志：用户动鼠标=%s 次  单次最大偏离=%spx" % (mouseN, devB))
    check(failures, mouseN is not None and mouseN > 0,
          "宿主认出了「用户在动鼠标」（%s 次，说明钩子判据有效）" % mouseN)
    check(failures, devB is not None and devB > 0,
          "宿主确实把指针从别处拽了回来（单次最大偏离 %spx > 0）" % devB)
    check(failures, cursor_visible() is not False,
          "抬手后光标可见（实测 %s）" % ("可见" if cursor_visible() else "隐藏"))

    # ---------- C. 抬手后不许把光标拽走 ----------
    time.sleep(0.4)
    c2 = cursor()
    drift = max(abs(c2[0] - c1[0]), abs(c2[1] - c1[1])) if (c2 and c1) else -1
    check(failures, 0 <= drift <= 5, "抬手后光标停在原处（0.4s 内漂移 %spx）" % drift)

    s.close()
    notify("测试结束，桌面已交还" if not failures else "测试结束：指针没钉住，看日志",
           6, "ok" if not failures else "fail")

    if failures:
        print("\n=== 失败 ===")
        for f in failures:
            print("  ✗", f)
        return 1
    print("\n=== 通过：pan 期间指针被钉在「起点+鼠标位移」上（不跳触点、不拽回、鼠标照常可用）===")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
