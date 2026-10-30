using System;
using System.Runtime.InteropServices;

namespace DrawTouchHost;

/// <summary>
/// 一支笔在某一时刻的采样。坐标已是映射到虚拟屏幕后的像素值（px/py）。
/// 压感为 0..1，倾斜为角度 -90..90。
/// </summary>
public enum PenState { Down, Move, Up, Hover }

public readonly struct PenSample
{
    public PenSample(int px, int py, float pressure, float tiltX, float tiltY, float rotation,
                     bool barrel, bool eraser, PenState state)
    {
        Px = px; Py = py; Pressure = pressure; TiltX = tiltX; TiltY = tiltY;
        Rotation = rotation; Barrel = barrel; Eraser = eraser; State = state;
    }
    public int Px { get; }
    public int Py { get; }
    public float Pressure { get; }
    public float TiltX { get; }
    public float TiltY { get; }
    public float Rotation { get; }
    public bool Barrel { get; }
    public bool Eraser { get; }
    public PenState State { get; }
}

public interface IInjector : IDisposable
{
    /// <summary>true 表示注入设备已就绪。</summary>
    bool Initialize();
    void Inject(in PenSample s);
}

/// <summary>
/// 通过 Win32 合成笔注入（CreateSyntheticPointerDevice + InjectSyntheticPointerInput）
/// 在 Windows 上产生真正带压感/倾斜的 PT_PEN 指针，可被 Photoshop / Krita / Clip Studio 等识别。
/// 结构体字段偏移经官方文档逐字节核对，x64 下使用显式布局避免对齐错误。
/// </summary>
internal sealed class PenInjector : IInjector
{
    // POINTER_INPUT_TYPE
    private const uint PT_PEN = 3;

    // POINTER_FLAGS
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_FIRSTBUTTON = 0x00000010;
    private const uint POINTER_FLAG_PRIMARY = 0x00002000;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    // POINTER_FEEDBACK_MODE
    private const int POINTER_FEEDBACK_INDIRECT = 2;

    // PEN_FLAGS
    private const uint PEN_FLAG_BARREL = 0x00000001;
    private const uint PEN_FLAG_INVERTED = 0x00000002;
    private const uint PEN_FLAG_ERASER = 0x00000004;

    // PEN_MASK
    private const uint PEN_MASK_PRESSURE = 0x00000001;
    private const uint PEN_MASK_ROTATION = 0x00000002;
    private const uint PEN_MASK_TILT_X = 0x00000004;
    private const uint PEN_MASK_TILT_Y = 0x00000008;

    // POINTER_INFO：实测 sizeof = 96（x64，对齐 8；字段排到 92 后补 4 字节尾部填充）。
    // 注意：写成 92 会让后面所有 pen 字段整体错位 4 字节，Windows 会直接拒绝注入。
    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct POINTER_INFO
    {
        [FieldOffset(0)] public uint pointerType;
        [FieldOffset(4)] public uint pointerId;
        [FieldOffset(8)] public uint frameId;
        [FieldOffset(12)] public uint pointerFlags;
        [FieldOffset(16)] public IntPtr sourceDevice;
        [FieldOffset(24)] public IntPtr hwndTarget;
        [FieldOffset(32)] public int pixelX;
        [FieldOffset(36)] public int pixelY;
        [FieldOffset(40)] public int himetricX;
        [FieldOffset(44)] public int himetricY;
        [FieldOffset(48)] public int pixelRawX;
        [FieldOffset(52)] public int pixelRawY;
        [FieldOffset(56)] public int himetricRawX;
        [FieldOffset(60)] public int himetricRawY;
        [FieldOffset(64)] public uint dwTime;
        [FieldOffset(68)] public uint historyCount;
        [FieldOffset(72)] public int inputData;
        [FieldOffset(76)] public uint dwKeyState;
        [FieldOffset(80)] public ulong performanceCount;
        [FieldOffset(88)] public uint buttonChangeType;
    }

    // POINTER_PEN_INFO：sizeof = 96(POINTER_INFO) + 24 = 120
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct POINTER_PEN_INFO
    {
        [FieldOffset(0)] public POINTER_INFO pointerInfo;
        [FieldOffset(96)] public uint penFlags;
        [FieldOffset(100)] public uint penMask;
        [FieldOffset(104)] public uint pressure;
        [FieldOffset(108)] public uint rotation;
        [FieldOffset(112)] public int tiltX;
        [FieldOffset(116)] public int tiltY;
    }

    // union 含 POINTER_TOUCH_INFO(140B) 按 8 对齐 => 144B，type 占 4B 后再 4B 填充 => 152B
    [StructLayout(LayoutKind.Explicit, Size = 152)]
    private struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public POINTER_PEN_INFO penInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, ref POINTER_TYPE_INFO pointerInfo, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroySyntheticPointerDevice(IntPtr device);

    // 虚拟屏幕原点（SM_XVIRTUALSCREEN / SM_YVIRTUALSCREEN）
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private IntPtr _device = IntPtr.Zero;
    private uint _frame;

