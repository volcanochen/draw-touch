#!/usr/bin/env python3
"""
取证：注入双指平移时，**屏幕像素上到底多了什么**。

背景（2026-11-01）：注入 62 帧实测光标位移 0px、低级钩子只有我们自己的事件、
真实设备 raw 计数不涨 —— 说明「跟着手指走的东西」不是光标。那它只能是系统在
触点处绘制的触摸视觉（触摸指示器 / 触摸指针）。本脚本注入并保持触点，同时在
触点附近截图，用"无触摸基线 vs 触摸中"的像素差异来判断：

  · 触点周围出现新图元  → 系统触摸视觉，钉光标治不了，只能关它或换路径
  · 触点周围毫无变化    → 该排除，回到光标本身继续查

用法：
    python tests/pan_visual_probe.py                 # 触点放在屏幕中央附近
    python tests/pan_visual_probe.py --hold 3.0

产出：tests/out/pan_visual_*.png（基线 / 触摸中），并打印触点坐标。
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import socket
import struct
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from desk_notify import notify
except Exception:
    def notify(*a, **k):
        return False

PROTO_VER = 4
TYPE_HELLO, TYPE_MOUSE = 0x05, 0x07
MOUSE_PAN = 17

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)


def dpi_aware():
    try:
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))   # PerMonitorV2
    except Exception:
        pass


def cursor_pos():
    pt = wt.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def grab(region):
    """截一块屏幕区域，返回 (w, h, bytes BGR)。用 GDI BitBlt（主屏可用）。"""
    x, y, w, h = region
    hdc_screen = user32.GetDC(None)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    gdi32.SelectObject(hdc_mem, bmp)
    gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, x, y, 0x00CC0020)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                    ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth, bi.biHeight = w, -h        # 负高度 = 自上而下
    bi.biPlanes, bi.biBitCount = 1, 32
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(hdc_mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(None, hdc_screen)
    return w, h, buf.raw


def save_png(path, w, h, raw):
    """不依赖 PIL：用 .NET 存 PNG。"""
    import subprocess
    bmp_path = str(path).replace(".png", ".bmp")

    class BIH(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                    ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

    fh = open(bmp_path, "wb")
    try:
        hdr = ctypes.sizeof(BIH) + 14
        fh.write(b"BM" + struct.pack("<IHHI", hdr + len(raw), 0, 0, hdr))
        bi = BIH()
        bi.biSize = ctypes.sizeof(BIH)
        bi.biWidth, bi.biHeight = w, -h
        bi.biPlanes, bi.biBitCount = 1, 32
        fh.write(bytes(bi))
        fh.write(raw)
    finally:
        fh.close()
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Add-Type -AssemblyName System.Drawing;"
                    f"$b=[System.Drawing.Image]::FromFile('{bmp_path}');"
                    f"$b.Save('{path}',[System.Drawing.Imaging.ImageFormat]::Png);$b.Dispose()"],
                   capture_output=True)
    Path(bmp_path).unlink(missing_ok=True)


def frame(ftype, payload):
    return b"SP" + bytes([PROTO_VER, ftype]) + struct.pack("<H", len(payload)) + payload


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--hold", type=float, default=4.0, help="触点保持按住的秒数")
    ap.add_argument("--pad", type=int, default=260, help="截图半个边长（像素）")
    args = ap.parse_args()

    dpi_aware()
    out = Path(__file__).resolve().parent / "out"
    out.mkdir(exist_ok=True)

    sx, sy = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
    # 触点是在**当前光标位置**附近建立的（宿主 UpdateTouchPan 以光标为起点，跨度 ±60），
    # 所以截图必须以光标为中心，不能取屏幕中央。
    cx, cy = cursor_pos()
    region = (max(0, cx - args.pad), max(0, cy - args.pad), args.pad * 2, args.pad * 2)
    print(f"屏幕 {sx}x{sy}，截图区域 {region}（以光标为中心），光标位置 {cursor_pos()}")

    notify("正在测试：注入触摸并截屏取证（不会点任何东西，约 12 秒）", 20, "busy")
    time.sleep(0.5)

    w, h, base_raw = grab(region)
    save_png(out / "pan_visual_base.png", w, h, base_raw)
    print("-> 基线截图已存 pan_visual_base.png")

    stop = threading.Event()

    def feeder():
        """持续发 PAN 帧保住「按住」状态；位移为 0，让触点在原地不动便于对比。"""
        try:
            s = socket.create_connection((args.host, args.port), timeout=5)
            s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            s.sendall(frame(TYPE_HELLO, struct.pack("<9f", 1494, 614, 1, 0, -1, -1, -1, PROTO_VER, 1)))
            time.sleep(0.4)
            t0 = time.time()
            # 每帧给一点点位移（≈0.7px）：完全静止会被系统判成 press-and-hold，
            # 有可能在落点窗口弹出右键菜单 —— 测试绝不能给用户桌面留垃圾。
            while not stop.is_set():
                s.sendall(frame(TYPE_MOUSE, struct.pack("<3f", MOUSE_PAN, 0.002, 0.001)))
                time.sleep(0.05)
            s.close()
        except Exception as e:
            print("feeder 结束：", e)

    th = threading.Thread(target=feeder, daemon=True)
    th.start()

    shots = []
    t_end = time.time() + args.hold
    i = 0
    while time.time() < t_end:
        time.sleep(0.55)
        i += 1
        w, h, raw = grab(region)
        p = out / f"pan_visual_hold{i}.png"
        save_png(p, w, h, raw)
        shots.append(p)
        print(f"-> 触摸中截图 {p.name} 光标={cursor_pos()}")

    stop.set()
    time.sleep(0.5)

    # 像素差异统计（同一区域，基线 vs 触摸中）
    def diff_count(a, b):
        n = 0
        for k in range(0, min(len(a), len(b)), 4):
            if abs(a[k] - b[k]) > 24 or abs(a[k + 1] - b[k + 1]) > 24 or abs(a[k + 2] - b[k + 2]) > 24:
                n += 1
        return n

    tot = region[2] * region[3]
    for p in shots:
        import subprocess
        raw = subprocess.run(["powershell", "-NoProfile", "-Command",
                              f"$b=[System.Drawing.Bitmap]::FromFile('{p}');"
                              f"$ms=New-Object System.IO.MemoryStream;"
                              f"$b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Bmp);"
                              f"$b.Dispose();"
                              f"[Convert]::ToBase64String($ms.ToArray())"],
                             capture_output=True, text=True).stdout.strip()
        if not raw:
            continue
        data = __import__("base64").b64decode(raw)
        off = struct.unpack("<I", data[10:14])[0]
        pix = data[off:]
        print(f"   {p.name}: 与基线差异像素 {diff_count(base_raw, pix)} / {tot}")

    notify("测试结束，桌面已交还", 5, "ok")
    print("-> 完成，截图在 tests/out/")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
