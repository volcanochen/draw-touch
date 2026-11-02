#!/usr/bin/env python3
"""
伪装手机，向正在运行的宿主发真实 MOUSE_PAN 帧，复现「pan 期间指针跟手」。

用途（2026-11-01）：钩子判据被日志证伪后，需要拿到「真实落点」场景下每帧的
光标/触点/钩子事件/原始输入快照。手工让用户去真机 pan 一次太慢，这里直接走同一条
TCP 协议（与真机完全一致的信令），宿主行为不打折。

用法：
    python tests/sim_pan_probe.py                 # 默认 40 帧、每帧 +7.2px
    python tests/sim_pan_probe.py --frames 60 --dx 0.03
"""
import argparse
import socket
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from desk_notify import notify
except Exception:                                    # 提示失败绝不影响测试
    def notify(*a, **k):
        return False

PROTO_VER = 4
TYPE_HELLO, TYPE_MOUSE = 0x05, 0x07
MOUSE_PAN, MOUSE_FINGER = 17, 15


def frame(ftype: int, payload: bytes) -> bytes:
    return b"SP" + bytes([PROTO_VER, ftype]) + struct.pack("<H", len(payload)) + payload


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--frames", type=int, default=40)
    ap.add_argument("--dx", type=float, default=0.02, help="每帧归一化横向位移（0.02≈7.2px）")
    ap.add_argument("--dy", type=float, default=0.0, help="每帧归一化纵向位移（负=向上）")
    ap.add_argument("--gap", type=float, default=0.025, help="帧间隔秒")
    args = ap.parse_args()

    notify("正在测试：伪装手机发双指平移帧（会注入触摸、移动光标）",
           args.frames * args.gap + 12, "busy")
    time.sleep(0.4)

    s = socket.create_connection((args.host, args.port), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    # HELLO：w,h,mode,work,frameL,frameT,frameScale,pv,panMode(=1 触摸注入)
    s.sendall(frame(TYPE_HELLO, struct.pack("<9f", 1494, 614, 1, 0, -1, -1, -1, PROTO_VER, 1)))
    time.sleep(0.5)

    print(f"-> 发送 {args.frames} 帧 MOUSE_PAN，每帧 dx={args.dx} dy={args.dy}")
    for i in range(args.frames):
        s.sendall(frame(TYPE_MOUSE, struct.pack("<3f", MOUSE_PAN, args.dx, args.dy)))
        if i % 10 == 0:
            print(f"   {i}/{args.frames}")
        time.sleep(args.gap)

    # 手指离开触摸板 → 宿主结束这一轮平移（触发 EndTouchPan）
    # 手指「按下 → 离开」才是状态迁移；只发 off 不算，宿主会拖到 10s 安全抬手
    s.sendall(frame(TYPE_MOUSE, struct.pack("<3f", MOUSE_FINGER, 1.0, 0.0)))
    time.sleep(0.12)
    s.sendall(frame(TYPE_MOUSE, struct.pack("<3f", MOUSE_FINGER, 0.0, 0.0)))
    time.sleep(0.6)
    s.close()
    notify("测试结束，桌面已交还", 5, "ok")
    print("-> 完成（宿主日志见 DrawTouchHost.log 的 [TouchPan][诊断] 行）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
