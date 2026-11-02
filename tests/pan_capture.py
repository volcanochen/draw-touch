#!/usr/bin/env python3
"""
取证：用户真机 pan 的**那一瞬间**，屏幕上到底画了什么。

背景（2026-11-01）：注入 62 帧实测光标位移 0px、低级钩子只有我们自己的事件、
真实设备 raw 计数不涨 ⇒「跟着手指走的东西」不是光标。怀疑是系统的触摸接触视觉
（本机 HKCU\\Control Panel\\Cursors\\ContactVisualization = 1，即开启）。

本脚本**不注入、不移动光标、不抢焦点**，只做两件事：
  1. 盯着宿主日志，等出现「开始平移」；
  2. 从那一刻起以光标为中心连续截图，产出 tests/out/live_*.png。

用法：
    python tests/pan_capture.py            # 然后立刻在手机上做一次双指平移
    python tests/pan_capture.py --wait 90  # 等待秒数
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from desk_notify import notify
except Exception:
    def notify(*a, **k):
        return False

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

LOG = Path(r"C:\Users\adminlocal\WorkBuddy\2026-10-26-17-47-23\SpenTablet\PcHost"
           r"\bin\Release\net8.0-windows\win-x64\DrawTouchHost.log")


def dpi_aware():
    try:
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        pass


def cursor_pos():
    pt = wt.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


def grab_png(path, x, y, w, h):
    hdc_screen = user32.GetDC(None)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    gdi32.SelectObject(hdc_mem, bmp)
    gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, x, y, 0x00CC0020)
    bi = BIH()
    bi.biSize = ctypes.sizeof(BIH)
    bi.biWidth, bi.biHeight = w, -h
    bi.biPlanes, bi.biBitCount = 1, 32
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(hdc_mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(None, hdc_screen)

    bmp_path = str(path).replace(".png", ".bmp")
    with open(bmp_path, "wb") as fh:
        hdr = ctypes.sizeof(BIH) + 14
        fh.write(b"BM" + struct.pack("<IHHI", hdr + len(buf.raw), 0, 0, hdr))
        fh.write(bytes(bi))
        fh.write(buf.raw)
    import subprocess
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Add-Type -AssemblyName System.Drawing;"
                    f"$b=[System.Drawing.Image]::FromFile('{bmp_path}');"
                    f"$b.Save('{path}',[System.Drawing.Imaging.ImageFormat]::Png);$b.Dispose()"],
                   capture_output=True)
    Path(bmp_path).unlink(missing_ok=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--wait", type=float, default=90, help="最多等多少秒")
    ap.add_argument("--pad", type=int, default=420, help="截图半边长")
    ap.add_argument("--shots", type=int, default=12)
    ap.add_argument("--gap", type=float, default=0.18)
    args = ap.parse_args()

    dpi_aware()
    out = Path(__file__).resolve().parent / "out"
    out.mkdir(exist_ok=True)
    for old in out.glob("live_*.png"):
        old.unlink(missing_ok=True)

    print(f"监听 {LOG.name} …（不注入、不动光标）")
    notify("正在等待你在手机上做一次双指平移（只截图，不动你的桌面）", 15, "busy")

    pos = LOG.stat().st_size if LOG.exists() else 0
    deadline = time.time() + args.wait
    fired = False
    while time.time() < deadline:
        if not LOG.exists():
            time.sleep(0.2); continue
        size = LOG.stat().st_size
        if size < pos:                      # 日志被截断/轮转
            pos = 0
        if size > pos:
            with open(LOG, "rb") as fh:
                fh.seek(pos)
                chunk = fh.read(size - pos)
            pos = size
            text = chunk.decode("utf-8", "replace")
            if "开始平移" in text:
                fired = True
                cx, cy = cursor_pos()
                x0 = max(0, cx - args.pad); y0 = max(0, cy - args.pad)
                print(f"-> 抓到平移开始，光标({cx},{cy})，开始截图")
                for i in range(args.shots):
                    grab_png(out / f"live_{i:02d}.png", x0, y0, args.pad * 2, args.pad * 2)
                    time.sleep(args.gap)
                break
        time.sleep(0.15)

    if not fired:
        print("-> 等待超时，没抓到平移（宿主没收到 MOUSE_PAN，或当时不在监听窗口内）")
        notify("没等到平移，测试结束", 5, "fail")
        return 2

    notify("截图完成，桌面未受影响", 5, "ok")
    print(f"-> 完成：{len(list(out.glob('live_*.png')))} 张，在 tests/out/")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
