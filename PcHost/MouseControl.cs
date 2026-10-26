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
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESKTOP = 0x4000;

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
    private struct INPUT
    {
        public int type;
        public MOUSEINPUT mi;
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
