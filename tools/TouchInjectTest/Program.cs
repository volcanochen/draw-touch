// TouchInjectTest —— 触摸注入自检：定位本机 InjectTouchInput err=87 的真因。
// 用法：TouchInjectTest <mode> <x> <y>
//   mode 0 = pointerId 1,2 + feedback NONE(2) + 接触面积   （新方案，host 现在用的）
//   mode 1 = pointerId 0,1 + feedback INDIRECT(1) + 接触面积（旧方案，复现故障）
//   mode 2 = pointerId 1,2 + feedback NONE(2) + 无接触面积
//   mode 3 = pointerId 1,2 + feedback DEFAULT(0) + 接触面积
// 流程：Down → 40ms → 同坐标 Update（按住不动）→ 40ms → 移动 Update → 40ms → Up
// 建议把 (x,y) 指到 PointerProbe 窗口上，可同时看到应用侧收到的消息。
// 每一步打印成败与 GetLastError；结果同时写 result.txt。
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class Program
{
    const uint PT_TOUCH = 2;
    const uint F_NEW = 1, F_INRANGE = 2, F_INCONTACT = 4;
    const uint F_DOWN = 0x00010000, F_UPDATE = 0x00020000, F_UP = 0x00040000;
    const uint MASK_CONTACTAREA = 1;

    [StructLayout(LayoutKind.Sequential)] struct PT2 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public uint type, id, frameId, flags;
        public IntPtr source, hwnd;
        public PT2 pix, him, pixRaw, himRaw;
        public uint dwTime, historyCount;
        public int inputData;
        public uint keyStates, perf, btn;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO info;
        public uint touchFlags, touchMask;
        public RECT rc, rcRaw;
        public uint orientation, pressure;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool InitializeTouchInjection(uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)] static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] c);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    // Win11 合成指针注入（与 InjectTouchInput 不同的 API 家族；文档仅支持 PT_TOUCH / PT_PEN）
    const uint PT_TOUCHPAD = 0xFE;   // 非法类型——之前误用导致 create 87，不是系统封禁
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint feedbackMode);
    [DllImport("user32.dll", SetLastError = true)] static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] info, uint count);
    [DllImport("user32.dll")] static extern bool DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    // 正确布局：C 里是 type(DWORD) + 4 字节对齐填充 + union（偏移 8 起，8 字节对齐）。
    // 之前误写成「完整 POINTER_INFO + 完整 TOUCH_INFO」并列——尺寸/偏移全错 → 封送垃圾 → 87。
    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TYPE_INFO
    {
        public uint type;                 // POINTER_INPUT_TYPE = PT_TOUCH
        public POINTER_TOUCH_INFO touch;  // union 成员（对齐自动落到偏移 8）
    }

    static IntPtr _synthDev;

    static bool SynthInject(POINTER_TYPE_INFO[] infos)
    {
        bool ok = InjectSyntheticPointerInput(_synthDev, infos, (uint)infos.Length);
        if (!ok) Console.WriteLine($"  inject FAIL err={Marshal.GetLastWin32Error()}");
        return ok;
    }

    // 构造同帧双指。参照社区可用样例：frameId 恒 0、historyCount=0、hwndTarget=前台窗口、
    // touchMask=PRESSURE（不用接触面积）、pressure=1、pointerId 从 1 开始。
    static POINTER_TYPE_INFO[] BuildSynthPair(int x, int y, uint flags, int span)
    {
        var arr = new POINTER_TYPE_INFO[2];
        for (int i = 0; i < 2; i++)
        {
            int px = x + (i == 0 ? -span : span);
            arr[i] = new POINTER_TYPE_INFO
            {
                type = 2u /*PT_TOUCH*/,
                touch = new POINTER_TOUCH_INFO
                {
                    info = new POINTER_INFO
                    {
                        type = 2u,
                        id = 1u + (uint)i,
                        frameId = 0,
                        flags = flags,
                        hwnd = GetForegroundWindow(),
                        pix = new PT2 { X = px, Y = y },
                        pixRaw = new PT2 { X = px, Y = y },
                        historyCount = 0,
                    },
                    touchMask = 0x00000004u,   // TOUCH_MASK_PRESSURE
                    pressure = 1,
                },
            };
        }
        return arr;
    }

    static string RunSynth(int x, int y, uint fb, uint ptype, uint maxCount = 2)
    {
        var sw = new StringWriter();
        _synthDev = CreateSyntheticPointerDevice(ptype, maxCount, fb);
        if (_synthDev == IntPtr.Zero)
        {
            var e = $"CreateSyntheticPointerDevice(type=0x{ptype:X} fb={fb} max={maxCount}) FAIL err={Marshal.GetLastWin32Error()}";
            Console.WriteLine(e); sw.WriteLine(e);
            return sw.ToString();
        }
        var okMsg = $"CreateSyntheticPointerDevice(type=0x{ptype:X} fb={fb} max={maxCount}) OK";
        Console.WriteLine(okMsg); sw.WriteLine(okMsg + "\r\n");
        try
        {
            // 时序参照可用样例
            SynthInject(BuildSynthPair(x, y, 0, 40));                                    // 预注入（样例含此步）
            Thread.Sleep(40);
            SynthInject(BuildSynthPair(x, y, F_INRANGE | F_INCONTACT | F_DOWN, 40));     // 双指同帧落下
            Thread.Sleep(40);
            SynthInject(BuildSynthPair(x, y, F_INRANGE | F_INCONTACT | F_UPDATE, 60));   // 张开
            Thread.Sleep(40);
            SynthInject(BuildSynthPair(x, y, F_INRANGE | F_INCONTACT | F_UPDATE, 80));
            Thread.Sleep(40);
            SynthInject(BuildSynthPair(x, y, F_INRANGE | F_INCONTACT | F_UPDATE, 100));
            Thread.Sleep(40);
            SynthInject(BuildSynthPair(x, y, F_INRANGE | F_UP, 100));                    // 抬起
            sw.WriteLine("(时序完成)");
        }
        finally { DestroySyntheticPointerDevice(_synthDev); }
        return sw.ToString();
    }

    static uint _frameId;
    static uint _idBase = 1;
    static uint _mask = MASK_CONTACTAREA;

    static POINTER_TOUCH_INFO Make(uint id, int x, int y, uint flags) => new POINTER_TOUCH_INFO
    {
        info = new POINTER_INFO
        {
            type = PT_TOUCH, id = id, frameId = ++_frameId, flags = flags,
            pix = new PT2 { X = x, Y = y }, pixRaw = new PT2 { X = x, Y = y },
            historyCount = 1,
        },
        touchMask = _mask,
        rc = new RECT { l = x - 3, t = y - 3, r = x + 3, b = y + 3 },
        rcRaw = new RECT { l = x - 3, t = y - 3, r = x + 3, b = y + 3 },
    };

    static string StepSingle(string name, int x, int y, uint flags)
    {
        var arr = new[] { Make(1, x, y, flags) };
        bool ok = InjectTouchInput(1, arr);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        string line = $"{name,-22} {(ok ? "OK" : $"FAIL err={err}")}";
        Console.WriteLine(line);
        return line + "\r\n";
    }

    static string Step(string name, int x, int y, uint flags, int span)
    {
        var arr = new[] { Make(_idBase, x - span, y, flags), Make(_idBase + 1, x + span, y, flags) };
        bool ok = InjectTouchInput(2, arr);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        string line = $"{name,-22} {(ok ? "OK" : $"FAIL err={err}")}";
        Console.WriteLine(line);
        return line + "\r\n";
    }

    static int Main(string[] args)
    {
        SetProcessDPIAware();
        int mode = args.Length > 0 ? int.Parse(args[0]) : 0;
        int x = args.Length > 1 ? int.Parse(args[1]) : 300;
        int y = args.Length > 2 ? int.Parse(args[2]) : 200;

        switch (mode)
        {
            case 1: _idBase = 0; break;
            case 2: _mask = 0; break;
            case 3: _idBase = 1; break;
        }
        uint fb = mode == 3 ? 0u : 2u;
        bool single = mode == 4;   // mode 4：单触点、单次调用

        var sw = new StringWriter();
        if (mode != 5)
            sw.WriteLine($"mode={mode} (ids={_idBase},{_idBase + 1} mask={(uint)_mask:X} feedback={fb}) @({x},{y})");
        Console.WriteLine(sw.ToString());

        if (mode != 5 && !InitializeTouchInjection(2, fb))
        {
            var e = $"InitializeTouchInput FAIL err={Marshal.GetLastWin32Error()}";
            Console.WriteLine(e); sw.WriteLine(e);
        }
        else if (mode != 5)
        {
            sw.WriteLine("InitializeTouchInjection OK\r\n");
            if (single)
            {
                sw.Write(StepSingle("SINGLE DOWN", x, y, F_INRANGE | F_INCONTACT | F_DOWN));
                Thread.Sleep(40);
                sw.Write(StepSingle("SINGLE UPDATE 同坐标", x, y, F_INRANGE | F_INCONTACT | F_UPDATE));
                Thread.Sleep(40);
                sw.Write(StepSingle("SINGLE UPDATE 移动", x + 10, y, F_INRANGE | F_INCONTACT | F_UPDATE));
                Thread.Sleep(40);
                sw.Write(StepSingle("SINGLE UP", x + 10, y, F_INRANGE | F_UP));
            }
            else
            {
                sw.Write(Step("DOWN (落下)", x, y, F_INRANGE | F_INCONTACT | F_DOWN, 40));
                Thread.Sleep(40);
                sw.Write(Step("UPDATE 同坐标(按住)", x, y, F_INRANGE | F_INCONTACT | F_UPDATE, 40));
                Thread.Sleep(40);
                sw.Write(Step("UPDATE 移动+10", x + 10, y, F_INRANGE | F_INCONTACT | F_UPDATE, 40));
                Thread.Sleep(40);
                sw.Write(Step("UP (抬起)", x + 10, y, F_INRANGE | F_UP, 40));
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "result.txt");
        if (mode == 5)
        {
            uint fb5 = args.Length > 3 ? uint.Parse(args[3]) : 3u;
            uint pt5 = args.Length > 4 ? uint.Parse(args[4]) : PT_TOUCHPAD;
            uint mc5 = args.Length > 5 ? uint.Parse(args[5]) : 2u;
            File.AppendAllText(path, $"mode=5 合成指针 @({x},{y}) fb={fb5} type=0x{pt5:X} maxCount={mc5}\r\n" + RunSynth(x, y, fb5, pt5, mc5) + "-----\r\n");
            Console.WriteLine("已写 " + path);
            return 0;
        }
        File.AppendAllText(path, sw.ToString() + "-----\r\n");
        Console.WriteLine($"已写 {path}");
        return 0;
    }
}
