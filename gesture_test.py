"""PC 端触摸板手势自测：直接发 MOUSE 帧，验证滚动/缩放/中键/前进后退是否真的生效。

用法: python gesture_test.py
"""
import socket
import struct
import sys
import time

HOST, PORT = "127.0.0.1", 6000
TYPE_MOUSE = 0x07

MOUSE_MOVE, MOUSE_LEFT_DOWN, MOUSE_LEFT_UP = 0, 1, 2
MOUSE_LEFT_CLICK, MOUSE_RIGHT_CLICK = 3, 4
MOUSE_SCROLL, MOUSE_ZOOM = 5, 6
MOUSE_MIDDLE_CLICK, MOUSE_BACK, MOUSE_FORWARD = 7, 8, 9


def frame(kind, dx=0.0, dy=0.0):
    payload = struct.pack("<fff", float(kind), dx, dy)
    payload += b"\x00" * (24 - len(payload))
    return b"SP" + bytes([1, TYPE_MOUSE]) + struct.pack("<H", 24) + payload


def main():
    s = socket.create_connection((HOST, PORT), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def send(kind, dx=0.0, dy=0.0, note=""):
        s.sendall(frame(kind, dx, dy))
        print(f"  发送 {note or kind}: kind={kind} dx={dx} dy={dy}")
        time.sleep(0.35)

    print("依次注入各手势帧，请观察 PC 端 SpenTabletHost.log：")
    send(MOUSE_SCROLL, 0.0, -0.15, "双指上滑（纵向滚动）")
    send(MOUSE_SCROLL, 0.12, 0.0, "双指右滑（横向滚动）")
    send(MOUSE_ZOOM, 1.25, 0.0, "双指捏合张开（缩放）")
    send(MOUSE_ZOOM, 0.8, 0.0, "双指捏合靠拢（缩小）")
    send(MOUSE_MIDDLE_CLICK, 0.0, 0.0, "三指点击（中键）")
    send(MOUSE_BACK, 0.0, 0.0, "三指右滑（后退）")
    send(MOUSE_FORWARD, 0.0, 0.0, "三指左滑（前进）")
    send(MOUSE_RIGHT_CLICK, 0.0, 0.0, "双指点击（右键）")
    send(MOUSE_LEFT_CLICK, 0.0, 0.0, "单指点击（左键）")
    s.close()
    print("完成")


if __name__ == "__main__":
    sys.exit(main())
