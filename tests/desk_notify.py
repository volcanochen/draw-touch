#!/usr/bin/env python3
"""
桌面测试的「开始 / 结束」提示 —— 测试脚本统一走这里。

规矩（用户定的）：动真实光标的测试**不要先停下来问要不要开始**，
直接跑，但必须让用户看得见两件事：
  1. 现在正在测试（不然他会以为电脑卡了 / 鼠标坏了）；
  2. 什么时候测完了、桌面交还了。

实现方式：调用 PcHost 的 `--notify` 模式弹一个右下角横幅 ——
不抢焦点、不占任务栏、到点自动消失，不需要任何人点按钮。
没有声卡/没有桌面会话时会静默降级，**绝不让提示失败把测试搞挂**。

命令行用法：
    python tests/desk_notify.py "正在测试：会动鼠标" --seconds 20 --kind busy
    python tests/desk_notify.py "测试完成" --seconds 6 --kind ok

脚本里用法（推荐）：
    from desk_notify import notify
    notify("冒烟测试开始：会真实移动鼠标、发一次点击", 25)
    ...
    notify("冒烟测试完成，桌面已交还", 6, "ok")
"""
import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HOST_CANDIDATES = [
    ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "SpenTabletHost.exe",
    ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "SpenTabletHost.exe",
    ROOT / "PcHost" / "bin" / "Debug" / "net8.0-windows" / "win-x64" / "SpenTabletHost.exe",
    ROOT / "PcHost" / "bin" / "Debug" / "net8.0-windows" / "SpenTabletHost.exe",
]

# 静默开关：批量跑（比如门禁）时只想弹一次提示，可用 SPEN_NO_NOTIFY=1 关掉
_disabled = os.environ.get("SPEN_NO_NOTIFY") == "1"
_warned = False


def host_exe():
    for p in HOST_CANDIDATES:
        if p.exists():
            return p
    return None


def notify(text, seconds=8, kind="busy"):
    """弹一个提示横幅（不阻塞、不等它关闭）。返回是否成功拉起。

    kind: busy=正在测试(橙) / ok=完成(绿) / fail=失败(红)
    """
    global _warned
    if _disabled:
        return False
    exe = host_exe()
    if exe is None:
        if not _warned:
            _warned = True
            print("（提示）找不到 SpenTabletHost.exe，跳过桌面横幅；"
                  "先编译一次 PcHost 就能看到测试提示。")
        return False
    try:
        subprocess.Popen(
            [str(exe), "--notify", text, "--seconds", str(int(seconds)), "--kind", kind],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            close_fds=True,
        )
        return True
    except Exception as e:                       # 没有桌面会话 / 被策略拦 → 降级
        if not _warned:
            _warned = True
            print(f"（提示）桌面横幅拉起失败：{e}")
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("text")
    ap.add_argument("--seconds", type=int, default=8)
    ap.add_argument("--kind", default="busy", choices=["busy", "ok", "fail"])
    a = ap.parse_args()
    ok = notify(a.text, a.seconds, a.kind)
    print("已弹出提示" if ok else "未能弹出提示（已降级，仅打印）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
