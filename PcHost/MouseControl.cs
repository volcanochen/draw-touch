using System;
using System.Runtime.InteropServices;

namespace DrawTouchHost;

/// <summary>
/// 「鼠标模式」下的输入控制。
///
/// 与数位板模式互斥：
///   数位板模式 —— 桌面显示操作范围框，S Pen 在框内作画，手指只用来拖框；
///   鼠标模式   —— 取消操作范围框，手指当触摸板（相对移动 / 单击=左键 / 双击=双击 /
///                 双击后不松手拖动=按住左键框选），
///                 S Pen 当绝对定位的鼠标笔（按下=左键拖动，覆盖整块虚拟屏幕）。
///
/// 移动采用「读当前光标位置 + 绝对定位」而不是 SendInput 的相对 mickey：
/// 相对量会被系统的指针加速（提高指针精确度）二次加工，手感随系统设置漂移；
/// 这样能保证手指划过屏幕的比例与光标位移严格 1:1。
/// </summary>
internal static class MouseControl
{
    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESKTOP = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int INPUT_KEYBOARD = 1;
    private const int WHEEL_DELTA = 120;
    private const int VK_CONTROL = 0x11;
    private const int VK_BROWSER_BACK = 0xA6;
    private const int VK_BROWSER_FORWARD = 0xA7;
    private const int VK_VOLUME_MUTE = 0xAD;
    private const int VK_VOLUME_DOWN = 0xAE;
    private const int VK_VOLUME_UP = 0xAF;
    private const int VK_MENU = 0x12;      // Alt
    private const int VK_SHIFT = 0x10;
    private const int VK_TAB = 0x09;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // INPUT 是联合体：type(4) + 4 字节对齐 + {MOUSEINPUT(32) | KEYBDINPUT(24)}
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr lParam);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    /// <summary>MONITORINFO.dwFlags 里代表「这是主显示器」的位。</summary>
    private const uint MONITORINFOF_PRIMARY = 0x00000001;
    /// <summary>GetCursorInfo 的 CURSORINFO.flags 里代表「光标当前可见」的位。</summary>
    private const int CURSOR_SHOWING = 0x00000001;

    /// <summary>设环境变量 SPEN_DEBUG_MOVE=1 可打印每一帧的坐标细节（排查「移位不对/Gain 不对」时用）。</summary>
    private static readonly bool _debugMove =
        Environment.GetEnvironmentVariable("SPEN_DEBUG_MOVE") == "1";

    public static bool LeftDown { get; private set; }

    /// <summary>读取当前光标位置（虚拟屏幕坐标）。</summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        x = 0; y = 0;
        if (!GetCursorPos(out var p)) return false;
        x = p.X; y = p.Y;
        return true;
    }

    /// <summary>主显示器句柄（dwFlags 带 MONITORINFOF_PRIMARY 的那个）。取不到返回 Zero。</summary>
    private static IntPtr GetPrimaryMonitorHandle()
    {
        IntPtr found = IntPtr.Zero;
        MonitorEnumProc cb = null!;
        cb = (IntPtr hMon, IntPtr hdc, IntPtr lprc, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(hMon, ref mi) && (mi.dwFlags & MONITORINFOF_PRIMARY) != 0)
            {
                found = hMon;
                return false;   // 停止枚举
            }
            return true;
        };
        try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero); } catch { }
        return found;
    }

    /// <summary>取某个显示器的矩形（虚拟屏幕物理坐标）。</summary>
    private static bool TryGetMonitorRect(IntPtr hMon, out RECT rect)
    {
        rect = default;
        if (hMon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(hMon, ref mi)) return false;
        rect = mi.rcMonitor;
        return true;
    }

    /// <summary>
    /// 这一帧能不能用「合成触摸注入」做捏合缩放？返回安全时给出捏合中心（= 当前光标位置）。
    ///
    /// 【为什么只允许主显示器 + 主显示器上的前台窗口】——这是 2026-10-30 用户报的
    /// 「副屏捏合 → 指针消失、飞到右边主屏的右侧」的根因，实测复现结论：
    ///   · 触点坐标落在**副屏**时，Windows 会把这次触摸「提升成鼠标」并按
    ///     **另一块显示器**的坐标空间解释它 → 光标被系统直接搬到主屏右边缘
    ///     （实测跳到主屏物理 x=3072，即逻辑 1755 = 主屏右沿）；
    ///   · 触摸接触点活动期间，Windows 还会**把光标藏起来**（CURSOR_SHOWING 变 false），
    ///     而且抬起后也不会自己恢复 —— 这就是「指针消失」。
    /// 结论：副屏 / 前台窗口不在主屏时**一律不注入触摸**，改走 Ctrl+滚轮回退
    ///（缩放中心仍然是光标位置，手感一致，且绝不会动到光标）。
    /// </summary>
    private static bool TouchZoomTargetSafe(out int cx, out int cy)
    {
        cx = 0; cy = 0;
        if (!GetCursorPos(out var p)) return false;
        var hPri = GetPrimaryMonitorHandle();
        if (hPri == IntPtr.Zero) return false;
        // ① 光标必须在主显示器上
        if (MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST) != hPri) return false;
        // ② 前台窗口也必须在主显示器上（触点会被投递给它，坐标按它的显示器解释）
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != hPri) return false;
        cx = p.X; cy = p.Y;
        return true;
    }

    /// <summary>光标偏离「捏合开始时的位置」超过这个像素数就算被系统挪动了。</summary>
    private const int TZ_PIN_TOLERANCE_PX = 3;

    /// <summary>
    /// 把光标钉回捏合开始时的位置。
    ///
    /// 触摸注入会被 Windows 提升成鼠标，光标因此可能被系统挪走（甚至飞到别的屏）。
    /// 每帧（keep-alive）检查一次、一旦发现偏移就立刻拉回来。
    /// </summary>
    private static bool PinCursorToSaved(int saveX, int saveY, bool force)
    {
        if (!GetCursorPos(out var p)) return false;
        int dx = p.X - saveX, dy = p.Y - saveY;
        if (dx == 0 && dy == 0) return false;
        if (!force && Math.Abs(dx) <= TZ_PIN_TOLERANCE_PX && Math.Abs(dy) <= TZ_PIN_TOLERANCE_PX)
            return false;
        SetCursorPos(saveX, saveY);
        AppLog.Info($"[TouchZoom] 光标被系统触摸提升挪动 ({p.X},{p.Y}) → 钉回 ({saveX},{saveY})");
        return true;
    }

    /// <summary>光标当前是不是被系统藏起来了（触摸期间会藏，且不会自己恢复）。</summary>
    private static bool CursorIsVisible()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        if (!GetCursorInfo(ref ci)) return true;   // 读不到就别乱动
        return (ci.flags & CURSOR_SHOWING) != 0;
    }

    /// <summary>
    /// 把被系统藏起来的光标**叫回来**。
    ///
    /// Windows 的触摸行为：接触点活动期间隐藏箭头光标，抬起后也不自动恢复
    ///（实测 CURSOR_SHOWING 一直是 false）→ 用户看到的就是「指针消失」。
    /// 恢复办法（实测有效）：
    ///   ① 先「原地刷新」SetCursorPos(同坐标) —— 不挪光标，最干净；
    ///   ② 还不行就用一次 **1 像素的相对移动**触发系统的指针刷新，再把光标放回原位。
    /// </summary>
    private static void EnsureCursorVisible(int keepX, int keepY)
    {
        if (CursorIsVisible()) return;
        SetCursorPos(keepX, keepY);
        if (CursorIsVisible()) return;
        Send(MOUSEEVENTF_MOVE, 1, 0);     // 1 像素相对移动 → 系统重新显示指针
        SetCursorPos(keepX, keepY);       // 复位，不让光标真的挪走
        if (!CursorIsVisible())
            AppLog.Warn("[TouchZoom] 光标仍被系统隐藏（触摸行为），已尽力恢复");
    }

    /// <summary>把归一化坐标(0..1)换算成虚拟屏幕像素并绝对定位光标。</summary>
    public static void MoveTo(MappingConfig cfg, int px, int py)
    {
        int ax = Norm(px, cfg.VirtualX, cfg.VirtualW);
        int ay = Norm(py, cfg.VirtualY, cfg.VirtualH);
        Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESKTOP, ax, ay);
    }

    /// <summary>
    /// 相对当前光标位置平移（像素）。超出虚拟屏幕会被夹住。
    ///
    /// 返回**没走完的残量**（系统截断掉的部分），交给调用方下一帧继续走。
    ///
    /// 为什么需要读回：绝对定位在下面这些场景会被系统改写或直接拒绝 ——
    ///   · Windows 虚拟桌面（Win+Ctrl+←/→）：每个桌面有自己的显示器布局，
    ///     目标坐标在当前桌面无效 → 系统不动光标（表现为「完全控制不了」）；
    ///   · 异构多屏 / 缩放不同的显示器：坐标会被夹到当前桌面的有效范围。
    /// 所以这里发完立刻读回光标真实位置：没走完就记残量；一个像素都没动
    /// 说明这次绝对定位被系统拒绝 → 退回「相对移动」保证控制不失效。
    /// </summary>
    public static (int remX, int remY) MoveBy(MappingConfig cfg, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return (0, 0);
        if (!GetCursorPos(out var p)) return (0, 0);

        int tx = (int)Math.Clamp((long)p.X + dx, cfg.VirtualX, cfg.VirtualX + cfg.VirtualW - 1);
        int ty = (int)Math.Clamp((long)p.Y + dy, cfg.VirtualY, cfg.VirtualY + cfg.VirtualH - 1);

        // 光标已经贴在屏幕边界、这一帧本来就推不动：**不能**据此判定注入失败。
        // （以前没这一条，指针停在屏幕角上时会误报「注入被权限拦截」。）
        if (tx == p.X && ty == p.Y) return (0, 0);

        Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESKTOP,
            Norm(tx, cfg.VirtualX, cfg.VirtualW), Norm(ty, cfg.VirtualY, cfg.VirtualH));

        if (!GetCursorPos(out var q)) return (0, 0);
        if (_debugMove)
            AppLog.Info($"[MoveBy] p=({p.X},{p.Y}) 目标=({tx},{ty}) 绝对后=({q.X},{q.Y}) " +
                        $"cfg屏幕=({cfg.VirtualX},{cfg.VirtualY}) {cfg.VirtualW}x{cfg.VirtualH} " +
                        $"GetSystemMetrics={GetSystemMetrics(76)},{GetSystemMetrics(77)} " +
                        $"{GetSystemMetrics(78)}x{GetSystemMetrics(79)}");
        if (q.X == p.X && q.Y == p.Y)
        {
            // 一个像素都没动：可能是绝对定位被系统拒绝（Windows 虚拟桌面 / 异构多屏），
            // 也可能是权限级别不够被 UIPI 静默丢弃 → 先用相对移动兜底，再验一次。
            Send(MOUSEEVENTF_MOVE, dx, dy);
            if (GetCursorPos(out var r))
            {
                if (r.X == p.X && r.Y == p.Y)
                {
                    // 绝对 + 相对都不动 = 不是坐标问题，是注入根本没生效（典型：前台是管理员权限程序）
                    InputGuard.NoteCursorStuck();
                }
                else
                {
                    InputGuard.NoteOk();
                }
            }
            return (0, 0);
        }
        InputGuard.NoteOk();
        // 走了一部分：把没走完的残量留给调用方（下一帧接着走）
        return (tx - q.X, ty - q.Y);
    }

    public static void Down()
    {
        if (LeftDown) return;
        Send(MOUSEEVENTF_LEFTDOWN);
        LeftDown = true;
    }

    public static void Up()
    {
        if (!LeftDown) return;
        Send(MOUSEEVENTF_LEFTUP);
        LeftDown = false;
    }

    public static void LeftClick()
    {
        Send(MOUSEEVENTF_LEFTDOWN);
        Send(MOUSEEVENTF_LEFTUP);
    }

    /// <summary>
    /// 左键双击。
    /// 四个事件放进**同一次** SendInput：拆成两次 LeftClick() 的话，两次之间要过一次网络/线程调度，
    /// 一旦超过系统双击时间（默认 500ms）就会被判成两次独立单击 —— 双击打开文件就失效了。
    /// </summary>
    public static void DoubleClick()
    {
        var inputs = new INPUT[4];
        for (int i = 0; i < 4; i++)
        {
            inputs[i] = new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { dwFlags = (i % 2 == 0) ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP }
            };
        }
        SendInputChecked(inputs, "双击");
    }

    public static void RightClick()
    {
        Send(MOUSEEVENTF_RIGHTDOWN);
        Send(MOUSEEVENTF_RIGHTUP);
    }

    // ================= 触摸板手势支持 =================

    /// <summary>垂直滚轮（按格）。notches &gt; 0 = 向上滚。</summary>
    public static void Wheel(int notches)
    {
        if (notches == 0) return;
        SendWheel(MOUSEEVENTF_WHEEL, notches * WHEEL_DELTA);
    }

    /// <summary>水平滚轮（按格）。</summary>
    public static void HWheel(int notches)
    {
        if (notches == 0) return;
        SendWheel(MOUSEEVENTF_HWHEEL, notches * WHEEL_DELTA);
    }

    /// <summary>Ctrl + 滚轮（按格）—— 绘图软件（PS/Krita）里就是缩放画布。</summary>
    public static void CtrlWheel(int notches)
    {
        if (notches == 0) return;
        SendKey(VK_CONTROL, up: false);
        SendWheel(MOUSEEVENTF_WHEEL, notches * WHEEL_DELTA);
        SendKey(VK_CONTROL, up: true);
    }

    // ---- 高分辨率（像素级）滚动：Windows 会当成触摸板的 pan 一样平滑滚动 ----

    /// <summary>像素级纵向滚动。pixels &gt; 0 = 向上。</summary>
    public static void WheelPixels(int pixels)
    {
        if (pixels == 0) return;
        SendWheel(MOUSEEVENTF_WHEEL, pixels);
    }

    /// <summary>像素级横向滚动。</summary>
    public static void HWheelPixels(int pixels)
    {
        if (pixels == 0) return;
        SendWheel(MOUSEEVENTF_HWHEEL, pixels);
    }

    /// <summary>Ctrl + 像素级滚轮（缩放画布）。整轮 Ctrl 按下→滚→抬起，独立的一次缩放操作。</summary>
    public static void CtrlWheelPixels(int pixels)
    {
        if (pixels == 0) return;
        SendKey(VK_CONTROL, up: false);
        SendWheel(MOUSEEVENTF_WHEEL, pixels);
        SendKey(VK_CONTROL, up: true);
    }

    // ============ 触摸板缩放会话（照抄真实触摸板抓包结果 2026-10-29） ============
    // PointerProbe 实测真实触摸板捏合发给应用的消息流：
    //   VK_CONTROL 按下 → WM_MOUSEWHEEL delta=±120 ×N（间隔 40~160ms）
    //   → 停手期间【一条消息都没有】→ VK_CONTROL 松开。
    // 结论：OneNote 的「模糊缩放、松手定稿」由 Ctrl 的 KEYUP 触发；停手不刷新是因为
    // 停手时没有消息，而不是有什么 keep-alive。旧实现每格都「按 Ctrl→滚→松 Ctrl」，
    // 每格都是一次独立缩放操作 → 每格立即重排（用户抱怨的「停一下就刷新」根源）。
    // 正确姿势：捏合开始只按一次 Ctrl，期间只发滚轮，MOUSE_FINGER off 才松开。

    private static bool _czActive;          // Ctrl 会话进行中
    private static double _czAccum;         // 未发送的缩放余量（格，小数）
    private static int _czSent;             // 本会话已发送的滚轮格数（调试显示用）
    private static int _czFlushCount;       // 本会话 flush 次数（日志节流用）
    private static long _czLastMs;          // 最后一个缩放帧时刻（兜底用）
    private const int CZ_MAX_ACCUM = 6;     // 余量上限（格），防失控
    private const int CZ_SAFETY_MS = 10000; // 手机断连兜底：10s 无帧自动松 Ctrl
    private const int CZ_FLUSH_MS = 16;     // 发送节拍：60Hz 匀速（真实触摸板 ~30格/s 匀速流）

    private static readonly System.Threading.Timer? _czTimer =
        new System.Threading.Timer(_ => CtrlZoomTick(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    private static void CtrlZoomTick()
    {
        try
        {
            if (!_czActive) return;
            long now = Environment.TickCount64;
            if (now - _czLastMs >= CZ_SAFETY_MS) { CtrlZoomEnd(); return; }
            CtrlZoomFlush();
            try { _czTimer?.Change(CZ_FLUSH_MS, System.Threading.Timeout.Infinite); } catch { }
        }
        catch { }
    }

    /// <summary>把累计余量以小数 delta 匀速发出（不按整格量化，消除爆发式节奏=卡顿）。</summary>
    private static void CtrlZoomFlush()
    {
        int delta = (int)Math.Round(_czAccum * 120.0);
        if (delta == 0) return;
        SendWheel(MOUSEEVENTF_WHEEL, delta);
        _czAccum -= delta / 120.0;
        _czSent += delta;
        _czFlushCount++;
        // 日志节流：会话首条 + 每 20 条 + 明显累计时输出
        if (_czFlushCount == 1 || _czFlushCount % 20 == 0 || Math.Abs(_czAccum) > 0.9)
            AppLog.Info($"[TouchZoom] 匀速滚轮 {delta:+0;-0}（会话累计 {_czSent / 120.0:+0.0;-0.0} 格，余量 {_czAccum:F2} 格）");
    }

    /// <summary>捏合开始/继续：Ctrl 只在首轮按下；只累计，发送由 60Hz 定时器匀速完成。</summary>
    public static void CtrlZoomFactor(double factor)
    {
        if (!_czActive)
        {
            SendKey(VK_CONTROL, up: false);
            _czActive = true;
            _czAccum = 0;
            _czSent = 0;
            _czFlushCount = 0;
            AppLog.Info("[TouchZoom] Ctrl↓ 缩放会话开始（Ctrl 按住跨整轮手势，60Hz 匀速滚轮）");
        }
        _czAccum = Math.Clamp(_czAccum + Math.Log(factor) * 10.0, -CZ_MAX_ACCUM, CZ_MAX_ACCUM);
        _czLastMs = Environment.TickCount64;
        try { _czTimer?.Change(CZ_FLUSH_MS, System.Threading.Timeout.Infinite); } catch { }
    }

    /// <summary>捏合结束（手指离开触摸板）：发完余量 → 松开 Ctrl → 应用此刻定稿刷新。</summary>
    public static void CtrlZoomEnd()
    {
        if (!_czActive) return;
        _czActive = false;
        CtrlZoomFlush();
        _czAccum = 0;
        SendKey(VK_CONTROL, up: true);
        AppLog.Info($"[TouchZoom] Ctrl↑ 缩放会话结束（共 {_czSent / 120.0:+0.0;-0.0} 格；松开 Ctrl = 应用定稿刷新）");
    }

    // ================= 真实「触摸板缩放」：模拟双指捏合（触摸注入） =================
    // 这是 Windows 精密触摸板的捏合缩放在系统层的等价物：把两个触摸接触点喂给系统，
    // 系统再像真实触摸板那样把它翻译成前台应用的缩放手势。浏览器 / 地图 / Office /
    // 图片查看器 / PDF 等都原生支持，手感与真实触摸板一致。
    // 若系统不支持触摸注入（很老的系统 / 注入失败），调用方应回退到 Ctrl+滚轮。

    private const uint POINTER_TYPE_TOUCH = 0x00000002;
    // 官方 POINTER_FLAGS 值（winuser.h）。
    // 状态机（与真实触摸接触一致）：
    //   接触开始 = INRANGE|INCONTACT|DOWN；接触移动 = INRANGE|INCONTACT|UPDATE；
    //   接触抬起 = INRANGE|UP。手势存续期间必须持续喂帧（同坐标 UPDATE 合法），
    //   抬起必须发 UP，否则接触点永远挂着。
    private const uint POINTER_FLAG_NEW = 0x00000001;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    private struct TZPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public TZPoint ptPixelLocation;
        public TZPoint ptHimetricLocation;
        public TZPoint ptPixelLocationRaw;
        public TZPoint ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong performanceCount;
        public uint buttonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint feedbackMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // 官方布局：type(DWORD) + 4 字节对齐填充 + union（POINTER_TOUCH_INFO，8 字节对齐 → 偏移 8）。
    // 之前误写成「完整 POINTER_INFO + 完整 TOUCH_INFO」并列 → 封送垃圾 → err=87。
    private const uint TOUCH_MASK_PRESSURE = 0x00000004;
    // 触摸反馈模式（winuser.h：DEFAULT=1、INDIRECT=2、NONE=3）。
    // 必须用 NONE：INDIRECT 会让 Windows 在屏幕上画出两个白色触摸圆环（用户看到的
    // 「虚拟手指点」）。NONE = 只投递手势、不画任何视觉反馈，
    // 实测（2026-10-30）fb=3 创建设备与整套注入时序均成功。
    private const uint TOUCH_FEEDBACK_NONE = 0x00000003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TYPE_INFO
    {
        public uint type;                  // POINTER_INPUT_TYPE = PT_TOUCH
        public POINTER_TOUCH_INFO touch;   // union 成员
    }

    private static IntPtr _synthDevice;

    private static bool _touchInit;
    private static bool _touchReady;
    private static bool _tzActive;
    private static int _tzFailCount;
    private static int _tzCx, _tzCy;     // 捏合中心（屏幕像素，整轮手势固定）
    // 当前半跨度（像素）。**必须是浮点**：手机每帧给的 factor 常常只有 1.001~1.02，
    // 用 int 存的话 24×1.01=24.24 会被截断回 24，微小增量永远累积不起来 →
    // 缩到最小跨度后怎么都放不大（2026-10-30 用户反馈「卡住」的根因）。
    private static double _tzSpan;
    private static double _tzMaxSpan;    // 本轮手势的跨度上限（受虚拟屏幕边界约束）
    private static bool _tzClampLogged;  // 本轮是否已经记过「触界」日志（避免刷屏）
    private static int _tzLastX, _tzLastY; // 最后一帧注入坐标（UP 必须与其完全一致）
    // 捏合开始时的光标位置：触摸注入被系统「提升成鼠标」时光标会被挪走，
    // 靠它把光标钉回原处（否则用户看到的是「光标消失 / 飞到别的屏」）。
    private static int _tzSaveX, _tzSaveY;
    private static bool _tzSaved;
    private static long _tzLastMs;       // 上一帧（真实或 keep-alive）注入时刻
    private static long _tzLastRealMs;   // 上一「真实事件」时刻（手机缩放帧/手势开始）
    private static readonly System.Threading.Timer? _tzTickTimer =
        new System.Threading.Timer(_ => TouchZoomTick(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    /// <summary>系统是否支持触摸注入（用于决定能否走真实触摸板缩放）。</summary>
    public static bool TouchZoomAvailable()
    {
        EnsureTouchInit();
        return _touchReady;
    }

    private static void EnsureTouchInit()
    {
        if (_touchInit) return;
        _touchInit = true;
        try
        {
            // InjectTouchInput 在新版 Windows 上已损坏（社区共识：任何参数永远 87）。
            // 改用 CreateSyntheticPointerDevice(PT_TOUCH) + InjectSyntheticPointerInput
            // —— 本机实测可用（2026-10-29）。参数照抄社区可用样例：maxCount=2。
            // 反馈模式用 NONE（3）：INDIRECT 会在屏幕上画出两个触摸圆环。
            _synthDevice = CreateSyntheticPointerDevice(POINTER_TYPE_TOUCH, 2, TOUCH_FEEDBACK_NONE);
            _touchReady = _synthDevice != IntPtr.Zero;
            if (_touchReady)
                AppLog.Info("[TouchZoom] 合成指针设备创建成功（双指触摸注入可用）");
            else
                AppLog.Warn($"[TouchZoom] 创建合成指针设备失败 err={Marshal.GetLastWin32Error()}（将回退 Ctrl+滚轮）");
        }
        catch (Exception ex)
        {
            _touchReady = false;
            AppLog.Warn($"[TouchZoom] 不支持合成指针注入：{ex.Message}（将回退 Ctrl+滚轮）");
        }
    }

    /// <summary>捏合基准半跨度（像素）。</summary>
    private const int TZ_BASE_SPAN = 90;
    // 上下限决定**一次手势**能表达的缩放范围（相对基准 90）：
    // 下限 12 = 可缩到 13%（7.5 倍），上限 900 = 可放到 10 倍。
    // 原来是 24/600：缩到 24 只要手指间距变到 27% 就触底，之后捏拢的量全丢，
    // 反向张开得先把丢失的量补回来 → 表现就是「缩小后放不大」。
    private const double TZ_MIN_SPAN = 12;
    private const double TZ_MAX_SPAN = 900;
    /// <summary>两指离屏幕边缘至少留这么多像素，防止注入到虚拟屏幕之外。</summary>
    private const int TZ_EDGE_MARGIN = 8;
    // 官方文档（InjectTouchInput）三条硬规则（2026-10-29 调研确认）：
    //  1) 相邻注入帧间隔超过 100ms → ERROR_TIMEOUT，接触点被系统判「过期」取消
    //     （应用收到手势终结 → 刷新重排）。手势存续期间必须持续喂帧。
    //  2) 「按住不动」的官方姿势 = DOWN 之后在【相同坐标】连发 UPDATE 帧——
    //     同坐标帧完全合法。之前「同坐标必 87」是我们对熔断的误诊。
    //  3) UP 帧坐标必须与上一帧 UPDATE 完全一致，否则 err=87 且【所有活动触点】
    //     被取消——怀疑就是最初熔断的真凶。
    // 真实触摸板手停时硬件仍 ≥125Hz 上报（坐标不变）→ 应用看到「坐标没变的活跃手势」
    // → 零位移增量 → 不刷新。我们用 60ms 同坐标 keep-alive 帧模仿（16Hz，留足余量）。
    /// <summary>手停 keep-alive 帧间隔(ms)：必须 &lt; 100（官方 ERROR_TIMEOUT 红线）。</summary>
    private const int TZ_KEEPALIVE_MS = 60;
    /// <summary>兜底：手机端这么久没有任何真实事件（缩放帧/离板帧）才强制抬手，
    /// 防手机断连后接触点永久残留。keep-alive 帧不刷新此计时。</summary>
    private const int TZ_SAFETY_LIFT_MS = 10000;

    private static void EndTouchZoom()
    {
        if (!_tzActive) return;
        _tzActive = false;
        // 必须发真正的 UP 帧（INRANGE|UP）把两个接触点抬起来，且坐标与上一帧一致（规则 3）。
        InjectOneFrame(phase: TouchPhase.Up, _tzLastX, _tzLastY, _tzSpan);
        // 抬完手把光标拉回原位并叫醒它：
        //   · 触摸被系统「提升成鼠标」时可能把光标拖走 → 钉回；
        //   · 触摸期间系统会藏起光标且不自己恢复 → 强制显示。
        if (_tzSaved)
        {
            PinCursorToSaved(_tzSaveX, _tzSaveY, force: true);
            EnsureCursorVisible(_tzSaveX, _tzSaveY);
            _tzSaved = false;
        }
        AppLog.Info($"[TouchZoom] 捏合结束（抬起接触点，应用定稿刷新）最终跨度±{_tzSpan:0.0}");
    }

    /// <summary>周期回调：手停时注入【同坐标】keep-alive 帧维持手势；太久没真实事件才兜底抬手。</summary>
    private static void TouchZoomTick()
    {
        try
        {
            if (!_tzActive) return;
            long now = Environment.TickCount64;
            if (now - _tzLastRealMs >= TZ_SAFETY_LIFT_MS) { EndTouchZoom(); return; }
            if (now - _tzLastMs >= TZ_KEEPALIVE_MS)
            {
                // 官方「按住不动」姿势：相同坐标连发 UPDATE 帧。
                // 应用只看到手势活跃、没有任何位移增量 → 不刷新、不平移。
                if (InjectOneFrame(TouchPhase.Move, _tzLastX, _tzLastY, _tzSpan))
                {
                    _tzLastMs = Environment.TickCount64;
                    // keep-alive 帧同样会触发系统的「触摸提升」副作用 → 立刻钉住并叫醒光标
                    if (_tzSaved)
                    {
                        PinCursorToSaved(_tzSaveX, _tzSaveY, force: false);
                        EnsureCursorVisible(_tzSaveX, _tzSaveY);
                    }
                }
                else
                {
                    // 注入失败（可能已被系统超时取消）→ 尝试清理后结束，下一轮重新 Down
                    EndTouchZoom();
                    return;
                }
            }
            try { _tzTickTimer?.Change(TZ_KEEPALIVE_MS, System.Threading.Timeout.Infinite); } catch { }
        }
        catch { }
    }

    /// <summary>触摸帧的三种阶段：落下 / 移动（保持接触）/ 抬起。</summary>
    private enum TouchPhase { Down, Move, Up }

    /// <summary>
    /// 推进一次捏合缩放。
    /// factor&gt;1 张开（放大），&lt;1 捏拢（缩小）。以当前光标位置为捏合中心。
    /// 返回 true 表示已成功用真实触摸注入发出（调用方无需再发 Ctrl+滚轮）；
    /// false 表示系统不支持或注入失败（调用方应回退到 Ctrl+滚轮）。
    /// </summary>
    public static bool UpdateTouchZoom(double factor, int cx, int cy)
    {
        EnsureTouchInit();
        if (!_touchReady) return false;

        // 【安全闸门】只有「光标 + 前台窗口都在主显示器」时才允许注入合成触摸。
        // 副屏注入会让 Windows 把触点按主屏坐标解释、再提升成鼠标 → 光标被搬到
        // 主屏右边缘；同时触摸期间光标会被系统藏起来且不自己恢复（指针消失）。
        // 这里直接返回 false，让调用方回退到 Ctrl+滚轮（缩放中心仍是光标，手感一致）。
        if (!TouchZoomTargetSafe(out cx, out cy))
        {
            if (_tzActive) EndTouchZoom();      // 手势中途失效 → 先把接触点抬干净
            return false;
        }

        long now = Environment.TickCount64;
        if (!_tzActive)
        {
            // 开新一轮捏合：在当前光标处落下两个接触点（基准跨度）。
            // 可用样例在 DOWN 前有一帧 flags=0 的「预注入」（复位设备状态），照抄，尽力而为。
            InjectSyntheticPointerInput(_synthDevice, BuildSynthFrame(0, cx, cy, TZ_BASE_SPAN), 2);
            _tzCx = cx; _tzCy = cy;
            _tzSpan = TZ_BASE_SPAN;
            _tzClampLogged = false;
            // 记住捏合开始时的光标位置：触摸被系统「提升成鼠标」时光标会被挪走，
            // 靠它把光标钉回原处（否则用户看到的是「光标消失 / 飞到别的屏」）。
            _tzSaveX = cx; _tzSaveY = cy; _tzSaved = true;
            // 上限受**主显示器**边界约束（不是虚拟桌面！）：两指水平排布，只需 x 方向不出界，
            // 否则注入坐标落到显示器之外会被系统丢弃/夹住，缩放就断了。
            // （副屏上界用虚拟桌面算会算出「跨到别的屏」的跨度——那正是光标飞走的帮凶。）
            var hPri = GetPrimaryMonitorHandle();
            int bound = (int)TZ_MAX_SPAN;
            if (TryGetMonitorRect(hPri, out var pm))
                bound = Math.Min(pm.Right - pm.Left, pm.Bottom - pm.Top);
            var vs = SystemInformation.VirtualScreen;
            int vBound = Math.Min(cx - vs.Left, vs.Right - cx);
            _tzMaxSpan = Math.Min(TZ_MAX_SPAN,
                Math.Max(TZ_MIN_SPAN, Math.Min(bound / 2, vBound) - TZ_EDGE_MARGIN));
            if (!InjectOneFrame(TouchPhase.Down, cx, cy, _tzSpan))
            {
                _tzSaved = false;
                return false;
            }
            _tzActive = true;
            _tzLastX = cx; _tzLastY = cy;
            AppLog.Info($"[TouchZoom] 开始捏合 @({cx},{cy}) 跨度±{_tzSpan:0.0} 上限{_tzMaxSpan:0.0}");
            EnsureCursorVisible(_tzSaveX, _tzSaveY);
        }
        else
        {
            // 整轮手势固定中心（捏合期间光标不会动）；跨度按相对系数缩放（浮点累积，
            // 再小的 factor 也会一点点攒起来，不会被截断吃掉）。
            // 跨度没变时就是同坐标帧——官方「按住不动」姿势，完全合法（规则 2）。
            double want = _tzSpan * factor;
            _tzSpan = Math.Clamp(want, TZ_MIN_SPAN, _tzMaxSpan);
            // 触界自检：夹住了说明这一段缩放量被丢弃（手指继续捏但画面对不上），
            // 记一条便于日后判断上下限是否还要放宽。
            if (!_tzClampLogged && Math.Abs(want - _tzSpan) > 0.5)
            {
                _tzClampLogged = true;
                AppLog.Warn($"[TouchZoom] 跨度触界 want={want:0.0} → {_tzSpan:0.0}（上限{_tzMaxSpan:0.0}）");
            }
            if (!InjectOneFrame(TouchPhase.Move, _tzCx, _tzCy, _tzSpan))
            {
                // 触点可能已被系统取消 → 结束手势，下一个缩放帧重新 Down
                _tzActive = false;
                _tzSaved = false;
                return false;
            }
            _tzLastX = _tzCx; _tzLastY = _tzCy;
            // 每帧都要把光标钉回原位并叫醒它（触摸提升会挪走/藏起光标）
            PinCursorToSaved(_tzSaveX, _tzSaveY, force: false);
            EnsureCursorVisible(_tzSaveX, _tzSaveY);
        }
        _tzLastMs = now;
        _tzLastRealMs = now;
        // 下一拍：手停由 TouchZoomTick 注入同坐标 keep-alive 帧（<100ms 红线内）；
        // 正常结束由 MOUSE_FINGER off 触发
        try { _tzTickTimer?.Change(TZ_KEEPALIVE_MS, System.Threading.Timeout.Infinite); } catch { }
        return true;
    }

    /// <summary>构造同帧双指（参数照抄 TouchInjectTest mode 5 实测可用组合）。flags=0 即预注入复位帧。</summary>
    private static POINTER_TYPE_INFO[] BuildSynthFrame(uint flags, int cx, int cy, double span)
    {
        var arr = new POINTER_TYPE_INFO[2];
        for (int i = 0; i < 2; i++)
        {
            // 跨度是浮点（要能累积微小增量），注入时才取整到像素。
            int px = (int)Math.Round(i == 0 ? cx - span : cx + span);
            arr[i] = new POINTER_TYPE_INFO
            {
                type = POINTER_TYPE_TOUCH,
                touch = new POINTER_TOUCH_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = POINTER_TYPE_TOUCH,
                        pointerId = 1u + (uint)i,          // 从 1 开始（0 在部分系统被判无效）
                        frameId = 0,                        // 可用样例：恒 0 不递增
                        pointerFlags = flags,
                        sourceDevice = IntPtr.Zero,
                        hwndTarget = GetForegroundWindow(), // 可用样例：喂给前台窗口
                        ptPixelLocation = new TZPoint { X = px, Y = cy },
                        ptPixelLocationRaw = new TZPoint { X = px, Y = cy },
                        historyCount = 0,                   // 可用样例：0
                    },
                    touchMask = TOUCH_MASK_PRESSURE,        // 只报压力，不用接触面积
                    pressure = 1,
                },
            };
        }
        return arr;
    }

    /// <summary>构造并发出一帧（两个接触点）：Down 落点 / Move 保持接触移动 / Up 抬起结束。</summary>
    /// <remarks>
    /// 参数严格照抄 TouchInjectTest mode 5（本机 2026-10-29 实测零 FAIL 的组合）：
    /// frameId 恒 0、historyCount=0、hwndTarget=前台窗口、touchMask=PRESSURE、pressure=1、
    /// pointerId 从 1 开始、双指同帧一次调用。
    /// </remarks>
    private static bool InjectOneFrame(TouchPhase phase, int cx, int cy, double span)
    {
        try
        {
            if (_synthDevice == IntPtr.Zero) return false;
            // 状态机（与真实触摸接触一致）：
            //   Down  = INRANGE|INCONTACT|DOWN   （接触开始）
            //   Move  = INRANGE|INCONTACT|UPDATE （接触移动）
            //   Up    = INRANGE|UP               （接触抬起终结）
            uint flags = phase switch
            {
                TouchPhase.Down => POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN,
                TouchPhase.Move => POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE,
                _ => POINTER_FLAG_INRANGE | POINTER_FLAG_UP,
            };
            bool ok = InjectSyntheticPointerInput(_synthDevice, BuildSynthFrame(flags, cx, cy, span), 2);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                // 连续失败 3 次视为系统级不支持，永久禁用走 Ctrl+滚轮回退。
                if (++_tzFailCount >= 3 && _touchReady)
                {
                    _touchReady = false;
                    AppLog.Warn($"[TouchZoom] 合成指针注入连续失败 err={err} ×{_tzFailCount}，已熔断并永久回退 Ctrl+滚轮");
                }
                else
                {
                    AppLog.Warn($"[TouchZoom] InjectSyntheticPointerInput 失败 err={err}");
                }
            }
            else
            {
                _tzFailCount = 0;
            }
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[TouchZoom] 注入异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>捏合注入还活着就立刻结束（模式切换 / 释放全部输入时调用，防接触点残留）。</summary>
    public static void EndTouchZoomIfActive()
    {
        if (_tzActive) EndTouchZoom();
    }

    /// <summary>中键点击（浏览器里=新标签页打开链接；绘图软件里常用来平移画布）。</summary>
    public static void MiddleClick()
    {
        Send(MOUSEEVENTF_MIDDLEDOWN);
        Send(MOUSEEVENTF_MIDDLEUP);
    }

    public static void Back() => TapKey(VK_BROWSER_BACK);

    public static void Forward() => TapKey(VK_BROWSER_FORWARD);

    // ---- 系统级快捷键：音量 / 静音 / 任务切换 ----

    public static void VolumeUp() => TapKey(VK_VOLUME_UP);

    public static void VolumeDown() => TapKey(VK_VOLUME_DOWN);

    public static void VolumeMute() => TapKey(VK_VOLUME_MUTE);

    /// <summary>
    /// 任务切换 Alt+Tab：按住 Alt 不放，敲一下 Tab 再松开 Alt —— 与系统 Alt+Tab 一致。
    /// back=true 时按 Alt+Shift+Tab（往回切）。
    /// </summary>
    public static void TaskSwitch(bool back = false)
    {
        if (back) SendKey(VK_SHIFT, up: false);
        SendKey(VK_MENU, up: false);        // Alt
        SendKey(VK_TAB, up: false);
        SendKey(VK_TAB, up: true);
        SendKey(VK_MENU, up: true);
        if (back) SendKey(VK_SHIFT, up: true);
    }

    private static void SendWheel(uint flag, int delta)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = flag }
            }
        };
        SendInputChecked(inputs, $"滚轮(flags=0x{flag:X4})");
    }

    private static void SendKey(int vk, bool up)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? KEYEVENTF_KEYUP : 0u }
            }
        };
        SendInputChecked(inputs, $"按键(vk=0x{vk:X2})");
    }

    private static void TapKey(int vk)
    {
        SendKey(vk, up: false);
        SendKey(vk, up: true);
    }

    /// <summary>切换模式 / 断线时调用，避免左键卡在按下状态。</summary>
    public static void ReleaseAll() => Up();

    private static int Norm(int v, int origin, int size)
    {
        if (size <= 0) size = 1;
        double f = (double)(v - origin) / size;
        f = Math.Clamp(f, 0.0, 1.0);
        return (int)Math.Round(f * 65535.0);
    }

    /// <summary>
    /// 统一的注入出口。
    ///
    /// **必须检查返回值**：前台窗口属于更高完整性级别的进程时（如以管理员身份运行的
    /// WorkBuddy），UIPI 会让 SendInput 直接返回 0 并置 ERROR_ACCESS_DENIED，
    /// 系统既不报错也不动光标。以前这里把返回值丢了，于是「指针冻住」只能靠猜。
    /// </summary>
    private static void SendInputChecked(INPUT[] inputs, string where)
    {
        uint n = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (n == 0) InputGuard.NoteSendFailed(where, Marshal.GetLastWin32Error());
        else InputGuard.NoteOk();
    }

    private static void Send(uint flags, int dx = 0, int dy = 0)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags }
            }
        };
        SendInputChecked(inputs, $"鼠标事件(flags=0x{flags:X4})");
    }
}
