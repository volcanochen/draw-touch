#!/usr/bin/env python3
"""
聚焦复现：host 在收到 T_HL（激光笔开/关）帧时的行为。

背景：发布门禁的协议冒烟里，「HL 开 → 激光笔已开启」失败，紧接着连接被中止，
host 日志停在崩溃点前一行（同步落盘的日志都没有异常记录 → 是硬崩溃）。

本脚本只做最小动作：握手 → 开激光笔 → 关激光笔，
并把 host 的 stdout/stderr 一起抓下来，避免「异常日志本身也丢了」看不到线索。

用法：python tests/hl_repro.py [--port 6000]
"""
import argparse
import os
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
from desk_notify import notify  # noqa: E402

HOST_EXE = ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "DrawTouchHost.exe"
LOG = HOST_EXE.parent / "DrawTouchHost.log"
DOTNET = Path(r"C:\Users\adminlocal\.dotnet\dotnet.exe")

MAGIC = b"SP"
VER = 1

T_HELLO, T_MOUSE, T_HL = 0x05, 0x07, 0x10


def f32(*vals):
    return struct.pack("<" + "f" * len(vals), *vals)


def frame(ftype, payload=b""):
    # 帧头必须是 MAGIC(2) + VER(1) + type(1) + len(2) —— 少一个字节就会
    # 被 host 判成「帧头错误」直接丢弃连接（踩过一次）
    return MAGIC + bytes([VER, ftype]) + struct.pack("<H", len(payload)) + payload


def log_size():
    try:
        return LOG.stat().st_size
    except OSError:
        return 0


def tail(n=40):
    try:
        data = LOG.read_bytes()
        return data[-n * 160:].decode("utf-8", "ignore")
    except OSError:
        return "(读不到日志)"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=6000)
    args = ap.parse_args()

    notify("激光笔复现测试：会短暂替换系统光标（约 15 秒）", 20, "busy")
    out = ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "hl_repro_stdout.txt"
    env = dict(os.environ, DOTNET_ROOT=str(DOTNET))
    host = subprocess.Popen([str(HOST_EXE), "--no-elevate"], env=env,
                            stdout=open(out, "w", encoding="utf-8", errors="replace"),
                            stderr=subprocess.STDOUT)
    ok = False
    try:
        for _ in range(30):
            time.sleep(0.5)
            try:
                socket.create_connection(("127.0.0.1", args.port), timeout=1).close()
                ok = True
                break
            except OSError:
                pass
        if not ok:
            print("host 没起来")
            return 1

        s = socket.create_connection(("127.0.0.1", args.port), timeout=5)
        s.sendall(frame(T_HELLO, f32(1080.0, 2400.0, 1.0, 0.0, 0.0, 0.0, 1.0, 2.0)))
        time.sleep(0.5)
        # 读掉 INFO 回包
        s.settimeout(1.0)
        try:
            s.recv(4096)
        except OSError:
            pass

        mark = log_size()
        print(f"--- 发送 T_HL 开（mark={mark}）---")
        s.sendall(frame(T_HL, f32(1.0, 0.0)))
        time.sleep(2.5)
        print("host 进程活着？", "是" if host.poll() is None else f"否（退出码 {host.poll()}）")
        print("--- 日志增量 ---")
        try:
            print(LOG.read_bytes()[mark:].decode("utf-8", "ignore"))
        except OSError:
            print("(读不到)")

        print("--- 发 T_HL 关 ---")
        try:
            s.sendall(frame(T_HL, f32(0.0, 0.0)))
            time.sleep(1.0)
            print("连接仍可用")
        except OSError as e:
            print(f"连接出错：{e}")

        print("--- host stdout/stderr ---")
        try:
            print(out.read_text(encoding="utf-8", errors="replace")[-3000:])
        except OSError:
            print("(读不到)")

        print("--- 日志尾部 ---")
        print(tail(25))
        return 0
    finally:
        try:
            host.terminate()
            host.wait(timeout=5)
        except Exception:
            try:
                host.kill()
            except Exception:
                pass
        notify("激光笔复现测试结束，桌面已交还", 6, "ok")


if __name__ == "__main__":
    sys.exit(main())
