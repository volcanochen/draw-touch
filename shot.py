"""抓取指定标题的窗口并保存为 PNG（纯 ctypes，不依赖 PIL / Add-Type）。

用法: python shot.py "SpenTablet 设置" out.png
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import zlib

u = ctypes.windll.user32
g = ctypes.windll.gdi32

# 必须让自己也 DPI 感知，否则 GetWindowRect 返回的是虚拟化后的尺寸，截不到真实区域
try:
    ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass


class RECT(ctypes.Structure):
    _fields_ = [('l', wt.LONG), ('t', wt.LONG), ('r', wt.LONG), ('b', wt.LONG)]


class BIH(ctypes.Structure):
    _fields_ = [('biSize', wt.DWORD), ('biWidth', wt.LONG), ('biHeight', wt.LONG),
                ('biPlanes', wt.WORD), ('biBitCount', wt.WORD), ('biCompression', wt.DWORD),
                ('biSizeImage', wt.DWORD), ('biX', wt.LONG), ('biY', wt.LONG),
                ('biClrUsed', wt.DWORD), ('biClrImportant', wt.DWORD)]


def grab(title, out):
    h = u.FindWindowW(None, title)
    if not h:
        print("not found:", title)
        return False
    rc = RECT()
    u.GetWindowRect(h, ctypes.byref(rc))
    w, ht = rc.r - rc.l, rc.b - rc.t
    dpi = u.GetDpiForWindow(h) if hasattr(u, "GetDpiForWindow") else 0
    print(f"window {w}x{ht} dpi={dpi}")
    hdc = u.GetWindowDC(h)
    mdc = g.CreateCompatibleDC(hdc)
    bmp = g.CreateCompatibleBitmap(hdc, w, ht)
    g.SelectObject(mdc, bmp)
    u.PrintWindow(h, mdc, 2)
    bi = BIH()
    bi.biSize = 40
    bi.biWidth = w
    bi.biHeight = -ht
    bi.biPlanes = 1
    bi.biBitCount = 32
    buf = ctypes.create_string_buffer(w * ht * 4)
    g.GetDIBits(mdc, bmp, 0, ht, buf, ctypes.byref(bi), 0)
    g.DeleteObject(bmp)
    g.DeleteDC(mdc)
    u.ReleaseDC(h, hdc)
    raw = ctypes.string_at(buf, w * ht * 4)
    rows = []
    for y in range(ht):
        line = raw[y * w * 4:(y + 1) * w * 4]
        rgb = bytearray(w * 3)
        for i in range(w):
            rgb[i * 3] = line[i * 4 + 2]
            rgb[i * 3 + 1] = line[i * 4 + 1]
            rgb[i * 3 + 2] = line[i * 4]
        rows.append(bytes(rgb))
    data = b"".join(b"\x00" + x for x in rows)

    def chunk(t, d):
        c = t + d
        return struct.pack(">I", len(d)) + c + struct.pack(">I", zlib.crc32(c) & 0xffffffff)

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, ht, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(data, 6))
           + chunk(b"IEND", b""))
    open(out, "wb").write(png)
    print("saved", out, w, "x", ht)
    return True


if __name__ == "__main__":
    title = sys.argv[1] if len(sys.argv) > 1 else "SpenTablet 设置"
    out = sys.argv[2] if len(sys.argv) > 2 else "shot.png"
    sys.exit(0 if grab(title, out) else 1)
