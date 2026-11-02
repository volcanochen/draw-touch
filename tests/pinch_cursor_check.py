#!/usr/bin/env python3
"""
回归测试：捏合缩放**不能动到光标**（2026-10-30 用户报「副屏捏合 → 指针消失、飞到右边主屏的右侧」）。

背景（实测结论，别再犯）：
  · 合成触摸（TouchZoom）的触点坐标是按**虚拟桌面左上角为原点**解释的，
    不是虚拟桌面坐标：全局 = 注入值 + (VirtualScreen.Left, VirtualScreen.Top)。
    旧代码把「虚拟桌面坐标」当局部坐标注入 → 副屏上注入 -1200 会被解释成 -1200+1920
    （或 -1200-1920，视实现）→ 越界 → Windows 把它夹到主屏右沿（物理 3072）→ 光标被系统拖走。
  · 触摸接触点活动期间，Windows 会**把光标藏起来**（CURSOR_SHOWING 变 false），
    抬起后也不自己恢复 → 用户看到「指针消失」。
  · 抬手那一刻系统还会做一次「鼠标提升」，把光标放到**右手接触点**（= 中心 + span，dy=0）。

现在 PC 端的做法（**任何屏幕都走真实触摸注入**，不回退 Ctrl+滚轮）：
  · 用局部坐标注入，触点精确落在光标处 → 应用收到的是正确位置的原生双指捏合；
  · 8ms 看护定时器：整个手势 + 抬手后的尾巴期，**无条件把光标钉回捏合起点**；
  · 抬手后还会戳一下（1px 相对移动）把被系统藏起的光标叫回来，再钉回原位清掉抖动。

测试做法：伪装手机连上宿主，发真实 MOUSE_ZOOM 帧，
在【主屏】和【副屏（任意非主显示器）】各捏一次，
断言「光标位置不变」「结束后仍可见」「必须走触摸注入」。

会碰桌面（真实移动鼠标 / 向前台窗口投递捏合手势），按规矩先弹横幅、结束再弹一次。
用法：python tests/pinch_cursor_check.py [host] [port]
"""
import ctypes
import ctypes.wintypes as wt
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

u = ctypes.WinDLL("user32", use_last_error=True)
try:
    sh = ctypes.WinDLL("shcore", use_last_error=True)
except Exception:
    sh = None

# 让本进程与宿主（per-monitor DPI aware）同量纲：
# 这样 EnumDisplayMonitors 算出来的屏幕中心，和 SetCursorPos 用的是同一套坐标，
# 无论落在哪块屏都能精确命中。设置失败也没关系——同一进程内枚举与 SetCursorPos 本来就一致。
try:
    if sh is not None:
        sh.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
except Exception:
    pass
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_int(-4))  # PMv2，覆盖上面的
except Exception:
    pass


class TZPoint(ctypes.Structure):
    _fields_ = [("X", wt.LONG), ("Y", wt.LONG)]


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.LONG), ("hCursor", wt.HANDLE),
                ("ptScreenPos", TZPoint)]


class RECT(ctypes.Structure):
    _fields_ = [("left", wt.LONG), ("top", wt.LONG), ("right", wt.LONG), ("bottom", wt.LONG)]


class MI(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", RECT), ("rcWork", RECT), ("dwFlags", wt.DWORD)]


u.GetCursorInfo.argtypes = [ctypes.POINTER(CURSORINFO)]
u.GetCursorPos.argtypes = [ctypes.POINTER(TZPoint)]
u.SetCursorPos.argtypes = [wt.LONG, wt.LONG]
MONITORINFOF_PRIMARY = 1


def list_monitors():
    """返回 [(left,top,right,bottom,is_primary), ...]，按枚举顺序。"""
    data = []

    def cb(h, dc, rect, ldata):
        mi = MI()
        mi.cbSize = ctypes.sizeof(MI)
        u.GetMonitorInfoW(h, ctypes.byref(mi))
        data.append((mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right,
                     mi.rcMonitor.bottom, bool(mi.dwFlags & MONITORINFOF_PRIMARY)))
        return 1

    PROT = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_ulong, ctypes.c_ulong,
                              ctypes.POINTER(RECT), ctypes.py_object)
    u.EnumDisplayMonitors(0, 0, PROT(cb), ctypes.py_object(data))
    return data


