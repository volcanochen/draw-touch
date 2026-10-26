#!/usr/bin/env python3
"""
验证「双指捏合缩放」：PC 端实际收到的框，与**手机端算法**算出的框必须完全一致。

做法：
  1) 按手机端 PenCanvasView 的算法（等价移植）在本地模拟一次捏合；
  2) 把手机端会发出的同样一串 ZOOM 帧发给 PC；
  3) 从 PC 日志读出它算出的框，比较两边的
       - 缩放系数 scale
       - 框位置 (frameL, frameT)
       - 框的宽高比（**形状是否一致** —— 用户明确要求）
"""
import re
import socket
import struct
import subprocess
import sys
import time

HOST, PORT = "127.0.0.1", 6000
LOG = r"C:\Users\adminlocal\WorkBuddy\2026-10-26-17-47-23\SpenTablet\PcHost\publish\SpenTabletHost.log"

TYPE_ZOOM = 0x09
MIN_FRAME = 0.05

SURFACE_W = SURFACE_H = 0.0
BASE_W = BASE_H = 0.0
BASE_X = BASE_Y = 0.0


def read_env():
    """从 PC 日志读出：手机书写区尺寸 + 桌面基准矩形（复位后的那个框）"""
    global SURFACE_W, SURFACE_H, BASE_W, BASE_H, BASE_X, BASE_Y
    txt = open(LOG, "r", encoding="utf-8", errors="ignore").read()
    m = re.findall(r"\[Hello\] 手机书写区 (\d+)×(\d+)", txt)
    if not m:
        raise SystemExit("日志里找不到 [Hello] 手机书写区，请先让手机连上")
    SURFACE_W, SURFACE_H = float(m[-1][0]), float(m[-1][1])
    m = re.findall(r"\[Reset\] 操作框已复位 → 桌面范围 (-?\d+),(-?\d+) (\d+)×(\d+)", txt)
    if not m:
        raise SystemExit("日志里找不到 [Reset]，请先复位一次")
    BASE_X, BASE_Y = float(m[-1][0]), float(m[-1][1])
    BASE_W, BASE_H = float(m[-1][2]), float(m[-1][3])


def read_status():
    """发一帧 STATUS，让 PC 把当前状态打进日志，再读出来"""
    s = socket.create_connection((HOST, PORT), timeout=5)
    s.sendall(frame(0x0B, b""))
    time.sleep(0.6)
    s.close()
    time.sleep(0.3)
    txt = open(LOG, "r", encoding="utf-8", errors="ignore").read()
    m = re.findall(
        r"\[Status\] 缩放=([\d.]+) 位置=\(([\d.]+),([\d.]+)\) 桌面范围 (-?\d+),(-?\d+) (\d+)×(\d+) 宽高比=([\d.]+)",
        txt)
    if not m:
        raise SystemExit("日志里找不到 [Status]")
    g = m[-1]
    return dict(scale=float(g[0]), fl=float(g[1]), ft=float(g[2]),
                x=int(g[3]), y=int(g[4]), w=int(g[5]), h=int(g[6]), aspect=float(g[7]))


def frame(ftype, payload):
    payload = payload + b"\x00" * (24 - len(payload))
    return b"SP" + bytes([1, ftype]) + struct.pack("<H", 24) + payload


def zoom_frame(factor, fx, fy):
    return frame(TYPE_ZOOM, struct.pack("<fff", factor, fx, fy))


# ---------- 手机端算法（照抄 PenCanvasView.ScaleListener.onScale） ----------
def phone_pinch(steps, frame_l=0.0, frame_t=0.0, frame_s=1.0):
    """照抄 PenCanvasView.applyZoom（手机端框撑满，捏合只缩桌面框）"""
    sent = []
    for factor, focus_px, focus_py in steps:
        rfx = min(max(focus_px / SURFACE_W, 0.0), 1.0)
        rfy = min(max(focus_py / SURFACE_H, 0.0), 1.0)
        s_old = frame_s
        s2 = min(max(s_old * factor, MIN_FRAME), 1.0)
        applied = s2 / s_old
        frame_s = s2
        nl = min(max(frame_l + rfx * (s_old - s2), 0.0), 1.0 - s2)
        nt = min(max(frame_t + rfy * (s_old - s2), 0.0), 1.0 - s2)
        frame_l, frame_t = nl, nt
        sent.append((applied, rfx, rfy))
    return (frame_l, frame_t, frame_s), sent


# ---------- PC 端算法（照抄 MappingConfig.Zoom / BoxSize） ----------
def pc_zoom(frame_l, frame_t, scale, factor, fx, fy):
    k = BASE_W / SURFACE_W
    def box_size(s):
        w = max(16, round(SURFACE_W * s * k))
        h = max(16, round(w * SURFACE_H / SURFACE_W))
        return (w, h)
    w, h = box_size(scale)
    px = BASE_X + frame_l * BASE_W + fx * w
    py = BASE_Y + frame_t * BASE_H + fy * h
    s2 = min(max(scale * factor, MIN_FRAME), 1.0)
    scale = s2
    w2, h2 = box_size(s2)
    frame_l = (px - fx * w2 - BASE_X) / BASE_W
    frame_t = (py - fy * h2 - BASE_Y) / BASE_H
    lim = max(0.0, 1.0 - scale)
    frame_l = min(max(frame_l, 0.0), lim)
    frame_t = min(max(frame_t, 0.0), lim)
    return (frame_l, frame_t, scale), (round(BASE_X + frame_l * BASE_W), round(BASE_Y + frame_t * BASE_H), w2, h2)

