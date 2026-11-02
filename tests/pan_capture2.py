#!/usr/bin/env python3
"""
取证（第二轮）：用户真机 pan 时，把「屏幕像素变化」和「宿主视角的触点位置」对齐。

为什么上一轮不够（2026-11-01）：
  · GDI 截屏拍不到硬件光标，也拍不到部分系统覆盖层 → 「图上没有」不能当证据；
  · OneNote 内容在滚动，整幅都在变 → 差分全是噪声，看不出触点处有没有新图元。

本轮改进：
  1. 抓图时同步解析宿主日志最后一行诊断，拿到**同一时刻**的光标/锚点/可见/触点坐标；
  2. 帧间差分给出「差异像素数 + 差异包围盒」，并算包围盒中心到触点的距离；
  3. 建议在**静止内容**上 pan（空白页/桌面），这样差异只可能来自触点视觉。

用法：
    python tests/pan_capture2.py --wait 200
    python tests/pan_capture2.py --wait 200 --pad 500 --shots 18 --gap 0.12
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


def dpi_aware():
    try:
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        pass


def cursor_pos():
    pt = wt.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def cursor_visible() -> int:
    """CURSOR_SHOWING 标志（0=被系统隐藏）。"""
    class CURSORINFO(ctypes.Structure):
        _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD), ("hCursor", wt.HANDLE),
                    ("ptScreenPos", wt.POINT)]
    ci = CURSORINFO()
    ci.cbSize = ctypes.sizeof(CURSORINFO)
    if not user32.GetCursorInfo(ctypes.byref(ci)):
        return -1
    return 1 if (ci.flags & 0x00000001) else 0


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


def grab_raw(x, y, w, h):
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
    return buf.raw


def save_png(path, w, h, raw):
    bmp_path = str(path).replace(".png", ".bmp")
    with open(bmp_path, "wb") as fh:
        hdr = ctypes.sizeof(BIH) + 14
        fh.write(b"BM" + struct.pack("<IHHI", hdr + len(raw), 0, 0, hdr))
        bi = BIH()
        bi.biSize = ctypes.sizeof(BIH)
        bi.biWidth, bi.biHeight = w, -h
        bi.biPlanes, bi.biBitCount = 1, 32
        fh.write(bytes(bi))
        fh.write(raw)
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Add-Type -AssemblyName System.Drawing;"
                    f"$b=[System.Drawing.Image]::FromFile('{bmp_path}');"
                    f"$b.Save('{path}',[System.Drawing.Imaging.ImageFormat]::Png);$b.Dispose()"],
                   capture_output=True)
    Path(bmp_path).unlink(missing_ok=True)


def diff_bbox(a, b, w, h, step=2, thr=22):
    """返回 (差异像素数, 包围盒(minx,miny,maxx,maxy))，采样步长 step。"""
    n = 0
    minx, miny, maxx, maxy = w, h, -1, -1
    for y in range(0, h, step):
        base = y * w * 4
        for x in range(0, w, step):
            k = base + x * 4
            if abs(a[k] - b[k]) > thr or abs(a[k + 1] - b[k + 1]) > thr or abs(a[k + 2] - b[k + 2]) > thr:
                n += 1
                if x < minx: minx = x
                if x > maxx: maxx = x
                if y < miny: miny = y
                if y > maxy: maxy = y
    return n, (minx, miny, maxx, maxy)


def last_diag():
    """读日志最后一行 [TouchPan][诊断]，返回解析结果或 None。"""
    if not LOG.exists():
        return None
    with open(LOG, "rb") as fh:
        fh.seek(max(0, LOG.stat().st_size - 8192))
        tail = fh.read().decode("utf-8", "replace")
    last = None
    for line in tail.splitlines():
        if "[TouchPan][诊断]" in line:
            last = line
    if not last:
        return None
    m = RE_DIAG.search(last)
    if not m:
        return None
    g = [int(v) for v in m.groups()]
    return dict(cx=g[0], cy=g[1], ax=g[2], ay=g[3], dev=g[4], vis=g[5], tx=g[6], ty=g[7], line=last)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--wait", type=float, default=200)
    ap.add_argument("--pad", type=int, default=500)
    ap.add_argument("--shots", type=int, default=18)
    ap.add_argument("--gap", type=float, default=0.12)
    args = ap.parse_args()

    dpi_aware()
    out = Path(__file__).resolve().parent / "out"
    out.mkdir(exist_ok=True)
    for old in out.glob("live_*.png"):
        old.unlink(missing_ok=True)

    print(f"监听 {LOG.name}…（不注入、不动光标、不抢焦点）")
    notify("正在等你 pan：只截图，不动你的桌面", 15, "busy")

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

    cx, cy = cursor_pos()
    x0 = max(0, cx - args.pad); y0 = max(0, cy - args.pad)
    w = h = args.pad * 2
    print(f"-> 平移开始，光标({cx},{cy})，截图区域 ({x0},{y0})+{w}x{h}")

    frames, metas = [], []
    for i in range(args.shots):
        raw = grab_raw(x0, y0, w, h)
        save_png(out / f"live_{i:02d}.png", w, h, raw)
        d = last_diag()
        frames.append(raw); metas.append(d)
        if d:
            print(f"   #{i:02d} 光标({d['cx']},{d['cy']}) 锚点({d['ax']},{d['ay']}) "
                  f"偏差={d['dev']} 可见={d['vis']} 触点({d['tx']},{d['ty']}) "
                  f"| GetCursorInfo可见={cursor_visible()}")
        else:
            print(f"   #{i:02d} 无诊断行 GetCursorInfo可见={cursor_visible()}")
        time.sleep(args.gap)

    print("\n=== 相邻帧差分（找新增图元）===")
    for i in range(1, len(frames)):
        n, (mnx, mny, mxx, mxy) = diff_bbox(frames[i - 1], frames[i], w, h)
        tot = (w // 2) * (h // 2)
        if mxx < 0:
            print(f"   #{i-1:02d}->#{i:02d} 无差异")
            continue
        bx, by = x0 + (mnx + mxx) // 2, y0 + (mny + mxy) // 2
        d = metas[i]
        dist = ""
        if d:
            dist = f" 包围盒中心到触点 {max(abs(bx - d['tx']), abs(by - d['ty']))}px"
        print(f"   #{i-1:02d}->#{i:02d} 差异 {n}/{tot} 像素({n*100//max(1,tot)}%) "
              f"包围盒 屏幕({x0+mnx},{y0+mny})-({x0+mxx},{y0+mxy}) 中心({bx},{by}){dist}")

    notify("收集完成，桌面未受影响", 5, "ok")
    print("\n-> 完成，图在 tests/out/live_*.png")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
