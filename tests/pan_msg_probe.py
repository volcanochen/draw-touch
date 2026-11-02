#!/usr/bin/env python3
"""
A/B/A 实验：真触摸板 pan / 我们的触摸注入 pan / 我们的滚轮 pan，
落到**同一个窗口**上，看各自到底给应用发了什么消息（2026-11-01）。

为什么需要这一步：
第一轮采集（tests/real_pan_capture.py）已经证明——真实触摸板 pan 期间：
  · 光标**从不隐藏**（CURSOR_SHOWING 恒 1）；
  · 光标一次都没被 pan 移动（1547 次位置变化 100% 能对上一条物理鼠标事件，误差 ≤12px）；
也就是说硬件那条路压根没碰「光标 / 触摸接触」通道。
但它到底把 pan 变成什么消息发给应用，全局钩子看不到（那属于窗口消息），
所以这里开一个自己的窗口，直接数消息。

三个阶段（每段都会在窗口标题里显示进度）：
  A. 用户用**真实触摸板**双指 pan，落在本窗口上；
  B. 脚本伪装手机发 MOUSE_PAN（宿主 `平移方式=Touch` 触摸注入）；
  C. 脚本切到 `平移方式=Wheel` 再发同样的 MOUSE_PAN（滚轮路径）。
结论看最后的对照表：谁有 WM_MOUSEWHEEL、delta 是 120 的整数倍还是任意值、
有没有 WM_POINTER / WM_GESTURE / WM_TOUCH。

用法：
    python tests/pan_msg_probe.py                 # A 20s / B 8s / C 8s
    python tests/pan_msg_probe.py --a 15 --b 6 --c 6
"""
import argparse
import ctypes
import socket
import struct
import sys
import threading
import time
from collections import Counter
from ctypes import wintypes
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from desk_notify import notify
except Exception:
    def notify(*a, **k):
        return False

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)

# 64 位签名（不设 argtypes 时 LPARAM/WPARAM 会被当 32 位 int → OverflowError，wndproc 直接崩）
user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.DefWindowProcW.restype = LRESULT
user32.PeekMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND,
                                wintypes.UINT, wintypes.UINT, wintypes.UINT]
user32.DispatchMessageW.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.DispatchMessageW.restype = LRESULT
user32.TranslateMessage.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.DrawTextW.argtypes = [wintypes.HDC, wintypes.LPCWSTR, ctypes.c_int,
                             ctypes.POINTER(wintypes.RECT), wintypes.UINT]
user32.BeginPaint.argtypes = [wintypes.HWND, ctypes.c_void_p]
user32.BeginPaint.restype = wintypes.HDC
user32.EndPaint.argtypes = [wintypes.HWND, ctypes.c_void_p]
user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.LoadCursorW.restype = wintypes.HANDLE
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.DestroyWindow.argtypes = [wintypes.HWND]

WM_PAINT, WM_DESTROY = 0x000F, 0x0002
WM_MOUSEMOVE, WM_MOUSEWHEEL, WM_MOUSEHWHEEL = 0x0200, 0x020A, 0x020E
WM_GESTURE, WM_TOUCH, WM_POINTER = 0x0119, 0x0240, 0x0245
WM_KEYDOWN = 0x0100

WNDPROC_REF = None
LOG = []          # (phase, t, msg, extra)
PHASE = ["A"]
T0 = time.time()


def now():
    return round(time.time() - T0, 3)


def short(hi):
    v = hi & 0xFFFF
    return v - 65536 if v >= 32768 else v


