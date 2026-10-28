#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SpenTablet PC 端协议级冒烟测试（回归套件核心）。

模拟手机直连 PC host（127.0.0.1:6000），逐项发帧并断言：
  1. HELLO 握手 → 收到 INFO 回包（版本/机器名/IP）
  2. SHOT_REQ 截图 → 收到 TYPE_SHOT 分片并重组为合法 PNG（捕获功能回归）
  3. MODE 切触摸板/数位板 → host.log 出现模式切换日志
  4. MOUSE 移动/按键 → host.log 出现对应日志
  5. MOUSE_ZOOM 捏合 → 触摸板缩放 API（接触点须正常抬起）或 Ctrl+滚轮 回退
  6. HL 激光笔开/关 → host.log 出现开启/关闭
  7. NOTE 备注/换色/撤销/清空、PAN 平移、RESET 复位 → host.log 断言
  7b. 桌面笔迹走分层窗口：画布能建起来、画一笔不报错（去黑边回归）
  8. 「笔让位」兜底超时 / 9. 心跳谎报自愈
  10. SAVE_REQ 截屏保存 → 存到电脑图片库 + 下发手机相册

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

sys.path.insert(0, str(Path(__file__).resolve().parent))
from desk_notify import notify  # noqa: E402  测试期间的桌面横幅（开始 / 结束）

MAGIC = b"SP"
VER = 1

T_HELLO, T_PAN, T_MOUSE, T_MODE, T_ZOOM, T_RESET = 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A
T_INFO, T_NOTE, T_SHOT_REQ, T_SHOT, T_HL = 0x0C, 0x0D, 0x0E, 0x0F, 0x10
T_SAVE_REQ, T_SAVE = 0x11, 0x12
T_PEN_DOWN, T_PEN_MOVE, T_PEN_UP = 0x01, 0x02, 0x03

M_MOVE, M_LEFT_DOWN, M_LEFT_UP, M_LEFT_CLICK, M_RIGHT_CLICK = 0, 1, 2, 3, 4
M_SCROLL, M_ZOOM, M_MIDDLE, M_FINGER, M_LEFT_DOUBLE = 5, 6, 7, 15, 16

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


