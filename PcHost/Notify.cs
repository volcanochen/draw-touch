using System;
using System.Drawing;
using System.Media;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 桌面测试的「开始 / 结束」提示横幅 —— 给自动化测试脚本用的零交互通知。
///
/// 背景：协议冒烟、光标注入验证这些测试会**真的移动系统光标、发真实点击**。
/// 用户的要求是：不要先停下来问他要不要开始，但必须让他**看得见**
/// 「现在正在测」以及「什么时候测完了」。
///
/// 所以这个窗口刻意做成"不需要任何人理会"的形态：
///   · <b>不抢焦点</b>（WS_EX_NOACTIVATE）：不会打断正在打的字、不会切走当前窗口；
///   · 右下角置顶、不占任务栏、不进 Alt+Tab（WS_EX_TOOLWINDOW）；
///   · 到时间自动关闭 —— 不留需要点「确定」的对话框，测试脚本也不用回收它。
///
/// 用法（由 tests/desk_notify.py 调用）：
///   SpenTabletHost.exe --notify "正在测试：会动鼠标" --seconds 20 --kind busy
///
/// 为什么不复用托盘气泡：托盘气泡只在 host 进程活着时才有，而门禁测试会先杀掉
/// host 再起干净的实例，气泡发不出去；而且气泡需要 WER 注册，在 CI/无人值守时
/// 有可能被系统静默丢弃。自绘一个窗口最稳。
/// </summary>
internal static class Notify
{
    /// <summary>解析命令行并显示横幅，然后退出进程（不进入主程序逻辑）。</summary>
    public static void Run(string[] args)
    {
        string text = Option(args, "--notify", "SpenTablet 测试");
        string kind = Option(args, "--kind", "busy");
        int seconds = int.TryParse(Option(args, "--seconds", "8"), out int s) ? s : 8;
        Show(text, seconds, kind);
    }

    private static string Option(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return fallback;
    }

    /// <summary>显示横幅（阻塞到自动关闭）。kind: busy / ok / fail</summary>
    public static void Show(string text, int seconds, string kind)
    {
        if (seconds <= 0) seconds = 6;
        Color back = kind switch
        {
            "ok" => Color.FromArgb(0x2E, 0x9E, 0x4C),     // 绿：测试通过
            "fail" => Color.FromArgb(0xB8, 0x36, 0x36),   // 红：测试失败
            _ => Color.FromArgb(0xD3, 0x84, 0x1E),        // 橙：正在测试
        };
        string head = kind switch
        {
            "ok" => "✔ 测试完成",
            "fail" => "✖ 测试失败",
            _ => "● 正在测试",
        };

        using var form = new BannerForm(head, text, back, seconds);
        // 提示音：用户可能没在盯右下角，声音比颜色更容易被注意到
        try { SystemSounds.Asterisk.Play(); } catch { /* 无声卡也不影响 */ }
        Application.Run(form);
    }

    private sealed class BannerForm : Form
    {
        // 写全名：ImplicitUsings 会把 System.Threading 也引进来，Timer 有歧义
        private readonly System.Windows.Forms.Timer _close = new System.Windows.Forms.Timer();

        public BannerForm(string head, string body, Color back, int seconds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = back;
            Padding = new Padding(18, 14, 18, 14);

            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
            };
            var fHead = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            var fBody = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            panel.Controls.Add(new Label
            {
                Text = head, Font = fHead, ForeColor = Color.White,
                AutoSize = true, Margin = new Padding(0, 0, 0, 4),
            });
            panel.Controls.Add(new Label
            {
                Text = body, Font = fBody, ForeColor = Color.FromArgb(0xF2, 0xF2, 0xF2),
                AutoSize = true, MaximumSize = new Size(520, 0),
                Margin = Padding.Empty,
            });
            Controls.Add(panel);

            _close.Interval = Math.Max(1000, seconds * 1000);
            _close.Tick += (_, _) => { _close.Stop(); Close(); };
            _close.Start();
        }

        /// <summary>Show() 不激活本窗口，避免抢走前台焦点（正在写文档时尤其重要）。</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：点了也不会激活
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不占任务栏、不进 Alt+Tab
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 尺寸要等布局算完才知道 → 在 OnShown 里贴右下角
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);
        }
    }
}
