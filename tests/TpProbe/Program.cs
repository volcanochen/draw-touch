using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TpProbe;

/// <summary>
/// 2026-11-01：只回答一个问题 —— 「能否创建 PT_TOUCHPAD 类型的合成指针设备」。
///
/// 为什么值得单独试：pan 期间「跟着手指走的那个指针」经实测**不是**我们控制的光标
/// （宿主逐帧日志：光标恒在锚点、偏差恒 0；4 组 hwndTarget 对照实验：注入触摸既不动光标
/// 也不产生鼠标事件）。剩下的可能只有系统对「触摸」这一输入类型自带的视觉/提升行为。
/// 真实触摸板（PT_TOUCHPAD）与触摸屏（PT_TOUCH）在系统里是**两条不同的投递路径**：
/// 触摸板走「精确式触摸板」手势，通常不产生触摸点视觉。若它能创建成功，
/// 就等于把「和真实触摸板一样的实现」这条路打通了。
///
/// 之前只试过 (PT_TOUCHPAD, 2, NONE) 得到 err=87(ERROR_INVALID_PARAMETER)，
/// 这里把所有合理组合都扫一遍：maxCount × feedbackMode。
/// </summary>
internal static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint feedbackMode);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("kernel32.dll")]
    private static extern int GetLastError();

    private static readonly string[] TypeName = { "?", "PT_POINTER", "PT_TOUCH", "PT_PEN", "PT_MOUSE", "PT_TOUCHPAD" };
    private static readonly string[] FbName = { "?", "DEFAULT", "INDIRECT", "NONE" };

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("pointerType × maxCount × feedbackMode");
        Console.WriteLine(new string('-', 62));
        foreach (uint pt in new uint[] { 5, 2, 3 })          // TOUCHPAD / TOUCH / PEN
        {
            for (uint n = 1; n <= 4; n++)
            {
                for (uint fb = 1; fb <= 3; fb++)
                {
                    IntPtr dev = CreateSyntheticPointerDevice(pt, n, fb);
                    int err = Marshal.GetLastWin32Error();
                    string res = dev != IntPtr.Zero ? "OK  ← 可用" : $"失败 err={err}";
                    Console.WriteLine($"{TypeName[pt],-11} maxCount={n} feedback={FbName[fb],-8} → {res}");
                    if (dev != IntPtr.Zero) DestroySyntheticPointerDevice(dev);
                }
            }
        }
    }
}
