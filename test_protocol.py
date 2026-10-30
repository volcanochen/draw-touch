#!/usr/bin/env python3
"""
PC 端协议自测脚本：直连 127.0.0.1:6000，模拟手机发送 MODE / MOUSE / PEN / PAN 帧，
用来验证「手指模式互斥」在 PC 侧的行为：
  - MODE=1 → 切到鼠标模式，桌面范围框应隐藏
  - MOUSE_MOVE → 光标真的移动
  - MOUSE_LEFT_CLICK / MOUSE_RIGHT_CLICK → 真的发出左键/右键
  - 鼠标模式下 PAN 帧必须被忽略（互斥）
  - MODE=0 → 回到数位板模式，范围框恢复显示
"""
import ctypes
import socket
import struct
import sys
import time

HOST, PORT = "127.0.0.1", 6000

TYPE_PEN_DOWN = 0x01
TYPE_PEN_MOVE = 0x02
TYPE_PEN_UP = 0x03
TYPE_PEN_HOVER = 0x04
TYPE_HELLO = 0x05
TYPE_PAN = 0x06
TYPE_MOUSE = 0x07
TYPE_MODE = 0x08

MOUSE_MOVE, MOUSE_LEFT_CLICK, MOUSE_RIGHT_CLICK = 0, 3, 4


def frame(ftype: int, payload: bytes) -> bytes:
    assert len(payload) == 24
    return b"SP" + bytes([1, ftype]) + struct.pack("<H", 24) + payload


def payload(*floats) -> bytes:
    b = b"".join(struct.pack("<f", f) for f in floats)
    return b + b"\x00" * (24 - len(b))


def mode_frame(mode: int) -> bytes:
    return frame(TYPE_MODE, payload(float(mode)))


def mouse_frame(kind: int, dx: float, dy: float) -> bytes:
    return frame(TYPE_MOUSE, payload(float(kind), dx, dy))


def pen_frame(ftype: int, x: float, y: float, p: float = 0.5) -> bytes:
    # x, y, pressure, tiltX, tiltY, buttons(u16), reserved(u16)
    b = struct.pack("<fffff", x, y, p, 0.0, 0.0) + struct.pack("<HH", 0, 0)
    return frame(ftype, b)


def pan_frame(dx: float, dy: float) -> bytes:
    return frame(TYPE_PAN, payload(dx, dy))


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


def cursor():
    """返回**物理像素**坐标。
    注意：本脚本是 DPI 无感知进程，GetCursorPos 给的是逻辑坐标（如 175% 缩放下会偏小），
    而主机按物理像素计算，所以这里要乘回缩放倍率才能对得上。"""
    p = POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(p))
    u = ctypes.windll.user32
    # SM_XVIRTUALSCREEN=76 Y=77 CX=78 CY=79（本进程视角 = 逻辑值）
    logical_w = u.GetSystemMetrics(78)
    scale = PHYSICAL_W / logical_w if logical_w else 1.0
    return int(round(p.x * scale)), int(round(p.y * scale))


PHYSICAL_W = 3072   # 主机日志里的虚拟屏幕物理宽度（相同步请按实际改）


def find_region_frame():
    """返回 (找到?, 可见?) —— 用窗口标题检索桌面范围框。"""
    user32 = ctypes.windll.user32
    found = {"hwnd": 0}

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def enum(hwnd, _lparam):
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == "DrawTouchRegionFrame":
                found["hwnd"] = hwnd
                return False
        return True

    user32.EnumWindows(enum, 0)
    if not found["hwnd"]:
        return False, False
    return True, bool(user32.IsWindowVisible(found["hwnd"]))


def main():
    s = socket.create_connection((HOST, PORT), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    print(f"已连接到 {HOST}:{PORT}")

    def send(b, note=""):
        s.sendall(b)
        if note:
            print(f"  -> 发送 {note}")
        time.sleep(0.35)  # 等 PC 处理完并落日志

    print("\n[1] 初始状态应为数位板模式")
    ok, vis = find_region_frame()
    print(f"  范围框: 存在={ok} 可见={vis}")

    print("\n[2] 发送 MODE=1（切到鼠标模式）")
    send(mode_frame(1), "MODE=1")
    ok, vis = find_region_frame()
    print(f"  范围框: 存在={ok} 可见={vis}  -> 期望 可见=False（框被取消）")

    print("\n[3] 鼠标模式：手指滑动 → 光标右移 0.1（≈307px）")
    x0, y0 = cursor()
    for _ in range(5):
        send(mouse_frame(MOUSE_MOVE, 0.02, 0.0))
    x1, y1 = cursor()
    print(f"  光标 ({x0},{y0}) -> ({x1},{y1})  位移 dx={x1-x0} dy={y1-y0}  -> 期望 dx≈+307")

    print("\n[4] 鼠标模式：手指下滑 → 光标下移 0.1（≈192px）")
    for _ in range(5):
        send(mouse_frame(MOUSE_MOVE, 0.0, 0.02))
    x2, y2 = cursor()
    print(f"  光标 ({x1},{y1}) -> ({x2},{y2})  位移 dx={x2-x1} dy={y2-y1}  -> 期望 dy≈+192")

    print("\n[5] 鼠标模式：单击 → 左键；双击 → 右键")
    send(mouse_frame(MOUSE_LEFT_CLICK, 0, 0), "MOUSE_LEFT_CLICK")
    send(mouse_frame(MOUSE_RIGHT_CLICK, 0, 0), "MOUSE_RIGHT_CLICK")

    print("\n[6] 互斥检查：鼠标模式下发送 PAN 帧（应被忽略）")
    send(pan_frame(0.3, 0.0), "PAN(0.3,0)")

    print("\n[7] 鼠标模式下 S Pen 落笔 → 绝对定位鼠标笔")
    send(pen_frame(TYPE_PEN_DOWN, 0.5, 0.5, 0.6), "PEN_DOWN(0.5,0.5)")
    send(pen_frame(TYPE_PEN_UP, 0.5, 0.5, 0.0), "PEN_UP(0.5,0.5)")
    x3, y3 = cursor()
    print(f"  光标 ({x3},{y3})  -> 期望 ≈(1536,960)")

    print("\n[8] 发送 MODE=0（回到数位板模式）")
    send(mode_frame(0), "MODE=0")
    ok, vis = find_region_frame()
    print(f"  范围框: 存在={ok} 可见={vis}  -> 期望 可见=True（框恢复）")

    print("\n[9] 数位板模式：PAN 应生效")
    send(pan_frame(0.1, 0.0), "PAN(0.1,0)")

    s.close()
    print("\n完成。请比对 PC 端 DrawTouchHost.log 中的 [Mode]/[Mouse]/[MousePen]/[Pan] 记录。")


if __name__ == "__main__":
    sys.exit(main())
