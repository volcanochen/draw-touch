#!/usr/bin/env python3
"""
横向滚轮（WM_MOUSEHWHEEL）受控探针 —— 只为取证，不下结论。

背景：要回答「横向平移能不能像纵向一样走滚轮」这个问题，必须先有证据：
  1. SendInput(MOUSEEVENTF_HWHEEL) 到底有没有把消息送到光标下的控件；
  2. 送达的 wParam 符号与「内容移动方向」的对应关系；
  3. 标准控件（EDIT）收到后会不会真的横向滚动。

做法：自己创建一个标准 EDIT 控件（长单行文本 + ES_AUTOHSCROLL），摆到主屏空位，
把光标移上去，注入若干次横向滚轮，读控件自己的横向滚动位置（GetScrollPos SB_HORZ）。
同时子类化窗口过程，记录收到的 WM_MOUSEHWHEEL / WM_MOUSEWHEEL 原始 wParam。

纪律：动真实光标 → 前后各弹一次横幅；结束恢复原光标位置；窗口不抢焦点（WS_EX_NOACTIVATE）。
用法：python tests/hwheel_probe.py
"""
import ctypes
import os
import sys
import time
from ctypes import wintypes
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from desk_notify import notify  # noqa: E402

u = ctypes.WinDLL("user32", use_last_error=True)
k = ctypes.WinDLL("kernel32", use_last_error=True)

# ---- 与宿主同量纲：per-monitor v2 ----
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    try:
        ctypes.WinDLL("shcore").SetProcessDpiAwareness(2)
    except Exception:
        pass

WM_MOUSEWHEEL = 0x020A
WM_MOUSEHWHEEL = 0x020E
MOUSEEVENTF_WHEEL = 0x0800
MOUSEEVENTF_HWHEEL = 0x1000
SB_HORZ = 0
GWLP_WNDPROC = -4

ES_MULTILINE = 0x0004
ES_AUTOHSCROLL = 0x0080
WS_POPUP = 0x80000000
WS_VISIBLE = 0x10000000
WS_VSCROLL = 0x00200000
WS_HSCROLL = 0x00100000


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
u.GetScrollPos.restype = ctypes.c_int
u.GetScrollPos.argtypes = [wintypes.HWND, ctypes.c_int]
u.SetWindowLongPtrW.restype = ctypes.c_void_p
u.SetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_void_p]
u.CallWindowProcW.restype = ctypes.c_ssize_t
u.CallWindowProcW.argtypes = [ctypes.c_void_p, wintypes.HWND, wintypes.UINT,
                              ctypes.c_size_t, ctypes.c_ssize_t]
u.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
u.SendInput.restype = wintypes.UINT

WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wintypes.HWND, wintypes.UINT,
                             ctypes.c_size_t, ctypes.c_ssize_t)

received = []          # [(message, delta)] 记录到达控件的原始滚轮消息
_old_proc = None


def _wndproc(hwnd, msg, wparam, lparam):
    if msg in (WM_MOUSEWHEEL, WM_MOUSEHWHEEL):
        raw = (wparam >> 16) & 0xFFFF
        delta = raw - 0x10000 if raw & 0x8000 else raw
        received.append(("HWHEEL" if msg == WM_MOUSEHWHEEL else "WHEEL", delta))
    return u.CallWindowProcW(_old_proc, hwnd, msg, wparam, lparam)


_wndproc_c = WNDPROC(_wndproc)


def pump(ms):
    """泵消息：控件在我们自己的线程上，不泵就不会处理滚轮消息。"""
    end = time.time() + ms / 1000.0
    msg = MSG()
    while time.time() < end:
        while u.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
            u.TranslateMessage(ctypes.byref(msg))
            u.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.002)


def send_wheel(flag, delta):
    inp = INPUT()
    inp.type = 0           # INPUT_MOUSE
    inp.mi = MOUSEINPUT(0, 0, ctypes.c_uint32(delta & 0xFFFFFFFF).value, flag, 0, None)
    n = u.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
    if n != 1:
        print("  !! SendInput 失败 err=%s" % ctypes.get_last_error())


