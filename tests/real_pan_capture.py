#!/usr/bin/env python3
"""
采集「真实触摸板双指 pan + 真实鼠标同时移动」时的现场（2026-11-01）。

目的：搞清楚**硬件驱动**那条路为什么能「pan 的同时鼠标还能动、指针还不分裂」，
再决定我们该模拟哪一段。全部靠采样，不猜。

采什么（每项都有时间戳，可对齐）：
  ① 光标位置 + 可见性（`GetCursorPos` / `CURSORINFO.flags`），5ms 一采；
     可见性翻转单独记一条 —— 我们注入触摸时 `CURSOR_SHOWING` 会变 0，真触摸板不该变。
  ② 低级鼠标钩子（`WH_MOUSE_LL`）每一条鼠标事件：坐标、`flags`（`LLMHF_INJECTED`）、
     `dwExtraInfo`、消息类型。用来判断：触摸板 pan 到底会不会产生鼠标事件；
     真实鼠标那些事件是不是「物理」的（flags=0）。
  ③ 前台窗口（每 200ms 一次）：pan 落到哪个应用上，便于理解它收的是滚轮还是触摸。
  ④ 原始输入（Raw Input，`RIDEV_INPUTSINK` + message-only 窗口）：按设备统计上报次数，
     区分「触摸板 HID（usage page 0x0D）」与「鼠标（page 0x01 usage 0x02）」，
     并记录设备名 —— 真触摸板 pan 期间应该只有触摸板在报，鼠标只在物理移动时报。

用法：
    python tests/real_pan_capture.py --seconds 45
退出后把屏幕上的汇总贴给我，或直接看 tests/out/real_pan_capture.json。
"""
import argparse
import ctypes
import json
import sys
import threading
import time
from ctypes import wintypes
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    from desk_notify import notify
except Exception:
    def notify(*a, **k):
        return False

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))   # 与宿主同量纲：物理像素
except Exception:
    pass

# ---------------- 基础结构 ----------------

class POINT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("hCursor", wintypes.HANDLE), ("ptScreenPos", POINT)]


class MSLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("pt", POINT), ("mouseData", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_void_p)]


class RAWINPUTDEVICE(ctypes.Structure):
    _fields_ = [("usUsagePage", wintypes.USHORT), ("usUsage", wintypes.USHORT),
                ("dwFlags", wintypes.DWORD), ("hwndTarget", wintypes.HWND)]


class RAWINPUTHEADER(ctypes.Structure):
    _fields_ = [("dwType", wintypes.DWORD), ("dwSize", wintypes.DWORD),
                ("hDevice", wintypes.HANDLE), ("wParam", wintypes.WPARAM)]


WH_MOUSE_LL = 14
WM_INPUT = 0x00FF
WM_QUIT = 0x0012
RIDEV_INPUTSINK = 0x00000100
RIM_TYPEHID = 2
CURSOR_SHOWING = 0x1


WM_MOUSE_MSGS = {0x0200: "MOVE", 0x0201: "LDOWN", 0x0202: "LUP", 0x0203: "RDBL",
                 0x0204: "RDN", 0x0205: "RUP", 0x0206: "MDBL", 0x0207: "MDN",
                 0x0208: "MUP", 0x020A: "WHEEL", 0x020B: "XDOWN", 0x020C: "XUP",
                 0x020E: "HWHEEL"}

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
HOOKPROC = ctypes.WINFUNCTYPE(LRESULT, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)

# ---- 64 位签名（不设 argtypes 的话，WPARAM/HWND 会被当 32 位 int，直接 OverflowError）----
user32.CallNextHookEx.argtypes = [ctypes.c_void_p, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]
user32.CallNextHookEx.restype = LRESULT
user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD]
user32.SetWindowsHookExW.restype = ctypes.c_void_p
user32.UnhookWindowsHookEx.argtypes = [ctypes.c_void_p]
user32.GetMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT]
user32.TranslateMessage.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.DispatchMessageW.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.DispatchMessageW.restype = LRESULT
user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.DefWindowProcW.restype = LRESULT
user32.RegisterClassW.argtypes = [ctypes.c_void_p]
user32.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
                                   ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                   wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, ctypes.c_void_p]
user32.CreateWindowExW.restype = wintypes.HWND
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetRawInputData.argtypes = [wintypes.HANDLE, wintypes.UINT, ctypes.c_void_p,
                                   ctypes.POINTER(wintypes.UINT), wintypes.UINT]
