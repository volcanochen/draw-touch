using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DrawTouchHost;

/// <summary>
/// 原始输入（Raw Input）鼠标监听 —— 只做**取证**，不参与任何产品判断。
///
/// 为什么要多这一层：低级鼠标钩子（WH_MOUSE_LL）分不清「物理鼠标」和「系统触摸提升」，
/// 宿主日志实测这两者在钩子里是**完全同形**的（flags=0x0 extra=0x0），
/// 结果锚点被触摸提升事件搬走 486 次 → 指针跟着手指跑。钩子这条路已证伪。
///
/// Raw Input 走的是另一条链路：HID 设备的原始报文。按文档，它应当只包含真实设备
/// （物理鼠标 hDevice != 0；SendInput 注入 hDevice == 0），触摸被系统翻译出的
/// 鼠标移动**不来自 HID 鼠标**，理应不出现。这一点必须实测，不能凭文档下结论，
/// 所以这里只统计、打日志，等真机 pan 一次的数据再决定能不能当判据。
/// </summary>
internal static class RawMouseWatcher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
        public uint time; public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags, pad, usButtonFlags, usButtonData;
        public uint ulRawButtons;
        public int lLastX, lLastY;
        public uint ulExtraInformation;
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint cb);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint cb, uint hdrSize);

    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static readonly WndProc ProcDelegate = WndProcImpl;
    private static Thread? _thread;
    private static volatile bool _started;
    private static volatile bool _ok;

    // ---- 统计（诊断用，跨线程只写不读竞争可接受）----
    public static long Total;
    public static long DevZero;          // hDevice == 0：SendInput 之类的系统注入
    public static long DevNonZero;       // hDevice != 0：真实 HID 鼠标
    public static long LastEventMs;      // 最后一次 raw 鼠标事件时间
    public static int LastDx, LastDy;
    public static long LastDev;
    public static bool Ready => _ok;

    public static void EnsureStarted()
    {
        if (_started) return;
        lock (typeof(RawMouseWatcher))
        {
            if (_started) return;
            _started = true;
            _thread = new Thread(() =>
            {
                try
                {
                    var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ProcDelegate), lpszClassName = "DrawTouchRawMouse" };
                    RegisterClassW(ref wc);
                    IntPtr wnd = CreateWindowExW(0, "DrawTouchRawMouse", "raw", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (wnd == IntPtr.Zero)
                    {
                        AppLog.Warn($"[RawMouse] 建消息窗口失败（err={Marshal.GetLastWin32Error()}）→ 无原始输入数据");
                        return;
                    }
                    var rid = new RAWINPUTDEVICE[1];
                    rid[0].usUsagePage = 0x01; rid[0].usUsage = 0x02;   // Generic Desktop / Mouse
                    rid[0].dwFlags = RIDEV_INPUTSINK; rid[0].hwndTarget = wnd;
                    if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                    {
                        AppLog.Warn($"[RawMouse] 注册失败（err={Marshal.GetLastWin32Error()}）");
                        return;
                    }
                    _ok = true;
                    AppLog.Info("[RawMouse] 原始输入监听已就绪（只统计，不参与判断）");
                    while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[RawMouse] 线程异常：{ex.Message}");
                }
            })
            { IsBackground = true, Name = "raw-mouse" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_INPUT) { Read(l); return IntPtr.Zero; }
        return DefWindowProcW(h, m, w, l);
    }

    private static void Read(IntPtr hRaw)
    {
        uint hdrSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, hdrSize);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, hdrSize) != size) return;
            var hdr = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (hdr.dwType != 0) return;                 // 只看 RIM_TYPEMOUSE
            var ms = Marshal.PtrToStructure<RAWMOUSE>(IntPtr.Add(buf, (int)hdr.dwSize));
            Total++;
            if (hdr.hDevice == IntPtr.Zero) DevZero++; else DevNonZero++;
            LastEventMs = Environment.TickCount64;
            LastDx = ms.lLastX; LastDy = ms.lLastY; LastDev = hdr.hDevice.ToInt64();
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
