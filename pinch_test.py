#!/usr/bin/env python3
"""
用 adb sendevent 生成「双指捏合」手势，测试手机端缩放操作范围框。
adb 的 input 命令只支持单指，多指必须走 evdev 原始事件（协议 B）。

用法: python pinch_test.py [in|out]
  in  = 双指靠拢（范围框变小，更精细）
  out = 双指张开（范围框变大，覆盖更多桌面）
"""
import subprocess
import sys

ADB = r"C:\VOLCANO\myws\myEnv\tools\android-sdk\platform-tools\adb.exe"
DEV = "/dev/input/event9"   # sec_touchscreen

# 屏幕逻辑分辨率（wm size）
SCREEN_W, SCREEN_H = 720, 1544
# 触摸原始量程 0..4095
RAW_MAX = 4095.0

# evdev 常量（十进制）
EV_SYN, EV_KEY, EV_ABS = 0, 1, 3
SYN_REPORT = 0
BTN_TOUCH = 330
ABS_MT_SLOT = 47
ABS_MT_TOUCH_MAJOR = 48
ABS_MT_POSITION_X = 53
ABS_MT_POSITION_Y = 54
ABS_MT_TRACKING_ID = 57

Y = 800          # 捏合的纵向位置（画布中部，避开上下控制栏）
X0_START, X1_START = 180, 540   # 初始两指间距 360px


def raw(px):
    return int(round(px / SCREEN_W * RAW_MAX))


def raw_y(py):
    return int(round(py / SCREEN_H * RAW_MAX))


def ev(t, c, v):
    return f"sendevent {DEV} {t} {c} {v}"


def frame(slot, x=None, y=None, tid=None, touch=None):
    """一帧触摸事件（协议 B：先设 slot，再写数据，最后 SYN）。"""
    out = [ev(EV_ABS, ABS_MT_SLOT, slot)]
    if tid is not None:
        out.append(ev(EV_ABS, ABS_MT_TRACKING_ID, tid))
    if x is not None:
        out.append(ev(EV_ABS, ABS_MT_POSITION_X, raw(x)))
    if y is not None:
        out.append(ev(EV_ABS, ABS_MT_POSITION_Y, raw_y(y)))
    if touch is not None:
        out.append(ev(EV_ABS, ABS_MT_TOUCH_MAJOR, touch))
    out.append(ev(EV_SYN, SYN_REPORT, 0))
    return out


def build(direction: str):
    steps = 10
    span = 150 if direction == "in" else -150   # in=靠拢 out=张开
    cmds = []

    # 第 1 根手指落下
    cmds += frame(0, X0_START, Y, tid=100, touch=60)
    cmds += [ev(EV_KEY, BTN_TOUCH, 1)]
    # 第 2 根手指落下
    cmds += frame(1, X1_START, Y, tid=101, touch=60)

    # 逐步捏合/张开
    for i in range(1, steps + 1):
        d = span * i / steps
        x0 = X0_START + d
        x1 = X1_START - d
        cmds += frame(0, x0, Y)
        cmds += frame(1, x1, Y)

    # 两指抬起
    cmds += frame(0, tid=-1)
    cmds += frame(1, tid=-1)
    cmds += [ev(EV_KEY, BTN_TOUCH, 0), ev(EV_SYN, SYN_REPORT, 0)]
    return cmds


def main():
    direction = sys.argv[1] if len(sys.argv) > 1 else "in"
    cmds = build(direction)
    cmd = " ; ".join(cmds)
    print(f"生成 {len(cmds)} 条 sendevent，方向={direction}（{'靠拢/缩小' if direction=='in' else '张开/放大'}）")
    r = subprocess.run([ADB, "shell", cmd], capture_output=True, text=True, timeout=60)
    if r.stderr.strip():
        print("STDERR:", r.stderr.strip()[:500])
    print("done")


if __name__ == "__main__":
    main()