def wndproc(hwnd, msg, wp, lp):
    try:
        if msg == WM_MOUSEWHEEL:
            LOG.append((PHASE[0], now(), "WHEEL", short(wp >> 16)))
        elif msg == WM_MOUSEHWHEEL:
            LOG.append((PHASE[0], now(), "HWHEEL", short(wp >> 16)))
        elif msg == WM_POINTER:
            LOG.append((PHASE[0], now(), "POINTER", int(wp)))
        elif msg == WM_GESTURE:
            LOG.append((PHASE[0], now(), "GESTURE", 0))
        elif msg == WM_TOUCH:
            LOG.append((PHASE[0], now(), "TOUCH", 0))
        elif msg == WM_MOUSEMOVE:
            LOG.append((PHASE[0], now(), "MOVE", 0))
        elif msg == WM_PAINT:
            ps = ctypes.create_string_buffer(72)
            hdc = user32.BeginPaint(hwnd, ps)
            r = wintypes.RECT()
            user32.GetClientRect(hwnd, ctypes.byref(r))
            text = ("pan 消息探测窗\n\n"
                    f"阶段 {PHASE[0]}    时间 {now():.0f}s\n"
                    f"WHEEL {sum(1 for l in LOG if l[2] == 'WHEEL')}  "
                    f"HWHEEL {sum(1 for l in LOG if l[2] == 'HWHEEL')}  "
                    f"POINTER {sum(1 for l in LOG if l[2] == 'POINTER')}  "
                    f"GESTURE {sum(1 for l in LOG if l[2] == 'GESTURE')}\n\n"
                    "请在窗口内做双指 pan（鼠标可以同时在别处动）")
            user32.DrawTextW(hdc, text, -1, ctypes.byref(r), 0x00000010 | 0x00000004)  # CENTER|VCENTER
            user32.EndPaint(hwnd, ps)
            return 0
        elif msg == WM_DESTROY:
            user32.PostQuitMessage(0)
            return 0
    except Exception as e:
        print("[wndproc]", e)
    return user32.DefWindowProcW(hwnd, msg, wp, lp)


class WNDCLASS(ctypes.Structure):
    _fields_ = [("style", wintypes.UINT), ("lpfnWndProc", WNDPROC),
                ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int),
                ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
                ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH),
                ("lpszMenuName", wintypes.LPCWSTR), ("lpszClassName", wintypes.LPCWSTR)]


# ---------------- 协议：驱动我们的宿主 ----------------

PV = 4
T_HELLO, T_MOUSE = 0x05, 0x07
MOUSE_PAN, MOUSE_FINGER = 17, 15


def frame(t, p):
    return b"SP" + bytes([PV, t]) + struct.pack("<H", len(p)) + p


