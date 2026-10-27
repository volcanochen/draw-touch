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

    /// <summary>把归一化坐标(0..1)换算成虚拟屏幕像素并绝对定位光标。</summary>
    public static void MoveTo(MappingConfig cfg, int px, int py)
    {
        int ax = Norm(px, cfg.VirtualX, cfg.VirtualW);
        int ay = Norm(py, cfg.VirtualY, cfg.VirtualH);
        Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESKTOP, ax, ay);
    }

    /// <summary>相对当前光标位置平移（像素）。超出虚拟屏幕会被夹住。</summary>
    public static void MoveBy(MappingConfig cfg, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        if (!GetCursorPos(out var p)) return;
        int nx = Math.Clamp(p.X + dx, cfg.VirtualX, cfg.VirtualX + cfg.VirtualW - 1);
        int ny = Math.Clamp(p.Y + dy, cfg.VirtualY, cfg.VirtualY + cfg.VirtualH - 1);
        MoveTo(cfg, nx, ny);
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

    /// <summary>中键点击（浏览器里=新标签页打开链接；绘图软件里常用来平移画布）。</summary>
    public static void MiddleClick()
    {
        Send(MOUSEEVENTF_MIDDLEDOWN);
        Send(MOUSEEVENTF_MIDDLEUP);
    }

    public static void Back() => TapKey(VK_BROWSER_BACK);

    public static void Forward() => TapKey(VK_BROWSER_FORWARD);

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
