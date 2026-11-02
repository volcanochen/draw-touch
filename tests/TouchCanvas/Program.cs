using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TouchCanvas
{
    /// <summary>
    /// 一张「我自己的」全屏静态画布，用来做触摸视觉取证。
    ///
    /// 为什么需要它：在别人的窗口（OneNote / 聊天窗）上做差分，内容一动整片像素都在变，
    /// 那个「跟着触点走的小团」会被噪声吃掉。用纯黑静态画布，任何变化都只可能是
    /// Windows 为触摸画出来的视觉（触摸指针 / 接触圆圈 / 提升光标）。
    ///
    /// 另外：窗口是我自己的进程，所以 SetWindowFeedbackSetting 对它**是有效的**
    /// （2026-11-01 实测：跨进程 Set=0 失败，本进程 Set=1 成功），可以用来做 A/B。
    ///
    /// 用法: TouchCanvas --seconds 25 [--feedback on|off] [--mouseinpointer 0|1]
    /// </summary>
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetWindowFeedbackSetting(IntPtr hwnd, int feature, uint mustPack, uint size, ref int val);
        [DllImport("user32.dll")] private static extern bool EnableMouseInPointer(bool enable);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040, SWP_NOACTIVATE = 0x0010;

        private const int FEEDBACK_TOUCH_CONTACTVISUALIZATION = 1;
        private const int FEEDBACK_TOUCH_TAP = 2;
        private const int FEEDBACK_TOUCH_DOUBLETAP = 3;
        private const int FEEDBACK_TOUCH_PRESSANDHOLD = 4;
        private const int FEEDBACK_TOUCH_RIGHTTAP = 5;
        private const int FEEDBACK_GESTURE_PRESSANDTAP = 11;

        [STAThread]
        private static void Main(string[] args)
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            int seconds = 25;
            string feedback = "keep";
            int mouseInPointer = -1;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seconds": seconds = int.Parse(args[++i]); break;
                    case "--feedback": feedback = args[++i].ToLowerInvariant(); break;
                    case "--mouseinpointer": mouseInPointer = int.Parse(args[++i]); break;
                }
            }

            var f = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(0, 0, GetSystemMetrics(0), GetSystemMetrics(1)),
                BackColor = Color.Black,
                TopMost = true,
                ShowInTaskbar = false,
                Text = "TouchCanvas"
            };
            f.Paint += (s, e) =>
            {
                using var font = new Font("Microsoft YaHei", 26f, FontStyle.Regular);
                using var brush = new SolidBrush(Color.FromArgb(90, 90, 90));
                e.Graphics.DrawString(
                    $"触摸视觉取证画布（静态纯黑）\n{seconds} 秒后自动关闭，请勿操作\nfeedback={feedback}",
                    font, brush, new PointF(60, 60));
            };

            var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
            timer.Tick += (s, e) => { timer.Stop(); f.Close(); };
            timer.Start();

            f.Shown += (s, e) =>
            {
                Console.WriteLine($"hwnd={f.Handle}  bounds={f.Bounds}");
                bool zp = SetWindowPos(f.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                                       SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                Console.WriteLine($"SetWindowPos(TOPMOST) = {zp}  IsWindowVisible={IsWindowVisible(f.Handle)}  " +
                                  $"GW_HWNDFIRST={GetWindow(f.Handle, 0)}");
                if (mouseInPointer >= 0)
                {
                    bool ok = EnableMouseInPointer(mouseInPointer != 0);
                    Console.WriteLine($"EnableMouseInPointer({mouseInPointer}) = {ok}");
                }
                int on = feedback == "on" ? 1 : 0;
                if (feedback != "keep")
                {
                    int okCount = 0;
                    foreach (int feat in new[] { FEEDBACK_TOUCH_CONTACTVISUALIZATION, FEEDBACK_TOUCH_TAP, FEEDBACK_TOUCH_DOUBLETAP, FEEDBACK_TOUCH_PRESSANDHOLD, FEEDBACK_TOUCH_RIGHTTAP, FEEDBACK_GESTURE_PRESSANDTAP })
                    {
                        int v = on;
                        if (SetWindowFeedbackSetting(f.Handle, feat, 0, 4, ref v)) okCount++;
                    }
                    Console.WriteLine($"SetWindowFeedbackSetting(全部6项 -> {on}) 成功 {okCount}/6");
                }
                f.Activate();
                Console.WriteLine($"READY{Environment.NewLine}");
            };

            Application.Run(f);
            Console.WriteLine("画布已关闭");
        }
    }
}
