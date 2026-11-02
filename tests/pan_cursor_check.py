#!/usr/bin/env python3
"""
回归测试：双指平移（横/斜）走的是**真实触摸注入**，不是滚轮（2026-10-31 用户报
「副屏 OneNote 横向双指不动 / 很慢 / 不受控制」，并要求「必须用注入触摸，不能用 ctrl+滚轮」）。

背景（实测结论，别再犯）：
  · 旧版双指平移走**滚轮**（竖直滚轮 + 横向滚轮 WM_MOUSEHWHEEL）；
    OneNote 等只认竖直滚轮、忽略横向滚轮 → 横向/斜向直接失效；
    且滚轮发往哪个窗口取决于光标位置、副屏上极易发错窗口 → 「很慢/不受控制」。
  · 现改为与捏合同源的**触摸注入**：注入两个一起平移的触摸接触点，
    系统翻译成前台应用的原生平移手势，各方向、主屏副屏一致，手感与真实触摸板相同。

测试做法：伪装手机连上宿主，发真实 MOUSE_PAN(17) 帧（模拟横/斜双指平移；v4 起双指平移
有专属帧，单指滚动仍是 MOUSE_SCROLL），
在【主屏】和【副屏（任意非主显示器）】各测一次，断言：
  1) 光标位置几乎不动（宿主平移期间**不钉光标**，只按需点亮）；
  2) 平移期间光标**不可见**（系统藏起来就让它藏着，宿主不点亮）；
  3) 日志出现「[TouchPan] 开始平移」→ 确认走的是触摸注入；
  4) 日志**不出现**「[Mouse] 滚动」→ 确认没回退成滚轮。

会碰桌面（真实移动鼠标 / 向前台窗口投递平移手势），按规矩先弹横幅、结束再弹一次。
用法：python tests/pan_cursor_check.py [host] [port]
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

# 让本进程与宿主（per-monitor DPI aware）同量纲
try:
    if sh is not None:
        sh.SetProcessDpiAwareness(2)
except Exception:
    pass
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_int(-4))
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
    mons = list_monitors()
    prim = sec = None
    for m in mons:
        cx = (m[0] + m[2]) // 2
        cy = (m[1] + m[3]) // 2
        if m[4]:
            prim = (cx, cy)
        elif sec is None:
            sec = (cx, cy)
    return prim, sec


TYPE_MOUSE = 0x07
MOUSE_SCROLL, MOUSE_FINGER, MOUSE_PAN = 5, 15, 17

# 宿主日志（按最近修改挑一个；测试实例跑在 publish_test/ 下时会优先命中它）
LOG_CANDIDATES = [
    ROOT / "PcHost" / "publish_test" / "DrawTouchHost.log",
    ROOT / "PcHost" / "publish" / "DrawTouchHost.log",
    ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "DrawTouchHost.log",
]


def host_log():
    best, best_t = None, -1.0
    for p in LOG_CANDIDATES:
        if p.exists():
            t = p.stat().st_mtime
            if t > best_t:
                best, best_t = p, t
    return best


def log_mark():
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
    return bool(ci.flags & 1)


def hello_frame(pan_mode=1):
    """握手：9 个 float，第 8 个=协议版本(4)，第 9 个=双指平移方式(1=触摸注入)。

    一定要发：否则脚本是在「宿主上一次连接留下的 PanMode」上跑，结论不可复现。
    """
    payload = struct.pack("<9f", 1080.0, 2400.0, 1.0, 1.0, 0.0, 0.0, 1.0, 4.0, float(pan_mode))
    return bytes([0x53, 0x50, 0x01, 0x05]) + struct.pack("<H", len(payload)) + payload


def frame(kind, x=0.0, y=0.0):
    payload = struct.pack("<fff", float(kind), float(x), float(y))
    return bytes([0x53, 0x50, 0x01, TYPE_MOUSE]) + struct.pack("<H", len(payload)) + payload


def pan(host, port, dx, dy, seconds=1.2):
    """发一轮真实双指平移。dx/dy 是「手机归一化位移」（与 PenCanvasView 同量纲）。

    返回 (期间最大偏移px, 期间隐藏采样数, 总采样数, 结束后是否可见, 结束后位置, 本轮新增日志)
    """
    s = socket.create_connection((host, port), timeout=5)
    try:
        s.sendall(hello_frame())      # 先握手：确保 PanMode=触摸注入，结论可复现
        time.sleep(0.5)
        logp, logsz = log_mark()
        s.sendall(frame(MOUSE_FINGER, 1.0, 0.0))     # 手指落在触摸板上
        time.sleep(0.12)
        base = cursor_pos()
        worst = 0
        hidden = 0
        samples = 0
        t0 = time.time()
        while time.time() - t0 < seconds:
            s.sendall(frame(MOUSE_PAN, dx, dy))
            time.sleep(0.03)
            pos = cursor_pos()
            if pos is None:                          # 桌面被锁/切换 → 本轮作废
                return None, None, None, None, None, ""
            samples += 1
            if base:
                worst = max(worst, abs(pos[0] - base[0]), abs(pos[1] - base[1]))
            if cursor_visible() is False:
                hidden += 1
        s.sendall(frame(MOUSE_FINGER, 0.0, 0.0))     # 手指离开 → 结束平移会话
        time.sleep(1.0)                              # 等系统的「鼠标提升」+ 看护尾巴期(700ms)彻底结束
        after = cursor_pos()
        if base and after:
            worst = max(worst, abs(after[0] - base[0]), abs(after[1] - base[1]))
        return worst, hidden, samples, cursor_visible(), after, log_since(logp, logsz)
    finally:
        s.close()


def run_case(name, target, host, port, dx, dy, failures):
    if not move_cursor(target[0], target[1]):
        print("[%s] 光标挪不到目标屏（桌面不可用？），本项作废" % name)
        return False
    print("\n[%s] 平移前 光标=%s 可见=%s" % (name, cursor_pos(), cursor_visible()))
    worst, hidden, samples, vis_after, pos_after, log = pan(host, port, dx, dy)
    if worst is None:
        print("[%s] 桌面中途不可用（锁屏？），本项作废" % name)
        return False
    injected = "开始平移" in log
    fell_back = "[Mouse] 滚动" in log        # 回退成滚轮会出现这条日志
    hid_pct = (hidden * 100 // samples) if samples else 0
    print("[%s] 最大偏移=%s px，期间隐藏采样=%s/%s（%s%%），结束后可见=%s 位置=%s，"
          "走触摸注入=%s 回退滚轮=%s"
          % (name, worst, hidden, samples, hid_pct, vis_after, pos_after, injected, fell_back))
    if worst > 5:
        failures.append("%s双指平移把光标挪动了 %s px（应原地不动）" % (name, worst))
    if vis_after is not True:
        failures.append("%s双指平移**结束后光标仍不可见**（指针消失）" % name)
    # 2026-11-01 定稿（用户规则）：平移期间**不要把光标点亮**，系统藏起来就让它藏着；
    # 同时每帧把指针位置钉在「进入 pan 之前的位置」（谁挪的都不例外）。
    # 正确期望：出现「开始平移」（走注入）、不出现「滚动」（没回退滚轮）、
    #           期间光标绝大多数采样**隐藏**（看不到指针）、结束后恢复可见。
    if not injected:
        failures.append("%s双指平移**没走触摸注入**（链路异常 / 回退成滚轮了）" % name)
    if fell_back:
        failures.append("%s双指平移**走了滚轮**（应与 PanMode=Touch 一致，走触摸注入）" % name)
    if samples and hidden * 100 // samples < 90:
        failures.append("%s平移期间光标只有 %d%% 采样隐藏（应 ≥90%%：pan 时不该看到指针）"
                        % (name, hidden * 100 // samples))
    return True


def scroll_guard(host, port, failures):
    """回归保护：单指滚动(dx=0) 必须继续走滚轮，绝不能变成触摸平移（否则单指滚动会平移、点一格跳 200+px）。

    返回 True 表示本项已判定（通过/失败），False 表示跳过（桌面不可用）。
    """
    orig = cursor_pos()
    if orig is None:
        return False
    if not move_cursor(orig[0], orig[1]):
        return False
    s = socket.create_connection((host, port), timeout=5)
    try:
        s.sendall(hello_frame())      # 先握手：确保 PanMode=触摸注入，结论可复现
        time.sleep(0.5)
        logp, logsz = log_mark()
        s.sendall(frame(MOUSE_FINGER, 1.0, 0.0))
        time.sleep(0.12)
        t0 = time.time()
        while time.time() - t0 < 0.8:
            s.sendall(frame(MOUSE_SCROLL, 0.0, 0.1))   # dx=0 → 单指/点击滚动，必须是滚轮
            time.sleep(0.05)
        s.sendall(frame(MOUSE_FINGER, 0.0, 0.0))
        time.sleep(0.5)
        log = log_since(logp, logsz)
    finally:
        s.close()
    injected = "开始平移" in log
    wheel = "[Mouse] 滚动" in log
    print("\n[单指滚动保护] dx=0 时 走触摸注入=%s 走滚轮=%s" % (injected, wheel))
    if injected:
        failures.append("单指滚动(dx=0)**被当成了触摸平移**（会导致单指滚动变成平移、点击滚动巨幅跳动）")
        return True
    if not wheel:
        failures.append("单指滚动(dx=0) 既没走触摸也没走滚轮（链路异常）")
        return True
    print("[单指滚动保护] dx=0 正确走了滚轮，未误触触摸平移 ✓")
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
        print("[跳过] 输入桌面不可用（屏幕已锁定？），无法测平移对光标的影响")
        return 0

    prim, sec = screen_centers()
    print("原始光标:", orig, "可见:", cursor_visible())
    print("屏幕中心: 主屏=%s 副屏=%s 拓扑=%s" % (prim, sec, list_monitors()))
    notify("正在测试：双指横/斜平移是否走真实触摸注入（会短暂移动鼠标、前台窗口可能平移）", 45)
    failures = []
    skipped = False
    try:
        # dx>0 = 横向向右平移；dy>0 = 纵向向下平移（与旧滚轮版同符号，方向不突变）
        cases = [
            ("主屏-横向", lambda: prim, 0.12, 0.0),
            ("主屏-斜向", lambda: prim, 0.12, 0.12),
        ]
        if sec is not None:
            cases += [
                ("副屏-横向", lambda: sec, 0.12, 0.0),
                ("副屏-斜向", lambda: sec, 0.12, 0.12),
            ]
        else:
            print("[副屏] 没有第二块显示器，副屏项作废（单机环境无法复现副屏问题）")
            skipped = True

        for nm, tgt, dx, dy in cases:
            if not run_case(nm, tgt(), host, port, dx, dy, failures):
                skipped = True

        # 保护：单指滚动(dx=0) 绝不能误触触摸平移（否则单指滚动变平移、点一格跳 200+px）
        if not scroll_guard(host, port, failures):
            skipped = True

        # 平移结束后，光标必须**能自由移动**（不能被看护钉住不动）
        print("\n[可移动性] 平移结束后挪动光标…")
        probe = (100, 100)
        if orig:
            # 探测点必须落在虚拟屏幕内：越界时系统会把坐标夹住，
            # 表现为「挪不动」，那是坐标本身无效，不是被看护钉住（本机光标常停在屏幕右边缘）。
            left, top = u.GetSystemMetrics(76), u.GetSystemMetrics(77)
            right, bottom = left + u.GetSystemMetrics(78), top + u.GetSystemMetrics(79)
            dx = 137 if orig[0] + 137 < right else -137
            dy = 43 if orig[1] + 43 < bottom else -43
            probe = (orig[0] + dx, orig[1] + dy)
        if move_cursor(probe[0], probe[1]):
            print("[可移动性] 光标可自由移动 ✓")
        else:
            failures.append("平移结束后**光标被钉住挪不动**（看护尾巴期没放行）")
            print("[可移动性] 光标挪不动 ✗（期望 %s）" % (probe,))
        if orig:
            u.SetCursorPos(orig[0], orig[1])
        time.sleep(0.2)
        print("已恢复光标:", cursor_pos(), "可见:", cursor_visible())
        notify("测试完成，桌面已交还", 6, "ok" if not failures else "fail")
    finally:
        pass

    if failures:
        print("\n=== 失败 ===")
        for f in failures:
            print("  ✗", f)
        return 1
    if skipped:
        print("\n=== 跳过：部分屏幕不可用（如无副屏/锁屏），本轮不计数通过 ===")
        return 0
    print("\n=== 通过：横/斜双指平移走触摸注入，指针全程隐藏且原地不动（主屏+副屏）===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