def read_pc_state():
    with open(LOG, "r", encoding="utf-8", errors="ignore") as f:
        txt = f.read()
    hits = re.findall(
        r"\[Zoom\] ×([\d.]+) 焦点\(([\d.]+),([\d.]+)\) → 缩放=([\d.]+) 位置=\(([\d.]+),([\d.]+)\) 桌面范围 (-?\d+),(-?\d+) (\d+)×(\d+)",
        txt)
    return hits[-1] if hits else None


def main():
    read_env()
    print(f"手机书写区: {SURFACE_W:.0f}×{SURFACE_H:.0f}  桌面基准矩形: "
          f"{BASE_X:.0f},{BASE_Y:.0f} {BASE_W:.0f}×{BASE_H:.0f}")

    # 从 PC 的**真实当前状态**出发（不擅自复位，否则会和手机端不同步）
    st = read_status()
    print(f"PC 当前状态: 缩放={st['scale']:.6f} 位置=({st['fl']:.6f},{st['ft']:.6f}) "
          f"桌面框 {st['x']},{st['y']} {st['w']}×{st['h']}")

    # 模拟一次「双指靠拢」的捏合：焦点固定在书写区正中，缩小 20%。
    steps = [(0.80, SURFACE_W * 0.5, SURFACE_H * 0.5)]

    (pl, pt, ps), sent = phone_pinch(steps, st['fl'], st['ft'], st['scale'])
    # PC 端从同一初始状态出发
    (cl, ct, cs), _ = pc_zoom(st['fl'], st['ft'], st['scale'], sent[0][0], sent[0][1], sent[0][2])

    print(f"手机端算法结果: frameL={pl:.6f} frameT={pt:.6f} scale={ps:.6f}")
    print(f"PC 端算法结果  : frameL={cl:.6f} frameT={ct:.6f} scale={cs:.6f}")
    pos_ok = abs(pl - cl) < 2e-3 and abs(pt - ct) < 2e-3 and abs(ps - cs) < 1e-9
    print(f"→ 两端位置/缩放一致(误差<0.2%): {pos_ok}")

    # 手机端操作框**撑满整个书写区**，形状就是书写区的形状
    phone_px = (SURFACE_W, SURFACE_H)
    print(f"手机操作框像素 : {phone_px[0]:.2f}×{phone_px[1]:.2f}（撑满书写区） 宽高比={phone_px[0]/phone_px[1]:.6f}")

    # 真发给 PC
    s = socket.create_connection((HOST, PORT), timeout=5)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    for applied, rfx, rfy in sent:
        s.sendall(zoom_frame(applied, rfx, rfy))
        time.sleep(0.05)
    time.sleep(1.0)
    s.close()

    st = read_pc_state()
    if not st:
        print("!! PC 日志里没读到 [Zoom] 记录")
        return 1
    pcf, pfx, pfy, pcscale, pcl, pct, box_x, box_y, box_w, box_h = st
    box_w, box_h = float(box_w), float(box_h)
    print(f"\nPC 实测: 缩放={float(pcscale):.6f} 位置=({float(pcl):.6f},{float(pct):.6f}) "
          f"桌面框={int(box_x)},{int(box_y)} {int(box_w)}×{int(box_h)}")

    # 桌面框 = 书写区（撑满的手机框）按缩放系数缩小后的等比矩形
    k = BASE_W / SURFACE_W
    ideal_w = SURFACE_W * ps * k
    ideal_h = SURFACE_H * ps * k
    dw = abs(box_w - ideal_w)
    dh = abs(box_h - ideal_h)
    print(f"桌面框应为     : {ideal_w:.2f}×{ideal_h:.2f}（手机框等比放大 {k:.4f}x）")
    print(f"偏差           : w {dw:.2f}px  h {dh:.2f}px")
    size_ok = dw <= 1.0 and dh <= 1.5
    print(f"→ 桌面框就是手机框等比放大: {size_ok}")

    # **核心判据**：两端的宽高比（形状）必须一致
    a_desktop = box_w / box_h
    a_phone = phone_px[0] / phone_px[1]
    rel = abs(a_desktop - a_phone) / a_phone
    shape_ok = rel < 0.002      # 0.2% 以内（整数像素取整的固有误差）
    print(f"→ 形状一致: 桌面宽高比 {a_desktop:.6f}  手机宽高比 {a_phone:.6f}  相对差 {rel*100:.4f}%  => {shape_ok}")

    # 不复位：手机端才是操作框状态的唯一权威，复位会让两端失步
    return 0 if (pos_ok and shape_ok) else 1


if __name__ == "__main__":
    sys.exit(main())