def log_text_since(log: Path, since_size: int) -> str:
    """取日志自 since_size 之后的全部文本（用来从日志里抠出保存路径等细节）。"""
    try:
        return log.read_bytes()[since_size:].decode("utf-8", "ignore")
    except OSError:
        return ""


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
    mark_shot = log_size(log)
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
    # 截屏依赖「输入桌面」：host 若由服务/非交互会话（CI、计划任务、自动化代理）拉起，
    # GetDC(NULL) 返回空 → CopyFromScreen 抛「句柄无效」，这是环境限制而非功能回归。
    # 这种情况标记 SKIP，不判 FAIL（否则门禁永远红）。
    shot_blocked = log_contains(log, "截图失败：句柄无效", mark_shot, timeout=1.0)
    if shot_blocked and not ok_shot:
        print("  [SKIP] SHOT_REQ → 当前会话拿不到输入桌面（非交互启动），跳过截图断言")
    else:
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

    # 光标连推 25 帧会贴到屏幕**右下角**，而右下角正好是任务栏的「显示桌面」热点 ——
    # 在那儿点一下会把用户所有窗口最小化。所以点之前先往上挪一截，离开底边再点。
    for _ in range(8):
        s.sendall(frame(T_MOUSE, f32(M_MOVE, 0.0, -0.05)))
        time.sleep(0.01)

    mark = log_size(log)
    s.sendall(frame(T_MOUSE, f32(M_LEFT_CLICK, 0, 0)))
    check("MOUSE 左键点击 → 日志确认", log_contains(log, "单击 → 左键", mark))

    # ---- 4b. 左键双击（码 16）：必须走「一次 SendInput 发 下-上-下-上」的专门实现 ----
    mark = log_size(log)
    s.sendall(frame(T_MOUSE, f32(M_LEFT_DOUBLE, 0, 0)))
    check("MOUSE_LEFT_DOUBLE → 双击帧被处理", log_contains(log, "双击 → 左键 ×2", mark))

    # ---- 5. 捏合缩放：触摸板缩放 API 或 Ctrl+滚轮 二选一，必须有一条生效 ----
    # （部分 Windows 版本触摸注入被系统禁用/损坏 → 熔断后走 Ctrl+滚轮，同样算通过）
    mark = log_size(log)
    for f in (1.1, 1.1, 1.1, 1.1, 1.1):
        s.sendall(frame(T_MOUSE, f32(M_ZOOM, f, 0)))
        time.sleep(0.05)
    ok_any = log_contains(log, "[Mouse] 捏合 ×", mark)
    used_touch = log_contains(log, "[TouchZoom] 开始捏合", mark)
    used_fallback = log_contains(log, "Ctrl+滚轮", mark)
    check("MOUSE_ZOOM → 缩放帧被处理", ok_any)
    check("缩放路径生效（触摸板缩放 API 或 Ctrl+滚轮）", used_touch or used_fallback,
          "触摸板缩放API" if used_touch else ("Ctrl+滚轮" if used_fallback else "两者皆无"))
    if used_touch:
        ok_end = log_contains(log, "[TouchZoom] 捏合结束（抬起接触点）", mark, timeout=5)
        check("捏合结束后接触点自动抬起（UP 帧修复回归）", ok_end)
    else:
        # 回退路径成立的条件（二选一）：
        #   a) 触摸注入被系统禁用 → 出现熔断日志「已熔断」；
        #   b) 配置里注入模式本来就是 Pen（用笔注入，不碰触摸）→ 直接走 Ctrl+滚轮，
        #      这种情况下根本不涉及熔断，不能要求有熔断日志。
        ok_cb = log_contains(log, "已熔断", 0, timeout=2)
        pen_mode = log_contains(log, "注入模式: Pen", 0, timeout=1.0)
        check("缩放回退路径成立（触摸注入熔断 或 配置为 Pen 注入）", ok_cb or pen_mode,
              "已熔断" if ok_cb else ("注入模式=Pen" if pen_mode else "两者皆无"))

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
    mark_note = log_size(log)
    s.sendall(frame(T_NOTE, f32(1.0, 0.0)))
    check("NOTE 开 → 日志确认", log_contains(log, "[Note] action=1", mark_note))
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

    # ---- 7b. 桌面笔迹分层窗口（去黑边回归）----
    # 回归背景：旧实现是 Black 背景 + TransparencyKey=Black，抗锯齿边缘与黑底混色 → 笔迹带黑框。
    # 现改为 WS_EX_LAYERED + PArgb DIB + UpdateLayeredWindow。这里断言「画布建得起来 + 画一笔不炸」。
    # 注意：PEN 帧负载需 ≥24B：x, y, pressure, tiltX, tiltY, buttons(占位)
    def pen(x: float, y: float, p: float = 1.0) -> bytes:
        return f32(x, y, p, 0.0, 0.0, 0.0)

    s.sendall(frame(T_PEN_DOWN, pen(0.35, 0.35)))
    for i in range(12):
        s.sendall(frame(T_PEN_MOVE, pen(0.35 + i * 0.02, 0.35 + i * 0.01)))
        time.sleep(0.02)
    s.sendall(frame(T_PEN_UP, pen(0.55, 0.45, 0.0)))
    check("桌面笔迹 → 分层画布就绪（真透明，无黑边）",
          log_contains(log, "[NoteOverlay] 分层画布就绪", mark_note, timeout=5))
    # 四个失败锚点只要出现一个就算回归失败（用 mark_note 起算，覆盖整个笔迹阶段）
    broke = [a for a in ("[NoteOverlay] 画笔迹异常", "[NoteOverlay] 推送画面失败",
                         "[NoteOverlay] CreateDIBSection 失败", "[NoteOverlay] UpdateLayeredWindow 失败")
             if log_contains(log, a, mark_note, timeout=0.6)]
    check("画一笔笔迹不报错", not broke, "、".join(broke))
    # 测试笔迹不能留在用户桌面上：画完立刻清空并关闭备注层
    s.sendall(frame(T_NOTE, f32(3.0, 0.0)))   # 清空
    time.sleep(0.2)
    s.sendall(frame(T_NOTE, f32(0.0, 0.0)))   # 关
    time.sleep(0.2)

    # ---- 8. 「笔让位」兜底超时：手机漏发「手指离开」时不能永久僵死 ----
    # 回归背景：手机上抬最后一根手指时若笔还按着屏幕，旧版会漏发"手指离开"信号，
    # PC 的 _fingerOnPad 永远停在 true，之后笔怎么动光标都不动（心跳却正常）。
    s.sendall(frame(T_MODE, f32(1.0)))  # 回到触摸板
    time.sleep(0.3)
    mark = log_size(log)
    s.sendall(frame(T_MOUSE, f32(M_FINGER, 1.0, 0.0)))  # 手指落上触摸板 → S Pen 让位
    check("MOUSE_FINGER=1 → S Pen 让位", log_contains(log, "手指已落在触摸板上", mark))
    # 之后不再发任何 MOUSE 帧 → 4 秒后应自动解除让位
    mark = log_size(log)
    ok_release = log_contains(log, "自动解除「笔让位」", mark, timeout=10.0)
    check("4s 无手指事件 → 自动解除「笔让位」（防永久僵死）", ok_release)

    # ---- 9. 自愈：心跳谎报已发帧数 → PC 必须真的断开连接 ----
    # 回归背景：旧版这里的 break 只跳出 switch、不跳出读循环，自愈等于没写。
    mark = log_size(log)
    s2 = socket.create_connection((args.host, args.port), timeout=5)
    try:
        s2.sendall(frame(T_HELLO, hello))
        recv_frame(s2, timeout=5)  # 吃掉 INFO 回包
        # 第一条心跳定基线（PC 以此为准，不用全局残留值）
        s2.sendall(frame(0x00, struct.pack("<I", 1)))
        time.sleep(0.3)
        # 谎报：手机自称又发了 5000 个鼠标帧，实际一个没发
        s2.sendall(frame(0x00, struct.pack("<I", 5001)))
        closed = False
        deadline = time.time() + 6
        while time.time() < deadline:
            try:
                if s2.recv(64) == b"":
                    closed = True
                    break
            except (ConnectionResetError, ConnectionAbortedError):
                closed = True
                break
            except socket.timeout:
                pass
        check("谎报已发帧数 → PC 主动断开连接（自愈真的生效）", closed)
        check("断开原因写入日志", log_contains(log, "帧丢失", mark))
    finally:
        try:
            s2.close()
        except OSError:
            pass

    # ---- 10. 截屏保存（TYPE_SAVE_REQ=0x11：full, 存电脑, 存手机）----
    # 新功能回归：抓「桌面 + 笔迹」存成 PNG，电脑落盘 + 分片下发手机相册。
    # 与 SHOT_REQ 同理，非交互会话拿不到输入桌面时标记 SKIP，不判 FAIL。
    mark = log_size(log)
    s.sendall(frame(T_SAVE_REQ, f32(0.0, 1.0, 1.0)))  # 范围框 / 存电脑 / 存手机
    # 顺序很关键：**先把下发的分片读完**，再去做日志断言。
    # PNG 动辄几百 KB，服务端 SendSave 会一路写满发送缓冲后阻塞；客户端这时候跑去
    # 轮询日志 = 不读 socket，写就被卡死，最后抛「无法将数据写入传输连接」，
    # 「已下发手机存相册」这条日志永远写不出来 —— 上一版就是这么误报的。
    got = 0
    total = -1
    deadline = time.time() + 25
    try:
        while time.time() < deadline:
            fr = recv_frame(s, timeout=10)
            if fr is None:
                break
            if fr[0] != T_SAVE:
                continue
            t, _seq = struct.unpack("<IH", fr[1][:6])
            if _seq == 0:
                got = 0
                total = t
            if len(fr[1]) == 6:      # 终止帧（n=0）：收完它服务端才算写完
                break
            got += len(fr[1]) - 6
    except socket.timeout:
        pass
    if total < 0 and log_contains(log, "[Save] 截屏失败：句柄无效", mark, timeout=1.0):
        print("  [SKIP] SAVE_REQ → 当前会话拿不到输入桌面（非交互启动），跳过截屏断言")
    else:
        ok_pc = log_contains(log, "[Save] 已存到电脑：", mark, timeout=3.0)
        check("SAVE_REQ → 已保存到电脑", ok_pc)
        if ok_pc:
            import re
            m = re.search(r"\[Save\] 已存到电脑：(.+?)（", log_text_since(log, mark))
            file_ok, detail = False, ""
            if m:
                fp = Path(m.group(1).strip())
                file_ok = fp.exists() and fp.stat().st_size > 0 and \
                    fp.read_bytes()[:8] == b"\x89PNG\r\n\x1a\n"
                detail = f"{fp.name} {fp.stat().st_size if fp.exists() else 0}B"
            check("落盘文件是合法 PNG", file_ok, detail)
        check("SAVE_REQ → 收到下发给手机的分片 PNG",
              total > 0 and got >= total, f"{got}/{total}B")

    s.close()
    print(f"\n== 结果：{len(passed)} 通过, {len(failed)} 失败 ==")
    if failed:
        print("失败项: " + ", ".join(failed))
        notify(f"协议冒烟结束：{len(failed)} 项失败，桌面已交还", 8, "fail")
        sys.exit(1)
    print("ALL GREEN")
    notify(f"协议冒烟结束：{len(passed)} 项全通过，桌面已交还", 8, "ok")


if __name__ == "__main__":
    main()
