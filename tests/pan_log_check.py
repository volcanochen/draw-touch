#!/usr/bin/env python3
"""
日志型回归：双指平移的**路由与位移守恒**（2026-11-01 第 4 版，两种模式都覆盖）。

为什么是日志型：宿主进程在交互桌面里（能读光标、能注入），脚本只发真实协议帧、
再读宿主日志做断言；只有「光标能不能同时动」这类需要读光标的项才额外直接读
（见 tests/pan_mouse_together.py）。

覆盖矩阵（PanMode 由握手帧强制切换，所以默认值改了也不会让本脚本假红）：
  A. panMode=Touch（默认）—— 触摸注入
     · 横/斜/单指都要真走注入（日志「开始平移」），且**不发滚轮**；
     · 全程不动鼠标时，日志里的「单次最大偏离」必须 ≤3px（指针被逐帧钉住，不许跟手跑）。
  B. panMode=Wheel —— 高分辨率滚轮
     · 横/斜/单指的滚轮增量守恒（含**慢速**：逐帧四舍五入会把慢速位移全丢掉的老 bug）；
     · 不许出现触摸注入（否则接触期间光标会被系统隐藏）。
会碰桌面（真实注入触摸 / 真实滚轮，前台窗口内容会动），按规矩由脚本自己弹横幅。
用法：python tests/pan_log_check.py [host] [port]
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
except Exception:      # 提示失败绝不能把测试搞挂
    def notify(*a, **k):
        print("[notify]", a)

LOG_CANDIDATES = [
    ROOT / "PcHost" / "publish_test" / "DrawTouchHost.log",
    ROOT / "PcHost" / "publish" / "DrawTouchHost.log",
    ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "DrawTouchHost.log",
]

T_HELLO, T_MOUSE = 0x05, 0x07
MOUSE_SCROLL, MOUSE_FINGER, MOUSE_PAN = 5, 15, 17
SCROLL_PIXELS_PER_SCREEN = 360.0   # 与 PC 端常量保持一致
PV = 4
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
    payload = struct.pack("<9f", 1080.0, 2400.0, 1.0, 1.0, 0.0, 0.0, 1.0, float(PV), float(pan_mode))
    return bytes([0x53, 0x50, 0x01, T_HELLO]) + struct.pack("<H", len(payload)) + payload


def wheels(log):
    hits = re.findall(r"\[Mouse\]\s*滚动\s+v=(-?\d+)px\s+h=(-?\d+)px", log)
    sv = sum(int(a) for a, _ in hits)
    sh = sum(int(b) for _, b in hits)
    return sv, sh, len(hits)


def net_dev(log):
    vals = re.findall(r"单次最大偏离\s*(\d+)px", log)
    return max((int(v) for v in vals), default=None)


def drive(s, dx, dy, frames, interval=0.03, kind=MOUSE_PAN):
    s.sendall(mouse_frame(MOUSE_FINGER, 1.0, 0.0))
    time.sleep(0.12)
    for _ in range(frames):
        s.sendall(mouse_frame(kind, dx, dy))
        time.sleep(interval)
    s.sendall(mouse_frame(MOUSE_FINGER, 0.0, 0.0))
    time.sleep(0.5)


def check(failures, ok, msg):
    print(("   ✓ " if ok else "   ✗ ") + msg)
    if not ok:
        failures.append(msg)


def case_touch(s, logp, name, dx, dy, frames, failures):
    sz = logp.stat().st_size
    drive(s, dx, dy, frames)
    log = log_since(logp, sz)
    sv, sh, n = wheels(log)
    started = "开始平移" in log
    dev = net_dev(log)
    print("\n[Touch·%s] 帧数=%d dx=%.5f dy=%.5f  注入=%s 滚轮条数=%d 指针最大偏离=%s"
          % (name, frames, dx, dy, started, n, dev))
    check(failures, started, "%s：走了触摸注入（日志「开始平移」）" % name)
    check(failures, n == 0, "%s：没有同时发滚轮（避免两条路径打架）" % name)
    check(failures, dev is not None and dev <= 3,
          "%s：没动鼠标时指针被钉住（单次最大偏离 %s px，应 ≤3）" % (name, dev))


def case_single_finger(s, logp, name, dy, frames, failures):
    """单指滚动（MOUSE_SCROLL）：无论 PanMode 是什么，都必须走滚轮、不注入触摸。"""
    sz = logp.stat().st_size
    drive(s, 0.0, dy, frames, kind=MOUSE_SCROLL)
    log = log_since(logp, sz)
    sv, sh, n = wheels(log)
    want_v = -dy * SCROLL_PIXELS_PER_SCREEN * frames
    print("\n[单指·%s] 帧数=%d dy=%.3f  滚轮条数=%d 纵向=%+dpx（期望 %+.0f）"
          % (name, frames, dy, n, sv, want_v))
    check(failures, "开始平移" not in log, "%s：单指滚动**不许**起触摸注入（会隐藏光标）" % name)
    check(failures, sh == 0, "%s：不该有横向滚动（实发 %+dpx）" % (name, sh))
    check(failures, abs(want_v) < 5 or sv * (1 if want_v > 0 else -1) >= abs(want_v) * 0.75,
          "%s：纵向滚动守恒（%+dpx，应约 %+.0f）" % (name, sv, want_v))


def case_wheel(s, logp, name, dx, dy, frames, failures,
               expect_h=True, expect_v=False, min_ratio=0.75, kind=MOUSE_PAN):
    sz = logp.stat().st_size
    drive(s, dx, dy, frames, kind=kind)
    log = log_since(logp, sz)
    sv, sh, n = wheels(log)
    injected = "开始平移" in log
    want_h = dx * SCROLL_PIXELS_PER_SCREEN * frames
    # 纵向负号：PC 端是 v = -dy * GAIN（手指下滑 dy>0 → 纵向滚轮为负）。已认可的行为，照抄。
    want_v = -dy * SCROLL_PIXELS_PER_SCREEN * frames
    print("\n[Wheel·%s] 帧数=%d dx=%.5f dy=%.5f  滚轮条数=%d 纵向=%+dpx（期望 %+.0f）横向=%+dpx（期望 %+.0f）"
          % (name, frames, dx, dy, n, sv, want_v, sh, want_h))
    check(failures, not injected, "%s：没走触摸注入（滚轮模式下不该注入触摸）" % name)
    if expect_v:
        check(failures, abs(want_v) < 5 or sv * (1 if want_v > 0 else -1) >= abs(want_v) * min_ratio,
              "%s：纵向位移守恒（%+dpx，应约 %+.0f）" % (name, sv, want_v))
    else:
        check(failures, sv == 0, "%s：不该有纵向滚动（实发 %+dpx）" % (name, sv))
    if expect_h:
        check(failures, abs(want_h) < 5 or sh * (1 if want_h > 0 else -1) >= abs(want_h) * min_ratio,
              "%s：横向位移守恒（%+dpx，应约 %+.0f；慢速时逐帧四舍五入会全丢）" % (name, sh, want_h))
    else:
        check(failures, sh == 0, "%s：不该有横向滚动（实发 %+dpx）" % (name, sh))


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

    notify("正在测试：双指平移两种模式的路由与位移守恒（会动到前台窗口）", 60)
    failures = []
    s = socket.create_connection((host, port), timeout=5)

    # ---------- A. 触摸注入（默认） ----------
    s.sendall(hello(PAN_TOUCH))
    time.sleep(0.6)
    case_touch(s, logp, "横向·常速", 0.05, 0.0, 20, failures)
    case_touch(s, logp, "斜向", 0.02, 0.02, 20, failures)
    # 关键回归：PanMode=Touch 时**单指滚动**必须仍然走滚轮（不许被卷进触摸注入）
    case_single_finger(s, logp, "垂直滚动", 0.1, 12, failures)
    case_single_finger(s, logp, "点击滚一格", 0.15, 1, failures)

    # ---------- B. 滚轮 ----------
    s.sendall(hello(PAN_WHEEL))
    time.sleep(0.6)
    case_wheel(s, logp, "横向·常速", 0.05, 0.0, 20, failures, expect_h=True, expect_v=False)
    # 关键回归：每帧只有 0.18px，旧实现逐帧四舍五入全部丢弃
    case_wheel(s, logp, "横向·慢速(关键)", 0.0005, 0.0, 40, failures, expect_h=True, expect_v=False)
    case_wheel(s, logp, "斜向", 0.02, 0.02, 20, failures, expect_h=True, expect_v=True)
    case_wheel(s, logp, "单指·保护", 0.0, 0.1, 12, failures, expect_h=False, expect_v=True, kind=MOUSE_SCROLL)

    # 收尾：把模式切回默认（触摸注入），免得把用户宿主留在滚轮模式
    s.sendall(hello(PAN_TOUCH))
    time.sleep(0.4)
    s.close()

    notify("测试完成，桌面已交还", 6, "ok" if not failures else "fail")

    if failures:
        print("\n=== 失败 ===")
        for f in failures:
            print("  ✗", f)
        return 1
    print("\n=== 通过：触摸注入走原生手势且不碰光标；滚轮模式位移守恒（含慢速）；单指滚动未被误伤 ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