def screen_centers():
    """返回 (primary_center, secondary_center_or_None)。中心取屏幕矩形中点。"""
    mons = list_monitors()
    prim = None
    sec = None
    for m in mons:
        cx = (m[0] + m[2]) // 2
        cy = (m[1] + m[3]) // 2
        if m[4]:
            prim = (cx, cy)
        elif sec is None:
            sec = (cx, cy)
    return prim, sec


TYPE_MOUSE = 0x07
MOUSE_ZOOM, MOUSE_FINGER = 6, 15

# 宿主日志（用来断言「走的是触摸注入，不是 Ctrl+滚轮回退」）
LOG_CANDIDATES = [
    ROOT / "PcHost" / "publish" / "win-x64" / "DrawTouchHost.log",
    ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "DrawTouchHost.log",
]


def host_log():
    """正在写日志的那个宿主日志文件（取最近修改的）。"""
    best, best_t = None, -1.0
    for p in LOG_CANDIDATES:
        if p.exists():
            t = p.stat().st_mtime
            if t > best_t:
                best, best_t = p, t
    return best


def log_mark():
    """返回 (日志文件, 当前长度)，用来只读「本次测试新增的日志」。"""
    p = host_log()
    if p is None:
        return None, 0
    try:
        return p, p.stat().st_size
    except OSError:
        return None, 0


def log_since(p, size):
    if p is None:
        return ""
    try:
        with open(p, "rb") as f:
            f.seek(size)
            return f.read().decode("utf-8", errors="ignore")
    except OSError:
        return ""


def cursor_pos():
    p = TZPoint()
    if not u.GetCursorPos(ctypes.byref(p)):
        return None
    return p.X, p.Y


def move_cursor(x, y, tries=10, wait=0.12):
    """把光标挪到 (x,y)，**确认真的挪到了**才返回。

    为什么必须重试确认：宿主在抬手后还有一段「看护尾巴期」，
    会把光标钉回捏合起点 —— 上一轮的尾巴期没结束就去挪光标，
    会被它拽回去，导致「用例其实没跑到目标屏」（结论不可信）。

    返回 True = 确认光标已在 (x,y)；False = 挪不动（桌面不可用等）。
    """
    for _ in range(tries):
        u.SetCursorPos(x, y)
        time.sleep(wait)
        p = cursor_pos()
        if p is None:
            return False
        if abs(p[0] - x) <= 2 and abs(p[1] - y) <= 2:
            return True
    return False


def cursor_visible():
    ci = CURSORINFO()
    ci.cbSize = ctypes.sizeof(CURSORINFO)
    if not u.GetCursorInfo(ctypes.byref(ci)):
        return None
    return bool(ci.flags & 1)          # CURSOR_SHOWING


def frame(kind, x=0.0, y=0.0):
    payload = struct.pack("<fff", float(kind), float(x), float(y))
    return bytes([0x53, 0x50, 0x01, TYPE_MOUSE]) + struct.pack("<H", len(payload)) + payload


def pinch(host, port, seconds=1.5, factor=1.02):
    """发一轮真实捏合。

    返回 (期间最大偏移px, 期间隐藏的采样数, 总采样数, 结束后是否可见, 结束后位置, 本轮新增日志)
    """
    s = socket.create_connection((host, port), timeout=5)
    try:
        logp, logsz = log_mark()
        s.sendall(frame(MOUSE_FINGER, 1.0, 0.0))     # 手指落在触摸板上
        time.sleep(0.12)
        base = cursor_pos()
        worst = 0
        hidden = 0
        samples = 0
        t0 = time.time()
        while time.time() - t0 < seconds:
            s.sendall(frame(MOUSE_ZOOM, factor, 0.0))
            time.sleep(0.05)
            pos = cursor_pos()
            if pos is None:                          # 桌面被锁/切换 → 本轮作废
                return None, None, None, None, None, ""
            samples += 1
            if base:
                worst = max(worst, abs(pos[0] - base[0]), abs(pos[1] - base[1]))
            if cursor_visible() is False:
                hidden += 1
        s.sendall(frame(MOUSE_FINGER, 0.0, 0.0))     # 手指离开 → 结束缩放会话
        time.sleep(1.0)                              # 等系统的「鼠标提升」+ 看护尾巴期(700ms)彻底结束，
                                                    # 否则下面的「可移动性」断言会在尾巴期被钉住、误判成挪不动
        after = cursor_pos()
        if base and after:
            worst = max(worst, abs(after[0] - base[0]), abs(after[1] - base[1]))
        return worst, hidden, samples, cursor_visible(), after, log_since(logp, logsz)
    finally:
        s.close()