user32.GetRawInputData.restype = wintypes.UINT
user32.GetRawInputDeviceInfoW.argtypes = [wintypes.HANDLE, wintypes.UINT, ctypes.c_void_p,
                                          ctypes.POINTER(wintypes.UINT)]
user32.RegisterRawInputDevices.argtypes = [ctypes.POINTER(RAWINPUTDEVICE), wintypes.UINT, wintypes.UINT]
kernel32.GetModuleHandleW.restype = wintypes.HINSTANCE


# ---------------- 采样数据 ----------------

T0 = time.time()
lock = threading.Lock()
cursor_samples = []       # (t, x, y, visible)
vis_changes = []          # (t, visible)
mouse_events = []         # (t, msg, x, y, flags, extra)
fg_windows = []           # (t, hwnd, title)
raw_by_device = {}        # hDev -> {"n":int, "first":t, "name":str, "usages":set()}
raw_samples = []          # (t, hDev, size)


def now() -> float:
    return round(time.time() - T0, 3)


def cursor_state():
    ci = CURSORINFO(cbSize=ctypes.sizeof(CURSORINFO))
    if not user32.GetCursorInfo(ctypes.byref(ci)):
        return None
    return ci.ptScreenPos.x, ci.ptScreenPos.y, bool(ci.flags & CURSOR_SHOWING)


def sampler(stop: threading.Event):
    last = None
    last_vis = None
    while not stop.is_set():
        st = cursor_state()
        if st:
            x, y, vis = st
            t = now()
            if (x, y) != last:
                with lock:
                    cursor_samples.append((t, x, y, int(vis)))
                last = (x, y)
            if vis != last_vis:
                with lock:
                    vis_changes.append((t, int(vis)))
                last_vis = vis
        time.sleep(0.005)


def fg_watcher(stop: threading.Event):
    last = None
    buf = ctypes.create_unicode_buffer(512)
    while not stop.is_set():
        h = user32.GetForegroundWindow()
        if h != last:
            last = h
            n = user32.GetWindowTextW(h, buf, 512)
            title = buf.value if n else ""
            cls = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(h, cls, 256)
            with lock:
                fg_windows.append((now(), int(h), f"{title} [{cls.value}]"))
        time.sleep(0.2)


def hook_thread(stop: threading.Event, ready: threading.Event):
    holder = {}

    def proc(nCode, wParam, lParam):
        if nCode >= 0:
            info = ctypes.cast(lParam, ctypes.POINTER(MSLLHOOKSTRUCT)).contents
            with lock:
                mouse_events.append((now(), int(wParam),
                                     int(info.pt.x), int(info.pt.y),
                                     int(info.flags), int(info.dwExtraInfo or 0)))
        return user32.CallNextHookEx(holder.get("hook", 0), nCode, wParam, lParam)

    cb = HOOKPROC(proc)
    hook = user32.SetWindowsHookExW(WH_MOUSE_LL, cb, None, 0)
    holder["hook"] = hook
    holder["cb"] = cb
    ready.set()
    msg = wintypes.MSG()
    while not stop.is_set():
        r = user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
        if r <= 0:
            break
        user32.TranslateMessage(ctypes.byref(msg))
        user32.DispatchMessageW(ctypes.byref(msg))
        # GetMessage 阻塞；stop 置位时由外部 PostThreadMessage 唤醒
    if hook:
        user32.UnhookWindowsHookEx(hook)


# ---------------- 原始输入（message-only 窗口） ----------------

def device_name(h_dev):
    size = wintypes.UINT(0)
    user32.GetRawInputDeviceInfoW(h_dev, 0x20000000, None, ctypes.byref(size))   # RIDI_DEVICENAME
    if not size.value:
        return "?"
    buf = ctypes.create_unicode_buffer(size.value + 1)
    if user32.GetRawInputDeviceInfoW(h_dev, 0x20000000, buf, ctypes.byref(size)) <= 0:
        return "?"
    return buf.value


