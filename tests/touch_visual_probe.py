#!/usr/bin/env python3
"""
触摸视觉取证（第四轮）：自己注入 + 自己抓图，不依赖用户当眼睛。

为什么前几轮都判定不了（2026-11-01）：
  · GDI BitBlt 默认标志 SRCCOPY 不含 CAPTUREBLT，**分层/叠加窗口的图元会被漏掉**；
  · 硬件光标本来就不在桌面表面里（所以要另外 DrawIconEx 画上去）。
  这一轮补上 CAPTUREBLT(0x40000000)，尽量把叠加层也拉进来。

实验设计（关键）：
  注入「接触存在、但位移恒为 0」的 MOUSE_PAN 帧 ——
  触点静止、画面完全不滚动 ⇒ 画面上任何新增图元都只能来自**系统触摸视觉**，
  不会和内容滚动混在一起（前几轮差分被内容滚动吃掉就是栽在这里）。

用法：
    python tests/touch_visual_probe.py                 # 30 帧静止接触
    python tests/touch_visual_probe.py --frames 60 --pad 420
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import socket
import struct
import sys
import time
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from desk_notify import notify
except Exception:                                   # 提示失败绝不影响测试
    def notify(*a, **k):
        return False

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

PROTO_VER = 4
TYPE_HELLO, TYPE_MOUSE = 0x05, 0x07
MOUSE_PAN, MOUSE_FINGER = 17, 15

SRCCOPY = 0x00CC0020
CAPTUREBLT = 0x40000000

OUT = Path(__file__).resolve().parent / "out4"


def dpi_aware():
    try:
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        pass


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD)]


def cursor_pos():
    p = wt.POINT()
    if not user32.GetCursorPos(ctypes.byref(p)):
        return None
    return p.x, p.y


def grab(x0, y0, w, h, captureblt=True):
    """抓一块屏幕区域，返回 BGRA top-down 的 bytes。"""
    hdc_screen = user32.GetDC(None)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    gdi32.SelectObject(hdc_mem, bmp)
    rop = SRCCOPY | (CAPTUREBLT if captureblt else 0)
    gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, x0, y0, rop)

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


def write_png(path, w, h, bgra):
    rows = []
    for y in range(h):
        row = bgra[y * w * 4:(y + 1) * w * 4]
        out = bytearray(w * 4)
        out[0::4] = row[2::4]
        out[1::4] = row[1::4]
        out[2::4] = row[0::4]
        out[3::4] = b"\xff" * w
        rows.append(b"\x00" + bytes(out))
    raw = b"".join(rows)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 6))
           + chunk(b"IEND", b""))
    Path(path).write_bytes(png)


def diff(a, b, w, h):
    """返回 (差异像素数, 包围盒 x0,y0,x1,y1)。按 4 字节像素比较。"""
    n = 0
    x0 = y0 = 10 ** 9
    x1 = y1 = -1
    for y in range(h):
        base = y * w * 4
        row = b[base:base + w * 4]
        old = a[base:base + w * 4]
        if row == old:
            continue
        for x in range(w):
            i = x * 4
            if row[i:i + 3] != old[i:i + 3]:
                n += 1
                if x < x0:
                    x0 = x
                if x > x1:
                    x1 = x
                if y < y0:
                    y0 = y
                if y > y1:
                    y1 = y
    if n == 0:
        return 0, None
    return n, (x0, y0, x1, y1)


def frame(ftype, payload):
    return b"SP" + bytes([PROTO_VER, ftype]) + struct.pack("<H", len(payload)) + payload


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--frames", type=int, default=30)
    ap.add_argument("--pad", type=int, default=360, help="抓图半宽（像素）")
    ap.add_argument("--gap", type=float, default=0.12)
    ap.add_argument("--no-blt", action="store_true", help="不带 CAPTUREBLT 做对照")
    args = ap.parse_args()

    dpi_aware()
    OUT.mkdir(exist_ok=True)

    cp = cursor_pos()
    if cp is None:
        print("!! 读不到光标位置（桌面被锁？）—— 测试作废，绝不算通过")
        return 2
    cx, cy = cp
    sx, sy = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
    w = h = args.pad * 2
    x0 = max(0, min(sx - w, cx - args.pad))
    y0 = max(0, min(sy - h, cy - args.pad))
    print(f"光标 {cx},{cy}  屏幕 {sx}x{sy}  抓图区 ({x0},{y0}) {w}x{h}  "
          f"CAPTUREBLT={not args.no_blt}")

    notify("正在测试：注入静止触摸并抓屏（画面不会滚动）",
           int(args.frames * args.gap) + 14, "busy")
    time.sleep(0.5)

    # ① 基线：连续两帧，得到「静止时的噪声底」
    b1 = grab(x0, y0, w, h, not args.no_blt)
    time.sleep(0.15)
    b2 = grab(x0, y0, w, h, not args.no_blt)
    noise, _ = diff(b1, b2, w, h)
    print(f"基线帧间差异 = {noise} px（这就是噪声底，后面要和它比）")
    write_png(OUT / "base.png", w, h, b1)

    # ② 连宿主，发「静止接触」的 pan 帧
    s = socket.create_connection((args.host, args.port), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.sendall(frame(TYPE_HELLO, struct.pack("<9f", 1494, 614, 1, 0, -1, -1, -1, PROTO_VER, 1)))
    time.sleep(0.5)

    shots = []
    print(f"-> 注入 {args.frames} 帧静止 pan（dx=dy=0）")
    for i in range(args.frames):
        s.sendall(frame(TYPE_MOUSE, struct.pack("<3f", MOUSE_PAN, 0.0, 0.0)))
        time.sleep(args.gap)
        if i in (0, 2, 5, 9, 14, 20, args.frames - 1):
            img = grab(x0, y0, w, h, not args.no_blt)
            n, box = diff(b1, img, w, h)
            shots.append((i, n, box, img))
            print(f"   帧{i:>3}: 与基线差异 {n:>6} px  包围盒={box}")
            write_png(OUT / f"contact_{i:02d}.png", w, h, img)

    s.sendall(frame(TYPE_MOUSE, struct.pack("<3f", MOUSE_FINGER, 0.0, 0.0)))
    time.sleep(0.5)

    # ③ 抬手后再抓一帧：视觉应当随时间消失
    after = grab(x0, y0, w, h, not args.no_blt)
    n, box = diff(b1, after, w, h)
    print(f"抬手后: 与基线差异 {n} px  包围盒={box}")
    write_png(OUT / "after_up.png", w, h, after)
    s.close()

    # 结论提示（判据：差异必须显著高于噪声底，且包围盒应落在触点附近）
    best = max(shots, key=lambda t: t[1]) if shots else (0, 0, None, None)
    print()
    print(f"最大差异帧 = 帧{best[0]}  {best[1]} px  包围盒={best[2]}")
    print(f"噪声底 = {noise} px")
    print(f"触点应在 光标({cx},{cy}) 上下各 60px，即 ({cx},{cy-60}) 与 ({cx},{cy+60})；"
          f"换算到图内 y = {cy - 60 - y0} 与 {cy + 60 - y0}")
    print("图已存到 tests/out4/")

    notify("测试结束，桌面已交还", 5, "ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
