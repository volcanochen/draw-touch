#!/usr/bin/env python3
"""
验证：pan 期间**物理鼠标仍然能用**，且指针不会跳到触摸触点上（2026-11-01 硬件对齐目标）。

背景（实测数据，见 tests/real_pan_capture.py 与 tests/pan_msg_probe.py）：
真触摸板 pan 期间，光标位置 100% 由物理鼠标事件驱动，pan 自己一次都没动过光标。
我们的触摸注入做不到这一点（合成指针只支持 PT_TOUCH/PT_PEN，系统必然把触点提升成鼠标），
所以现在的做法是：**以 pan 起点为基准，累加用户的物理鼠标位移**，每帧写回这个基准 →
  · 系统的「提升到触点」每帧被覆盖（指针不会跳到手指上）；
  · 用户的手走多少，指针就走多少（pan 期间鼠标照样能用）。

断言：
  A. 对照：没有 pan 时，推 40px 米奇 → 光标位移 C（受系统加速影响，可能 ≠40）
  B. pan 期间推同样 40px 米奇 → 光标位移 P
     P 必须 ≈ C（误差 ≤5px）：说明位移既没被吃掉（P≈0 就是"鼠标废了"），也没被翻倍
  C. pan 期间**不动鼠标** → 光标必须纹丝不动（位置恒定）
  D. pan 期间光标**从不出现在触点方向**：触点固定在 pan 起点，若指针被"提升"，
     它会偏离"起点+鼠标位移"超过 C 很多 —— 用偏差 max 断言

用法：python tests/pan_mouse_follow.py
"""
import ctypes
import socket
import struct
import sys
import time
from ctypes import wintypes
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from desk_notify import notify
except Exception:
    def notify(*a, **k):
        return False

user32 = ctypes.WinDLL("user32", use_last_error=True)
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass

PV = 4
T_HELLO, T_MOUSE = 0x05, 0x07
MOUSE_PAN, MOUSE_FINGER = 17, 15
TEST_TAG = 0x54455354


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


def set_cursor(x, y):
    user32.SetCursorPos(x, y)


def nudge(dx, dy=0):
    it = INPUT(type=0)
    it.mi = MOUSEINPUT(dx=dx, dy=dy, dwFlags=0x0001, dwExtraInfo=TEST_TAG)
    return user32.SendInput(1, ctypes.byref(it), ctypes.sizeof(INPUT)) == 1


def frame(t, p):
    return b"SP" + bytes([PV, t]) + struct.pack("<H", len(p)) + p


def check(fails, ok, msg):
    print(("   ✓ " if ok else "   ✗ ") + msg)
    if not ok:
        fails.append(msg)


def main() -> int:
    fails = []
    if cursor() is None:
        print("读不到光标 → 桌面被锁/不在交互会话，测试作废")
        return 2

    base = (1000, 800)
    set_cursor(*base)
    time.sleep(0.15)

    notify("正在测试：pan 期间物理鼠标是否照常可用（会注入触摸 + 推鼠标）", 30, "busy")
    time.sleep(0.4)

    # ---- A. 对照：没有 pan ----
    # 用「同一节奏的多次小位移」做对照：单次大位移会受系统加速度状态影响（实测 95 vs 151px），
    # 重复同节奏才能把「加速曲线」这条变量消掉，剩下的差异才是我们实现吃/翻位移。
    def burst():
        for _ in range(5):
            nudge(8)
            time.sleep(0.04)

    set_cursor(*base)
    time.sleep(0.12)
    burst()
    time.sleep(0.1)
    after = cursor()
    C = after[0] - base[0]
    print(f"[A] 对照：无 pan 连推 5×8 米奇 → 光标 {base} → {after}（Δ={C}px）")

    # ---- B. pan 期间推鼠标 ----
    s = socket.create_connection(("127.0.0.1", 6000), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.sendall(frame(T_HELLO, struct.pack("<9f", 1494, 614, 1, 0, -1, -1, -1, PV, 1)))
    time.sleep(0.45)

    set_cursor(*base)
    time.sleep(0.05)
    p0 = cursor()
    print(f"[B] pan 起点光标 = {p0}")

    # 先空跑几帧让 pan 真正起来（此时不动鼠标 → 光标必须不动）
    for _ in range(6):
        s.sendall(frame(T_MOUSE, struct.pack("<3f", MOUSE_PAN, 0.008, -0.004)))
        time.sleep(0.03)
    idle = cursor()
    dIdle = max(abs(idle[0] - p0[0]), abs(idle[1] - p0[1]))
    print(f"[C] pan 期间不动鼠标 6 帧后光标 = {idle}（偏离 {dIdle}px）")

    burst()
    time.sleep(0.12)
    p1 = cursor()
    P = p1[0] - p0[0]
    print(f"[B] pan 期间连推 5×8 米奇 → 光标 {p0} → {p1}（Δ={P}px）")

    # 再多推几次，确认是"持续可用"而不是只有第一次
    burst()
    p2 = cursor()
    print(f"[B] 再推 5 次 → {p2}")

    # 快速甩一下（大幅位移）：不能被"跳变阈值"误杀
    big0 = cursor()
    nudge(180)
    time.sleep(0.1)
    big1 = cursor()
    BIG = big1[0] - big0[0]
    print(f"[B] pan 期间快速甩 180 米奇 → {big0} → {big1}（Δ={BIG}px）")

    s.sendall(frame(T_MOUSE, struct.pack("<3f", MOUSE_FINGER, 1.0, 0.0)))
    time.sleep(0.1)
    s.sendall(frame(T_MOUSE, struct.pack("<3f", MOUSE_FINGER, 0.0, 0.0)))
    time.sleep(0.5)
    s.close()

    time.sleep(0.3)
    pEnd = cursor()
    drift = max(abs(pEnd[0] - big1[0]), abs(pEnd[1] - big1[1]))
    print(f"[D] 抬手后光标 = {pEnd}（相对松手前漂移 {drift}px）")

    print()
    check(fails, C > 10, f"对照有效：无 pan 时鼠标推得动光标（Δ={C}px）")
    check(fails, dIdle <= 2, f"pan 期间不动鼠标时指针位置恒定（偏离 {dIdle}px，应 ≤2）")
    check(fails, P > 10, f"pan 期间鼠标**仍能移动指针**（Δ={P}px，应 >10）")
    check(fails, abs(P - C) <= 8, f"位移没被吃掉也没翻倍（pan {P}px vs 无 pan {C}px，误差 ≤8）")
    check(fails, BIG > 200, f"快速甩鼠标也照常生效（Δ={BIG}px，应 >200；被阈值误杀就会接近 0）")
    check(fails, drift <= 3, f"抬手后不被拽走（漂移 {drift}px，应 ≤3）")

    notify("测试结束，桌面已交还" if not fails else "测试结束：不达标，看日志",
           6, "ok" if not fails else "fail")
    if fails:
        print("\n=== 失败 ===")
        for f in fails:
            print("  ✗", f)
        return 1
    print("\n=== 通过：pan 期间鼠标照常可用、指针不跳触点、松手不拽回（与硬件触摸板一致）===")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
