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
    private readonly Icon _iconOff;   // 未连接：深蓝底
    private readonly Icon _iconOn;    // 已连接：绿色底
    private bool _lastConnected;
    private ConfigForm? _form;
    private ToolStripMenuItem? _mWork;

    public TrayApp(MappingConfig cfg, TcpPenServer server, Action<bool> setDebug, bool openConfig = false)
    {
        _cfg = cfg;
        _server = server;
        _setDebug = setDebug;

        _iconBmp = BuildIconBitmap(false);
        var onBmp = BuildIconBitmap(true);
        _iconOff = Icon.FromHandle(_iconBmp.GetHicon());
        _iconOn = Icon.FromHandle(onBmp.GetHicon());
        onBmp.Dispose();
        _tray = new NotifyIcon
        {
            Icon = _iconOff,
            Text = "SpenTablet 数位板｜未连接",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => OpenConfig();
        _tray.BalloonTipShown += (_, _) => { };

        // 每 2 秒把帧统计写进日志（无控制台时唯一的“还活着”信号）
        _tick = new System.Windows.Forms.Timer { Interval = 2000 };
        string lastDist = "";
        _tick.Tick += (_, _) =>
        {
            AppLog.Info($"[统计] DOWN={_server.CountDown} MOVE={_server.CountMove} UP={_server.CountUp} " +
                        $"HOVER={_server.CountHover} NOOP={_server.CountNoop} | 最新 ({_server.LastX},{_server.LastY}) " +
                        $"p={_server.LastPressure:F2}");
            // 帧类型分布（诊断用）：只在分布变化时记录，避免刷屏
            var dist = string.Join(" ", _server.FrameTypes.OrderBy(k => k.Key)
                .Select(k => $"0x{k.Key:X}={k.Value}"));
            if (dist != lastDist) { lastDist = dist; AppLog.Info($"[帧分布] {dist}"); }
            // 手机端切换模式时同步托盘勾选（Changed 事件来自网络线程，这里在 UI 线程上跟）
            if (_mWork != null && _mWork.Checked != _cfg.IsMouseMode)
                _mWork.Checked = _cfg.IsMouseMode;
            // 托盘图标反映连接状态：连上=绿色、断开=蓝色，悬停文字也带状态
            bool conn = _server.ClientConnected;
            if (conn != _lastConnected)
            {
                _lastConnected = conn;
                _tray.Icon = conn ? _iconOn : _iconOff;
            }
            _tray.Text = $"{(_cfg.IsMouseMode ? "SpenTablet 鼠标模式" : "SpenTablet 数位板")}｜{(conn ? "已连接手机" : "未连接")}";
        };
        _tick.Start();

        AppLog.Info("[Host] 已驻留系统托盘，右键图标可打开配置。");

        // 气泡提示里直接把「手机端该填什么」说清楚，省得用户去翻文档
        string ip = NetInfo.GuessTetheringIp();
        bool fw = NetInfo.HasFirewallRule(cfg.ListenPort);
        string tip = string.IsNullOrEmpty(ip)
            ? $"已启动，监听端口 {cfg.ListenPort}\n右键托盘图标 → 配置 / 网络诊断"
            : $"已启动，监听端口 {cfg.ListenPort}\n手机端 PC IP 填：{ip}" +
              (fw ? "" : "\n⚠ 防火墙尚未放行该端口，请右键 → 配置 → 允许防火墙");
        _tray.ShowBalloonTip(6000, "SpenTablet 已就绪", tip, ToolTipIcon.Info);

        if (openConfig)
        {
            // 消息循环起来之后再开窗，否则窗体句柄还没建好
            var t = new System.Windows.Forms.Timer { Interval = 300 };
            t.Tick += (_, _) => { t.Stop(); OpenConfig(); };
            t.Start();
        }
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
            AppLog.Info($"[Host] 托盘切换为 {(on ? "鼠标" : "数位板")} 模式");   // 悬停文字由 2 秒定时器统一刷新
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

        var mCopyIp = new ToolStripMenuItem("复制手机端要填的 IP");
        mCopyIp.Click += (_, _) =>
        {
            try
            {
                string ip = NetInfo.GuessTetheringIp();
                Clipboard.SetText(string.IsNullOrEmpty(ip) ? NetInfo.DescribeAll() : ip);
                _tray.ShowBalloonTip(2500, "SpenTablet",
                    string.IsNullOrEmpty(ip) ? "没有检测到可用 IP" : $"已复制：{ip}", ToolTipIcon.Info);
            }
            catch (Exception ex) { MessageBox.Show($"复制失败：{ex.Message}", "SpenTablet"); }
        };
        menu.Items.Add(mCopyIp);

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

    /// <summary>画一个简单的“笔”图标：深色/绿色底 + 白色斜线笔尖。底色区分连接状态。</summary>
    private static Bitmap BuildIconBitmap(bool connected)
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        // 已连接 = 绿底（一眼看出手机在线）；未连接 = 深蓝底
        using var bg = new SolidBrush(connected ? Color.FromArgb(34, 160, 80) : Color.FromArgb(30, 90, 160));
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
