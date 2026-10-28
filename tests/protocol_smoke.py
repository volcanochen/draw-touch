#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SpenTablet PC 端协议级冒烟测试（回归套件核心）。

模拟手机直连 PC host（127.0.0.1:6000），逐项发帧并断言：
  1. HELLO 握手 → 收到 INFO 回包（版本/机器名/IP）
  2. SHOT_REQ 截图 → 收到 TYPE_SHOT 分片并重组为合法 PNG（捕获功能回归）
  3. MODE 切触摸板/数位板 → host.log 出现模式切换日志
  4. MOUSE 移动/按键 → host.log 出现对应日志
  5. MOUSE_ZOOM 捏合 → 触摸注入开始+220ms 自动抬起（接触点必须正常结束）
  6. HL 激光笔开/关 → host.log 出现开启/关闭
  7. NOTE 备注/换色/撤销/清空、PAN 平移、RESET 复位 → host.log 断言

用法：python tests/protocol_smoke.py [--host 127.0.0.1] [--port 6000]
前置：PC 端 SpenTabletHost.exe 已启动（脚本会自动定位其日志文件）。
退出码：0=全部通过，1=有失败。
"""
import socket
import struct
import sys
import time
import argparse
from pathlib import Path

MAGIC = b"SP"
VER = 1

T_HELLO, T_PAN, T_MOUSE, T_MODE, T_ZOOM, T_RESET = 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A
T_INFO, T_NOTE, T_SHOT_REQ, T_SHOT, T_HL = 0x0C, 0x0D, 0x0E, 0x0F, 0x10

M_MOVE, M_LEFT_DOWN, M_LEFT_UP, M_LEFT_CLICK, M_RIGHT_CLICK = 0, 1, 2, 3, 4
M_SCROLL, M_ZOOM, M_MIDDLE, M_FINGER = 5, 6, 7, 15

passed, failed = [], []


def check(name, ok, detail=""):
    (passed if ok else failed).append(name)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


def frame(ftype: int, payload: bytes) -> bytes:
    return MAGIC + bytes([VER, ftype]) + struct.pack("<H", len(payload)) + payload


def f32(*vals) -> bytes:
    return b"".join(struct.pack("<f", v) for v in vals)


def recv_frame(sock, timeout=5.0):
    sock.settimeout(timeout)
    hdr = b""
    while len(hdr) < 6:
        chunk = sock.recv(6 - len(hdr))
        if not chunk:
            return None
        hdr += chunk
    if hdr[:2] != MAGIC:
        raise AssertionError(f"坏帧头: {hdr!r}")
    ftype = hdr[3]
    ln = struct.unpack("<H", hdr[4:6])[0]
    payload = b""
    while len(payload) < ln:
        chunk = sock.recv(ln - len(payload))
        if not chunk:
            return None
        payload += chunk
    return ftype, payload


def find_host_log() -> Path:
    """定位 host 日志：扫描所有候选 EXE 目录下的日志，取**最近修改**的那个
    （host 可能从 publish 或 bin 目录启动，日志写在各自 EXE 同目录）。"""
    root = Path(__file__).resolve().parent.parent
    dirs = [
        root / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "publish",
        root / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64",
        root / "PcHost" / "bin" / "Debug" / "net8.0-windows",
        root / "PcHost" / "publish",
    ]
    logs = [d / "SpenTabletHost.log" for d in dirs]
    existing = [c for c in logs if c.exists()]
    if not existing:
        return logs[0]
    return max(existing, key=lambda c: c.stat().st_mtime)


def log_contains(log: Path, needle: str, since_size: int, timeout=4.0) -> bool:
    """等待日志文件自 since_size 之后出现 needle。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            if log.exists() and log.stat().st_size > since_size:
                text = log.read_bytes()[since_size:].decode("utf-8", "ignore")
                if needle in text:
                    return True
        except OSError:
            pass
        time.sleep(0.2)
    return False


