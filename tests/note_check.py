#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
备注（NoteOverlay）桌面笔迹探针。

模拟手机直连 PC host：开备注 → 画一笔红色竖线（归一化坐标）→ 截屏对比。
不移动光标、不点击任何窗口；结束后清空并关闭备注，恢复原状态。

用法：
  python note_check.py snap  <out.png>            # 截取整块虚拟屏幕
  python note_check.py draw                       # 连 host 画一笔（约 1.5 秒）
  python note_check.py diff <before.png> <after.png>  # 对比两图找新增红色像素
"""
import socket
import struct
import sys
import time
import ctypes
import argparse
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import os as _os
if not _os.environ.get("SPEN_NO_NOTIFY"):
    from desk_notify import notify

MAGIC = b"SP"
T_NOTE, T_PEN_DOWN, T_PEN_MOVE, T_PEN_UP = 0x0D, 0x01, 0x02, 0x03
NOTE_OFF, NOTE_ON, NOTE_UNDO, NOTE_CLEAR, NOTE_COLOR = 0, 1, 2, 3, 4


def frame(ftype: int, payload: bytes) -> bytes:
    return MAGIC + bytes([1, ftype]) + struct.pack("<H", len(payload)) + payload


def f32(*vals) -> bytes:
    return b"".join(struct.pack("<f", v) for v in vals)


def pen_frame(ftype: int, x: float, y: float, pressure: float = 0.6) -> bytes:
    return frame(ftype, f32(x, y, pressure, 0.0, 0.0) + struct.pack("<HH", 1, 0))


def note_frame(action: int, arg: float = 0.0) -> bytes:
    return frame(T_NOTE, f32(float(action), arg))


def virtual_screen():
    # 必须用 PMv2（与 host 同一感知级别）：system-aware 在混合 DPI 多屏下
    # 的 GetSystemMetrics 是按主屏 DPI 折算的怪值，截图区域会抓错。
    if not ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
        ctypes.windll.user32.SetProcessDPIAware()
    u = ctypes.windll.user32
    return (u.GetSystemMetrics(76), u.GetSystemMetrics(77),
            u.GetSystemMetrics(78), u.GetSystemMetrics(79))


def snap(out: str):
    vx, vy, vw, vh = virtual_screen()
    print(f"虚拟屏幕: ({vx},{vy}) {vw}x{vh}")
    gdi32, user32 = ctypes.windll.gdi32, ctypes.windll.user32
    hdc_screen = user32.GetDC(None)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    # BITMAPINFOHEADER（自上而下）
    class BMIH(ctypes.Structure):
        _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                    ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                    ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                    ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                    ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                    ("biClrImportant", ctypes.c_uint32)]
    bi = BMIH(ctypes.sizeof(BMIH), vw, -vh, 1, 32, 0, 0, 0, 0, 0, 0)
    bits = ctypes.c_void_p()
    dib = gdi32.CreateDIBSection(hdc_screen, ctypes.byref(bi), 0, ctypes.byref(bits), None, 0)
    gdi32.SelectObject(hdc_mem, dib)
    gdi32.BitBlt(hdc_mem, 0, 0, vw, vh, hdc_screen, vx, vy, 0x00CC0020)  # SRCCOPY
    # 存成 24 位 BMP（简单起见，PIL 无依赖转换不做了，直接写 BMP）
    import os
    row = vw * 3
    pad = (4 - row % 4) % 4
    img_size = (row + pad) * vh
    hdr = b"BM" + struct.pack("<IHHI", 54 + img_size, 0, 0, 54)
    ih = struct.pack("<IiiHHIIiiII", 40, vw, vh, 1, 24, 0, img_size, 0, 0, 0, 0)
    buf = (ctypes.c_char * (vw * vh * 4)).from_address(bits.value)
    with open(out, "wb") as f:
        f.write(hdr + ih)
        for y in range(vh - 1, -1, -1):  # BMP 自下而上
            rowdata = bytearray()
            base = y * vw * 4
            for x in range(vw):
                b0, b1, b2, _ = buf[base + x * 4: base + x * 4 + 4]
                rowdata += bytes((b0, b1, b2))
            f.write(bytes(rowdata) + b"\x00" * pad)
    gdi32.DeleteObject(dib)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(None, hdc_screen)
    print(f"已保存 {out} ({os.path.getsize(out)} 字节)")


def draw(host: str, port: int, keep: bool = False):
    if not _os.environ.get("SPEN_NO_NOTIFY"):
        notify("正在测试：备注笔迹探针将在桌面画一笔", seconds=6, kind="busy")
    try:
        _draw_impl(host, port, keep)
    finally:
        if not _os.environ.get("SPEN_NO_NOTIFY"):
            notify("测试结束，桌面已交还", seconds=5, kind="ok")


def _draw_impl(host: str, port: int, keep: bool = False):
    s = socket.create_connection((host, port), timeout=5)
    s.sendall(note_frame(NOTE_ON))
    s.sendall(note_frame(NOTE_COLOR, 0.0))  # 红
    # 画一条竖线：归一化 (0.5, 0.25) → (0.5, 0.75)
    s.sendall(pen_frame(T_PEN_DOWN, 0.5, 0.25))
    for i in range(1, 41):
        s.sendall(pen_frame(T_PEN_MOVE, 0.5, 0.25 + 0.5 * i / 40))
        time.sleep(0.01)
    s.sendall(pen_frame(T_PEN_UP, 0.5, 0.75))
    time.sleep(0.8)
    if not keep:
        # 恢复原状：清空 + 关闭
        s.sendall(note_frame(NOTE_CLEAR))
        s.sendall(note_frame(NOTE_OFF))
        time.sleep(0.2)
    s.close()
    print("已画一笔" + ("（保留在桌面上）" if keep else "并恢复（清空 + 关闭备注）"))


def diff(before: str, after: str):
    def load(p):
        data = Path(p).read_bytes()
        off = struct.unpack("<I", data[10:14])[0]
        w = struct.unpack("<i", data[18:22])[0]
        h = struct.unpack("<i", data[22:26])[0]
        row = (w * 3 + 3) // 4 * 4
        px = {}
        for yy in range(h):
            base = off + yy * row
            for xx in range(w):
                i = base + xx * 3
                b, g, r = data[i], data[i + 1], data[i + 2]
                px[(xx, h - 1 - yy)] = (r, g, b)
        return w, h, px
    w, h, pa = load(before)
    _, _, pb = load(after)
    red = []
    for k in pb:
        if pb[k] != pa.get(k):
            r, g, b = pb[k]
            if r > 170 and g < 130 and b < 130:
                red.append(k)
    print(f"新增红色像素: {len(red)}")
    if red:
        xs = [p[0] for p in red]
        ys = [p[1] for p in red]
        print(f"包围盒: x[{min(xs)},{max(xs)}] y[{min(ys)},{max(ys)}]")
        print("结论: 笔迹已显示在桌面上")
    else:
        print("结论: 桌面上没有出现笔迹（备注层失效）")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["snap", "draw", "diff"])
    ap.add_argument("a", nargs="?", default="")
    ap.add_argument("b", nargs="?", default="")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--keep", action="store_true", help="画完后不清理（供窗口检查）")
    args = ap.parse_args()
    if args.cmd == "snap":
        snap(args.a)
    elif args.cmd == "draw":
        draw(args.host, args.port, args.keep)
    elif args.cmd == "diff":
        diff(args.a, args.b)
