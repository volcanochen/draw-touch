#!/usr/bin/env python3
"""
复现「捕获 → 绘制 → 移动范围框 → 再绘制 → PC 端崩溃」的协议回放脚本。
直连 127.0.0.1:6000，按真实时序把帧灌进去；跑完观察 SpenTabletHost.log 是否有异常。
"""
import socket, struct, sys, time

HOST, PORT = "127.0.0.1", 6000

TYPE_PEN_DOWN, TYPE_PEN_MOVE, TYPE_PEN_UP = 0x01, 0x02, 0x03
TYPE_HELLO, TYPE_PAN, TYPE_MODE = 0x05, 0x06, 0x07
TYPE_NOTE, TYPE_SHOT_REQ = 0x0D, 0x0E


def frame(ftype, payload=b""):
    return b"SP" + bytes([1, ftype]) + struct.pack("<H", len(payload)) + payload


def pen(ftype, x, y, p=0.5):
    return frame(ftype, struct.pack("<fffff", x, y, p, 0.0, 0.0) + struct.pack("<HH", 0, 0))


def f2(*fs):
    return b"".join(struct.pack("<f", f) for f in fs)


def note(action, arg=0.0):
    return frame(TYPE_NOTE, f2(float(action), float(arg)))


def stroke(s, x0, y0, n=250, note_tag=""):
    s.sendall(pen(TYPE_PEN_DOWN, x0, y0, 0.5))
    for i in range(n):
        x = min(1.0, max(0.0, x0 + i * 0.0015))
        y = min(1.0, max(0.0, y0 + i * 0.0004))
        s.sendall(pen(TYPE_PEN_MOVE, x, y, 0.5))
        time.sleep(0.004)
    s.sendall(pen(TYPE_PEN_UP, x, y, 0.0))
    print(f"  画完一笔{n}点 {note_tag}")


def pan(s, dx, dy, times=1):
    for _ in range(times):
        s.sendall(frame(TYPE_PAN, f2(dx, dy)))
        time.sleep(0.02)


def main():
    s = socket.create_connection((HOST, PORT), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    print("已连接")

    # HELLO：模拟手机书写区 720x1467，保持等比，数位板，框 (0.5, 0.007, 0.5)
    s.sendall(frame(TYPE_HELLO, f2(720.0, 1467.0, 1.0, 0.0, 0.5, 0.007, 0.5)))
    time.sleep(0.3)

    print("[1] 开启桌面备注 + 换绿色")
    s.sendall(note(1)); time.sleep(0.2)
    s.sendall(note(4, 3)); time.sleep(0.2)

    print("[2] 画几笔（多笔、含单点笔迹）")
    stroke(s, 0.1, 0.2, 200, "(长笔)")
    s.sendall(pen(TYPE_PEN_DOWN, 0.3, 0.4, 0.5)); s.sendall(pen(TYPE_PEN_UP, 0.3, 0.4, 0.0))
    print("  画完一笔单点笔迹")
    stroke(s, 0.05, 0.6, 150, "(第二笔)")

    print("[3] 捕获（SHOT_REQ）")
    s.sendall(frame(TYPE_SHOT_REQ))
    time.sleep(0.6)

    print("[4] 截图后再画一笔")
    stroke(s, 0.2, 0.3, 120, "(截图后)")

    print("[5] 移动范围框（模拟手指平移，20 帧）")
    pan(s, 0.02, -0.004, 20)
    time.sleep(0.3)

    print("[6] 移动后再画几笔（崩溃场景的关键步骤）")
    stroke(s, 0.15, 0.5, 200, "(移动后第一笔)")
    stroke(s, 0.25, 0.7, 200, "(移动后第二笔)")

    print("[7] 再截一次 + 移动 + 画（循环 2 轮）")
    for r in range(2):
        s.sendall(frame(TYPE_SHOT_REQ)); time.sleep(0.6)
        pan(s, 0.015, 0.003, 15); time.sleep(0.3)
        stroke(s, 0.1 + 0.05 * r, 0.35, 150, f"(轮{r})")

    print("[8] 撤销 / 清空")
    s.sendall(note(2)); time.sleep(0.2)
    s.sendall(note(3)); time.sleep(0.2)
    s.sendall(note(0)); time.sleep(0.2)

    s.close()
    print("回放完成。请看 PC 端 SpenTabletHost.log 是否出现异常，以及进程是否还活着。")


if __name__ == "__main__":
    sys.exit(main())