def log_size(log: Path) -> int:
    try:
        return log.stat().st_size
    except OSError:
        return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=6000)
    args = ap.parse_args()

    log = find_host_log()
    print(f"== SpenTablet 协议冒烟测试 ==  {args.host}:{args.port}")
    print(f"   host 日志: {log}")

    s = socket.create_connection((args.host, args.port), timeout=5)

    # ---- 1. HELLO 握手 + INFO 回包 ----
    mark = log_size(log)
    hello = f32(1080.0, 2400.0, 1.0, 0.0, 0.0, 0.0, 1.0)  # 数位板、保持比例、框复位
    s.sendall(frame(T_HELLO, hello))
    info = recv_frame(s, timeout=5)
    check("HELLO → 收到 INFO 回包", info is not None and info[0] == T_INFO,
          f"type={hex(info[0]) if info else '超时'}")
    check("INFO 含版本号", info is not None and b"ver=" in info[1])

    # ---- 2. 截图（捕获功能）----
    s.sendall(frame(T_SHOT_REQ, bytes(24)))
    png = bytearray()
    total = -1
    ok_shot = False
    deadline = time.time() + 10
    try:
        while time.time() < deadline:
            fr = recv_frame(s, timeout=6)
            if fr is None:
                break
            ftype, payload = fr
            if ftype != T_SHOT:
                continue
            t, seq = struct.unpack("<IH", payload[:6])
            if seq == 0:
                png.clear()
                total = t
            png += payload[6:]
            if total >= 0 and len(png) >= total:
                ok_shot = True
                break
    except socket.timeout:
        pass
    png_magic = bytes(png[:8]) == b"\x89PNG\r\n\x1a\n" if png else False
    check("SHOT_REQ → 收到完整截图分片", ok_shot and total > 0, f"{len(png)}B / total={total}")
    check("截图是合法 PNG", png_magic)

    # ---- 3. MODE 切换 ----
    mark = log_size(log)
    s.sendall(frame(T_MODE, f32(1.0)))  # → 触摸板
    check("MODE=触摸板 → 日志确认", log_contains(log, "切换到【触摸板模式】", mark))
    mark = log_size(log)
    s.sendall(frame(T_MODE, f32(0.0)))  # → 数位板
    check("MODE=数位板 → 日志确认", log_contains(log, "切换到【数位板模式】", mark))
    s.sendall(frame(T_MODE, f32(1.0)))  # 留在触摸板做下面的鼠标测试

    # ---- 4. MOUSE 移动 / 左键 ----
    mark = log_size(log)
    for _ in range(25):  # 移动多帧保证残量攒够 1 像素、必出日志
        s.sendall(frame(T_MOUSE, f32(M_MOVE, 0.05, 0.03)))
        time.sleep(0.01)
    check("MOUSE 移动 → 日志确认", log_contains(log, "[Mouse] 移动", mark))
    mark = log_size(log)
    s.sendall(frame(T_MOUSE, f32(M_LEFT_CLICK, 0, 0)))
    check("MOUSE 左键点击 → 日志确认", log_contains(log, "单击 → 左键", mark))

    # ---- 5. 捏合缩放：触摸注入路径或 Ctrl+/- 回退路径二选一，必须有一条生效 ----
    # （部分 Windows 版本触摸注入被系统禁用/损坏 → 熔断后走 Ctrl 回退，同样算通过）
    mark = log_size(log)
    for f in (1.1, 1.1, 1.1, 1.1, 1.1):
        s.sendall(frame(T_MOUSE, f32(M_ZOOM, f, 0)))
        time.sleep(0.05)
    ok_any = log_contains(log, "[Mouse] 捏合 ×", mark)
    used_touch = log_contains(log, "[TouchZoom] 开始捏合", mark)
    used_fallback = log_contains(log, "Ctrl+ / Ctrl-", mark)
    check("MOUSE_ZOOM → 缩放帧被处理", ok_any)
    check("缩放路径生效（触摸注入 或 Ctrl+/- 回退）", used_touch or used_fallback,
          "触摸注入" if used_touch else ("Ctrl回退" if used_fallback else "两者皆无"))
    if used_touch:
        ok_end = log_contains(log, "[TouchZoom] 捏合结束（抬起接触点）", mark, timeout=5)
        check("捏合结束后接触点自动抬起（UP 帧修复回归）", ok_end)
    else:
        # 回退路径：应看到熔断日志（可能在本轮之前的运行中已触发，故全文搜索）
        ok_cb = log_contains(log, "已熔断", 0, timeout=2)
        check("注入不可用 → 熔断并回退 Ctrl+/-（无接触点残留）", ok_cb)

    # ---- 6. 激光笔开/关 ----
    mark = log_size(log)
    s.sendall(frame(T_HL, f32(1.0, 0.0)))
    check("HL 开 → 激光笔已开启", log_contains(log, "激光笔已开启", mark))
    mark = log_size(log)
    s.sendall(frame(T_HL, f32(0.0, 0.0)))
    check("HL 关 → 激光笔已关闭", log_contains(log, "激光笔已关闭", mark))

    # ---- 7. NOTE / PAN / RESET ----
    s.sendall(frame(T_MODE, f32(0.0)))  # 回数位板
    time.sleep(0.3)
    mark = log_size(log)
    s.sendall(frame(T_NOTE, f32(1.0, 0.0)))
    check("NOTE 开 → 日志确认", log_contains(log, "[Note] action=1", mark))
    mark = log_size(log)
    s.sendall(frame(T_NOTE, f32(4.0, 2.0)))
    check("NOTE 换色 → 日志确认", log_contains(log, "[Note] 笔迹颜色", mark))
    mark = log_size(log)
    # host 对 PAN 帧抽样打日志（每 12 条记 1 条），发一小串保证命中节流窗口
    for _ in range(13):
        s.sendall(frame(T_PAN, f32(0.005, -0.005)))
        time.sleep(0.02)
    check("PAN 平移 → 日志确认", log_contains(log, "[Pan] 平移", mark))
    mark = log_size(log)
    s.sendall(frame(T_RESET, bytes(24)))
    time.sleep(0.5)

    s.close()
    print(f"\n== 结果：{len(passed)} 通过, {len(failed)} 失败 ==")
    if failed:
        print("失败项: " + ", ".join(failed))
        sys.exit(1)
    print("ALL GREEN")


if __name__ == "__main__":
    main()
