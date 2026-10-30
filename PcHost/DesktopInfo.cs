using System;
using System.Runtime.InteropServices;

namespace DrawTouchHost;

/// <summary>
/// 桌面环境探针：判断「是不是换了一块屏 / 换了一个 Windows 虚拟桌面」。
///
/// 两个典型故障都靠它发现：
///   ① 多显示器：插拔显示器、改分辨率、接远程桌面后，虚拟屏幕尺寸会变。
///      而虚拟屏幕只在**启动时**取过一次的话，后面的坐标映射全错
///      （光标被夹死 → 完全控制不了；笔落在框外）。
///   ② Windows 虚拟桌面（Win+Ctrl+←/→）：每个虚拟桌面有各自的显示器布局，
///      绝对定位的光标坐标在另一个桌面上是无效的 —— 系统会拒绝这次移动，
///      表现同样是「完全控制不了」。切换桌面时前台窗口所属的桌面对象会变，
///      用它来发现切换时刻，切换后重新锚定光标。
/// </summary>
internal static class DesktopInfo
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex,
        System.Text.StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    private const int UOI_NAME = 2;

    /// <summary>
    /// 当前前台窗口所在的「桌面对象」句柄。
    /// 切换 Windows 虚拟桌面时这个值会变；普通情况下一直是同一个。
    /// </summary>
    public static IntPtr CurrentDesktopId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        uint pid;
        uint tid = GetWindowThreadProcessId(hwnd, out pid);
        if (tid == 0) return IntPtr.Zero;
        return GetThreadDesktop(tid);
    }

    /// <summary>桌面对象的名字（虚拟桌面是一个数字 ID 字符串，诊断用）。</summary>
    public static string CurrentDesktopName()
    {
        var id = CurrentDesktopId();
        if (id == IntPtr.Zero) return "?";
        var sb = new System.Text.StringBuilder(256);
        int need;
        if (GetUserObjectInformation(id, UOI_NAME, sb, sb.Capacity, out need))
            return sb.ToString();
        return "?";
    }
}
