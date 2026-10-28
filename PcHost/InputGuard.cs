using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SpenTabletHost;

/// <summary>
/// 输入注入权限守卫 —— 解决「指针在某些窗口上完全动不了，切到别的窗口立刻恢复」。
///
/// 根因（UIPI / 用户界面特权隔离）：
///   前台窗口所属进程的**完整性级别比本进程高**时（典型：前台是「以管理员身份运行」的程序，
///   例如管理员权限启动的 WorkBuddy），Windows 会**静默丢弃**普通权限进程的
///   SendInput / SetCursorPos —— 不抛异常、不返回错误，光标就是不动。
///   于是表现为：「Everything 里好好的，一切到 WorkBuddy 指针就冻住」。
///
/// 对策：
///   1. 注入失败/光标没动 → 立刻诊断「前台是哪个进程、权限多高」，写进日志（说清原因，不再靠猜）；
///   2. 托盘气泡提示 + 菜单「以管理员身份重启」，一键把权限补平；
///   3. 启动时若配置允许，直接以管理员身份重启（UAC 一次，之后全程可用）。
///
/// 注意：这是 Windows 的安全边界，**没有**普通权限下的绕法；唯一解法是让本进程的
/// 完整性级别 ≥ 前台程序（或以 SYSTEM 服务方式注入，代价远超收益）。
/// </summary>
internal static class InputGuard
{
    // ================= 对外状态 =================

    /// <summary>当前是否判定为「注入被权限拦截」。</summary>
    public static bool Blocked { get; private set; }

    /// <summary>最近一次判定的原因（给托盘提示用）。</summary>
    public static string LastReason { get; private set; } = "";

    private static long _lastLogAt;
    private static long _lastNoticeAt;
    private static string? _notice;     // 待托盘消费的提示文案（跨线程用 Interlocked 交换）
    private static bool _elevated;      // 本进程是否已提权（启动时算一次）

    /// <summary>本进程是否以管理员身份运行。</summary>
    public static bool IsElevated => _elevated;

    /// <summary>启动时调用一次：记录自身权限，便于日志一眼定位。</summary>
    public static void Init()
    {
        _elevated = TryGetElevation((uint)Environment.ProcessId, out bool e) && e;
        AppLog.Info(_elevated
            ? "[Guard] 本进程：管理员权限（可注入任何前台程序）"
            : "[Guard] 本进程：普通权限（前台若是管理员权限程序，注入会被系统静默丢弃）");
    }

    /// <summary>取走一条待显示的提示（没有就返回 null）。托盘定时器里调用。</summary>
    public static string? TakeNotice()
        => System.Threading.Interlocked.Exchange(ref _notice, null);

    /// <summary>SendInput 返回 0：被系统拒绝。</summary>
    public static void NoteSendFailed(string where, int err)
    {
        var reason = $"{where} 注入被系统拒绝" + (err != 0 ? $"（Win32 错误 {err}）" : "");
        Mark(reason);
    }

    /// <summary>发完绝对定位 + 相对兜底，光标一个像素都没动：判定为被权限拦截。</summary>
    public static void NoteCursorStuck()
    {
        Mark("光标连相对移动都不生效");
    }

    /// <summary>注入成功：解除拦截状态（从高权限窗口切回普通窗口时会自愈）。</summary>
    public static void NoteOk()
    {
        if (!Blocked) return;
        Blocked = false;
        AppLog.Info("[Guard] 注入已恢复正常（前台窗口的权限不再高于本进程）");
    }

    private static void Mark(string reason)
    {
        long now = Environment.TickCount64;
        bool firstOrStale = !Blocked || now - _lastLogAt > 5000;
        Blocked = true;

        if (firstOrStale)
        {
            _lastLogAt = now;
            string fg = DescribeForeground(out bool fgElevated, out string fgName);
            string advice = fgElevated
                ? $"前台程序「{fgName}」以**管理员权限**运行，而本程序是普通权限 → " +
                  "Windows 的 UIPI 会静默丢弃我们的鼠标注入。请右键托盘图标 →「以管理员身份重启」。"
                : "未能确定原因（前台程序权限并不高于本进程），请把本段日志发给开发者。";
            LastReason = $"{reason}；前台：{fg}；{advice}";
            AppLog.Warn($"[Guard] 指针注入失效：{LastReason}");
        }

        // 托盘气泡：30 秒内不重复弹（用户正在操作，别刷屏）
        if (now - _lastNoticeAt > 30_000)
        {
            _lastNoticeAt = now;
            string fg2 = DescribeForeground(out bool fgElevated2, out string fgName2);
            System.Threading.Interlocked.Exchange(ref _notice,
                fgElevated2
                    ? $"「{fgName2}」以管理员权限运行，普通权限的主机无法控制鼠标。\n" +
                      "右键托盘图标 →「以管理员身份重启」即可彻底解决。"
                    : "指针注入失效，详见日志。");
            _ = fg2;
        }
    }