def main():
    # 主屏尺寸（DpiAware 下是物理像素）
    pw = u.GetSystemMetrics(0)
    ph = u.GetSystemMetrics(1)
    x, y, w, h = 120, 120, min(760, pw - 260), 220

    cursor_saved = wintypes.POINT()
    u.GetCursorPos(ctypes.byref(cursor_saved))

    notify("正在测试：横向滚轮探针（会移动光标到测试窗口，不抢焦点）", 30, "busy")
    print("主屏 %dx%d，探针窗口 (%d,%d) %dx%d" % (pw, ph, x, y, w, h))

    hwnd = u.CreateWindowExW(
        0x08000000 | 0x00000080 | 0x00000008,  # WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
        "EDIT",
        ("SpenTablet HWHEEL probe  " * 120),
        WS_POPUP | WS_VISIBLE | WS_VSCROLL | WS_HSCROLL | ES_MULTILINE | ES_AUTOHSCROLL,
        x, y, w, h, None, None, None, None,
    )
    if not hwnd:
        print("创建窗口失败 err=%s" % ctypes.get_last_error())
        notify("横向滚轮探针失败（窗口创建不出来）", 8, "fail")
        return 2

    global _old_proc
    _old_proc = u.SetWindowLongPtrW(hwnd, GWLP_WNDPROC,
                                    ctypes.cast(_wndproc_c, ctypes.c_void_p))
    pump(120)

    # 光标移到窗口中央（滚轮只发给光标下的窗口）
    u.SetCursorPos(x + w // 2, y + h // 2)
    pump(150)

    # ---- 前置条件校验：光标下必须真的是我们的探针窗口 ----
    pt = wintypes.POINT(x + w // 2, y + h // 2)
    under = u.WindowFromPoint(pt)
    cur = wintypes.POINT()
    u.GetCursorPos(ctypes.byref(cur))
    print("光标实际位置 = (%d,%d)，期望 (%d,%d)" % (cur.x, cur.y, pt.x, pt.y))
    print("光标下的窗口 = %s，探针窗口 = %s → %s"
          % (under, hwnd, "一致(前提成立)" if under == hwnd else "不一致(前提不成立)"))
    if under != hwnd:
        print("！！前置条件不成立：滚轮会被发给别的窗口，本次结果不能用来判断 HWHEEL")
        u.DestroyWindow(hwnd)
        u.SetCursorPos(cursor_saved.x, cursor_saved.y)
        notify("横向滚轮探针前置条件不成立（窗口未置顶），已结束", 8, "fail")
        return 3

    base = u.GetScrollPos(hwnd, SB_HORZ)
    print("初始横向滚动位置 = %d" % base)
    print("控件客户区宽=%d（内容远宽于此，应存在横向滚动范围）" % (w - 4))

    results = []
    for label, flag, delta in [
        ("HWHEEL +360", MOUSEEVENTF_HWHEEL, 360),
        ("HWHEEL +360", MOUSEEVENTF_HWHEEL, 360),
        ("HWHEEL -720", MOUSEEVENTF_HWHEEL, -720),
    ]:
        before = u.GetScrollPos(hwnd, SB_HORZ)
        n_before = len(received)
        send_wheel(flag, delta)
        pump(220)
        after = u.GetScrollPos(hwnd, SB_HORZ)
        got = received[n_before:]
        print("%-13s 收到=%s  滚动位置 %d → %d（变化 %+d）"
              % (label, got if got else "无", before, after, after - before))
        results.append((label, got, after - before))

    # 对照：纵向滚轮（确认这套探针本身是有效的）
    before = u.GetScrollPos(hwnd, SB_HORZ)
    n_before = len(received)
    send_wheel(MOUSEEVENTF_WHEEL, -360)
    pump(220)
    got = received[n_before:]
    print("[对照] WHEEL -360  收到=%s（应出现 WHEEL，证明探针通路有效）" % (got if got else "无"))

    u.DestroyWindow(hwnd)
    u.SetCursorPos(cursor_saved.x, cursor_saved.y)
    notify("横向滚轮探针结束，桌面已交还", 6, "ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
