#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
用 PrintWindow 直接抓 NoteOverlay 分层窗口自身内容（绕过屏幕合成），
验证 ULW 推送的笔迹是否真的进了窗口表面。

用法：python printwin_check.py [hwnd]   # 不带参数则自动找 host 进程的分层窗口
"""
import ctypes
import re
import subprocess
import sys
from ctypes import wintypes

u = ctypes.windll.user32
g = ctypes.windll.gdi32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))


class BMIH(ctypes.Structure):
    _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                ("biSizeImage", ctypes.c_uint32), ("x", ctypes.c_int32),
                ("y", ctypes.c_int32), ("c1", ctypes.c_uint32), ("c2", ctypes.c_uint32)]


def find_layered_windows():
    out = subprocess.check_output(
        'tasklist /FI "IMAGENAME eq SpenTabletHost.exe" /FO CSV',
        shell=True).decode("gbk", "replace")
    pids = [int(p) for p in re.findall(r'"SpenTabletHost\.exe","(\d+)"', out)]
    found = []

    def cb(h, l):
        pid = wintypes.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if pid.value in pids:
            ex = u.GetWindowLongW(h, -20) & 0xFFFFFFFF
            if ex & 0x80000 and u.IsWindowVisible(h):
                found.append(h)
        return True

    WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    u.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def check(hwnd):
    r = wintypes.RECT()
    u.GetWindowRect(hwnd, ctypes.byref(r))
    W, H = r.right - r.left, r.bottom - r.top
    hdc = u.GetDC(None)
    mem = g.CreateCompatibleDC(hdc)
    bi = BMIH(ctypes.sizeof(BMIH), W, -H, 1, 32, 0, 0, 0, 0, 0, 0)
    bits = ctypes.c_void_p()
    dib = g.CreateDIBSection(hdc, ctypes.byref(bi), 0, ctypes.byref(bits), None, 0)
    g.SelectObject(mem, dib)
    ok = u.PrintWindow(hwnd, mem, 2)  # PW_RENDERFULLCONTENT
    u.ReleaseDC(None, hdc)
    if not ok or not bits.value:
        print(f"hwnd={hwnd} PrintWindow 失败")
        return
    buf = (ctypes.c_ubyte * (W * H * 4)).from_address(bits.value)
    red = 0
    alpha_nonzero = 0
    samples = []
    for y in range(H):
        base = y * W * 4
        for x in range(W):
            i = base + x * 4
            b, gg, rr, aa = buf[i], buf[i + 1], buf[i + 2], buf[i + 3]
            if aa > 0:
                alpha_nonzero += 1
                if aa > 100 and rr > 170 and gg < 130 and b < 130:
                    red += 1
                    if len(samples) < 5:
                        samples.append((x, y, (rr, gg, b, aa)))
    print(f"hwnd={hwnd} rect=({r.left},{r.top}) {W}x{H} "
          f"非透明像素={alpha_nonzero} 红色像素={red} {samples}")
    g.DeleteObject(dib)
    g.DeleteDC(mem)


if __name__ == "__main__":
    hwnds = [int(sys.argv[1])] if len(sys.argv) > 1 else find_layered_windows()
    for h in hwnds:
        check(h)
