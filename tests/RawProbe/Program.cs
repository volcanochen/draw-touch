using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RawProbe;

/// <summary>
/// v2（2026-11-01）：定位「pan 期间光标跟手 / 出现第二个指针」的触发条件。
///
/// v1 已实测（不是推测）：
///   · 触摸注入到**自建不可见窗口**时，钩子 0 事件、原始输入 0 事件、光标位移 0px；
///   · 但真机上确实有跟手现象 → 说明触发条件与「触摸投递到哪个窗口」有关。
///
/// 本探针固定落点，只改 InjectSyntheticPointerInput 的 hwndTarget，四组对照：
///   A. NULL（系统自己按坐标路由）
///   B. 前台窗口（**产品代码现在的做法**）
///   C. WindowFromPoint(落点)（落点真正所在的窗口）
///   D. 自建不可见窗口（v1 的做法，作为阴性对照）
/// 每组都记录：光标位移、钩子事件数（按 flags 分类）、原始输入数、光标可见性。
/// </summary>
internal static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptPixelLocationRaw, ptHimetricLocation, ptHimetricLocationRaw;
        public uint dwTime, historyCount; public int inputData; public uint dwKeyStates;
        public ulong PerformanceCount; public uint ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags, touchMask;
        public RECT rcContact, rcContactRaw;
        public uint orientation, pressure;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TYPE_INFO_TOUCH { public uint type; public POINTER_TOUCH_INFO touch; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags, pad, usButtonFlags, usButtonData;
        public uint ulRawButtons; public int lLastX, lLastY; public uint ulExtraInformation;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint feedbackMode);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, IntPtr pointerInfo, uint count);
    [DllImport("user32.dll")] private static extern bool DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern bool DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint cb, uint hdrSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint cb);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    private const uint PT_TOUCH = 2, FB_NONE = 3;
    private const uint FLAG_INRANGE = 0x2, FLAG_INCONTACT = 0x4, FLAG_PRIMARY = 0x2000;
    private const uint FLAG_DOWN = 0x10000, FLAG_UPDATE = 0x20000, FLAG_UP = 0x40000;
    private const uint TOUCH_MASK_PRESSURE = 0x4;
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const int WH_MOUSE_LL = 14;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr PER_MONITOR_AWARE_V2 = new(-4);

    // ---- 每组统计 ----
    private sealed class Stat
    {
        public int Hook, HookNonInjected, HookInjected, Raw, RawDevNonZero, RawDevZero;
        public readonly List<string> Sample = new();
    }

    private static Stat _st = new();
    private static IntPtr _wnd;
    private static IntPtr _procPtr;
    private static readonly WndProc WndProcDelegate = WndProcImpl;
    private static readonly LowLevelMouseProc HookDelegate = HookProc;
    private static readonly StringBuilder RawSample = new();

    private static void Out(string s = "") => Console.WriteLine(s);

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            _st.Hook++;
            if ((info.flags & 1) != 0) _st.HookInjected++; else _st.HookNonInjected++;
            if (_st.Sample.Count < 4)
                _st.Sample.Add($"({info.pt.X},{info.pt.Y}) f=0x{info.flags:X} e=0x{info.dwExtraInfo.ToInt64():X}");
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_INPUT) { ReadRaw(l); return IntPtr.Zero; }
        return DefWindowProcW(h, m, w, l);
    }

    private static void ReadRaw(IntPtr hRaw)
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
            if (hdr.dwType != 0) return;
            _st.Raw++;
            if (hdr.hDevice == IntPtr.Zero) _st.RawDevZero++; else _st.RawDevNonZero++;
            if (RawSample.Length < 200)
            {
                var ms = Marshal.PtrToStructure<RAWMOUSE>(IntPtr.Add(buf, (int)hdr.dwSize));
                RawSample.Append($"dev=0x{hdr.hDevice.ToInt64():X},dx={ms.lLastX},dy={ms.lLastY} ");
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool CursorShown()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        return !GetCursorInfo(ref ci) || (ci.flags & 1) != 0;
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
            Thread.Sleep(1);
        } while (sw.ElapsedMilliseconds < ms);
    }

    private static IntPtr BuildFrame(uint flags, IntPtr hwnd, int cx, int cy, int gap)
    {
        var arr = new TYPE_INFO_TOUCH[2];
        for (int i = 0; i < 2; i++)
        {
            int py = cy + (i == 0 ? -gap : gap);
            arr[i] = new TYPE_INFO_TOUCH
            {
                type = PT_TOUCH,
                touch = new POINTER_TOUCH_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = PT_TOUCH,
                        pointerId = 1u + (uint)i,
                        pointerFlags = (i == 0 ? FLAG_PRIMARY : 0u) | flags,
                        hwndTarget = hwnd,
                        ptPixelLocation = new POINT { X = cx, Y = py },
                        ptPixelLocationRaw = new POINT { X = cx, Y = py },
                    },
                    touchMask = TOUCH_MASK_PRESSURE,
                    pressure = 1,
                },
            };
        }
        int sz = Marshal.SizeOf(typeof(TYPE_INFO_TOUCH));
        IntPtr buf = Marshal.AllocHGlobal(sz * 2);
        for (int i = 0; i < 2; i++) Marshal.StructureToPtr(arr[i], buf + i * sz, false);
        return buf;
    }

    private static bool Inject(IntPtr dev, uint flags, IntPtr hwnd, int cx, int cy, int gap)
    {
        var f = BuildFrame(flags, hwnd, cx, cy, gap);
        bool ok = InjectSyntheticPointerInput(dev, f, 2);
        Marshal.FreeHGlobal(f);
        return ok;
    }

    private static string Desc(IntPtr h)
    {
        if (h == IntPtr.Zero) return "NULL";
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out uint pid);
        try
        {
            var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return $"{sb} (pid={pid} {p.ProcessName})";
        }
        catch { return $"{sb} (pid={pid})"; }
    }

    private static void RunGroup(IntPtr dev, string name, IntPtr target, int cxLocal, int cyLocal,
        int startGX, int startGY, int vsLeft, int vsTop)
    {
        _st = new Stat();
        RawSample.Clear();
        SetCursorPos(startGX, startGY);
        Pump(120);
        GetCursorPos(out var before);
        int shown = 0, total = 0;
        Inject(dev, FLAG_DOWN | FLAG_INRANGE | FLAG_INCONTACT, target, cxLocal, cyLocal, 60);
        Pump(40);
        int curX = cxLocal;
        for (int f = 0; f < 20; f++)
        {
            curX += 6;
            Inject(dev, FLAG_UPDATE | FLAG_INRANGE | FLAG_INCONTACT, target, curX, cyLocal, 60);
            Pump(14);
            if (CursorShown()) shown++;
            total++;
        }
        Inject(dev, FLAG_UP, target, curX, cyLocal, 60);
        Pump(150);
        GetCursorPos(out var after);

        int dx = after.X - before.X, dy = after.Y - before.Y;
        Out($"[{name}] target={Desc(target)}");
        Out($"   光标 {before.X},{before.Y} → {after.X},{after.Y}  位移=({dx},{dy})  " +
            $"可见采样 {shown}/{total}");
        Out($"   钩子={_st.Hook}次（非注入同形物理 {_st.HookNonInjected} / 带INJECTED {_st.HookInjected}） " +
            $"原始输入={_st.Raw}次（真实设备 {_st.RawDevNonZero} / hDevice=0 {_st.RawDevZero}）");
        if (_st.Sample.Count > 0) Out("   钩子样本: " + string.Join(" | ", _st.Sample));
        if (RawSample.Length > 0) Out("   原始输入样本: " + RawSample);
        Out();
    }

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2);
        int vsLeft = GetSystemMetrics(76), vsTop = GetSystemMetrics(77);

        _procPtr = Marshal.GetFunctionPointerForDelegate(WndProcDelegate);
        var wc = new WNDCLASS { lpfnWndProc = _procPtr, lpszClassName = "RawProbeWnd2" };
        RegisterClassW(ref wc);
        _wnd = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, "RawProbeWnd2", "RawProbe",
            WS_POPUP, 0, 0, 8, 8, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_wnd == IntPtr.Zero) { Out("建窗口失败 err=" + Marshal.GetLastWin32Error()); return; }

        var rid = new RAWINPUTDEVICE[1];
        rid[0].usUsagePage = 0x01; rid[0].usUsage = 0x02; rid[0].dwFlags = RIDEV_INPUTSINK; rid[0].hwndTarget = _wnd;
        bool rawOk = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        IntPtr hook = SetWindowsHookEx(WH_MOUSE_LL, HookDelegate, IntPtr.Zero, 0);
        Out($"原始输入注册={rawOk}  钩子={(hook != IntPtr.Zero)}");
        Out($"虚拟屏原点=({vsLeft},{vsTop}) 前台={Desc(GetForegroundWindow())}");
        Out();

        // 落点：主屏中部（避开屏幕边缘与任务栏）
        int gx = 1400, gy = 900;
        var pt = new POINT { X = gx, Y = gy };
        IntPtr hit = WindowFromPoint(pt);
        Out($"落点 ({gx},{gy}) 处的窗口 = {Desc(hit)}");
        Out();

        var dev = CreateSyntheticPointerDevice(PT_TOUCH, 2, FB_NONE);
        if (dev == IntPtr.Zero) { Out("合成指针设备创建失败 err=" + Marshal.GetLastWin32Error()); return; }

        int cx = gx - vsLeft, cy = gy - vsTop;
        RunGroup(dev, "A NULL", IntPtr.Zero, cx, cy, gx, gy, vsLeft, vsTop);
        RunGroup(dev, "B 前台窗口", GetForegroundWindow(), cx, cy, gx, gy, vsLeft, vsTop);
        RunGroup(dev, "C 落点窗口", hit, cx, cy, gx, gy, vsLeft, vsTop);
        RunGroup(dev, "D 自建窗口", _wnd, cx, cy, gx, gy, vsLeft, vsTop);

        DestroySyntheticPointerDevice(dev);
        Out("提示：光标最后应回到落点附近；物理鼠标未被动过。");
    }
}
