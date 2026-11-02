#!/usr/bin/env python3
"""
取证（第三轮）：把「系统真实光标」显式画进截图，并排标出触点位置。

为什么前两轮不算数（2026-11-01）：
  · GDI BitBlt 拍不到硬件光标 —— 光标在独立叠加层，不在桌面表面；触摸视觉同层，同样拍不到。
  · 所以「图里没有」既可能是真没有，也可能只是在不可截层 → 不能当证据。

本轮做法：
  1. GetCursorInfo 逐帧读取 flags / hCursor 句柄 / 屏幕位置。flags 里
     CURSOR_SHOWING=0x1、CURSOR_SUPPRESSED=0x2（触摸期间被系统抑制）。
     **hCursor 句柄是否在 pan 过程中变化**，是判断「系统是否换了另一个光标图形」的硬指标。
  2. 用 DrawIconEx 把真实光标**显式画到**截图对应位置上（放大 2 倍便于观察），
     并在触点位置画红色圆圈 + 十字。这样图上一旦有第二个指针，位置就对不上标记，一眼可辨。
  3. 只剩一种情况无法截图：那个东西若真在不可截层，图上只会看到我们的标记而没有指针
     —— 这本身就是结论（它不属于可截取的桌面表面）。

用法：
    python tests/pan_capture3.py --wait 200
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import re
import struct
import subprocess
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
RE_DIAG = re.compile(r"光标\((-?\d+),(-?\d+)\) 锚点\((-?\d+),(-?\d+)\) 偏差=(\d+) 可见=(\d+) 触点\((-?\d+),(-?\d+)\)")

CURSOR_SHOWING = 0x0001
CURSOR_SUPPRESSED = 0x0002


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD), ("hCursor", wt.HANDLE),
                ("ptScreenPos", wt.POINT)]


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


def dpi_aware():
    try:
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        pass


def cursor_info():
    ci = CURSORINFO()
    ci.cbSize = ctypes.sizeof(CURSORINFO)
    if not user32.GetCursorInfo(ctypes.byref(ci)):
        return None
    return ci


def grab_annotated(path, x0, y0, w, h, ci, tx=None, ty=None, scale=2):
    """截屏 + 把真实光标画上去 + 标出触点，返回原始像素供差分。"""
    hdc_screen = user32.GetDC(None)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    gdi32.SelectObject(hdc_mem, bmp)
    gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, x0, y0, 0x00CC0020)

    # ① 把系统真实光标画到对应位置（GDI 截屏拍不到它，只能显式画）
    if ci and ci.hCursor:
        cxp = ci.ptScreenPos.x - x0
        cyp = ci.ptScreenPos.y - y0
        # 画一个定位框，保证"哪怕图标本身是空白的"也能看出位置
        pen = gdi32.CreatePen(0, 2, 0x0000FF)          # 红（COLORREF = 0x00BBGGRR）
        old = gdi32.SelectObject(hdc_mem, pen)
        gdi32.MoveToEx(hdc_mem, cxp - 14, cyp, None)
        gdi32.LineTo(hdc_mem, cxp + 14, cyp)
        gdi32.MoveToEx(hdc_mem, cxp, cyp - 14, None)
        gdi32.LineTo(hdc_mem, cxp, cyp + 14)
        gdi32.SelectObject(hdc_mem, old)
        gdi32.DeleteObject(pen)
        user32.DrawIconEx(hdc_mem, cxp, cyp, ci.hCursor, 32 * scale, 32 * scale, 0, None, 0x0003)

    # ② 触点位置画蓝圈
    if tx is not None and ty is not None:
        px = tx - x0
        py = ty - y0
        pen = gdi32.CreatePen(0, 3, 0xFF0000)          # 蓝
        old = gdi32.SelectObject(hdc_mem, pen)
        gdi32.SelectObject(hdc_mem, gdi32.GetStockObject(5))   # NULL_BRUSH
        gdi32.Ellipse(hdc_mem, px - 26, py - 26, px + 26, py + 26)
        gdi32.SelectObject(hdc_mem, old)
        gdi32.DeleteObject(pen)

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
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Add-Type -AssemblyName System.Drawing;"
                    f"$b=[System.Drawing.Image]::FromFile('{bmp_path}');"
                    f"$b.Save('{path}',[System.Drawing.Imaging.ImageFormat]::Png);$b.Dispose()"],
                   capture_output=True)
    Path(bmp_path).unlink(missing_ok=True)
    return buf.raw


def last_diag():
    if not LOG.exists():
        return None
    with open(LOG, "rb") as fh:
        fh.seek(max(0, LOG.stat().st_size - 8192))
        tail = fh.read().decode("utf-8", "replace")
    last = None
    for line in tail.splitlines():
        if "[TouchPan][诊断]" in line:
            last = line
    m = RE_DIAG.search(last or "")
    if not m:
        return None
    g = [int(v) for v in m.groups()]
    return dict(cx=g[0], cy=g[1], ax=g[2], ay=g[3], dev=g[4], vis=g[5], tx=g[6], ty=g[7])


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--wait", type=float, default=200)
    ap.add_argument("--pad", type=int, default=460)
    ap.add_argument("--shots", type=int, default=10)
    ap.add_argument("--gap", type=float, default=0.25)
    args = ap.parse_args()

    dpi_aware()
    out = Path(__file__).resolve().parent / "out3"
    out.mkdir(exist_ok=True)
    for old in out.glob("pan_*.png"):
        old.unlink(missing_ok=True)

    print("监听平移…本轮会把「系统真实光标」显式画进图里（红框+放大2倍），触点画蓝圈")
    notify("正在等你 pan：截图会标注真实光标与触点", 15, "busy")

    pos = LOG.stat().st_size if LOG.exists() else 0
    deadline = time.time() + args.wait
    fired = False
    while time.time() < deadline:
        if not LOG.exists():
            time.sleep(0.2); continue
        size = LOG.stat().st_size
        if size < pos:
            pos = 0
        if size > pos:
            with open(LOG, "rb") as fh:
                fh.seek(pos)
                chunk = fh.read(size - pos)
            pos = size
            if "开始平移" in chunk.decode("utf-8", "replace"):
                fired = True
                break
        time.sleep(0.12)

    if not fired:
        print("-> 超时：没抓到平移")
        notify("没等到平移", 5, "fail")
        return 2

    ci0 = cursor_info()
    cx, cy = (ci0.ptScreenPos.x, ci0.ptScreenPos.y) if ci0 else (0, 0)
    x0 = max(0, cx - args.pad); y0 = max(0, cy - args.pad)
    w = h = args.pad * 2
    print(f"-> 平移开始，GetCursorInfo 位置({cx},{cy}) flags=0x{ci0.flags:X} "
          f"hCursor={ci0.hCursor}  截图区 ({x0},{y0})+{w}x{h}")

    seen_handles = {}
    for i in range(args.shots):
        ci = cursor_info()
        d = last_diag()
        tx = ty = None
        if d:
            tx, ty = d["tx"], d["ty"]
        grab_annotated(out / f"pan_{i:02d}.png", x0, y0, w, h, ci, tx, ty)
        hs = str(ci.hCursor) if ci else "None"
        seen_handles.setdefault(hs, 0)
        seen_handles[hs] += 1
        fl = ci.flags if ci else -1
        tag = []
        if fl & CURSOR_SHOWING: tag.append("SHOWING")
        if fl & CURSOR_SUPPRESSED: tag.append("SUPPRESSED")
        print(f"   #{i:02d} 光标({ci.ptScreenPos.x},{ci.ptScreenPos.y}) flags=0x{fl:X}[{'+'.join(tag) or '0'}] "
              f"hCursor={ci.hCursor} | 触点({tx},{ty}) 宿主报可见={d['vis'] if d else '?'}")
        time.sleep(args.gap)

    print("\n=== hCursor 句柄统计（同一轮 pan 里是否换过光标图形）===")
    for k, v in seen_handles.items():
        print(f"   hCursor {k}: {v} 次")
    print(f"   不同句柄种类 = {len(seen_handles)}  → "
          f"{'只有一种，系统没在 pan 期间切换光标图形' if len(seen_handles) == 1 else '出现过多种，需要逐帧对照'}")

    notify("收集完成", 5, "ok")
    print(f"\n-> 图在 {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
