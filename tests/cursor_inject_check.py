#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
光标注入实测：验证「前台是管理员权限程序时，提权后的 host 仍能移动光标」。

做法：记录注入前光标位置 → 通过 TCP 让 host 走真实的鼠标注入路径（TYPE_MOUSE 移动帧）
→ 再记录注入后位置。位置由 tests/CursorProbe（--cursor）读取，避免自己重复实现 P/Invoke。

用法：
    python tests/cursor_inject_check.py            # 默认增量 +240,+120 像素
    python tests/cursor_inject_check.py --dx 300 --dy 0

退出码：0=光标确实移动了，1=没动（注入被拦）。
"""
import argparse
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path

MAGIC, VER = b"SP", 1
T_HELLO, T_MOUSE, T_MODE = 0x05, 0x07, 0x08
M_MOVE = 0

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
from desk_notify import notify  # noqa: E402  测试期间的桌面横幅（开始 / 结束）

PROBE = ROOT / "CursorProbe" / "bin" / "Release" / "net8.0-windows" / "CursorProbe.dll"
DOTNET = Path(r"C:\Users\adminlocal\.dotnet\dotnet.exe")


def frame(t, payload):
    return MAGIC + bytes([VER, t]) + struct.pack("<H", len(payload)) + payload


def f32(*v):
    return b"".join(struct.pack("<f", x) for x in v)


def cursor_pos():
    out = subprocess.run([str(DOTNET), str(PROBE), "--cursor"],
                         capture_output=True, text=True, timeout=20).stdout.strip()
    x, y = out.split(",")
    return int(x), int(y)


def inject(sock, dx, dy):
    """按方向发一串移动帧（每帧 0.02 归一化步长，增益=1 时约 38px/帧）。"""
    step = 0.02
    n = max(1, int(round(max(abs(dx), abs(dy)) / (step * 1920))))
    scale = max(abs(dx), abs(dy))
    sx = (dx / scale) * step if dx else 0.0
    sy = (dy / scale) * step if dy else 0.0
    for _ in range(n):
        sock.sendall(frame(T_MOUSE, f32(M_MOVE, sx, sy)))
        time.sleep(0.02)
    time.sleep(0.3)
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--dx", type=int, default=240)
    ap.add_argument("--dy", type=int, default=120)
    a = ap.parse_args()

    if not PROBE.exists():
        print(f"缺探针：{PROBE}\n先执行 (cd tests/CursorProbe && dotnet build -c Release)")
        return 2

    # 这个测试会真的把系统光标推走 —— 按用户的规矩：不先问他，但让他看得见
    notify(f"光标注入测试开始：会把鼠标移动 ({a.dx},{a.dy})", 12, "busy")

    s = socket.create_connection((a.host, a.port), timeout=5)
    # 握手：否则 host 不会走完整初始化（映射/范围框都依赖 HELLO）
    s.sendall(frame(T_HELLO, f32(1494, 614, 2.433, 1.0)))
    time.sleep(0.25)
    s.sendall(frame(T_MODE, f32(1.0)))     # 触摸板模式
    time.sleep(0.25)

    x0, y0 = cursor_pos()
    print(f"注入前光标: ({x0},{y0})")

    n = inject(s, a.dx, a.dy)
    x1, y1 = cursor_pos()
    # 光标贴着屏幕边缘时，朝外的方向本来就推不动（会被夹住）→ 反向再来一次
    if (x1, y1) == (x0, y0):
        print(f"朝 ({a.dx},{a.dy}) 推不动（可能已到屏幕边缘），反向重试…")
        a.dx, a.dy = -a.dx, -a.dy
        x0, y0 = x1, y1
        n = inject(s, a.dx, a.dy)
        x1, y1 = cursor_pos()

    s.close()
    dx_got, dy_got = x1 - x0, y1 - y0
    print(f"注入后光标: ({x1},{y1})  位移=({dx_got},{dy_got})  帧数={n}  方向=({a.dx},{a.dy})")

    # 注意：探针与 host 的 DPI 感知不同，同一个光标在两边读到的坐标差一个固定比例
    #（实测 1244 ↔ 2177，比值 1.75 = 系统缩放）。所以这里只校验「方向对不对、确实动了」，
    # 精确位移请看 host 日志里 SPEN_DEBUG_MOVE=1 时的 [MoveBy] p→目标→实际。
    if abs(dx_got) <= 2 and abs(dy_got) <= 2:
        print("FAIL 光标完全没动 → 注入被拦截（前台程序的权限高于 host）")
        notify("光标注入测试：失败（光标没动）", 8, "fail")
        return 1
    same_dir = (a.dx == 0 or dx_got * a.dx > 0) and (a.dy == 0 or dy_got * a.dy > 0)
    if not same_dir:
        print("FAIL 光标动了，但方向相反 → 坐标映射有问题")
        notify("光标注入测试：失败（方向相反）", 8, "fail")
        return 1
    print(f"PASS 光标确实被 host 移动了（探针坐标系缩放比 ≈ {dx_got / max(1, abs(a.dx)):.2f}）")
    notify(f"光标注入测试完成，桌面已交还（光标现在在 {x1},{y1}）", 8, "ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