    // ================= 诊断 =================

    /// <summary>描述前台窗口：「WorkBuddy (pid 47532)」。</summary>
    public static string DescribeForeground(out bool elevated, out string procName)
    {
        elevated = false;
        procName = "?";
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "（无前台窗口）";

        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out uint pid);
        try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { procName = "?"; }
        if (TryGetElevation(pid, out bool e)) elevated = e;
        return $"{procName} (pid {pid}, 标题「{sb}」, {(elevated ? "管理员权限" : "普通权限")})";
    }

    private static bool TryGetElevation(uint pid, out bool elevated)
    {
        elevated = false;
        var hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(hp, TOKEN_QUERY, out var tok)) return false;
            try
            {
                // TokenElevation = 20，返回单个 DWORD：0=未提权 1=已提权
                if (!GetTokenInformation(tok, 20, out uint e, 4, out _)) return false;
                elevated = e != 0;
                return true;
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(hp); }
    }

    // ================= 自助诊断 =================

    /// <summary>
    /// 主动自检：走一次真实的注入路径（往屏幕中间方向挪 40px 再还原），
    /// 返回一句人话结论。托盘菜单「检查指针注入权限」用它，
    /// 让用户在出问题的当下就能确认「到底是权限被拦，还是别的原因」。
    /// </summary>
    public static string SelfCheck(MappingConfig cfg)
    {
        if (!MouseControl.TryGetCursorPos(out int x, out int y))
            return "读不到光标位置（GetCursorPos 失败）。";

        // 朝屏幕中间方向挪，避免贴着边界时本来就挪不动造成误判
        int dir = x < cfg.VirtualX + cfg.VirtualW / 2 ? 1 : -1;
        bool wasBlocked = Blocked;
        MouseControl.MoveBy(cfg, 40 * dir, 0);
        bool moved = MouseControl.TryGetCursorPos(out int x2, out _) && x2 != x;
        MouseControl.MoveTo(cfg, x, y);      // 还原

        string fg = DescribeForeground(out bool fgElevated, out _);
        string self = _elevated ? "管理员权限" : "普通权限";
        if (moved)
        {
            if (!wasBlocked) NoteOk();
            return $"✅ 注入正常：本程序为{self}，光标可以移动。\n当前前台：{fg}";
        }

        Blocked = true;
        if (fgElevated)
        {
            return $"❌ 注入被系统拦截。\n\n本程序：{self}\n当前前台：{fg}\n\n" +
                   "原因：前台程序以**管理员权限**运行，Windows 的 UIPI 会静默丢弃" +
                   "低权限进程的鼠标注入（不报错、光标就是不动）。\n\n" +
                   "解决：右键托盘图标 →「以管理员身份重启」，或直接以管理员身份运行本程序。";
        }
        return $"❌ 注入被系统拦截，但前台程序权限并不高于本程序。\n\n" +
               $"本程序：{self}\n当前前台：{fg}\n\n" +
               "请把日志（托盘 →「打开日志」）发给开发者。";
    }

    // ================= 提权重启 =================

    /// <summary>提权重启时传给新实例的标记：看到它就绝不再提权一次（防「探测异常 → 无限弹 UAC」）。</summary>
    public const string RestartMarker = "--elevated-restart";

    /// <summary>
    /// 以管理员身份重启本程序（会弹一次 UAC）。
    /// 调用方随后应立刻退出当前实例：新实例启动时会重试绑定端口，等旧进程释放。
    /// </summary>
    public static bool RelaunchElevated()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                AppLog.Warn("[Guard] 取不到自身路径，无法提权重启");
                return false;
            }
            AppLog.Info("[Guard] 正在以管理员身份重启…");
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",              // UAC 提权；用户取消会抛 Win32Exception(1223)
                Arguments = RestartMarker,   // 新实例不再尝试提权，避免死循环
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[Guard] 提权重启失败/被取消：{ex.Message}");
            return false;
        }
    }

    // ================= P/Invoke =================

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint acc, bool inherit, uint pid);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr p, uint acc, out IntPtr tok);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr tok, int cls, out uint info, int len, out int ret);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
