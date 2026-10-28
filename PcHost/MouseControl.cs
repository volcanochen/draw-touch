using System;
using System.Runtime.InteropServices;

namespace SpenTabletHost;

/// <summary>
/// 「鼠标模式」下的输入控制。
///
/// 与数位板模式互斥：
///   数位板模式 —— 桌面显示操作范围框，S Pen 在框内作画，手指只用来拖框；
///   鼠标模式   —— 取消操作范围框，手指当触摸板（相对移动 / 单击=左键 / 双击=右键），
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    public static bool LeftDown { get; private set; }

    /// <summary>读取当前光标位置（虚拟屏幕坐标）。</summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        x = 0; y = 0;
        if (!GetCursorPos(out var p)) return false;
        x = p.X; y = p.Y;
        return true;
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

        Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESKTOP,
            Norm(tx, cfg.VirtualX, cfg.VirtualW), Norm(ty, cfg.VirtualY, cfg.VirtualH));

        if (!GetCursorPos(out var q)) return (0, 0);
        if (q.X == p.X && q.Y == p.Y)
        {
            // 一个像素都没动：绝对定位被系统拒绝（典型是 Windows 虚拟桌面）→ 相对移动兜底
            Send(MOUSEEVENTF_MOVE, dx, dy);
            return (0, 0);
        }
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

    /// <summary>Ctrl + 像素级滚轮（缩放画布）。</summary>
    public static void CtrlWheelPixels(int pixels)
    {
        if (pixels == 0) return;
        SendKey(VK_CONTROL, up: false);
        SendWheel(MOUSEEVENTF_WHEEL, pixels);
        SendKey(VK_CONTROL, up: true);
    }

    // ================= 真实「触摸板缩放」：模拟双指捏合（触摸注入） =================
    // 这是 Windows 精密触摸板的捏合缩放在系统层的等价物：把两个触摸接触点喂给系统，
    // 系统再像真实触摸板那样把它翻译成前台应用的缩放手势。浏览器 / 地图 / Office /
    // 图片查看器 / PDF 等都原生支持，手感与真实触摸板一致。
    // 若系统不支持触摸注入（很老的系统 / 注入失败），调用方应回退到 Ctrl++/-。

    private const uint POINTER_TYPE_TOUCH = 0x00000002;
    private const uint POINTER_FLAG_DOWN = 0x00000001;
    private const uint POINTER_FLAG_UPDATE = 0x00000002;
    private const uint POINTER_FLAG_UP = 0x00000004;
    private const uint POINTER_FLAG_INRANGE = 0x00000008;
    private const uint POINTER_FLAG_INCONTACT = 0x00000010;
    private const uint TOUCH_MASK_CONTACTAREA = 0x0001;
    private const uint TOUCH_FEEDBACK_NONE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct TZPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

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
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    private static bool _touchInit;
    private static bool _touchReady;
    private static bool _tzActive;
    private static uint _tzFrameId;
    private static int _tzCx, _tzCy;     // 捏合中心（屏幕像素）
    private static int _tzSpan;          // 当前半跨度（像素）
    private static long _tzLastMs;
    private static readonly System.Threading.Timer? _tzEndTimer =
        new System.Threading.Timer(_ => EndTouchZoom(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

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
            // maxCount=2：同时两个接触点；dwMode=TOUCH_FEEDBACK_NONE 关掉系统的视觉反馈
            _touchReady = InitializeTouchInjection(2, TOUCH_FEEDBACK_NONE);
            if (!_touchReady)
                AppLog.Warn($"[TouchZoom] 初始化触摸注入失败 err={Marshal.GetLastWin32Error()}（将回退 Ctrl++/-）");
        }
        catch (Exception ex)
        {
            _touchReady = false;
            AppLog.Warn($"[TouchZoom] 不支持触摸注入：{ex.Message}（将回退 Ctrl++/-）");
        }
    }

    /// <summary>捏合基准半跨度（像素）。</summary>
    private const int TZ_BASE_SPAN = 90;
    private const int TZ_MIN_SPAN = 24;
    private const int TZ_MAX_SPAN = 600;
    /// <summary>手指停止发捏合帧超过此时长(ms)就抬起接触点，结束这次捏合。</summary>
    private const int TZ_IDLE_MS = 220;

    private static void EndTouchZoom()
    {
        if (!_tzActive) return;
        _tzActive = false;
        InjectOneFrame(down: false, _tzCx, _tzCy, _tzSpan);
        AppLog.Info("[TouchZoom] 捏合结束（抬起接触点）");
    }

    /// <summary>
    /// 推进一次捏合缩放。
    /// factor&gt;1 张开（放大），&lt;1 捏拢（缩小）。以当前光标位置为捏合中心。
    /// 返回 true 表示已成功用真实触摸注入发出（调用方无需再发 Ctrl++/-）；
    /// false 表示系统不支持或注入失败（调用方应回退到 Ctrl++/-）。
    /// </summary>
    public static bool UpdateTouchZoom(double factor, int cx, int cy)
    {
        EnsureTouchInit();
        if (!_touchReady) return false;
        long now = Environment.TickCount64;
        if (!_tzActive || now - _tzLastMs > TZ_IDLE_MS)
        {
            // 开新一轮捏合：在当前光标处落下两个接触点（基准跨度）
            _tzCx = cx; _tzCy = cy;
            _tzSpan = TZ_BASE_SPAN;
            if (!InjectOneFrame(down: true, cx, cy, _tzSpan)) return false;
            _tzActive = true;
            AppLog.Info($"[TouchZoom] 开始捏合 @({cx},{cy}) 跨度±{_tzSpan}");
        }
        else
        {
            // 按相对系数缩放当前跨度（前台应用据此算出缩放量）
            int ns = (int)Math.Clamp(_tzSpan * factor, TZ_MIN_SPAN, TZ_MAX_SPAN);
            _tzSpan = ns;
            if (!InjectOneFrame(down: false, cx, cy, _tzSpan)) return false;
        }
        _tzLastMs = now;
        // 220ms 内没新帧就自动抬起（结束捏合）
        try { _tzEndTimer?.Change(TZ_IDLE_MS, System.Threading.Timeout.Infinite); } catch { }
        return true;
    }

    /// <summary>构造并发出一帧（两个接触点）：down=true 落点，false 移动/抬起。</summary>
    private static bool InjectOneFrame(bool down, int cx, int cy, int span)
    {
        try
        {
            var a = MakeContact(0, cx - span, cy, down);
            var b = MakeContact(1, cx + span, cy, down);
            var arr = new[] { a, b };
            _tzFrameId++;
            bool ok = InjectTouchInput(2, arr);
            if (!ok) AppLog.Warn($"[TouchZoom] InjectTouchInput 失败 err={Marshal.GetLastWin32Error()}");
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[TouchZoom] 注入异常：{ex.Message}");
            return false;
        }
    }

    private static POINTER_TOUCH_INFO MakeContact(uint id, int x, int y, bool down)
    {
        const int r = 3; // 接触点包围盒半径（像素）
        return new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_TYPE_TOUCH,
                pointerId = id,
                frameId = _tzFrameId,
                pointerFlags = down
                    ? (POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT)
                    : (POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT),
                sourceDevice = IntPtr.Zero,
                hwndTarget = IntPtr.Zero,
                ptPixelLocation = new TZPoint { X = x, Y = y },
                ptHimetricLocation = new TZPoint { X = 0, Y = 0 },
                ptPixelLocationRaw = new TZPoint { X = x, Y = y },
                ptHimetricLocationRaw = new TZPoint { X = 0, Y = 0 },
                dwTime = 0,
                historyCount = 0,
                inputData = 0,
                dwKeyStates = 0,
                performanceCount = 0,
                buttonChangeType = 0,
            },
            touchFlags = 0,
            touchMask = TOUCH_MASK_CONTACTAREA,
            rcContact = new RECT { left = x - r, top = y - r, right = x + r, bottom = y + r },
            rcContactRaw = new RECT { left = x - r, top = y - r, right = x + r, bottom = y + r },
            orientation = 0,
            pressure = 0,
        };
    }

    /// <summary>Ctrl + “+” （放大）。</summary>
    public static void CtrlPlus()
    {
        SendKey(VK_CONTROL, up: false);
        TapKey(0xBB); // VK_OEM_PLUS（主键盘的 +/=）
        SendKey(VK_CONTROL, up: true);
    }

    /// <summary>Ctrl + “-” （缩小）。</summary>
    public static void CtrlMinus()
    {
        SendKey(VK_CONTROL, up: false);
        TapKey(0xBD); // VK_OEM_MINUS（主键盘的 -/_）
        SendKey(VK_CONTROL, up: true);
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
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
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
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
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
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }
}
