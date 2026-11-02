#!/usr/bin/env python3
"""
纵向滚轮「最小移动距离」受控探针 —— 只为取证，不下结论。

要回答的两个问题：
  1. 宿主发出的滚轮增量最小是 1 单位（wParam=±1）——它在标准控件里到底会不会让内容动？
  2. 若单次 1 单位不动，多次 1 单位会不会累积？（决定「很慢的纵向平移能不能滚起来」）

做法：自建多行 EDIT 控件（含纵向滚动条、几百行文本），摆到主屏空位，光标移上去，
注入不同大小的 WHEEL 增量，读控件自己的「首个可见行」(EM_GETFIRSTVISIBLELINE)。
同时子类化窗口过程确认到达控件的原始 wParam。

纪律：动真实光标 → 前后各弹一次横幅；结束恢复原光标位置；窗口不抢焦点（WS_EX_NOACTIVATE）。
用法：python tests/wheel_granularity_probe.py
"""
import ctypes
import sys
import time
from ctypes import wintypes
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from desk_notify import notify  # noqa: E402

u = ctypes.WinDLL("user32", use_last_error=True)

# 与宿主同量纲：per-monitor v2（否则读到虚拟化坐标）
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    try:
        ctypes.WinDLL("shcore").SetProcessDpiAwareness(2)
    except Exception:
        pass

WM_MOUSEWHEEL = 0x020A
MOUSEEVENTF_WHEEL = 0x0800
EM_GETFIRSTVISIBLELINE = 0x00CE
GWLP_WNDPROC = -4
WS_POPUP = 0x80000000
WS_VISIBLE = 0x10000000
WS_VSCROLL = 0x00200000
ES_MULTILINE = 0x0004
ES_AUTOVSCROLL = 0x0040


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wintypes.LONG),
        ("dy", wintypes.LONG),
        ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_void_p),
    ]


class _INPUTUNION(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT)]


class INPUT(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _INPUTUNION)]


class MSG(ctypes.Structure):
    _fields_ = [
        ("hwnd", wintypes.HWND),
        ("message", wintypes.UINT),
        ("wParam", ctypes.c_size_t),
        ("lParam", ctypes.c_ssize_t),
        ("time", wintypes.DWORD),
        ("pt", wintypes.POINT),
        ("lPrivate", wintypes.DWORD),
    ]


u.CreateWindowExW.restype = wintypes.HWND
u.CreateWindowExW.argtypes = [
    wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
    ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID,
]
u.SendMessageW.restype = ctypes.c_ssize_t
u.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, ctypes.c_size_t, ctypes.c_ssize_t]
u.SetWindowLongPtrW.restype = ctypes.c_void_p
u.SetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_void_p]
u.CallWindowProcW.restype = ctypes.c_ssize_t
u.CallWindowProcW.argtypes = [ctypes.c_void_p, wintypes.HWND, wintypes.UINT,
                              ctypes.c_size_t, ctypes.c_ssize_t]
u.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
u.SendInput.restype = wintypes.UINT

WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wintypes.HWND, wintypes.UINT,
                             ctypes.c_size_t, ctypes.c_ssize_t)

received = []
_old_proc = None


def _wndproc(hwnd, msg, wparam, lparam):
    if msg == WM_MOUSEWHEEL:
        raw = (wparam >> 16) & 0xFFFF
        delta = raw - 0x10000 if raw & 0x8000 else raw
        received.append(delta)
    return u.CallWindowProcW(_old_proc, hwnd, msg, wparam, lparam)


_wndproc_c = WNDPROC(_wndproc)


def pump(ms):
    end = time.time() + ms / 1000.0
    msg = MSG()
    while time.time() < end:
        while u.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
            u.TranslateMessage(ctypes.byref(msg))
            u.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.002)


def send_wheel(delta):
    inp = INPUT()
    inp.type = 0
    inp.mi = MOUSEINPUT(0, 0, ctypes.c_uint32(delta & 0xFFFFFFFF).value, MOUSEEVENTF_WHEEL, 0, None)
    if u.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT)) != 1:
        print("  !! SendInput 失败 err=%s" % ctypes.get_last_error())


def first_line(hwnd):
    return u.SendMessageW(hwnd, EM_GETFIRSTVISIBLELINE, 0, 0)


def main():
    pw = u.GetSystemMetrics(0)
    x, y, w, h = 120, 140, min(720, pw - 260), 240

    saved = wintypes.POINT()
    u.GetCursorPos(ctypes.byref(saved))

    notify("正在测试：滚轮最小步长探针（会移动光标到测试窗口，不抢焦点）", 40, "busy")
    print("主屏 %dx%d，探针窗口 (%d,%d) %dx%d" % (pw, u.GetSystemMetrics(1), x, y, w, h))

    text = "\r\n".join("行 %03d  SpenTablet wheel granularity probe" % i for i in range(1, 400))
    hwnd = u.CreateWindowExW(
        0x08000000 | 0x00000080 | 0x00000008,   # NOACTIVATE | TOOLWINDOW | TOPMOST
        "EDIT", text,
        WS_POPUP | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL,
        x, y, w, h, None, None, None, None,
    )
    if not hwnd:
        print("创建窗口失败 err=%s" % ctypes.get_last_error())
        notify("滚轮探针失败（窗口创建不出来）", 8, "fail")
        return 2

    global _old_proc
    _old_proc = u.SetWindowLongPtrW(hwnd, GWLP_WNDPROC, ctypes.cast(_wndproc_c, ctypes.c_void_p))
    pump(150)

    u.SetCursorPos(x + w // 2, y + h // 2)
    pump(150)

    pt = wintypes.POINT(x + w // 2, y + h // 2)
    under = u.WindowFromPoint(pt)
    print("光标下窗口 = %s，探针窗口 = %s → %s"
          % (under, hwnd, "一致(前提成立)" if under == hwnd else "不一致(前提不成立)"))
    if under != hwnd:
        u.DestroyWindow(hwnd)
        u.SetCursorPos(saved.x, saved.y)
        notify("滚轮探针前置条件不成立（窗口未置顶），已结束", 8, "fail")
        return 3

    print("\n单次注入：delta → 首可见行变化（负 delta = 向下滚，行号变大）")
    print("-" * 62)
    for delta in [1, 2, 3, 4, 8, 15, 30, 60, 119, 120]:
        before = first_line(hwnd)
        n0 = len(received)
        send_wheel(-delta)
        pump(180)
        after = first_line(hwnd)
        got = received[n0:]
        print("delta=%-4d 送达=%-8s 首可见行 %d → %d（%+d）"
              % (delta, got if got else "无", before, after, after - before))

    print("\n连续注入 20 次 delta=-1（模拟最慢的纵向平移）")
    print("-" * 62)
    before = first_line(hwnd)
    n0 = len(received)
    for _ in range(20):
        send_wheel(-1)
        pump(40)
    pump(200)
    after = first_line(hwnd)
    print("送达 %d 条，首可见行 %d → %d（%+d）"
          % (len(received) - n0, before, after, after - before))

    # 把控件滚回顶部，避免留下奇怪状态
    for _ in range(40):
        send_wheel(120)
        pump(15)

    u.DestroyWindow(hwnd)
    u.SetCursorPos(saved.x, saved.y)
    notify("滚轮探针结束，桌面已交还", 6, "ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
