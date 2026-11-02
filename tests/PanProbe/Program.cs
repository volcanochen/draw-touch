using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PanProbe;

/// <summary>
/// v3（2026-11-01 深夜）：v2 已经证明的两件事（均已实测，非推测）：
///   · 注入接触期间系统**不会**把光标拽到触点（10 帧偏移全 0）；
///   · 接触期间鼠标通道**完全可用**（+80 米奇 → 339px，与无接触时 338px 一致）。
/// 剩下两个问号，本轮只测这两件：
///   Q1 目标窗口真正在前台、且自己消费触摸时，光标还会不会被隐藏？（v2 里窗口没拿到前台，数据不算数）
///   Q2 一边 pan 一边**持续**移动鼠标时，光标会不会反复被隐藏？停手多久后被隐藏？
///   Q3 若会被隐藏，能否用「±1 米奇轻推」把光标点亮，且不把光标推走（漂移要可测）。
///
/// 不使用 ShowCursor（全局计数，一改就污染后续所有测量）。
/// </summary>
internal static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
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
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT { [FieldOffset(0)] public int type; [FieldOffset(8)] public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTUREINFO
    {
        public uint cbSize, dwFlags, dwID; public IntPtr hwndTarget;
        public POINTS ptsLocation; public uint dwInstanceID, dwSequenceID;
        public ulong ullArguments; public uint cbExtraArgs;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINTS { public short X, Y; }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint feedbackMode);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, IntPtr pointerInfo, uint count);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern bool DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern bool CloseGestureInfoHandle(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO p);

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    private const uint PT_TOUCH = 2, FB_NONE = 3;
    private const uint FLAG_INRANGE = 0x2, FLAG_INCONTACT = 0x4, FLAG_PRIMARY = 0x2000;
    private const uint FLAG_DOWN = 0x10000, FLAG_UPDATE = 0x20000, FLAG_UP = 0x40000;
    private const uint TOUCH_MASK_PRESSURE = 0x4;
    private const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004;
    private const uint WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_POINTERDOWN = 0x0246, WM_POINTERUPDATE = 0x0245, WM_GESTURE = 0x0119;
    private const uint GID_PAN = 4;
    private static readonly IntPtr PER_MONITOR_AWARE_V2 = new(-4);

    private static readonly StringBuilder Report = new();
    private static int _vsLeft, _vsTop;
    private static int _ptrDown, _ptrUpd, _gesture, _pan, _panDx;
    private static IntPtr _procPtr;
    private static readonly WndProc TargetProcDelegate = TargetProc;

    private static IntPtr TargetProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case WM_POINTERDOWN: _ptrDown++; break;
            case WM_POINTERUPDATE: _ptrUpd++; break;
            case WM_GESTURE:
                _gesture++;
                var gi = new GESTUREINFO { cbSize = (uint)Marshal.SizeOf(typeof(GESTUREINFO)) };
                if (GetGestureInfo(l, ref gi))
                {
                    if (gi.dwID == GID_PAN) { _pan++; _panDx += (int)(gi.ullArguments & 0xFFFFFFFF); }
                    CloseGestureInfoHandle(l);
                }
                break;
        }
        return DefWindowProcW(h, m, w, l);
    }

    private static void Out(string s = "") { Console.WriteLine(s); Report.AppendLine(s); }

    private static bool Shown()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        if (!GetCursorInfo(ref ci)) return true;
        return (ci.flags & 1) != 0;
    }

    private static void Pump()
    {
        for (int i = 0; i < 8; i++)
        {
            if (!PeekMessageW(out var m, IntPtr.Zero, 0, 0, PM_REMOVE)) break;
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
    }

    private static void MouseMove(int mickeys)
    {
        var inp = new INPUT[1];
        inp[0].type = 0; inp[0].mi.dx = mickeys; inp[0].mi.dy = 0; inp[0].mi.dwFlags = MOVE;
        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y); Thread.Sleep(40);
        var d = new INPUT[1]; d[0].type = 0; d[0].mi.dwFlags = LEFTDOWN;
        SendInput(1, d, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(30);
        var u = new INPUT[1]; u[0].type = 0; u[0].mi.dwFlags = LEFTUP;
        SendInput(1, u, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(200); Pump();
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

    private static void ResetCounters() { _ptrDown = _ptrUpd = _gesture = _pan = 0; _panDx = 0; }

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2);
        _procPtr = Marshal.GetFunctionPointerForDelegate(TargetProcDelegate);
        _vsLeft = GetSystemMetrics(76); _vsTop = GetSystemMetrics(77);

        var wc = new WNDCLASS { lpfnWndProc = _procPtr, lpszClassName = "PanTargetV3", hbrBackground = new IntPtr(5) };
        RegisterClassW(ref wc);
        IntPtr tgt = CreateWindowExW(0, "PanTargetV3", "PanProbe v3（消费 WM_POINTER/WM_GESTURE）",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE, 900, 140, 520, 360,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (tgt == IntPtr.Zero) { Out("建窗口失败 err=" + Marshal.GetLastWin32Error()); return; }

        GetWindowRect(tgt, out var rt);
        int gx = (rt.L + rt.R) / 2, gy = (rt.T + rt.B) / 2;
        int cx = gx - _vsLeft, cy = gy - _vsTop;

        SetForegroundWindow(tgt);
        Thread.Sleep(150);
        ClickAt(gx, gy);              // 点一下自己窗口，确保真正拿到前台/焦点
        Out($"[目标窗口] 全局中心=({gx},{gy}) 注入局部=({cx},{cy})  前台={GetForegroundWindow() == tgt}");

        IntPtr dev = CreateSyntheticPointerDevice(PT_TOUCH, 2, FB_NONE);
        Out($"[设备] PT_TOUCH 合成设备={(dev == IntPtr.Zero ? "创建失败" : "OK")}");

        // ---------- T1：只接触不动，1 秒（前台窗口，看是否隐藏 / 是否收到 pointer ----------
        Out("\n=== T1. 双指接触保持 1s（不做任何鼠标动作）===");
        ResetCounters();
        GetCursorPos(out var c0);
        Inject(dev, FLAG_INRANGE | FLAG_INCONTACT | FLAG_DOWN, tgt, cx, cy, 40);
        Thread.Sleep(80); Pump();
        int hid1 = 0, dev1 = 0, n1 = 20;
        for (int i = 0; i < n1; i++)
        {
            Thread.Sleep(50); Pump();
            if (!Shown()) hid1++;
            GetCursorPos(out var p);
            dev1 = Math.Max(dev1, Math.Max(Math.Abs(p.X - c0.X), Math.Abs(p.Y - c0.Y)));
        }
        Out($"  隐藏 {hid1}/{n1} 帧   光标最大偏移 {dev1}px");
        Out($"  目标收到 WM_POINTERDOWN={_ptrDown} WM_POINTERUPDATE={_ptrUpd} WM_GESTURE={_gesture} GID_PAN={_pan}");

        // ---------- T2：持续 pan + 持续移动鼠标（用户要的「同时」）----------
        Out("\n=== T2. 持续 pan（每帧注入）+ 持续移动鼠标，共约 1s ===");
        ResetCounters();
        GetCursorPos(out var c2);
        int px = cx, hid2 = 0, n2 = 30, drift = 0;
        var vis2 = new StringBuilder();
        for (int i = 0; i < n2; i++)
        {
            if (i % 2 == 0) { px -= 12; Inject(dev, FLAG_INRANGE | FLAG_INCONTACT | FLAG_UPDATE, tgt, px, cy, 40); }
            MouseMove(6);                       // 模拟物理鼠标每 30ms 走 6 米奇
            Thread.Sleep(30); Pump();
            bool s = Shown();
            if (!s) hid2++;
            vis2.Append(s ? "." : "x");
            GetCursorPos(out var pc);
            drift = pc.X - c2.X;
        }
        Out($"  可见性（每 30ms 一采样，. = 可见  x = 隐藏）：{vis2}");
        Out($"  隐藏 {hid2}/{n2} 帧   光标相对起点 X={drift}px   pan 累积位移={_panDx}px（GID_PAN {_pan} 次）");

        // ---------- T2b：pan 继续但鼠标停手，看多久被隐藏 ----------
        Out("\n=== T2b. 停止鼠标动作后，光标还能保持可见多久 ===");
        int shownMs = 0;
        for (int i = 0; i < 20; i++)
        {
            if (i % 2 == 0) { px -= 12; Inject(dev, FLAG_INRANGE | FLAG_INCONTACT | FLAG_UPDATE, tgt, px, cy, 40); }
            Thread.Sleep(50); Pump();
            if (Shown()) shownMs += 50; else break;
        }
        Out($"  停手后仍可见 {shownMs}ms（0 = 立刻被隐藏）");

        Inject(dev, FLAG_INRANGE | FLAG_UP, tgt, px, cy, 40);
        Thread.Sleep(200); Pump();
        Out($"  抬手 200ms 后 光标可见={Shown()}");
        MouseMove(2); Thread.Sleep(80); Pump();
        Out($"  抬手后发 2 米奇鼠标动作 → 光标可见={Shown()}");

        // ---------- T3：用 ±1 米奇轻推能否让光标一直亮着 ----------
        Out("\n=== T3. 接触期间每 120ms 做一次 +1/-1 米奇轻推，能否保持可见 ===");
        ResetCounters();
        GetCursorPos(out var c3);
        Inject(dev, FLAG_INRANGE | FLAG_INCONTACT | FLAG_DOWN, tgt, cx, cy, 40);
        Thread.Sleep(120); Pump();
        int hid3 = 0, n3 = 30;
        var vis3 = new StringBuilder();
        for (int i = 0; i < n3; i++)
        {
            if (i % 2 == 0) { px -= 8; Inject(dev, FLAG_INRANGE | FLAG_INCONTACT | FLAG_UPDATE, tgt, px, cy, 40); }
            if (i % 4 == 0) { MouseMove(1); Thread.Sleep(8); MouseMove(-1); }
            Thread.Sleep(22); Pump();
            bool s = Shown();
            if (!s) hid3++;
            vis3.Append(s ? "." : "x");
        }
        GetCursorPos(out var c3b);
        Inject(dev, FLAG_INRANGE | FLAG_UP, tgt, px, cy, 40);
        Thread.Sleep(150); Pump();
        Out($"  可见性：{vis3}");
        Out($"  隐藏 {hid3}/{n3} 帧   轻推造成的光标净漂移 = {c3b.X - c3.X}px");
        Out($"  抬手后 光标可见={Shown()}");

        DestroySyntheticPointerDevice(dev);
        DestroyWindow(tgt);
        System.IO.File.WriteAllText("report3.txt", Report.ToString(), Encoding.UTF8);
        Out("\n报告已写入 report3.txt");
    }
}
