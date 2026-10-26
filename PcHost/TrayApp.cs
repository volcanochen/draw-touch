using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 系统托盘常驻：不再占用控制台窗口，右键菜单可打开配置、开关调试浮窗、查看日志、退出。
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly MappingConfig _cfg;
    private readonly TcpPenServer _server;
    private readonly Action<bool> _setDebug;
    private readonly System.Windows.Forms.Timer _tick;
    private readonly Bitmap _iconBmp;
    private ConfigForm? _form;
    private ToolStripMenuItem? _mWork;

    public TrayApp(MappingConfig cfg, TcpPenServer server, Action<bool> setDebug)
    {
        _cfg = cfg;
        _server = server;
        _setDebug = setDebug;

        _iconBmp = BuildIconBitmap();
        _tray = new NotifyIcon
        {
            Icon = Icon.FromHandle(_iconBmp.GetHicon()),
            Text = "SpenTablet 数位板",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => OpenConfig();
        _tray.BalloonTipShown += (_, _) => { };

        // 每 2 秒把帧统计写进日志（无控制台时唯一的“还活着”信号）
        _tick = new System.Windows.Forms.Timer { Interval = 2000 };
        _tick.Tick += (_, _) =>
        {
            AppLog.Info($"[统计] DOWN={_server.CountDown} MOVE={_server.CountMove} UP={_server.CountUp} " +
                        $"HOVER={_server.CountHover} NOOP={_server.CountNoop} | 最新 ({_server.LastX},{_server.LastY}) " +
                        $"p={_server.LastPressure:F2}");
            // 手机端切换模式时同步托盘勾选（Changed 事件来自网络线程，这里在 UI 线程上跟）
            if (_mWork != null && _mWork.Checked != _cfg.IsMouseMode)
            {
                _mWork.Checked = _cfg.IsMouseMode;
                _tray.Text = _cfg.IsMouseMode ? "SpenTablet 鼠标模式" : "SpenTablet 数位板";
            }
        };
        _tick.Start();

        AppLog.Info("[Host] 已驻留系统托盘，右键图标可打开配置。");
        _tray.ShowBalloonTip(3000, "SpenTablet", "已启动，右键托盘图标可打开配置", ToolTipIcon.Info);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var mConfig = new ToolStripMenuItem("配置…");
        mConfig.Click += (_, _) => OpenConfig();
        menu.Items.Add(mConfig);

        var mWork = new ToolStripMenuItem("鼠标模式（手指当触摸板）") { Checked = _cfg.IsMouseMode };
        _mWork = mWork;
        mWork.Click += (_, _) =>
        {
            bool on = !mWork.Checked;
            mWork.Checked = on;
            _cfg.SetWorkMode(on);
            if (on) MouseControl.ReleaseAll();
            _tray.Text = on ? "SpenTablet 鼠标模式" : "SpenTablet 数位板";
            AppLog.Info($"[Host] 托盘切换为 {(on ? "鼠标" : "数位板")} 模式");
            ConfigStore.Save(_cfg);
        };
        menu.Items.Add(mWork);

        var mDebug = new ToolStripMenuItem("调试浮窗") { Checked = _cfg.Debug };
        mDebug.Click += (_, _) =>
        {
            bool on = !mDebug.Checked;
            mDebug.Checked = on;
            _cfg.Debug = on;
            _setDebug(on);
            ConfigStore.Save(_cfg);
        };
        menu.Items.Add(mDebug);

        var mLog = new ToolStripMenuItem("打开日志");
        mLog.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppLog.FilePath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"打开日志失败：{ex.Message}", "SpenTablet"); }
        };
        menu.Items.Add(mLog);

        menu.Items.Add(new ToolStripSeparator());

        var mExit = new ToolStripMenuItem("退出");
        mExit.Click += (_, _) => { AppLog.Info("[Host] 用户退出"); Exit(); };
        menu.Items.Add(mExit);

        return menu;
    }

    private void OpenConfig()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new ConfigForm(_cfg, _server, _setDebug);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Show();
        _form.BringToFront();
        _form.Activate();
    }

    /// <summary>画一个简单的“笔”图标：深色底 + 白色斜线笔尖。</summary>
    private static Bitmap BuildIconBitmap()
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var bg = new SolidBrush(Color.FromArgb(30, 90, 160));
        g.FillEllipse(bg, 0, 0, 31, 31);
        using var pen = new Pen(Color.White, 4f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawLine(pen, 7, 25, 21, 11);
        using var tip = new SolidBrush(Color.Orange);
        g.FillEllipse(tip, 19, 7, 7, 7);
        return bmp;
    }

    private void Exit()
    {
        _tick.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _server.Dispose();
        _iconBmp.Dispose();
        ExitThread();
    }
}