    public bool Initialize()
    {
        // PT_PEN 的 maxCount 必须为 1
        _device = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_INDIRECT);
        if (_device == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"[PenInjector] CreateSyntheticPointerDevice 失败, LastError={err}");
            return false;
        }
        return true;
    }

    public void Inject(in PenSample s)
    {
        if (_device == IntPtr.Zero) return;

        var pti = new POINTER_TYPE_INFO { type = PT_PEN };
        ref var pi = ref pti.penInfo.pointerInfo;
        pi.pointerType = PT_PEN;
        pi.pointerId = 1;
        pi.frameId = ++_frame;
        pi.historyCount = 1;
        // InjectSyntheticPointerInput 的 ptPixelLocation **不是**标准虚拟屏幕坐标
        // （主屏左上角为原点），而是以整个虚拟屏幕的最左上角为原点（virtual-screen-relative）。
        // 第二屏在主屏左侧/上方时标准坐标为负，直接喂给 API 会被系统错判
        // （实测笔被钉在主屏最右边缘）。这里平移到 relative 空间；每次取最新值，
        // 显示器插拔/改布局也自动适应。单屏时 VirtualX/Y = 0，无影响。
        int relX = s.Px - GetSystemMetrics(SM_XVIRTUALSCREEN);
        int relY = s.Py - GetSystemMetrics(SM_YVIRTUALSCREEN);
        pi.pixelX = relX; pi.pixelY = relY;
        pi.pixelRawX = relX; pi.pixelRawY = relY;
        // himetric 实测被 Windows 忽略（坐标以 pixelX/pixelY 为准），这里按 96 DPI 惯例填上保持一致
        pi.himetricX = (int)(relX * 26.4583); pi.himetricY = (int)(relY * 26.4583);
        pi.himetricRawX = pi.himetricX; pi.himetricRawY = pi.himetricY;

        uint pf = s.State switch
        {
            PenState.Down => POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON | POINTER_FLAG_PRIMARY,
            PenState.Up => POINTER_FLAG_UP | POINTER_FLAG_PRIMARY,
            PenState.Hover => POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_PRIMARY,
            _ => POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON | POINTER_FLAG_PRIMARY,
        };
        pi.pointerFlags = pf;

        uint penFlags = 0;
        if (s.Eraser) penFlags |= PEN_FLAG_INVERTED | PEN_FLAG_ERASER;
        if (s.Barrel) penFlags |= PEN_FLAG_BARREL;
        pti.penInfo.penFlags = penFlags;
        pti.penInfo.penMask = PEN_MASK_PRESSURE | PEN_MASK_ROTATION | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;
        pti.penInfo.pressure = (uint)Math.Clamp((int)(s.Pressure * 1024), 0, 1024);
        pti.penInfo.rotation = (uint)Math.Clamp((int)s.Rotation, 0, 359);
        pti.penInfo.tiltX = (int)Math.Clamp(s.TiltX, -90, 90);
        pti.penInfo.tiltY = (int)Math.Clamp(s.TiltY, -90, 90);

        if (!InjectSyntheticPointerInput(_device, ref pti, 1))
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_NOT_READY(21) 是正常节流，可忽略
            if (err != 21)
                Console.Error.WriteLine($"[PenInjector] 注入失败 LastError={err}");
        }
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            DestroySyntheticPointerDevice(_device);
            _device = IntPtr.Zero;
        }
    }
}

/// <summary>
/// 回退模式：用 SendInput 模拟鼠标（无压感）。保证任何 Windows 版本都能把手机当“画笔定位器”用。
/// 坐标按虚拟桌面归一化到 0..65535。
/// </summary>
internal sealed class MouseInjector : IInjector
{
    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    private readonly int _vx, _vy, _vw, _vh;
    private bool _down;

    public MouseInjector(int vx, int vy, int vw, int vh)
    {
        _vx = vx; _vy = vy; _vw = vw; _vh = vh;
    }

    public bool Initialize() => true;

    public void Inject(in PenSample s)
    {
        int dx = (int)(((s.Px - _vx) * 65535.0) / _vw);
        int dy = (int)(((s.Py - _vy) * 65535.0) / _vh);
        dx = Math.Clamp(dx, 0, 65535);
        dy = Math.Clamp(dy, 0, 65535);

        if (s.State == PenState.Down && !_down)
        {
            Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESKTOP, dx, dy);
            Send(MOUSEEVENTF_LEFTDOWN, dx, dy);
            _down = true;
        }
        else if (s.State == PenState.Up && _down)
        {
            Send(MOUSEEVENTF_LEFTUP, dx, dy);
            _down = false;
        }
        else if (s.State == PenState.Move || s.State == PenState.Hover)
        {
            Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESKTOP, dx, dy);
        }
    }

    private static void Send(uint flags, int dx, int dy)
    {
        var inputs = new[] { new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } } };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose() { }
}