def raw_thread(stop: threading.Event, ready: threading.Event):
    hinst = kernel32.GetModuleHandleW(None)
    wc = ctypes.create_string_buffer(64)
    cls_name = "SpenRawSink"
    wndproc_holder = {}

    def _on_input(wp):
        size = wintypes.UINT(0)
        user32.GetRawInputData(wp, 0x10000003, None, ctypes.byref(size),
                               ctypes.sizeof(RAWINPUTHEADER))
        if not size.value:
            return 0
        buf = ctypes.create_string_buffer(size.value)
        n = user32.GetRawInputData(wp, 0x10000003, buf, ctypes.byref(size),
                                   ctypes.sizeof(RAWINPUTHEADER))
        if n > 0:
            hdr = ctypes.cast(buf, ctypes.POINTER(RAWINPUTHEADER)).contents
            key = int(hdr.hDevice)
            with lock:
                d = raw_by_device.setdefault(
                    key, {"n": 0, "name": device_name(hdr.hDevice), "first": now()})
                d["n"] += 1
                raw_samples.append((now(), key, int(hdr.dwSize), int(hdr.dwType)))
        return 0

    def wndproc(hwnd, msg, wp, lp):
        if msg == WM_INPUT:
            try:
                return _on_input(wp)
            except Exception as e:
                print("[raw] WM_INPUT 处理异常:", e)
            size = wintypes.UINT(0)
            user32.GetRawInputData(wp, 0x10000003, None, ctypes.byref(size),   # RID_INPUT
                                   ctypes.sizeof(RAWINPUTHEADER))
            if size.value:
                buf = ctypes.create_string_buffer(size.value)
                n = user32.GetRawInputData(wp, 0x10000003, buf, ctypes.byref(size),
                                           ctypes.sizeof(RAWINPUTHEADER))
                if n > 0:
                    hdr = ctypes.cast(buf, ctypes.POINTER(RAWINPUTHEADER)).contents
                    key = int(hdr.hDevice)
                    with lock:
                        d = raw_by_device.setdefault(
                            key, {"n": 0, "name": device_name(hdr.hDevice), "first": now()})
                        d["n"] += 1
                        raw_samples.append((now(), key, int(hdr.dwSize), int(hdr.dwType)))
            return 0
        return user32.DefWindowProcW(hwnd, msg, wp, lp)

    cb = WNDPROC(wndproc)
    wndproc_holder["cb"] = cb

    class WNDCLASS(ctypes.Structure):
        _fields_ = [("style", wintypes.UINT), ("lpfnWndProc", WNDPROC),
                    ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int),
                    ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
                    ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH),
                    ("lpszMenuName", wintypes.LPCWSTR), ("lpszClassName", wintypes.LPCWSTR)]

    wcst = WNDCLASS(0, cb, 0, 0, hinst, None, None, None, None, cls_name)
    user32.RegisterClassW(ctypes.byref(wcst))
    # 注意：message-only 窗口（HWND_MESSAGE）收不到 WM_INPUT，必须用普通窗口；
    # 不 ShowWindow 即可，用户看不到它，也不会抢焦点。
    # 隐藏窗口收不到 WM_INPUT（实测），必须「显示」出来；挪到屏幕外 1x1 + 不抢焦点即可。
    hwnd = user32.CreateWindowExW(0x08000000 | 0x00000080,   # WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                                  cls_name, "raw sink", 0x80000000,   # WS_POPUP
                                  -32000, -32000, 1, 1, None, None, hinst, None)
    if hwnd:
        user32.ShowWindow(hwnd, 4)   # SW_SHOWNOACTIVATE
    if not hwnd:
        ready.set()
        return

    devs = (RAWINPUTDEVICE * 4)()
    # 鼠标（page 1 / usage 2）、触摸板（HID page 0x0D / usage 0x05）、触摸屏(0x04)、笔(0x02)
    specs = [(0x01, 0x02), (0x0D, 0x05), (0x0D, 0x04), (0x0D, 0x02)]
    for i, (pg, us) in enumerate(specs):
        devs[i].usUsagePage = pg
        devs[i].usUsage = us
        devs[i].dwFlags = RIDEV_INPUTSINK
        devs[i].hwndTarget = hwnd
    user32.RegisterRawInputDevices(devs, 4, ctypes.sizeof(RAWINPUTDEVICE))
    ready.set()

    msg = wintypes.MSG()
    while not stop.is_set():
        r = user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
        if r <= 0:
            break
        user32.TranslateMessage(ctypes.byref(msg))
        user32.DispatchMessageW(ctypes.byref(msg))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=45.0)
    ap.add_argument("--selfmove", action="store_true", help="自检：自己用 SendInput 推光标")
    args = ap.parse_args()

    stop = threading.Event()
    threads = []

    for fn, name in ((sampler, "sampler"), (fg_watcher, "fg")):
        th = threading.Thread(target=fn, args=(stop,), daemon=True, name=name)
        th.start()
        threads.append(th)

    ready_hook = threading.Event()
    th = threading.Thread(target=hook_thread, args=(stop, ready_hook), daemon=True, name="hook")
    th.start()
    threads.append(th)
    ready_hook.wait(2)

    ready_raw = threading.Event()
    th = threading.Thread(target=raw_thread, args=(stop, ready_raw), daemon=True, name="raw")
    th.start()
    threads.append(th)
    ready_raw.wait(2)

    msg = ("正在采集真实触摸板：请现在做「双指 pan + 同时移动鼠标」，"
           f"{int(args.seconds)} 秒后自动结束")
    notify(msg, int(args.seconds) + 3, "busy")
    print(msg)
    print("（期间请把 pan 落在你平时用的应用上，鼠标在屏幕上随便挪）")

    if args.selfmove:
        class _MI(ctypes.Structure):
            _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG),
                        ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                        ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_void_p)]
        class _IN(ctypes.Structure):
            class _U(ctypes.Union):
                _fields_ = [("mi", _MI)]
            _anonymous_ = ("u",)
            _fields_ = [("type", wintypes.DWORD), ("u", _U)]
        for i in range(6):
            it = _IN(type=0)
            it.mi = _MI(dx=12, dy=0, dwFlags=0x0001, dwExtraInfo=0x54455354)
            user32.SendInput(1, ctypes.byref(it), ctypes.sizeof(_IN))
            time.sleep(0.25)

    time.sleep(args.seconds)
    stop.set()
    for th in threads:
        th.join(1.5)

    with lock:
        cs, vc = list(cursor_samples), list(vis_changes)
        me = list(mouse_events)
        fg = list(fg_windows)
        raw = dict(raw_by_device)

    # ---------- 汇总 ----------
    print("\n===== 采集结果 =====")
    print(f"时长 {args.seconds:.0f}s；光标位置变化样本 {len(cs)} 个；可见性翻转 {len(vc)} 次")

    if cs:
        xs = [c[1] for c in cs]
        ys = [c[2] for c in cs]
        print(f"光标轨迹：X {min(xs)}→{max(xs)}（跨度 {max(xs)-min(xs)}px），"
              f"Y {min(ys)}→{max(ys)}（跨度 {max(ys)-min(ys)}px）")
        print("前 12 个位置点：", [(c[0], c[1], c[2]) for c in cs[:12]])
    print("可见性翻转：", vc[:12], "（1=可见 0=隐藏；触摸板 pan 不该出现 0）")

    print(f"\n鼠标事件：{len(me)} 条")
    kinds = {}
    for e in me:
        kinds[WM_MOUSE_MSGS.get(e[1], hex(e[1]))] = kinds.get(WM_MOUSE_MSGS.get(e[1], hex(e[1])), 0) + 1
    print("  类型分布：", kinds)
    inj = [e for e in me if e[4] & 0x1]
    print(f"  其中 INJECTED(0x1) = {len(inj)} 条；物理(flags=0) = {len(me) - len(inj)} 条")
    print("  前 10 条：", [(e[0], WM_MOUSE_MSGS.get(e[1], hex(e[1])), e[2], e[3], hex(e[4])) for e in me[:10]])

    print(f"\n前台窗口变化：{len(fg)} 次")
    for f in fg[:8]:
        print("  ", f[0], f[2])

    print(f"\n原始输入设备：{len(raw)} 个")
    for k, v in raw.items():
        print(f"  dev={k} 上报={v['n']} 次  首个={v['first']}s  名称={v['name'][:90]}")

    out = Path(__file__).resolve().parent / "out"
    out.mkdir(exist_ok=True)
    dst = out / "real_pan_capture.json"
    dst.write_text(json.dumps({
        "duration": args.seconds,
        "cursor": cs, "vis_changes": vc,
        "mouse_events": me[:5000],
        "foreground": fg,
        "raw_devices": {str(k): v for k, v in raw.items()},
        "raw_samples": raw_samples[:8000],
    }, ensure_ascii=False, indent=1), encoding="utf-8")
    print("\n明细已写入:", dst)
    notify("采集结束，桌面已交还", 6, "ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
