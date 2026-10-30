using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CursorProbe;

/// <summary>
/// 「指针在某个前台窗口下动不了」的取证工具。
///
/// 关键点：先把自己设成 per-monitor DPI aware，否则 SendInput 绝对坐标用的像素空间
/// 和 GetSystemMetrics 拿到的不一致，会算出一堆假的位移（这是本工具第一版的坑）。
///
/// 输出：前台窗口/进程提权、各关注进程提权、裁剪区、绝对/相对注入实测。
/// 只读 + 每次移动后立刻还原光标。
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out RECT r);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] i, int cb);

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUT
    { [FieldOffset(0)] public int type; [FieldOffset(8)] public MOUSEINPUT mi; }

    private const uint MOVE = 0x0001, ABSOLUTE = 0x8000, VIRTUALDESKTOP = 0x4000;
    private static readonly IntPtr PER_MONITOR_AWARE_V2 = new(-4);

    private static readonly StringBuilder Report = new();

    private static void Out(string line = "")
    {
        Console.WriteLine(line);
        Report.AppendLine(line);
    }

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // --cursor：只打印当前光标位置（供外部脚本比对「注入到底有没有让光标动」）
        if (args.Length > 0 && args[0] == "--cursor")
        {
            GetCursorPos(out var only);
            Console.WriteLine($"{only.X},{only.Y}");
            return;
        }

        // --elev <进程名>：打印该进程所有实例的提权状态
        if (args.Length >= 2 && args[0] == "--elev")
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(args[1]);
            if (procs.Length == 0) { Console.WriteLine($"{args[1]}: 没有运行"); return; }
            foreach (var pr in procs)
                Console.WriteLine($"{args[1]} pid={pr.Id} 提权={Elev((uint)pr.Id)}");
            return;
        }

        // --focus <标题片段>：把匹配的窗口切到前台（验证「某个程序前台时能不能注入」）
        if (args.Length >= 2 && args[0] == "--focus")
        {
            IntPtr h = FindByTitle(args[1]);
            if (h == IntPtr.Zero) { Console.WriteLine($"没找到标题含「{args[1]}」的窗口"); return; }
            ShowWindow(h, 9 /*SW_RESTORE*/);
            bool ok = SetForegroundWindow(h);
            Thread.Sleep(350);
            Console.WriteLine($"置前台={ok} 当前前台=\"{TitleOf(GetForegroundWindow())}\"");
            return;
        }

        bool dpiOk = SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2);
        Out($"[DPI] 设为 per-monitor-v2 : {dpiOk}");
        Out();

        // ---- 前台窗口 ----
        var fg = GetForegroundWindow();
        var sb = new StringBuilder(512);
        GetWindowTextW(fg, sb, sb.Capacity);
        GetWindowThreadProcessId(fg, out var fpid);
        string fname = "?";
        try { fname = System.Diagnostics.Process.GetProcessById((int)fpid).ProcessName; } catch { }
        Out($"前台窗口 : \"{sb}\"");
        Out($"前台进程 : pid={fpid} name={fname} 提权={Elev(fpid)}   (1=已提权 0=未提权 -1=打不开)");
        Out($"本进程   : pid={Environment.ProcessId} 提权={Elev((uint)Environment.ProcessId)}");
        Out();

        // ---- 关注进程 ----
        Out("关注进程提权状态：");
        foreach (var name in new[] { "DrawTouchHost", "WorkBuddy", "explorer" })
        {
            foreach (var pr in System.Diagnostics.Process.GetProcessesByName(name))
                Out($"  {name,-16} pid={pr.Id,-6} 提权={Elev((uint)pr.Id)}");
        }
        Out();

        // ---- 光标裁剪区 ----
        GetClipCursor(out var cr);
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        Out($"虚拟屏幕 : ({vx},{vy}) {vw}x{vh}");
        Out($"裁剪区   : L={cr.L} T={cr.T} R={cr.R} B={cr.B} 宽={cr.R - cr.L} 高={cr.B - cr.T}");
        Out($"           {(cr.L <= vx && cr.T <= vy && cr.R >= vx + vw && cr.B >= vy + vh ? "未裁剪" : "★ 光标被夹住 ★")}");
        Out();

        // ---- 实测 ----
        GetCursorPos(out var p0);
        Out($"光标原位 : ({p0.X},{p0.Y})");
        Out($"SetCursorPos(+120,0)   : {TestSet(p0)}");
        Out($"SendInput 绝对(+120,0) : {TestAbs(p0, 120, 0)}");
        Out($"SendInput 相对(+120,0) : {TestRel(120, 0)}");
        Out($"SendInput 绝对(0,+100) : {TestAbs(p0, 0, 100)}");

        SetCursorPos(p0.X, p0.Y);
        Out();
        Out("（光标已还原）");

        // 无人值守运行时（计划任务）把结果落盘，供外部读取
        try
        {
            string outPath = args.Length > 0 ? args[0] : System.IO.Path.Combine(AppContext.BaseDirectory, "report.txt");
            System.IO.File.WriteAllText(outPath, Report.ToString(), Encoding.UTF8);
            Console.WriteLine($"\n[report] 已写入 {outPath}");
        }
        catch (Exception ex) { Console.WriteLine($"[report] 写入失败：{ex.Message}"); }
    }

    private static string TestSet(POINT p0)
    {
        SetCursorPos(p0.X + 120, p0.Y);
        Thread.Sleep(50);
        GetCursorPos(out var q);
        SetCursorPos(p0.X, p0.Y);
        return $"真实位移=({q.X - p0.X},{q.Y - p0.Y})";
    }

    private static string TestAbs(POINT p0, int dx, int dy)
    {
        int tx = p0.X + dx, ty = p0.Y + dy;
        var arr = new[]
        {
            new INPUT
            {
                type = 0,
                mi = new MOUSEINPUT { dx = NormX(tx), dy = NormY(ty), dwFlags = MOVE | ABSOLUTE | VIRTUALDESKTOP }
            }
        };
        uint n = SendInput(1, arr, Marshal.SizeOf<INPUT>());
        int err = Marshal.GetLastWin32Error();
        Thread.Sleep(50);
        GetCursorPos(out var q);
        SetCursorPos(p0.X, p0.Y);
        return $"返回={n} err={err} 真实位移=({q.X - p0.X},{q.Y - p0.Y})" + (n == 0 ? "  ★注入被拒 ★" : "");
    }

    private static string TestRel(int dx, int dy)
    {
        GetCursorPos(out var p0);
        var arr = new[] { new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOVE } } };
        uint n = SendInput(1, arr, Marshal.SizeOf<INPUT>());
        int err = Marshal.GetLastWin32Error();
        Thread.Sleep(50);
        GetCursorPos(out var q);
        SetCursorPos(p0.X, p0.Y);
        return $"返回={n} err={err} 真实位移=({q.X - p0.X},{q.Y - p0.Y})" + (n == 0 ? "  ★注入被拒 ★" : "");
    }

    // 注意：横纵必须用各自的 origin/size。第一版这里把 Y 也按宽度归一会算出假位移。
    private static int NormX(int v) => Norm(v, GetSystemMetrics(76), GetSystemMetrics(78));
    private static int NormY(int v) => Norm(v, GetSystemMetrics(77), GetSystemMetrics(79));

    // ---- 找窗口 / 切前台 ----
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    private static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static IntPtr FindByTitle(string needle)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (TitleOf(h).Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static int Norm(int v, int origin, int size)
    {
        if (size <= 0) size = 1;
        double f = (double)(v - origin) / size;
        f = Math.Clamp(f, 0.0, 1.0);
        return (int)Math.Round(f * 65535.0);
    }

    // ---- TOKEN_ELEVATION ----
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint acc, bool inh, uint pid);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr p, uint acc, out IntPtr tok);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr tok, int cls, out uint info, int len, out int ret);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static int Elev(uint pid)
    {
        var hp = OpenProcess(0x1000, false, pid);
        if (hp == IntPtr.Zero) return -1;
        try
        {
            if (!OpenProcessToken(hp, 0x0008, out var tok)) return -1;
            try
            {
                if (!GetTokenInformation(tok, 20, out uint e, 4, out _)) return -1;
                return (int)e;
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(hp); }
    }
}
