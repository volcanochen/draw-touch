// 触摸注入参数探针：逐个组合试 InjectTouchInput，找出 err=87 的确切原因。
// 每个实验：DOWN(两点) → UPDATE(移动) → UP，报告每步成败与 GetLastError。
using System;
using System.Runtime.InteropServices;

class Probe
{
    const uint PT_TOUCH = 2;
    const uint FLAG_INRANGE = 0x00000002;
    const uint FLAG_INCONTACT = 0x00000004;
    const uint FLAG_DOWN = 0x00010000;
    const uint FLAG_UPDATE = 0x00020000;
    const uint FLAG_UP = 0x00040000;
    const uint MASK_CONTACTAREA = 0x00000001;
    const uint MASK_NONE = 0x00000000;
    const uint FLAG_NEW = 0x00000001;

    // 实验开关
    static bool g_zeroFrameId = false;
    static bool g_newOnDown = false;
    static bool g_setDwTime = false;
    static uint g_feedback = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct PT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RC { public int l, t, r, b; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public PT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong performanceCount;
        public uint buttonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOUCH_INFO
    {
        public POINTER_INFO info;
        public uint touchFlags, touchMask;
        public RC rcContact, rcContactRaw;
        public uint orientation, pressure;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool InitializeTouchInjection(uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)] static extern bool InjectTouchInput(uint count, TOUCH_INFO[] c);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool PeekMessage(out object m, IntPtr h, uint a, uint b, uint r);
    [DllImport("kernel32.dll")] static extern uint GetTickCount();

    static uint frameId = 0;

    static TOUCH_INFO Make(uint id, int x, int y, uint flags, uint mask, int radius, uint histCount = 0)
    {
        return new TOUCH_INFO
        {
            info = new POINTER_INFO
            {
                pointerType = PT_TOUCH,
                pointerId = id,
                frameId = g_zeroFrameId ? 0u : frameId,
                pointerFlags = flags,
                historyCount = histCount,
                // 注入帧的 performanceCount 为 0 也是 err=87 的已知根因，一律填真实 QPC 值
                performanceCount = (ulong)System.Diagnostics.Stopwatch.GetTimestamp(),
                dwTime = g_setDwTime ? GetTickCount() : 0u,
                ptPixelLocation = new PT { X = x, Y = y },
                ptPixelLocationRaw = new PT { X = x, Y = y },
            },
            touchMask = mask,
            rcContact = new RC { l = x - radius, t = y - radius, r = x + radius, b = y + radius },
            rcContactRaw = new RC { l = x - radius, t = y - radius, r = x + radius, b = y + radius },
        };
    }

    static bool Inject(TOUCH_INFO[] arr, string step)
    {
        var ok = InjectTouchInput((uint)arr.Length, arr);
        var err = Marshal.GetLastWin32Error();
        Console.WriteLine($"    {step}: {(ok ? "OK" : $"FAIL err={err}")}");
        return ok;
    }

    static void Trial(string name, uint maxCount, uint id0, uint id1, uint downFlags, uint mask, int radius, bool twoPoints, bool primeQueue = false, uint hist = 0)
    {
        Console.WriteLine($"  实验 {name}: maxCount={maxCount} id=({id0},{id1}) downFlags=0x{downFlags:X} mask=0x{mask:X} r={radius} pts={(twoPoints ? 2 : 1)} hist={hist} frameId0={g_zeroFrameId} new={g_newOnDown} dwTime={g_setDwTime} fb={g_feedback}");
        if (primeQueue)
        {
            // 强制为当前线程创建 Win32 消息队列（怀疑注入 API 需要线程有消息队列）
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        }
        if (!InitializeTouchInjection(maxCount, g_feedback))
        {
            Console.WriteLine($"    InitializeTouchInjection({maxCount}) FAIL err={Marshal.GetLastWin32Error()}");
            return;
        }
        SetCursorPos(500, 500);
        GetCursorPos(out var cur);
        frameId++;
        var down = downFlags | FLAG_INRANGE | FLAG_INCONTACT;
        if (g_newOnDown) down |= FLAG_NEW;
        if (twoPoints)
        {
            var a = Make(id0, cur.X - 60, cur.Y, down, mask, radius, hist);
            var b = Make(id1, cur.X + 60, cur.Y, down, mask, radius, hist);
            if (!Inject(new[] { a, b }, "DOWN(两点)")) return;
        }
        else
        {
            var a = Make(id0, cur.X - 60, cur.Y, down, mask, radius, hist);
            if (!Inject(new[] { a }, "DOWN(单点)")) return;
        }
        frameId++;
        var upId = twoPoints ? new[] { id0, id1 } : new[] { id0 };
        for (int i = 0; i < 3; i++)
        {
            var arr = new TOUCH_INFO[twoPoints ? 2 : 1];
            for (int k = 0; k < arr.Length; k++)
                arr[k] = Make(upId[k], cur.X - 60 + (i + 1) * 5, cur.Y, FLAG_UPDATE | FLAG_INRANGE | FLAG_INCONTACT, mask, radius, hist);
            if (!Inject(arr, $"MOVE#{i}")) return;
        }
        frameId++;
        var ups = new TOUCH_INFO[twoPoints ? 2 : 1];
        for (int k = 0; k < ups.Length; k++)
            ups[k] = Make(upId[k], cur.X - 60 + 20, cur.Y, FLAG_UP | FLAG_INRANGE, mask, radius, hist);
        Inject(ups, "UP");
        Console.WriteLine();
    }

    static void Main()
    {
        Console.WriteLine("== InjectTouchInput 参数探针 ==");
        Console.WriteLine($"  Marshal.SizeOf(POINTER_INFO)={Marshal.SizeOf<POINTER_INFO>()}  TOUCH_INFO={Marshal.SizeOf<TOUCH_INFO>()}");
        // 远程会话里触摸注入可能被拒
        Console.WriteLine($"  SM_REMOTESESSION={GetSystemMetrics(0x1000)} (1=RDP会话)");
        Console.WriteLine($"  SM_MAXIMUMTOUCHES={GetSystemMetrics(95)} (0=无触摸屏硬件)");

        // 基线：hist=1 + 真实 QPC（已知两项修复）
        Trial("A hist=1 基线", 2, 0, 1, FLAG_DOWN, MASK_CONTACTAREA, 3, true, hist: 1);

        // L: frameId 置 0（怀疑注入时须由系统赋值）
        g_zeroFrameId = true;
        Trial("L frameId=0", 2, 0, 1, FLAG_DOWN, MASK_CONTACTAREA, 3, true, hist: 1);
        g_zeroFrameId = false;

        // M: DOWN 帧加 POINTER_FLAG_NEW
        g_newOnDown = true;
        Trial("M NEW on DOWN", 2, 0, 1, FLAG_DOWN, MASK_CONTACTAREA, 3, true, hist: 1);
        g_newOnDown = false;

        // N: TOUCH_FEEDBACK_DEFAULT
        g_feedback = 0;
        Trial("N feedback=DEFAULT", 2, 0, 1, FLAG_DOWN, MASK_CONTACTAREA, 3, true, hist: 1);
        g_feedback = 2;

        // O: dwTime 填 GetTickCount
        g_setDwTime = true;
        Trial("O dwTime=tick", 2, 0, 1, FLAG_DOWN, MASK_CONTACTAREA, 3, true, hist: 1);
        g_setDwTime = false;

        // P: 组合 L+M
        g_zeroFrameId = true; g_newOnDown = true;
        Trial("P frameId=0+NEW", 2, 0, 1, FLAG_DOWN, MASK_CONTACTAREA, 3, true, hist: 1);
        g_zeroFrameId = false; g_newOnDown = false;

        // Q: hover 起始（INRANGE|UPDATE）再转 DOWN——文档状态机的另一入口
        TrialQ("Q hover起始");
        // R: TOUCH_FEEDBACK_INDIRECT
        g_feedback = 1;
        Trial("R feedback=INDIRECT", 2, 0, 1, FLAG_DOWN, MASK_CONTACTAREA, 3, true, hist: 1);
        g_feedback = 2;
        // S: maxCount=256 大容量
        Trial("S maxCount=256", 256, 0, 1, FLAG_DOWN, MASK_CONTACTAREA, 3, true, hist: 1);
        // T: pressure+orientation 掩码
        Trial("T mask=CONTACTAREA|PRESSURE|ORIENTATION", 2, 0, 1, FLAG_DOWN, MASK_CONTACTAREA | 0x8 | 0x4, 3, true, hist: 1);
    }

    static void TrialQ(string name)
    {
        Console.WriteLine($"  实验 {name}: hover(INRANGE|UPDATE) → DOWN → UP");
        if (!InitializeTouchInjection(2, 2))
        {
            Console.WriteLine($"    Init FAIL err={Marshal.GetLastWin32Error()}");
            return;
        }
        SetCursorPos(500, 500);
        GetCursorPos(out var cur);
        frameId++;
        var h = Make(0, cur.X - 60, cur.Y, FLAG_UPDATE | FLAG_INRANGE, MASK_CONTACTAREA, 3, 1);
        if (!Inject(new[] { h }, "HOVER")) return;
        System.Threading.Thread.Sleep(15);
        frameId++;
        var d = Make(0, cur.X - 60, cur.Y, FLAG_DOWN | FLAG_INRANGE | FLAG_INCONTACT, MASK_CONTACTAREA, 3, 1);
        if (!Inject(new[] { d }, "DOWN")) return;
        System.Threading.Thread.Sleep(15);
        frameId++;
        var u = Make(0, cur.X - 60, cur.Y, FLAG_UP | FLAG_INRANGE, MASK_CONTACTAREA, 3, 1);
        Inject(new[] { u }, "UP");
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

    static bool TrialThread(string name, uint maxCount, uint idBase)
    {
        Console.WriteLine($"  实验 {name}: ...");
        if (!InitializeTouchInjection(maxCount, 2))
        {
            Console.WriteLine($"    InitializeTouchInjection FAIL err={Marshal.GetLastWin32Error()}");
            return false;
        }
        GetCursorPos(out var cur);
        frameId++;
        var down = FLAG_DOWN | FLAG_INRANGE | FLAG_INCONTACT;
        var a = Make(idBase, cur.X - 60, cur.Y, down, MASK_CONTACTAREA, 3);
        var b = Make(idBase + 1, cur.X + 60, cur.Y, down, MASK_CONTACTAREA, 3);
        if (!Inject(new[] { a, b }, "DOWN(两点)")) return false;
        frameId++;
        var m1 = Make(idBase, cur.X - 55, cur.Y, FLAG_UPDATE | FLAG_INRANGE | FLAG_INCONTACT, MASK_CONTACTAREA, 3);
        var m2 = Make(idBase + 1, cur.X + 55, cur.Y, FLAG_UPDATE | FLAG_INRANGE | FLAG_INCONTACT, MASK_CONTACTAREA, 3);
        if (!Inject(new[] { m1, m2 }, "MOVE")) return false;
        frameId++;
        var u1 = Make(idBase, cur.X - 55, cur.Y, FLAG_UP | FLAG_INRANGE, MASK_CONTACTAREA, 3);
        var u2 = Make(idBase + 1, cur.X + 55, cur.Y, FLAG_UP | FLAG_INRANGE, MASK_CONTACTAREA, 3);
        Inject(new[] { u1, u2 }, "UP");
        return true;
    }
}
