#!/usr/bin/env python3
"""
双指平移方式的**开关取证**（2026-10-31，配合「手机端设置页 / PC 配置窗体 可切换滚轮↔触摸注入」）。

验证的是「开关真的能改变 PC 端的实现路径」，而不是只看配置字符串：
  1) 握手帧带 panMode=1(Touch) → 日志出现「平移方式切换 … → 触摸注入」；
     随后横向双指滑动 → 日志出现「双指平移 → 触摸注入」（真走了注入）
     —— 若本机不能创建合成指针设备，则必须出现「回退为滚轮」警告（两者必居其一，不许猜）。
  2) 握手帧带 panMode=0(Wheel) → 日志出现「平移方式切换 … → 滚轮」；
     同样的滑动 → 日志出现「滚动 v=0px h=…px」且**不再**出现触摸注入。

会碰桌面（真实注入触摸 / 真实滚轮，前台窗口内容会动），按规矩由脚本自己弹横幅。
用法：python tests/pan_mode_switch.py [host] [port]
"""
import re
import socket
import struct
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tests"))
try:
    from desk_notify import notify
except Exception:  # 提示失败绝不能把测试搞挂
    def notify(*a, **k):
        print("[notify]", a)

LOG_CANDIDATES = [
    ROOT / "PcHost" / "publish_test" / "DrawTouchHost.log",
    ROOT / "PcHost" / "publish" / "DrawTouchHost.log",
    ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "DrawTouchHost.log",
]

T_HELLO, T_MOUSE = 0x05, 0x07
MOUSE_SCROLL, MOUSE_FINGER, MOUSE_PAN = 5, 15, 17
PV = 4                       # 与 Protocol.PROTO_VER / TcpPenServer.ProtoVer 一致
PAN_WHEEL, PAN_TOUCH = 0, 1


def host_log():
    best, best_t = None, -1.0
    for p in LOG_CANDIDATES:
        if p.exists() and p.stat().st_mtime > best_t:
            best, best_t = p, p.stat().st_mtime
    return best


def log_since(p, size):
    try:
        with open(p, "rb") as f:
            f.seek(size)
            return f.read().decode("utf-8", errors="ignore")
    except OSError:
        return ""


def mouse_frame(kind, x=0.0, y=0.0):
    payload = struct.pack("<fff", float(kind), float(x), float(y))
    return bytes([0x53, 0x50, 0x01, T_MOUSE]) + struct.pack("<H", len(payload)) + payload


def hello(pan_mode):
    # 9 个 float：w h aspect work frameL frameT frameScale pv panMode
    payload = struct.pack("<9f", 1080.0, 2400.0, 1.0, 1.0, 0.0, 0.0, 1.0, float(PV), float(pan_mode))
    return bytes([0x53, 0x50, 0x01, T_HELLO]) + struct.pack("<H", len(payload)) + payload


def slide(s, frames=16, dx=0.03, dy=0.0):
    s.sendall(mouse_frame(MOUSE_FINGER, 1.0, 0.0))
    time.sleep(0.12)
    for _ in range(frames):
        s.sendall(mouse_frame(MOUSE_PAN, dx, dy))
        time.sleep(0.03)
    s.sendall(mouse_frame(MOUSE_FINGER, 0.0, 0.0))
    time.sleep(0.4)


def check(failures, ok, msg):
    print(("   ✓ " if ok else "   ✗ ") + msg)
    if not ok:
        failures.append(msg)


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 6000
    try:
        socket.create_connection((host, port), timeout=3).close()
    except Exception as e:
        print("[跳过] 宿主没在跑（%s:%s 连不上：%s）" % (host, port, e))
        return 0

    logp = host_log()
    if logp is None:
        print("[跳过] 找不到宿主日志")
        return 0

    notify("正在测试：双指平移方式开关（触摸注入 / 滚轮），会动到前台窗口", 60)
    failures = []

    # ---------- 1) 切到「触摸注入」 ----------
    s = socket.create_connection((host, port), timeout=5)
    sz = logp.stat().st_size
    s.sendall(hello(PAN_TOUCH))
    time.sleep(0.6)
    slide(s)
    log = log_since(logp, sz)
    print("\n[1] 握手 panMode=Touch 后横向滑动")
    check(failures, "平移方式切换" in log and "触摸注入" in log.split("平移方式切换")[-1][:80],
          "日志出现「平移方式切换 … → 触摸注入」")
    touched = len(re.findall(r"双指平移 → 触摸注入", log))
    fell_back = "回退为滚轮" in log
    check(failures, touched > 0 or fell_back,
          "真的走了触摸注入（%d 条）或明确回退为滚轮（%s）——两者必居其一" % (touched, fell_back))

    # ---------- 2) 切回「滚轮」 ----------
    sz = logp.stat().st_size
    s.sendall(hello(PAN_WHEEL))
    time.sleep(0.6)
    slide(s)
    s.close()
    log = log_since(logp, sz)
    print("\n[2] 握手 panMode=Wheel 后同样的滑动")
    check(failures, "平移方式切换" in log and "滚轮" in log.split("平移方式切换")[-1][:80],
          "日志出现「平移方式切换 … → 滚轮」")
    hits = re.findall(r"\[Mouse\]\s*滚动\s+v=(-?\d+)px\s+h=(-?\d+)px", log)
    sh = sum(int(b) for _, b in hits)
    check(failures, len(hits) > 0 and sh != 0,
          "走滚轮：滚动日志 %d 条、横向合计 %+dpx" % (len(hits), sh))
    check(failures, len(re.findall(r"双指平移 → 触摸注入", log)) == 0,
          "切回滚轮后不再有触摸注入")

    notify("测试完成，桌面已交还", 6, "ok" if not failures else "fail")

    if failures:
        print("\n=== 失败 ===")
        for f in failures:
            print("  ✗", f)
        return 1
    print("\n=== 通过：握手帧能真正切换平移实现路径，两条路都走得通 ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
