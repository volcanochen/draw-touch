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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
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
    /// 准备一次捏合注入的目标点。
    ///
    /// 【坐标系规则 —— 2026-10-30 实测钉死，别再改】
    /// 合成触摸（`InjectSyntheticPointerInput`）的 `ptPixelLocation` **不是**虚拟桌面坐标，
    /// 而是**相对虚拟桌面左上角的偏移**：
    ///     全局坐标 = 注入值 + (VirtualScreen.Left, VirtualScreen.Top)
    /// 实测（本机虚拟桌面 X=-1920 Y=0）：
    ///     注入 2625 → 全局 705；注入 120 → 全局 -1800（都精确命中）。
    /// 旧代码把虚拟桌面坐标**直接**当注入值 → 副屏上注入 -1200 被解释成 -3120 → 越界 →
    /// 被 Windows 夹到主屏右沿（物理 3072）→ 光标被系统直接拖走 —— 用户报的
    /// 「指针消失、飞到右边主屏的右侧」就是这么来的。
    ///
    /// 另外实测：Windows 的「触摸提升成鼠标」会把光标拽向**右接触点**
    /// （= 捏合中心 + span），且**逐帧渐进**地拽；应用自己消费了触摸（浏览器/Office 等）
    /// 就不会发生提升、光标纹丝不动。所以注入之后必须逐帧把光标钉回原位。
    ///
    /// 返回 false = 这一帧没法注入（调用方回退 Ctrl+滚轮）。
    /// </summary>
    private static bool TryPrepareTouchZoomTarget(out int gx, out int gy, out int lx, out int ly, out RECT mon)
    {
        gx = gy = lx = ly = 0;
        mon = default;
        if (!GetCursorPos(out var p)) return false;
        gx = p.X; gy = p.Y;
        // 注入用的是「相对虚拟桌面原点」的局部坐标
        var vs = SystemInformation.VirtualScreen;
        lx = gx - vs.Left;
        ly = gy - vs.Top;
        // 光标所在显示器：跨度上限按它的边界算（两指必须都留在这块屏上）
        var hMon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        if (!TryGetMonitorRect(hMon, out mon)) return false;
        return true;
    }

    /// <summary>
    /// 把光标钉回捏合开始时的位置。
    ///
    /// 触摸注入会被 Windows 提升成鼠标，光标因此可能被系统挪走（甚至飞到别的屏）。
    /// 看护窗口（手势进行中 + 尾巴期）内**无条件**钉回 —— 不靠「猜这偏移是不是系统干的」：
    ///
    /// · 手势进行中：用户的手指正在手机上做捏合，PC 上的光标只归我们管，必须钉死。
    /// · 尾巴期：仍无条件钉回。代价是抬手后约 <see cref="TZ_GUARD_TAIL_MS"/> 毫秒内
    ///   光标暂时不能由用户移动；但相比「光标飞走 / 消失」这完全可接受，
    ///   且测试会在尾巴期结束后再验证光标的「可移动性」（见 tests/pinch_cursor_check.py）。
    /// </summary>
    private static void PinCursorToSaved(int saveX, int saveY)
    {
        if (!GetCursorPos(out var p)) return;
        int dx = p.X - saveX, dy = p.Y - saveY;
        if (dx == 0 && dy == 0) return;
        // 平移期间记录「系统把光标拽走的最大幅度」：指针会不会乱跑，就靠这个指标量化
        if (_tpActive)
        {
            int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (d > _tpMaxDev) _tpMaxDev = d;
        }
        SetCursorPos(saveX, saveY);
        long now = Environment.TickCount64;
        if (now - _tzPinLogMs >= 1000)      // 限频：看护定时器每 ~16ms 跑一次，别刷爆日志
        {
            _tzPinLogMs = now;
            AppLog.Info($"[TouchZoom] 光标被系统触摸提升挪动 ({p.X},{p.Y}) → 钉回 ({saveX},{saveY})");
        }
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
    ///
    /// atSaved=false（抬手后的尾巴期）：用户可能已经挪过光标了，恢复可见时
    /// **只能挪回「当前所在的位置」**，不能拽回捏合开始时的旧位置 —— 否则又变成抢鼠标。
    /// </summary>
    private static void EnsureCursorVisible(int keepX, int keepY, bool atSaved = true, string tag = "TouchZoom")
    {
        if (CursorIsVisible()) return;
        // 实测（2026-10-30）：只有**第一帧 DOWN** 会把光标藏起来，
        // 之后的 MOVE/keep-alive/UP 帧都不会再藏 → 戳一次就够，不用每帧都戳。
        int cx = keepX, cy = keepY;
        if (!atSaved && GetCursorPos(out var cur)) { cx = cur.X; cy = cur.Y; }
        SetCursorPos(cx, cy);
        if (CursorIsVisible()) return;
        Send(MOUSEEVENTF_MOVE, 1, 0);     // 1 像素相对移动 → 系统重新显示指针
        SetCursorPos(cx, cy);              // 复位，不让光标真的挪走
        if (!CursorIsVisible())
        {
            _tzWasHidden = true;
            // 限频：看护每 8ms 跑一次，戳不回来时会一直刷 → 1 秒一条即可
            long now = Environment.TickCount64;
            if (now - _tzVisibleLogMs >= 1000)
            {
                _tzVisibleLogMs = now;
                AppLog.Warn($"[{tag}] 光标仍被系统隐藏（触摸行为），已尽力恢复");
            }
        }
        else if (_tzWasHidden)
        {
            // 曾经被藏起来、现在重新显示 → 打一条成功日志，便于测试核对「光标回来了」
            _tzWasHidden = false;
            AppLog.Info($"[{tag}] 光标已恢复可见");
        }
        // 【戳完必须再钉一次】
        // 那次「1 像素相对移动」是**相对**移动：系统处理它时会把光标带偏几个像素
        // （实测偏出 (-1193,894) 这样的 7px 抖动），而系统对相对移动的**位置更新是异步的**
        // → 上面那句 SetCursorPos(cx,cy) 复位可能发生在系统处理之前，等于没复位。
        // 所以这里无条件再钉一次，把「戳出来 + 系统异步带偏」一起清掉。
        // （顺序上必须放在最后：钉完之后不要再做会移动光标的操作。）
        SetCursorPos(cx, cy);
    }

    /// <summary>
    /// 捏合期间的「光标看护」定时器：每 16ms 把被系统挪走/藏起来的光标拉回来。
    ///
    /// 【为什么必须是定时器，不能「注入完立刻处理」】
    /// 合成触摸是**异步**被系统处理的：我们注入完马上 SetCursorPos，系统随后才处理这一帧，
    /// 然后把光标「提升」到右接触点、并把光标藏起来 → 我们那次纠正被系统覆盖掉。
    /// 定时器保证在「系统处理完之后」再纠正一次，光标才真的钉得住、也真的看得见。
    ///
    /// 【抬手后还要再守一会儿】
    /// 抬手那一刻系统才做「鼠标提升」，把光标拽到**右接触点**（= 捏合中心 + span）
    /// ——这一步发生在我们发完 UP 帧**之后**，所以抬手后还得继续守
    /// <see cref="TZ_GUARD_TAIL_MS"/> 毫秒，才能把光标拉回原位。
    /// </summary>
    private static void TouchZoomGuard()
    {
        try
        {
            long now = Environment.TickCount64;
            if (now > _tzGuardUntilMs)
            {
                _tzSaved = false;
                if (_tpActive) EndTouchPan();   // 平移尾巴期到点：兜底抬手，防接触点残留
                try { _tzGuardTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                return;
            }
            if (!_tzSaved && !_tpActive) return;
            // 【顺序很重要：先恢复可见性，再钉回位置】
            // 恢复可见性要用「1 像素相对移动」去戳醒系统 —— 这个相对移动**会把光标带偏**
            // （实测偏出 (-1193,894) 这样的 7px 抖动）。所以必须**戳完再钉**，
            // 顺序反了就会留下一个「戳出来」的偏移（用户看到光标在原地抖一下）。
            // EnsureCursorVisible 内部末尾已再 SetCursorPos 一次兜底，但保险起见仍「先戳后钉」。
            // 【此看护只服务捏合缩放】平移（pan）抬手后零看护——见 EndTouchPan 的 v7 注释。
            if (_tzSaved)
            {
                EnsureCursorVisible(_tzSaveX, _tzSaveY, atSaved: _tzActive);
                // 看护窗口（手势 + 尾巴期）内**无条件**把光标钉回捏合开始时的位置。
                // 不靠「猜这偏移是不是系统干的」——那样会误放过大偏移（实测出现 263/94px 颤抖）。
                PinCursorToSaved(_tzSaveX, _tzSaveY);
            }
            // 【平移期间的位置由 PinCursorDuringPan 负责】（每帧 + 8ms 节拍各钉一次）
        }
        catch { }
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
    private static long _tzPinLogMs;      // 上次「光标被挪走」日志的时刻（限频用）
    private static long _tzVisibleLogMs;  // 上次「光标仍被隐藏」日志的时刻（限频用）
    private static bool _tzWasHidden;     // 光标曾被系统隐藏、尚未恢复可见（用于打「已恢复」日志）
    private static long _tzLastMs;       // 上一帧（真实或 keep-alive）注入时刻
    private static long _tzLastRealMs;   // 上一「真实事件」时刻（手机缩放帧/手势开始）
    private static readonly System.Threading.Timer? _tzTickTimer =
        new System.Threading.Timer(_ => TouchZoomTick(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    /// <summary>光标看护定时器（16ms）：把系统挪走/藏起来的光标拉回来。</summary>
    private static readonly System.Threading.Timer? _tzGuardTimer =
        new System.Threading.Timer(_ => TouchZoomGuard(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    /// <summary>光标看护的截止时刻（毫秒时间戳）。</summary>
    private static long _tzGuardUntilMs;
    /// <summary>抬手后还要继续守多久（ms）—— 系统的「鼠标提升」是抬手之后才异步发生的。</summary>
    private const long TZ_GUARD_TAIL_MS = 700;
    /// <summary>单轮手势期间看护的上限（ms），防止异常情况下看护一直跑。</summary>
    private const long TZ_GUARD_MAX_MS = 600000;

    /// <summary>系统是否支持触摸注入（用于决定能否走真实触摸板缩放）。</summary>
    public static bool TouchZoomAvailable()
    {
        EnsureTouchInit();
        return _touchReady;
    }

    /// <summary>系统是否支持触摸注入（双指平移与捏合缩放共用同一套合成指针设备）。</summary>
    public static bool TouchPanAvailable()
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
        // 抬手之后系统才会做「鼠标提升」（把光标拽到右接触点），而且是**异步**的 →
        // 看护不能马上停，要再守一会儿把光标拉回原位、把藏起来的光标叫回来。
        _tzGuardUntilMs = Environment.TickCount64 + TZ_GUARD_TAIL_MS;
        try { _tzGuardTimer?.Change(8, 8); } catch { }
        AppLog.Info($"[TouchZoom] 捏合结束（抬起接触点，应用定稿刷新）最终跨度±{_tzSpan:0.0}");
    }

    // ================= 真实「触摸板平移」：模拟双指平移（触摸注入） =================
    // 与捏合缩放同源：把两个触摸接触点喂给系统、让它们**一起平移**，
    // 系统再像真实触摸板那样把它翻译成前台应用的**原生平移手势**
    // （Office / 浏览器 / 图片查看器 / PDF / 地图 等都原生支持，各方向一致，
    // 手感与真实触摸板相同）。
    //
    // 为什么用触摸注入而不是滚轮：原生手势各方向都支持，且作用于**触点下的那个窗口**
    // （滚轮发往哪个窗口取决于光标位置，副屏上容易发错）。
    // 注：早前代码里写过「OneNote 忽略横向滚轮 WM_MOUSEHWHEEL」——那是**未经证实的猜测**，
    // 已被受控探针 tests/hwheel_probe.py 证伪（横向滚轮正常送达、方向同号）。滚轮路径仍然
    // 保留为一等公民（PanMode=Wheel）与兜底方案。
    //
    // 若系统不支持触摸注入（很老的系统 / 注入失败），调用方（TcpPenServer）应回退到滚轮。

    // ---------- 真实鼠标动作探测（只读，仅用于「要不要点亮光标」） ----------
    //
    // 【为什么要探测】注入触摸期间 Windows 会隐藏光标，要让它重新显示只有一招：
    // 「1 像素相对移动 + 复位」。但用户此刻可能正在用物理鼠标 —— 这一戳会和他的移动抢光标。
    // 所以必须先知道「用户有没有在动鼠标」。
    //
    // 【为什么用低级鼠标钩子，而不是比较光标位置】实测数据不支持「位置变了=用户动了」：
    //   · 光标停在桌面上（没有窗口接收手势）注入 120 帧平移 → 偏移 0px；
    //   · 光标在真实应用窗口上做同样的注入 → 出现过 19~39px 偏移（系统侧副作用）。
    //   两种都会「位置变了」，靠位置分不清来源。
    //   低级钩子能读到 MSLLHOOKSTRUCT.flags 的 LLMHF_INJECTED 位：物理鼠标为 0、
    //   所有注入（含我们自己的 SendInput）为 1 —— 这是系统给出的、不需要猜的判据。
    private const int WH_MOUSE_LL = 14;
    private const uint LLMHF_INJECTED = 0x00000001;
    /// <summary>我们自己的注入打这个标记（dwExtraInfo），钩子里据此把自己排除。</summary>
    private static readonly IntPtr InjectionTag = (IntPtr)0x53505442;   // 'SPTB'
    /// <summary>测试脚本专用的注入标记（tests/pan_mouse_together.py 用 'TEST'）。</summary>
    private static readonly IntPtr TestTag = (IntPtr)0x54455354;        // 'TEST'
    private const int PAN_MOUSE_ACTIVE_MS = 250;   // 用户最后一次动鼠标后多久内算「正在用鼠标」
    private const int WM_MOUSEMOVE = 0x0200;
    /// <summary>
    /// 单次物理鼠标事件位移超过这个量就不记账 —— 只挡「别的程序/系统把光标瞬移」这种异常，
    /// **不能**拿小阈值去挡快速甩鼠标：系统加速度下 8 米奇就能走 70px，一次快速甩动过千像素很正常。
    /// （系统触摸提升产生的事件带 INJECTED 标志，压根进不到这段记账逻辑，不需要靠阈值区分。）
    /// </summary>
    private const int PAN_MOUSE_MAX_JUMP = 1500;

    private static long _lastRealMouseMs;
    private static IntPtr _mouseHook;
    private static Thread? _hookThread;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    // 必须保留静态引用：委托被 GC 回收后系统回调会踩到野指针
    private static readonly LowLevelMouseProc _mouseProc = MouseHookProc;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // 【判据】「用户动作」只认**物理鼠标**（LLMHF_INJECTED == 0）。
                //
                // 被证伪的两个宽松判据（2026-11-01 日志实测，别再用）：
                //   · 「位置变了就算用户动了」→ 触摸提升自己就会改位置；
                //   · 「dwExtraInfo != 0 就是外部注入 = 用户」→ 触摸提升的合成事件带
                //     `flags=0x1(INJECTED) extra=0xFF5157F2`，会被误判成用户在动鼠标。
                //     后果实测：一轮平移 32 次把锚点跟到触点 → 指针跟着手指跑 109px。
                //
                // 唯一例外是测试脚本：它注入鼠标并打上 TestTag（0x54455354 = 'TEST'），
                // 用来验证「用户移动鼠标时宿主不干预」。产品路径完全不依赖这个标记。
                bool mine = info.dwExtraInfo == InjectionTag;
                bool test = info.dwExtraInfo == TestTag;
                bool physical = (info.flags & LLMHF_INJECTED) == 0;
                _hookLastFlags = info.flags; _hookLastExtra = info.dwExtraInfo; _hookEvtCount++;
                _hookLastPtX = info.pt.X; _hookLastPtY = info.pt.Y;
                if (!mine && (physical || test))
                {
                    _lastRealMouseMs = Environment.TickCount64;   // 用户真的动了鼠标（或点了键）
                    if (_tpActive)
                    {
                        _tpRealMouseDuringPan++;
                        // 【把用户的鼠标位移"记账"，而不是整段放过】
                        // 硬件触摸板的实测行为（2026-11-01 采集）：pan 期间光标位置 100% 由物理鼠标
                        // 事件驱动，pan 自己一次都没动过光标。要在触摸注入下复现这个效果，就必须
                        // 「以 pan 起点为基准，累加用户的鼠标位移」，再由 PinCursorDuringPan 每帧写到
                        // 这个基准上 —— 这样：① 指针不会跳到触点；② 用户的手移动多少，指针就走多少。
                        //
                        // 注意：不能拿「当下 GetCursorPos」当基准（那时系统可能已把光标提升到触点），
                        // 也不能把位移直接丢掉（那就是"pan 期间鼠标废了"）。只有钩子事件坐标可信。
                        if ((int)wParam == WM_MOUSEMOVE)
                        {
                            // 基准必须是「我们上一次写回的位置」：光标被我们每帧钉住，
                            // 两次事件之间的绝对坐标不能相减（会被钉回抹平）。
                            int dx = info.pt.X - _tpLastWrittenX;
                            int dy = info.pt.Y - _tpLastWrittenY;
                            // 超过阈值的跳变不记账（多半是系统触摸提升/别的程序挪了光标，不是手的位移）
                            if (Math.Abs(dx) <= PAN_MOUSE_MAX_JUMP && Math.Abs(dy) <= PAN_MOUSE_MAX_JUMP)
                            {
                                _tpMouseDx += dx; _tpMouseDy += dy;
                                _tpLastWrittenX += dx; _tpLastWrittenY += dy;
                            }
                            else _tpMouseJumps++;
                        }
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>装低级鼠标钩子（只装一次；钩子线程自带消息循环）。</summary>
    private static void EnsureMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        lock (typeof(MouseControl))
        {
            if (_mouseHook != IntPtr.Zero) return;
            StartHookThread();
        }
    }

    /// <summary>
    /// 重装钩子（自愈用）：钩子被系统静默摘除后（实测会发生），旧句柄还在但永远收不到事件。
    /// 旧钩子 Unhook 掉、另起一条线程装新的。旧线程随其消息循环自行消亡。
    /// </summary>
    private static void RestartMouseHook()
    {
        lock (typeof(MouseControl))
        {
            try
            {
                if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            }
            catch { }
            StartHookThread();
        }
    }

    private static void StartHookThread()
    {
        _hookThread = new Thread(() =>
        {
            try
            {
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
                if (_mouseHook == IntPtr.Zero)
                {
                    AppLog.Warn($"[TouchPan] 鼠标钩子安装失败（err={Marshal.GetLastWin32Error()}）→ " +
                                "平移期间无法判断用户是否在动鼠标，将不做光标点亮");
                    return;
                }
                AppLog.Info("[TouchPan] 真实鼠标动作探测已就绪（低级鼠标钩子）");
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                AppLog.Warn("[TouchPan] 钩子线程消息循环退出（GetMessage ≤ 0）→ 钩子不再收事件");
            }
            catch (Exception ex)
            {
                AppLog.Warn("[TouchPan] 鼠标钩子线程异常：" + ex.Message);
            }
        })
        { IsBackground = true, Name = "MouseHook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    /// <summary>
    /// 平移期间**逐帧把指针钉回「进入 pan 之前那一刻的全局位置」**（用户 2026-11-01 定稿的方案）。
    ///
    /// 规则（照用户原话，别再自作聪明）：
    ///  · 第一帧的位置 = 进入 pan 之前的光标全局位置（<see cref="_tpSaveX"/>,<see cref="_tpSaveY"/>）；
    ///  · 之后**每一帧**都把指针挪回这个位置 —— 也就是「上一帧所在的位置」恒等于它；
    ///  · 谁挪的都不例外：系统触摸提升、用户物理鼠标、我们自己，一律挪回去。
    ///
    /// 与旧实现的差别（旧实现是「只在偏差 &gt; 2px 时钉、且用户一动鼠标就整段放过」）：
    ///  · 旧实现留了一个豁口 —— 用户一动鼠标就 `return`，一个字节都不碰；
    ///    于是系统解除光标抑制、把指针显示在它当时的位置上，用户就看到「两个位置都有指针」。
    ///  · 现在没有豁口：每帧无条件 SetCursorPos 回原位（位置本来就等于原位时只是空操作）。
    ///
    /// 平移期间**不点亮光标**（不戳 MOVE）：系统藏着就让它藏着，用户看不到指针；
    /// 抬手后由尾巴期看护（EndTouchPan → _tzWakeOnly → EnsureCursorVisible）恢复可见。
    /// </summary>
    private static void PinCursorDuringPan(bool countSample = true)
    {
        long now = Environment.TickCount64;
        if (!GetCursorPos(out var cur)) return;
        LogPanDiag(now, cur);

        // 目标位置 = pan 起点 + 用户在这段时间里用物理鼠标走掉的位移。
        // （没有鼠标动作时它就恒等于 pan 起点，即「上一帧所在的位置」。）
        int tx = _tpSaveX + _tpMouseDx;
        int ty = _tpSaveY + _tpMouseDy;

        int dev = Math.Max(Math.Abs(cur.X - tx), Math.Abs(cur.Y - ty));
        if (dev > _tpMaxDev) _tpMaxDev = dev;

        // 【每帧无条件写回去】不判阈值：要的是「位置 = 基准」，不是「大致不动」。
        SetCursorPos(tx, ty);
        _tpLastWrittenX = tx; _tpLastWrittenY = ty;
        _tpPinTotal++;

        if (dev > 0)
        {
            _tpPinHits++;
            if (dev > _tpPinMaxDev) _tpPinMaxDev = dev;
            if (now - _tpPinLogMs >= 250)
            {
                _tpPinLogMs = now;
                bool reread = GetCursorPos(out var after);
                AppLog.Info($"[TouchPan] 指针被挪走 {dev}px：({cur.X},{cur.Y}) → 钉回 " +
                            $"({tx},{ty})（起点({_tpSaveX},{_tpSaveY}) + 鼠标位移({_tpMouseDx},{_tpMouseDy})）" +
                            $" 钉回后={(reread ? $"({after.X},{after.Y})" : "读取失败")} " +
                            $"可见={(CursorIsVisible() ? 1 : 0)} " +
                            $"用户动鼠标={_tpRealMouseDuringPan}次 跳变丢弃={_tpMouseJumps} 钩子事件={_hookEvtCount} " +
                            $"最近flags=0x{_hookLastFlags:X} extra=0x{_hookLastExtra.ToInt64():X} " +
                            $"距上次用户动作={now - _lastRealMouseMs}ms");
            }
        }

        if (countSample)
        {
            if (CursorIsVisible()) _tpVisibleSamples++;
            else _tpHiddenSamples++;
        }
    }

    /// <summary>8ms 节拍：平移期间在系统「异步提升光标」之后补一次钉回（帧率不够时靠它兜住）。</summary>
    private static void PanPinTick()
    {
        try
        {
            if (!_tpActive)
            {
                try { _tpPinTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                return;
            }
            PinCursorDuringPan(countSample: false);
            try { _tpPinTimer?.Change(PAN_PIN_PERIOD_MS, Timeout.Infinite); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// 抬手后的「让路式」覆盖节拍（8ms，见 EndTouchPan 的 v8 注释）。
    ///
    /// 让路判据有**两条**（2026-11-03 教训：钩子可能整个失效——实测装上后连注入事件都收不到，
    /// 只靠钩子判「用户动鼠标」时，钩子一死就变成 8ms 硬拉回 = 指针在原地打转好几秒）：
    ///  ① 钩子判据（活着时最准）：抬手后钩子报过物理鼠标事件 → 立即永久停手；
    ///  ② 位置判据（不依赖钩子）：读光标，**离触点远 = 有人在拖**（系统提升渲染只会把
    ///     光标画在触点附近）→ 停手；只在「触点附近」才盖回定稿（提升渲染的特征位置）。
    /// </summary>
    private static void PanLiftTick()
    {
        try
        {
            long now = Environment.TickCount64;
            bool userMoved = _lastRealMouseMs >= _tpLiftStartMs;   // ① 钩子判据：抬手后动过物理鼠标
            if (userMoved || now - _tpLiftStartMs >= PAN_LIFT_MAX_MS)
            {
                try { _tpLiftTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                if (userMoved)
                    AppLog.Info($"[TouchPan] 抬手收尾：用户已接管（钩子报告鼠标动作），停止覆盖（写了 {_tpLiftWrites} 次）");
                else
                    AppLog.Info($"[TouchPan] 抬手收尾：超时停止覆盖（写了 {_tpLiftWrites} 次）");
                return;
            }
            if (GetCursorPos(out var p))
            {
                // ② 位置判据（盲盖期之后才启用）：离触点远 = 用户在拖 → 立刻让路
                bool blindPhase = now - _tpLiftStartMs < PAN_LIFT_BLIND_MS;
                if (!blindPhase)
                {
                    bool nearTouch = Math.Max(Math.Abs(p.X - _tpTouchGX), Math.Abs(p.Y - _tpTouchGY)) <= _tpSpan * 2 + 60;
                    if (!nearTouch)
                    {
                        try { _tpLiftTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                        AppLog.Info($"[TouchPan] 抬手收尾：光标({p.X},{p.Y}) 已离开触点({_tpTouchGX},{_tpTouchGY}) → 判定用户接管，停止覆盖（写了 {_tpLiftWrites} 次）");
                        return;
                    }
                }
                int ddx = p.X - _tpFinalX, ddy = p.Y - _tpFinalY;
                if (ddx != 0 || ddy != 0)
                {
                    SetCursorPos(_tpFinalX, _tpFinalY);   // 盖住系统的提升渲染
                    _tpLiftWrites++;
                }
                else if (CursorIsVisible())
                {
                    // 位置正确且可见：系统已自己恢复，收工
                    try { _tpLiftTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                    AppLog.Info($"[TouchPan] 抬手收尾：指针已稳定在定稿位置，停止覆盖（写了 {_tpLiftWrites} 次）");
                    return;
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 平移期间的**无条件**逐帧取证日志（150ms 一行）。
    ///
    /// 上一轮的教训（代价很大）：只在「偏差 &gt; 2px」时才打日志，结果真正「跟手」的那一轮
    /// 一个字节都没留下 —— 锚点被触摸提升事件搬到了触点，偏差恒等于 0，日志条件永远不成立，
    /// 只能靠事后猜。现在把每帧的真实状态全记下来：锚点、光标、触点、钩子事件、原始输入计数。
    /// </summary>
    private static void LogPanDiag(long now, POINT cur)
    {
        if (now - _diagLogMs < 150) return;
        _diagLogMs = now;
        int vsLeft = GetSystemMetrics(76), vsTop = GetSystemMetrics(77);
        int touchGX = _tpCx + vsLeft, touchGY = _tpCy + vsTop;
        _tpTouchGX = touchGX; _tpTouchGY = touchGY;   // 供抬手后 PanLiftTick 判「提升渲染」用
        int devAnchor = Math.Max(Math.Abs(cur.X - _tpSaveX), Math.Abs(cur.Y - _tpSaveY));
        int hookToTouch = Math.Max(Math.Abs(_hookLastPtX - touchGX), Math.Abs(_hookLastPtY - touchGY));
        AppLog.Info($"[TouchPan][诊断] 光标({cur.X},{cur.Y}) 锚点({_tpSaveX},{_tpSaveY}) 偏差={devAnchor} " +
                    $"可见={(CursorIsVisible() ? 1 : 0)} 触点({touchGX},{touchGY}) " +
                    $"钩子={_hookEvtCount}次 最近={(_hookEvtCount > 0 ? $"({_hookLastPtX},{_hookLastPtY}) f=0x{_hookLastFlags:X} e=0x{_hookLastExtra.ToInt64():X} 距触点={hookToTouch}px" : "无")} " +
                    $"原始输入={RawMouseWatcher.Total}次(真实设备{RawMouseWatcher.DevNonZero}/注入{RawMouseWatcher.DevZero}) " +
                    $"距上次raw={(_hookEvtCount > 0 || RawMouseWatcher.Total > 0 ? now - RawMouseWatcher.LastEventMs : -1)}ms");
    }

    private const int TP_BASE_SPAN = 60;     // 平移用的固定半跨度（不需要像捏合那样张合）
    private static bool _tpActive;
    private static int _tpCx, _tpCy;        // 平移中心（局部坐标，整轮手势随手指平移）
    private static double _tpSpan;
    private static int _tpSaveX, _tpSaveY;  // 【pan 期间指针的固定位置】= 进入 pan 前那一刻的光标全局坐标
    private static double _tpRemX, _tpRemY; // 亚像素残量（慢速平移也要 1:1，不丢位移）
    private static int _tpLastX, _tpLastY;
    private static long _tpLastMs, _tpLastRealMs;
    private static double _tpSpreadX = 0, _tpSpreadY = 1; // 两指排布方向（单位向量）：默认竖直排布
    private static int _tpMaxDev;                        // 平移期间光标离开固定位置的最大幅度(px)
    // ---- 诊断用：钩子最近一次事件的原始特征（判断「触摸提升会不会伪装成物理鼠标事件」）----
    private static uint _hookLastFlags;
    private static IntPtr _hookLastExtra;
    private static int _hookLastPtX, _hookLastPtY;
    private static long _hookEvtCount;
    private static long _diagLogMs;             // 逐帧诊断日志限流
    private static int _tpStartX, _tpStartY;   // 本轮平移的原始起点（= 固定位置，诊断用）
    private static long _tpPinTotal;            // 平移期间「执行钉回」的总次数（每帧 1 次 + 8ms 节拍各 1 次）
    private static long _tpPinHits;             // 其中「指针确实被挪走了」的次数
    private static int _tpPinMaxDev;            // 单次被挪走的最大幅度(px)
    private static long _tpPinLogMs;            // 「钉回」日志限流
    private static int _tpRealMouseDuringPan;   // 平移期间检测到的真实鼠标动作次数
    // ---- 「以 pan 起点为基准累加物理鼠标位移」——复刻硬件触摸板的行为 ----
    private static int _tpMouseDx, _tpMouseDy;  // 平移期间用户用物理鼠标走掉的净位移
    private static int _tpLastWrittenX, _tpLastWrittenY;  // 我们最后一次写回光标的位置（记账基准）
    private static int _tpFinalX, _tpFinalY;    // 抬手定稿位置（= 起点 + 鼠标位移），抬手时一次性写回
    // ---- 抬手后的「让路式」覆盖（见 EndTouchPan 的 v8 注释）----
    // 8ms 与 pan 期间同节拍：实测（2026-11-03）抬手后位置恒在定稿（写了 0 次=没偏离过），
    // 但可见性抑制能持续 3s+；用户看到的「闪一下」= 抬手瞬间系统最后一次提升渲染
    // 与下一拍覆盖之间的空窗 → 节拍越密空窗越小。
    private const int PAN_LIFT_PERIOD_MS = 8;    // 覆盖节拍
    private const long PAN_LIFT_MAX_MS = 1500;   // 硬超时：系统抬手提升不会比这更久（打转的最坏时长）
    /// <summary>盲盖期：抬手后头 250ms 提升渲染必然活跃，无条件盖（人手从 pan 切到拖鼠标
    /// 需要的反应时间大于它；位置判据在这之后才启用，防止大 pan 时定稿离触点远、
    /// 第一拍就误让路、提升随后画出来没人盖 → 「闪一下」回归）。</summary>
    private const long PAN_LIFT_BLIND_MS = 250;
    private static long _tpLiftStartMs;          // 抬手时刻（区分「抬手后」的物理鼠标事件）
    private static int _tpLiftWrites;            // 诊断：抬手后实际写回次数
    /// <summary>本轮 pan 的触点全局坐标（LogPanDiag 每帧刷新；抬手后判「提升渲染」用）。</summary>
    private static int _tpTouchGX, _tpTouchGY;
    private static readonly System.Threading.Timer? _tpLiftTimer =
        new System.Threading.Timer(_ => PanLiftTick(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    private static int _tpMouseJumps;           // 被判成「跳变、不记账」的次数
    private static long _hookCheckMs;           // 钩子自愈：上次检查时刻
    private static long _hookCountAtCheck;      // 钩子自愈：上次检查时的计数
    private static int _hookRestartCount;       // 钩子自愈：本轮 pan 内重装次数（防刷屏上限）
    private static int _tpHiddenSamples;        // 平移期间「光标隐藏」的采样次数（用户要求 pan 时无指针）
    private static int _tpVisibleSamples;       // 平移期间「光标意外可见」的采样次数（越接近 0 越好）
    private const int TP_KEEPALIVE_MS = 60;
    private const int TP_SAFETY_LIFT_MS = 10000;
    private const int PAN_PIN_PERIOD_MS = 8;    // 平移期间补钉节拍（系统提升是异步的，单靠帧率会漏）
    private static readonly System.Threading.Timer? _tpTickTimer =
        new System.Threading.Timer(_ => TouchPanTick(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    private static readonly System.Threading.Timer? _tpPinTimer =
        new System.Threading.Timer(_ => PanPinTick(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    /// <summary>平移手势结束（手指离开触摸板）：抬起两个接触点，应用此刻定稿。</summary>
    private static void EndTouchPan()
    {
        if (!_tpActive) return;
        _tpActive = false;
        try { _tpPinTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        InjectOneFrame(TouchPhase.Up, _tpLastX, _tpLastY, _tpSpan, _tpSpreadX, _tpSpreadY);
        // 【v8：抬手后「让路式」覆盖】
        // v7（零看护）的问题——用户实测：pan 抬手瞬间指针被 pan 带走（跳到触点），
        // 一动鼠标又跳回 pan 原点，闪一下。机制：
        //   · pan 期间系统把光标「提升」渲染到触点，我们每 8ms 写回盖掉 → 指针稳在定稿位置；
        //   · 抬手后系统的提升渲染**仍在持续**（物理鼠标动之前不放弃）→ 我们一停手，提升就露出来
        //     → 指针显示在触点（「被 pan 带走」）；定稿 SetCursorPos 只赢了一次，赢不了持续提升；
        //   · 用户一动鼠标 → 系统放弃提升渲染 → 指针回到真实位置（= 定稿 = pan 原点）→「闪一下」。
        // v6 时代的 700ms 尾巴看护能盖住它，但无条件写回会和用户的物理鼠标打架（上一条用户反馈）。
        // ⇒ 现在：抬手后继续以 30ms 节拍把指针写回定稿位置（盖住提升），但——
        //   · 钩子发现**抬手后有过物理鼠标事件** → 立即永久停手（让路，系统自己会放弃提升）；
        //   · 指针已在定稿位置且可见 → 收工；
        //   · 硬超时 PAN_LIFT_MAX_MS → 停。
        _tpFinalX = _tpSaveX + _tpMouseDx; _tpFinalY = _tpSaveY + _tpMouseDy;
        SetCursorPos(_tpFinalX, _tpFinalY);
        _tpLiftStartMs = Environment.TickCount64;
        _tpLiftWrites = 0;
        try { _tpLiftTimer?.Change(PAN_LIFT_PERIOD_MS, PAN_LIFT_PERIOD_MS); } catch { }
        AppLog.Info($"[TouchPan] 平移结束（抬起接触点）最终中心局部({_tpLastX},{_tpLastY})；" +
                    $"指针基准=({_tpSaveX},{_tpSaveY})（= 进入 pan 之前的位置）+ 鼠标位移({_tpMouseDx},{_tpMouseDy})；" +
                    $"钉回执行 {_tpPinTotal} 次，其中指针被动过 {_tpPinHits} 次，单次最大偏离 {_tpPinMaxDev}px；" +
                    $"用户动鼠标 {_tpRealMouseDuringPan} 次（位移已累加、无跳变丢弃） 跳变丢弃={_tpMouseJumps}；" +
                    $"指针隐藏采样={_tpHiddenSamples}/{_tpHiddenSamples + _tpVisibleSamples}（可见 {_tpVisibleSamples} 次）；" +
                    $"钩子事件={_hookEvtCount} 最近flags=0x{_hookLastFlags:X} extra=0x{_hookLastExtra.ToInt64():X}");
    }

    /// <summary>平移仍活着就立刻结束（模式切换 / 断线 / 缩放开始 时调用，防接触点残留）。</summary>
    public static void EndTouchPanIfActive()
    {
        if (_tpActive) EndTouchPan();
    }

    private static void TouchPanTick()
    {
        try
        {
            if (!_tpActive) return;
            long now = Environment.TickCount64;
            // 【钩子自愈】pan 期间注入帧持续产生鼠标事件，钩子活着 `_hookEvtCount` 必然增长。
            // 实测（2026-11-03）：钩子会被系统静默摘掉（装上后连自己的注入事件都收不到），
            // 后果是「用户动鼠标」判据全瞎 → 抬手覆盖开始跟用户鼠标打架（原地打转）。
            // ⇒ 注入在继续而计数 500ms 不涨 → 判定钩子死亡 → 重装。
            if (_hookEvtCount == _hookCountAtCheck && now - _hookCheckMs >= 500 && _hookCheckMs != 0
                && _hookRestartCount < 3)
            {
                _hookCheckMs = now;
                _hookCountAtCheck = _hookEvtCount;
                _hookRestartCount++;
                AppLog.Warn($"[TouchPan] 钩子疑似失效（500ms 计数未涨，count={_hookEvtCount}）→ 重装（第 {_hookRestartCount} 次）");
                RestartMouseHook();
            }
            else if (now - _hookCheckMs >= 500 || _hookCheckMs == 0)
            {
                _hookCheckMs = now;
                _hookCountAtCheck = _hookEvtCount;
            }
            if (now - _tpLastRealMs >= TP_SAFETY_LIFT_MS) { EndTouchPan(); return; }
            if (now - _tpLastMs >= TP_KEEPALIVE_MS)
            {
                // 手停：同坐标连发 UPDATE 帧维持接触点（与捏合 keep-alive 同理）
                if (InjectOneFrame(TouchPhase.Move, _tpLastX, _tpLastY, _tpSpan, _tpSpreadX, _tpSpreadY))
                {
                    _tpLastMs = Environment.TickCount64;
                    PinCursorDuringPan();        // 手停但接触还在 → 仍逐帧钉住位置
                }
                else
                {
                    EndTouchPan();
                    return;
                }
            }
            try { _tpTickTimer?.Change(TP_KEEPALIVE_MS, System.Threading.Timeout.Infinite); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// 推进一次双指平移。sx/sy = **本帧希望接触点平移的屏幕像素量**（由调用方按
    /// 手机归一化位移 × 虚拟屏高 × 增益 算好；与 MOUSE_MOVE 同比例）。
    /// 返回 true = 已用真实触摸注入发出（调用方无需再发滚轮）；false = 系统不支持或注入失败
    /// （调用方应回退到滚轮）。
    /// </summary>
    public static bool UpdateTouchPan(double sx, double sy)
    {
        EnsureTouchInit();
        if (!_touchReady) return false;
        if (!TryPrepareTouchZoomTarget(out int gx, out int gy, out int lx, out int ly, out RECT mon))
        {
            if (_tpActive) EndTouchPan();
            return false;
        }
        long now = Environment.TickCount64;
        // 亚像素残量累积（和 MOUSE_MOVE / ScrollEmitter 同理，慢速平移也要 1:1 不丢）
        _tpRemX += sx; _tpRemY += sy;
        int mx = (int)Math.Round(_tpRemX);
        int my = (int)Math.Round(_tpRemY);
        _tpRemX -= mx; _tpRemY -= my;

        if (!_tpActive)
        {
            // 开新一轮平移：两指**竖直排布**（固定，不随方向旋转）。
            // 指针会不会动不取决于排布方向，而取决于「平移期间是否叫醒光标」——
            // 见下方光标看护：全程只静默钉住、不叫醒，横/竖/斜都与纵向一致（指针不动）。
            _tpSpreadX = 0; _tpSpreadY = 1;
            InjectSyntheticPointerInput(_synthDevice, BuildSynthFrame(0, lx, ly, TP_BASE_SPAN, _tpSpreadX, _tpSpreadY), 2);
            _tpCx = lx; _tpCy = ly; _tpSpan = TP_BASE_SPAN;
            _tpSaveX = gx; _tpSaveY = gy; _tpRemX = 0; _tpRemY = 0;
            if (!InjectOneFrame(TouchPhase.Down, lx, ly, _tpSpan, _tpSpreadX, _tpSpreadY))
            {
                return false;
            }
            _tpActive = true;
            _tpLastX = lx; _tpLastY = ly;
            AppLog.Info($"[TouchPan] 开始平移 @全局({gx},{gy}) 局部({lx},{ly}) 跨度±{_tpSpan}；" +
                        $"指针基准=({_tpSaveX},{_tpSaveY})（= 进入 pan 之前的位置；之后每帧写回「基准+鼠标位移」）");
            // 【指针位置规则（用户 2026-11-01 定稿）】
            //  第一帧的位置 = 进入 pan 之前的光标位置（_tpSaveX/_tpSaveY，来自上方 GetCursorPos）；
            //  之后每一帧都把指针挪回这个位置（PinCursorDuringPan），再叠一个 8ms 节拍补钉，
            //  因为系统「触摸提升光标」是异步的，只靠帧率会漏。
            //  代价（明确知悉）：pan 期间物理鼠标推不动指针 —— 这正是用户要的「位置恒定」。
            _tpMaxDev = 0;
            _tpStartX = gx; _tpStartY = gy; _hookEvtCount = 0;
            _hookCheckMs = 0; _hookCountAtCheck = 0; _hookRestartCount = 0;   // 自愈状态复位
            _tpPinTotal = 0; _tpPinHits = 0; _tpPinMaxDev = 0; _tpPinLogMs = 0; _tpRealMouseDuringPan = 0;
            _tpMouseDx = 0; _tpMouseDy = 0; _tpMouseJumps = 0;
            _tpLastWrittenX = _tpSaveX; _tpLastWrittenY = _tpSaveY;
            _diagLogMs = 0;     // 新一轮平移：立刻打第一行诊断
            RawMouseWatcher.EnsureStarted();   // 原始输入监听（只取证，见 RawMouseWatcher.cs）
            _tzSaved = false;   // 平移接管光标：清掉捏合尾巴期的标记，避免看护走「叫醒光标」分支
            EnsureMouseHook();  // 装钩子（只装一次）：统计「用户是否在动物理鼠标」
            _tpHiddenSamples = 0; _tpVisibleSamples = 0;   // 统计「pan 期间指针是否真的不可见」
            PinCursorDuringPan();
            try { _tpPinTimer?.Change(PAN_PIN_PERIOD_MS, Timeout.Infinite); } catch { }
        }
        else
        {
            if (mx == 0 && my == 0)
            {
                // 这一帧没攒够 1px：仅维持接触点，不平移（避免抖动）
                // 接触仍然活着 → 指针继续隐藏
                PinCursorDuringPan();
                _tpLastMs = now; _tpLastRealMs = now;
                try { _tpTickTimer?.Change(TP_KEEPALIVE_MS, System.Threading.Timeout.Infinite); } catch { }
                return true;
            }
            // 平移中心随手指移动（累积残量凑整后才平移，慢速也不丢）。
            // 两指排布方向**固定不变**（竖直排布）：平移只看两指质心位移，
            // 排布方向不影响「是不是平移」；固定排布可避免逐帧旋转带来的误判旋转手势。
            _tpCx += mx; _tpCy += my;
            if (!InjectOneFrame(TouchPhase.Move, _tpCx, _tpCy, _tpSpan, _tpSpreadX, _tpSpreadY))
            {
                _tpActive = false;
                try { _tpPinTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                try { _tzGuardTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                return false;
            }
            _tpLastX = _tpCx; _tpLastY = _tpCy;
            // 整轮平移期间每帧把指针钉回 pan 起点（见 PinCursorDuringPan）
            PinCursorDuringPan();
        }
        _tpLastMs = now; _tpLastRealMs = now;
        try { _tpTickTimer?.Change(TP_KEEPALIVE_MS, System.Threading.Timeout.Infinite); } catch { }
        return true;
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
                    // keep-alive 帧同样会触发系统的「触摸提升」副作用 → 立刻叫醒光标并钉住
                    // 顺序：先叫醒（会带偏光标），再钉回（把带偏的偏移清掉）。
                    if (_tzSaved)
                    {
                        _tzGuardUntilMs = Environment.TickCount64 + TZ_GUARD_MAX_MS;
                        EnsureCursorVisible(_tzSaveX, _tzSaveY);
                        PinCursorToSaved(_tzSaveX, _tzSaveY);
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
    ///
    /// 注：cx/cy 传不传都行 —— 这里**自己再读一次光标**，避免用调用方可能过期的坐标
    /// （捏合期间光标随时可能被系统挪动，坐标必须以当下为准）。
    /// </summary>
    public static bool UpdateTouchZoom(double factor, int cx, int cy)
    {
        EnsureTouchInit();
        if (!_touchReady) return false;

        // 准备注入目标：全局光标位置 + **局部注入坐标**（相对虚拟桌面原点）+ 光标所在显示器。
        // （坐标系规则的详细说明见 TryPrepareTouchZoomTarget 的注释——这是本 bug 的根因所在。）
        if (!TryPrepareTouchZoomTarget(out int gx, out int gy, out int lx, out int ly, out RECT mon))
        {
            if (_tzActive) EndTouchZoom();      // 手势中途失效 → 先把接触点抬干净
            return false;
        }

        long now = Environment.TickCount64;
        if (!_tzActive)
        {
            // 开新一轮捏合：在当前光标处落下两个接触点（基准跨度）。
            // 可用样例在 DOWN 前有一帧 flags=0 的「预注入」（复位设备状态），照抄，尽力而为。
            InjectSyntheticPointerInput(_synthDevice, BuildSynthFrame(0, lx, ly, TZ_BASE_SPAN), 2);
            _tzCx = lx; _tzCy = ly;               // 注入用局部坐标
            _tzSpan = TZ_BASE_SPAN;
            _tzClampLogged = false;
            // 记住捏合开始时的光标位置（**全局**坐标）：触摸被系统「提升成鼠标」时
            // 光标会被拽向右接触点，靠它把光标钉回原处（否则用户看到的是
            // 「光标消失 / 飞到别的屏」）。
            _tzSaveX = gx; _tzSaveY = gy; _tzSaved = true;
            // 跨度上限：两指水平排布，必须**都留在光标所在这块显示器上**
            // （用虚拟桌面算会算出跨到别的屏的跨度，那正是光标飞走的帮凶）。
            int spanLimit = Math.Min(gx - mon.Left, mon.Right - gx) - TZ_EDGE_MARGIN;
            _tzMaxSpan = Math.Min(TZ_MAX_SPAN, Math.Max(TZ_MIN_SPAN, spanLimit));
            if (!InjectOneFrame(TouchPhase.Down, lx, ly, _tzSpan))
            {
                _tzSaved = false;
                return false;
            }
            _tzActive = true;
            _tzLastX = lx; _tzLastY = ly;
            // 光标看护：16ms 一次，把系统「提升」掉的/藏起来的光标拉回来
            _tzGuardUntilMs = Environment.TickCount64 + TZ_GUARD_MAX_MS;
            try { _tzGuardTimer?.Change(8, 8); } catch { }
            AppLog.Info($"[TouchZoom] 开始捏合 @全局({gx},{gy}) 局部({lx},{ly}) " +
                        $"跨度±{_tzSpan:0.0} 上限{_tzMaxSpan:0.0}");
            // DOWN 帧会把光标藏起来 → 叫醒它（顺序：先叫醒后钉回，见 TouchZoomGuard 的说明）
            EnsureCursorVisible(_tzSaveX, _tzSaveY);
            PinCursorToSaved(_tzSaveX, _tzSaveY);
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
                try { _tzGuardTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                return false;
            }
            _tzLastX = _tzCx; _tzLastY = _tzCy;
            // 每帧都要把光标钉回原位并叫醒它（触摸提升会挪走/藏起光标）
            // 顺序：先叫醒（会带偏光标），再钉回（把带偏的偏移清掉）。
            _tzGuardUntilMs = now + TZ_GUARD_MAX_MS;   // 延长看护窗口
            EnsureCursorVisible(_tzSaveX, _tzSaveY);
            PinCursorToSaved(_tzSaveX, _tzSaveY);
        }
        _tzLastMs = now;
        _tzLastRealMs = now;
        // 下一拍：手停由 TouchZoomTick 注入同坐标 keep-alive 帧（<100ms 红线内）；
        // 正常结束由 MOUSE_FINGER off 触发
        try { _tzTickTimer?.Change(TZ_KEEPALIVE_MS, System.Threading.Timeout.Infinite); } catch { }
        return true;
    }

    /// <summary>构造同帧双指（参数照抄 TouchInjectTest mode 5 实测可用组合）。flags=0 即预注入复位帧。</summary>
    private static POINTER_TYPE_INFO[] BuildSynthFrame(uint flags, int cx, int cy, double span, double sx = 1, double sy = 0)
    {
        // 两指排布方向：(sx, sy) 为单位向量，接触点落在 (cx, cy) ± (sx, sy) * span。
        // 默认 (1, 0) = 水平排布（捏合缩放用）；平移改用「垂直于运动方向」以钉住指针。
        int ox = (int)Math.Round(sx * span);
        int oy = (int)Math.Round(sy * span);
        var arr = new POINTER_TYPE_INFO[2];
        for (int i = 0; i < 2; i++)
        {
            int px = cx + (i == 0 ? -ox : ox);
            int py = cy + (i == 0 ? -oy : oy);
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
                        ptPixelLocation = new TZPoint { X = px, Y = py },
                        ptPixelLocationRaw = new TZPoint { X = px, Y = py },
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
    private static bool InjectOneFrame(TouchPhase phase, int cx, int cy, double span, double sx = 1, double sy = 0)
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
            bool ok = InjectSyntheticPointerInput(_synthDevice, BuildSynthFrame(flags, cx, cy, span, sx, sy), 2);
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
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, dwExtraInfo = InjectionTag }
            }
        };
        SendInputChecked(inputs, $"鼠标事件(flags=0x{flags:X4})");
    }
}