def drive_pan(port, pan_mode, seconds, frames_hz=40):
    """让宿主按指定平移方式跑一段双指 pan。pan_mode: 0=Wheel 1=Touch"""
    s = socket.create_connection(("127.0.0.1", port), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.sendall(frame(T_HELLO, struct.pack("<9f", 1494, 614, 1, 0, -1, -1, -1, PV, pan_mode)))
    time.sleep(0.4)
    step = 1.0 / frames_hz
    for _ in range(int(seconds * frames_hz)):
        s.sendall(frame(T_MOUSE, struct.pack("<3f", MOUSE_PAN, 0.006, -0.004)))
        time.sleep(step)
    s.sendall(frame(T_MOUSE, struct.pack("<3f", MOUSE_FINGER, 1.0, 0.0)))
    time.sleep(0.1)
    s.sendall(frame(T_MOUSE, struct.pack("<3f", MOUSE_FINGER, 0.0, 0.0)))
    time.sleep(0.4)
    s.close()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", type=float, default=20.0, help="真触摸板 pan 采集秒数")
    ap.add_argument("--b", type=float, default=8.0, help="我们的触摸注入 pan 秒数")
    ap.add_argument("--c", type=float, default=8.0, help="我们的滚轮 pan 秒数")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--no-pump", action="store_true", help="跳过 B/C，只测真机")
    args = ap.parse_args()

    # 【必须】窗口只能由「创建它的那个线程」抽消息：放到 UI 线程里建+泵，
    # 主线程只管切阶段和驱协议帧（早先在主线程建窗、子线程 PeekMessage → 一条消息都收不到）。
    holder = {}
    ready = threading.Event()
    stop = threading.Event()

    def ui_thread():
        global WNDPROC_REF
        WNDPROC_REF = WNDPROC(wndproc)
        hinst = kernel32.GetModuleHandleW(None)
        cls = "SpenPanMsgProbe"
        wc = WNDCLASS(0, WNDPROC_REF, 0, 0, hinst, None,
                      user32.LoadCursorW(None, 32512), None, None, cls)
        wc.hbrBackground = ctypes.cast(6, wintypes.HBRUSH)      # COLOR_WINDOW
        user32.RegisterClassW.argtypes = [ctypes.c_void_p]
        user32.RegisterClassW(ctypes.byref(wc))
        user32.CreateWindowExW.restype = wintypes.HWND
        user32.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR,
                                           wintypes.DWORD, ctypes.c_int, ctypes.c_int,
                                           ctypes.c_int, ctypes.c_int, wintypes.HWND,
                                           wintypes.HMENU, wintypes.HINSTANCE, ctypes.c_void_p]
        hwnd = user32.CreateWindowExW(0x00000008, cls, "pan 消息探测窗（测试用）", 0x00C00000,
                                      360, 160, 900, 620, None, None, hinst, None)
        holder["hwnd"] = hwnd
        user32.ShowWindow(hwnd, 5)
        user32.SetForegroundWindow(hwnd)
        ready.set()
        msg = wintypes.MSG()
        while not stop.is_set():
            if user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
                if msg.message == 0x0012:
                    break
                user32.TranslateMessage(ctypes.byref(msg))
                user32.DispatchMessageW(ctypes.byref(msg))
            else:
                time.sleep(0.003)
        user32.DestroyWindow(hwnd)

    th = threading.Thread(target=ui_thread, daemon=True)
    th.start()
    ready.wait(3)

    total = args.a + (0 if args.no_pump else args.b + args.c)
    notify(f"正在测试：pan 消息探测。请先用真实触摸板在本窗口内做双指 pan（{args.a:.0f}s）",
           int(total) + 10, "busy")
    time.sleep(0.5)

    PHASE[0] = "A"
    print(f"[A] 请用真实触摸板双指 pan，落在探测窗里（{args.a:.0f}s）…")
    time.sleep(args.a)

    hwnd = holder.get("hwnd")
    if not args.no_pump:
        PHASE[0] = "B"
        print(f"[B] 宿主触摸注入 pan（{args.b:.0f}s）…")
        if hwnd:
            user32.SetCursorPos(810, 470)
            user32.SetForegroundWindow(hwnd)
        drive_pan(args.port, 1, args.b)

        PHASE[0] = "C"
        print(f"[C] 宿主滚轮 pan（{args.c:.0f}s）…")
        if hwnd:
            user32.SetCursorPos(810, 470)
            user32.SetForegroundWindow(hwnd)
        drive_pan(args.port, 0, args.c)

    stop.set()
    th.join(1.0)

    print("\n===== 对照表 =====")
    phases = ["A", "B", "C"] if not args.no_pump else ["A"]
    for ph in phases:
        rows = [l for l in LOG if l[0] == ph]
        kinds = Counter(l[2] for l in rows)
        wheels = [l[3] for l in rows if l[2] == "WHEEL"]
        hw = [l[3] for l in rows if l[2] == "HWHEEL"]
        name = {"A": "真触摸板 pan", "B": "我们·触摸注入", "C": "我们·滚轮"}[ph]
        print(f"\n[{ph}] {name}：{dict(kinds)}")
        if wheels:
            mult = sum(1 for v in wheels if v % 120 == 0)
            print(f"    纵向 WHEEL {len(wheels)} 条，样本={wheels[:10]}，其中 120 整数倍 {mult}/{len(wheels)}")
        if hw:
            print(f"    横向 HWHEEL {len(hw)} 条，样本={hw[:10]}")
    notify("测试结束，桌面已交还", 6, "ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