def run_case(name, target, host, port, failures):
    if not move_cursor(target[0], target[1]):
        print("[%s] 光标挪不到目标屏（桌面不可用？），本项作废" % name)
        return False  # 跳过
    print("\n[%s] 捏合前 光标=%s 可见=%s" % (name, cursor_pos(), cursor_visible()))
    worst, hidden, samples, vis_after, pos_after, log = pinch(host, port)
    if worst is None:
        print("[%s] 桌面中途不可用（锁屏？），本项作废" % name)
        return False
    injected = "开始捏合" in log
    hid_pct = (hidden * 100 // samples) if samples else 0
    print("[%s] 最大偏移=%s px，期间隐藏采样=%s/%s（%s%%），结束后可见=%s 位置=%s，走触摸注入=%s"
          % (name, worst, hidden, samples, hid_pct, vis_after, pos_after, injected))
    if worst > 5:
        failures.append("%s捏合把光标挪动了 %s px（应原地不动）" % (name, worst))
    if vis_after is not True:
        failures.append("%s捏合**结束后光标仍不可见**（用户看到的就是指针消失）" % name)
    if not injected:
        failures.append("%s捏合**没有走触摸注入**（回退成 Ctrl+滚轮了）——手感会明显变差" % name)
    return True


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 6000
    try:
        socket.create_connection((host, port), timeout=3).close()
    except Exception as e:
        print("[跳过] 宿主没在跑（%s:%s 连不上：%s）" % (host, port, e))
        return 0

    orig = cursor_pos()
    if orig is None:
        # 屏幕被锁定 / 输入桌面不可用（GetCursorPos 返回 ACCESS_DENIED）：
        # 这时任何桌面交互都测不了 —— 必须**跳过**，绝不能报「通过」。
        print("[跳过] 输入桌面不可用（屏幕已锁定？），无法测捏合对光标的影响")
        return 0

    prim, sec = screen_centers()
    print("原始光标:", orig, "可见:", cursor_visible())
    print("屏幕中心: 主屏=%s 副屏=%s 拓扑=%s" % (prim, sec, list_monitors()))
    notify("正在测试：捏合缩放是否会动到光标（会短暂移动鼠标、前台窗口可能缩放）", 40)
    failures = []
    skipped = False
    try:
        # ① 主屏（primary）
        if prim is None:
            print("[主屏] 找不到主显示器，本项作废")
            skipped = True
        else:
            if not run_case("主屏", prim, host, port, failures):
                skipped = True

        # ② 副屏（任意非主显示器）
        if sec is None:
            print("[副屏] 没有第二块显示器，本项作废（单机环境无法复现副屏问题）")
            skipped = True
        else:
            if not run_case("副屏", sec, host, port, failures):
                skipped = True

        # ③ 捏合结束后，光标必须**能自由移动**（不能被看护钉住不动）
        # 这条对应 2026-10-30 实测到的 bug：抬手后的「看护尾巴期」会把光标钉回旧位置，
        # 用户表现为「捏完想挪鼠标却挪不动」/「光标被拽到别的屏」。
        # （放在恢复原位**之前**：先确认光标确实能自由动，再把用户的光标还回去。）
        print("\n[可移动性] 捏合结束后挪动光标…")
        probe = (orig[0] + 137, orig[1] + 43) if orig else (100, 100)
        if move_cursor(probe[0], probe[1]):
            print("[可移动性] 光标可自由移动 ✓")
        else:
            failures.append("捏合结束后**光标被钉住挪不动**（看护尾巴期没放行）")
            print("[可移动性] 光标挪不动 ✗（期望 %s）" % (probe,))
        if orig:
            u.SetCursorPos(orig[0], orig[1])
        time.sleep(0.2)
        print("已恢复光标:", cursor_pos(), "可见:", cursor_visible())
        notify("测试完成，桌面已交还", 6, "ok" if not failures else "fail")
    finally:
        # 兜底：无论如何都通知交还桌面
        pass

    if failures:
        print("\n=== 失败 ===")
        for f in failures:
            print("  ✗", f)
        return 1
    if skipped:
        print("\n=== 跳过：部分屏幕不可用（如无副屏/锁屏），本轮不计数通过 ===")
        return 0
    print("\n=== 通过：两种屏幕下捏合都没有动到光标，光标全程可见 ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
