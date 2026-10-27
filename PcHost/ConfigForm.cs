using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 设置窗口。分四组：连接 / 输入 / 映射 / 桌面显示。
///
/// 布局用「标签列 + 控件列」的表格，窗口固定最小宽度，
/// 避免出现控件被裁切、标签折行的问题。
/// 底部是一个等宽字体的诊断信息框，实时显示虚拟屏幕、映射区域、帧统计与网络信息。
/// </summary>
internal sealed class ConfigForm : Form
{
    /// <summary>本 EXE 的构建时间（用于窗口标题区分版本）。</summary>
    private static string BuildStamp()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
                return System.IO.File.GetLastWriteTime(path).ToString("MM-dd HH:mm");
        }
        catch { }
        return "dev";
    }

    /// <summary>设计尺寸（96DPI 基准）。高 DPI 下由 WinForms 自动等比放大。</summary>
    private const int DESIGN_W = 700;
    private const int DESIGN_H = 820;

    private static readonly (string Text, string Key)[] AspectOptions =
    {
        ("跟随手机端设置（默认）", "Follow"),
        ("保持手机比例·居中留边", "Keep"),
        ("拉伸铺满目标区域", "Stretch"),
    };

    private readonly MappingConfig _cfg;
    private readonly TcpPenServer _server;
    private readonly Action<bool> _setDebug;

    private readonly NumericUpDown _nudPort;
    private readonly ComboBox _cmbMode;
    private readonly ComboBox _cmbWork;
    private readonly NumericUpDown _nudMouseSpeed;
    private readonly NumericUpDown _nudScale;
    private readonly ComboBox _cmbAspect;
    private readonly CheckBox _chkFull;
    private readonly NumericUpDown _nudX, _nudY, _nudW, _nudH;
    private readonly CheckBox _chkDebug;
    private readonly CheckBox _chkShowRegion;
    private readonly TextBox _txtInfo;
    private readonly Label _lblNet;
    private TableLayoutPanel _table;
    private Panel _diagPanel;
    private readonly System.Windows.Forms.Timer _timer;

    public ConfigForm(MappingConfig cfg, TcpPenServer server, Action<bool> setDebug)
    {
        _cfg = cfg;
        _server = server;
        _setDebug = setDebug;

        // 标题里带上构建时间戳：一眼看出当前跑的是哪个版本（避免对着旧窗口排查半天）
        Text = "SpenTablet 设置  ·  " + BuildStamp();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        Padding = new Padding(14);
        Font = new Font("Microsoft YaHei UI", 9f);
        // 关闭一切自动缩放/自动定尺寸：
        // 实测 WinForms 会把窗体缩到「内容首选尺寸」，导致设置项被裁掉。
        // 这里尺寸完全由我们自己按物理像素控制，最可控。
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = false;
        ClientSize = new Size(DESIGN_W, DESIGN_H);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Fill,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 上面是可滚动的设置区
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190)); // 下面是诊断区
        Controls.Add(root);

        // ---------------- 设置区（放滚动容器里，窗口再小也不会裁切）----------------
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 6, 0) };

        var table = _table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Top,
        };
        // 注意：第二列必须用**固定宽度**。用 Percent 的话，AutoSize 的表格会把它压到最小，
        // 复选框/标签的文字就会被裁掉（用户截图里就是这样）。
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));

        int row = 0;
        void AddRow(string label, Control ctl)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lb = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(0, 5, 0, 0)
            };
            table.Controls.Add(lb, 0, row);
            table.Controls.Add(ctl, 1, row);
            row++;
        }
        void AddFull(Control ctl)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(ctl, 1, row);
            row++;
        }
        void AddSection(string text)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var t = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 90, 150),
                Padding = new Padding(0, row == 0 ? 0 : 10, 0, 4)
            };
            table.Controls.Add(t, 0, row);
            table.SetColumnSpan(t, 2);
            row++;
        }

        // ==== 连接 ====
        AddSection("连接");
        _nudPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = cfg.ListenPort, Width = 100 };
        AddRow("监听端口", _nudPort);

        _lblNet = new Label
        {
            // AutoSize + Dock.Top：行数随字体/DPI 变化自动撑开，不会被固定高度裁掉
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Dock = DockStyle.Top,
            ForeColor = Color.FromArgb(0, 110, 60),
            Text = ""
        };
        AddFull(_lblNet);

        var netRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var btnCopyIp = new Button { Text = "复制 IP", Width = 84, Height = 26 };
        btnCopyIp.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(NetInfo.DescribeAll());
                btnCopyIp.Text = "已复制 ✓";
                BeginInvoke((Action)(() => { btnCopyIp.Text = "复制 IP"; }));
            }
            catch (Exception ex) { MessageBox.Show($"复制失败：{ex.Message}", "SpenTablet"); }
        };
        var btnFw = new Button { Text = "允许防火墙", Width = 96, Height = 26 };
        btnFw.Click += (_, _) =>
        {
            var (ok, msg) = NetInfo.AddFirewallRule((int)_nudPort.Value);
            MessageBox.Show(msg, "SpenTablet", MessageBoxButtons.OK,
                            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshInfo();
        };
        var tipFw = new Label
        {
            Text = "USB 网络共享连不上，多半是这里没放行（Windows 防火墙会静默丢弃入站连接，表现为「连接超时」）",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.FromArgb(150, 60, 0),
            Padding = new Padding(0, 6, 0, 0)
        };
        var btnDiag = new Button { Text = "网络诊断", Width = 84, Height = 26 };
        btnDiag.Click += (_, _) => { RefreshInfo(); ShowDiagnostics(); };
        netRow.Controls.Add(btnCopyIp);
        netRow.Controls.Add(btnFw);
        netRow.Controls.Add(btnDiag);
        AddFull(netRow);
        AddFull(tipFw);

        // ==== 输入 ====
        AddSection("输入");
        _cmbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        _cmbMode.Items.AddRange(new object[] { "Pen（合成笔·带压感）", "Mouse（SendInput 回退）" });
        _cmbMode.SelectedIndex = cfg.Mode.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AddRow("注入模式", _cmbMode);

        _cmbWork = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        _cmbWork.Items.AddRange(new object[]
        {
            "数位板（S Pen 作画·手指拖框）",
            "鼠标（手指当触摸板）",
        });
        _cmbWork.SelectedIndex = cfg.IsMouseMode ? 1 : 0;
        AddRow("工作模式", _cmbWork);

        _nudMouseSpeed = new NumericUpDown
        {
            Minimum = 0.2m, Maximum = 4m, DecimalPlaces = 1, Increment = 0.1m,
            Value = (decimal)Math.Clamp(cfg.MouseSpeed, 0.2, 4.0), Width = 100
        };
        AddRow("鼠标速度", _nudMouseSpeed);

        var zoomRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        _nudScale = new NumericUpDown
        {
            Minimum = 0.05m, Maximum = 1m, DecimalPlaces = 2, Increment = 0.05m,
            Value = (decimal)Math.Clamp(cfg.Scale, 0.05, 1.0), Width = 100, ReadOnly = true
        };
        zoomRow.Controls.Add(_nudScale);
        zoomRow.Controls.Add(new Label
        {
            Text = "（由手机端双指捏合控制）",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(6, 6, 0, 0)
        });
        AddRow("范围框缩放", zoomRow);

        // ==== 映射 ====
        AddSection("映射");
        _cmbAspect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        foreach (var (text, _) in AspectOptions) _cmbAspect.Items.Add(text);
        _cmbAspect.SelectedIndex = Math.Max(0, Array.FindIndex(AspectOptions, a =>
            a.Key.Equals(cfg.Aspect, StringComparison.OrdinalIgnoreCase)));
        AddRow("比例映射", _cmbAspect);

        _chkFull = new CheckBox { AutoSize = false, Width = 420, Text = "自动铺满整个虚拟屏幕", Checked = cfg.MappingX == 0 && cfg.MappingY == 0 && cfg.MappingW == 0 && cfg.MappingH == 0 };
        AddFull(_chkFull);

        _nudX = new NumericUpDown { Minimum = -10000, Maximum = 20000, Value = cfg.MappingX, Width = 74 };
        _nudY = new NumericUpDown { Minimum = -10000, Maximum = 20000, Value = cfg.MappingY, Width = 74 };
        _nudW = new NumericUpDown { Minimum = 0, Maximum = 20000, Value = cfg.MappingW, Width = 74 };
        _nudH = new NumericUpDown { Minimum = 0, Maximum = 20000, Value = cfg.MappingH, Width = 74 };

        var region = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        region.Controls.Add(new Label { Text = "X", AutoSize = true, Padding = new Padding(0, 6, 2, 0) });
        region.Controls.Add(_nudX);
        region.Controls.Add(new Label { Text = "Y", AutoSize = true, Padding = new Padding(8, 6, 2, 0) });
        region.Controls.Add(_nudY);
        region.Controls.Add(new Label { Text = "W", AutoSize = true, Padding = new Padding(8, 6, 2, 0) });
        region.Controls.Add(_nudW);
        region.Controls.Add(new Label { Text = "H", AutoSize = true, Padding = new Padding(8, 6, 2, 0) });
        region.Controls.Add(_nudH);
        AddRow("映射区域", region);

        // ==== 桌面显示 ====
        AddSection("桌面显示");
        _chkShowRegion = new CheckBox { AutoSize = false, Width = 420, Text = "在桌面显示操作范围框", Checked = cfg.ShowRegion };
        AddFull(_chkShowRegion);

        _chkDebug = new CheckBox { AutoSize = false, Width = 420, Text = "显示调试浮窗（实时看笔坐标/压感）", Checked = cfg.Debug };
        AddFull(_chkDebug);

        _chkFull.CheckedChanged += (_, _) =>
        {
            bool off = _chkFull.Checked;
            _nudX.Enabled = _nudY.Enabled = _nudW.Enabled = _nudH.Enabled = !off;
        };
        bool isFull = cfg.MappingX == 0 && cfg.MappingY == 0 && cfg.MappingW == 0 && cfg.MappingH == 0;
        _nudX.Enabled = _nudY.Enabled = _nudW.Enabled = _nudH.Enabled = !isFull;

        scroll.Controls.Add(table);
        root.Controls.Add(scroll, 0, 0);

        // ---------------- 诊断信息框 ----------------
        _txtInfo = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        var diagTitle = new Label
        {
            Text = "状态诊断",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Padding = new Padding(0, 6, 0, 4)
        };
        var diagPanel = _diagPanel = new Panel { Dock = DockStyle.Fill };
        diagPanel.Controls.Add(_txtInfo);
        diagPanel.Controls.Add(diagTitle);
        root.Controls.Add(diagPanel, 0, 1);

        // 按钮行
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };
        var btnClose = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, Width = 84 };
        var btnRestart = new Button { Text = "保存并重启", Width = 104 };
        var btnSave = new Button { Text = "保存并应用", Width = 104 };
        btnSave.Click += (_, _) => Save();
        btnRestart.Click += (_, _) => { Save(); Application.Restart(); };
        buttons.Controls.Add(btnClose);
        buttons.Controls.Add(btnRestart);
        buttons.Controls.Add(btnSave);
        AcceptButton = btnSave;
        CancelButton = btnClose;
        Controls.Add(buttons);

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => RefreshInfo();
        _timer.Start();
        RefreshInfo();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AppLog.Info($"[UI-HandleCreated] DeviceDpi={DeviceDpi} ClientSize={ClientSize.Width}x{ClientSize.Height} " +
                    $"Size={Width}x{Height} 内容需要={_table.PreferredSize.Width}x{_table.PreferredSize.Height}");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ForceDesignSize();
        GetWindowRect(Handle, out var wr);
        AppLog.Info($"[UI] 配置窗口 DeviceDpi={DeviceDpi} 客户区={ClientSize.Width}x{ClientSize.Height} " +
                    $"物理窗口={wr.Right - wr.Left}x{wr.Bottom - wr.Top} 字体高={Font.Height} " +
                    $"内容需要={_table.PreferredSize.Width}x{_table.PreferredSize.Height}");
    }

    /// <summary>
    /// 按物理像素把窗口摆成设计尺寸。
    /// 本进程是 PerMonitorV2 感知，WinForms 的 1 个布局单位 = 1 个物理像素，
    /// 所以这里直接用 SetWindowPos 改外框尺寸即可（标题栏/边框用差值补上）。
    /// </summary>
    private void ForceDesignSize()
    {
        if (!IsHandleCreated) return;
        int border = Width - ClientSize.Width;
        int caption = Height - ClientSize.Height;
        // 内容实际需要多少（字体随 DPI 变，高度不能写死）
        int needH = _table.PreferredSize.Height + 190 /*诊断区*/ + 52 /*按钮行*/ + 40 /*边距*/;
        int needW = _table.PreferredSize.Width + 170 /*标签列+边距*/;
        int targetW = Math.Max(DESIGN_W, needW) + border;
        int targetH = Math.Max(DESIGN_H, needH) + caption;
        if (Width != targetW || Height != targetH)
        {
            SetWindowPos(Handle, IntPtr.Zero, Left, Top, targetW, targetH, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        MinimumSize = new Size(targetW, targetH);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);



    private void RefreshInfo()
    {
        var r = _cfg.GetActiveRect();
        string phone = _cfg.PhoneAspect > 0
            ? $"{_cfg.PhoneAspect:0.###}（偏好 {(_cfg.PhonePrefersKeep ? "保持等比" : "拉伸")}）"
            : "未知（等待手机连接）";
        _nudScale.Value = (decimal)Math.Clamp(_cfg.Scale, 0.05, 1.0);

        bool fw = NetInfo.HasFirewallRule(_cfg.ListenPort);
        string tether = NetInfo.GuessTetheringIp();
        _lblNet.Text = "手机端要填的 PC IP：" +
                       (string.IsNullOrEmpty(tether) ? "（未检测到）" : tether) + "\n" +
                       "　" + NetInfo.TetherHint() + "\n" +
                       "局域网自动发现（mDNS）：" + _server.MdnsStatus + "\n" +
                       $"防火墙入站端口 {_cfg.ListenPort}：{(fw ? "已放行 ✓" : "未放行 ✗（点上面「允许防火墙」）")}";

        // 注意：WinForms 的 TextBox 只认 \r\n 换行，用 \n 会全部挤在一行里
        string nl = Environment.NewLine;
        _txtInfo.Text = string.Join(nl, new[]
        {
            $"虚拟屏幕 : {_cfg.VirtualX},{_cfg.VirtualY}  {_cfg.VirtualW}×{_cfg.VirtualH}",
            $"手机比例 : {phone}",
            $"实映射区 : {r.x},{r.y}  {r.w}×{r.h}  (宽高比 {(r.h > 0 ? ((double)r.w / r.h).ToString("0.###") : "-")})",
            $"工作模式 : {(_cfg.IsMouseMode ? "鼠标（范围框隐藏）" : "数位板（范围框显示）")}  鼠标速度×{_cfg.MouseSpeed:0.##}",
            $"范围框   : 缩放×{_cfg.Scale:0.###}  位置({_cfg.FrameL:0.###},{_cfg.FrameT:0.###})（手机端捏合控制）",
            $"帧统计   : DOWN={_server.CountDown} MOVE={_server.CountMove} UP={_server.CountUp} HOVER={_server.CountHover}",
            $"最新笔迹 : ({_server.LastX},{_server.LastY})  压感 {_server.LastPressure:0.00}",
            $"监听端口 : {_cfg.ListenPort}   防火墙：{(fw ? "已放行" : "未放行（点上面「允许防火墙」）")}",
            "",
            "网卡 IP（手机端填「疑似 USB 网络共享」那个）：",
            "",
            "USB 网络共享：手机开「设置→连接→移动网络共享→USB 网络共享」，",
            "PC 端这里显示的 IP 就是手机要填的；连不上先点「允许防火墙」。",
        }.Concat(NetInfo.DescribeAllLines()));
    }

    private void ShowDiagnostics()
    {
        var ips = NetInfo.ListIps();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【本机网卡】");
        foreach (var i in ips)
            sb.AppendLine($"  {i.IPv4,-16} {i.Name,-10} {i.Description}  掩码/{i.Prefix} {(i.LooksLikeTether ? "← 疑似 USB 网络共享" : "")}");
        if (ips.Count == 0) sb.AppendLine("  （没有可用网卡）");

        sb.AppendLine();
        sb.AppendLine("【USB 网络共享怎么连】");
        sb.AppendLine("  1) 手机：设置 → 连接 → 移动网络共享 → 打开「USB 网络共享」");
        sb.AppendLine("  2) PC 上运行 ipconfig，找到上面标「疑似 USB 网络共享」的那块网卡的 IPv4");
        sb.AppendLine("  3) 手机端 SpenTablet 填这个 IP + 端口 6000，或直接点「自动查找 PC」");
        sb.AppendLine("  4) 若提示「连接被拒绝」→ 点上方「允许防火墙」并同意 UAC");
        sb.AppendLine();
        sb.AppendLine("【方式 A · ADB 转发（最稳，推荐）】");
        sb.AppendLine("  PC 上执行：adb reverse tcp:6000 tcp:6000");
        sb.AppendLine("  手机端保持 127.0.0.1:6000 即可（装过 APK 后要重跑一次 reverse）");

        MessageBox.Show(sb.ToString(), "SpenTablet 网络诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Save()
    {
        _cfg.ListenPort = (int)_nudPort.Value;
        _cfg.Mode = _cmbMode.SelectedIndex == 1 ? "Mouse" : "Pen";
        _cfg.Aspect = AspectOptions[Math.Max(0, _cmbAspect.SelectedIndex)].Key;
        _cfg.Debug = _chkDebug.Checked;
        _cfg.ShowRegion = _chkShowRegion.Checked;
        _cfg.MouseSpeed = (double)_nudMouseSpeed.Value;
        // 操作框的缩放/位置由手机端捏合控制，PC 这里只读不写
        _cfg.SetWorkMode(_cmbWork.SelectedIndex == 1);
        if (_cfg.IsMouseMode) MouseControl.ReleaseAll();

        if (_chkFull.Checked)
            _cfg.ApplyMapping(0, 0, 0, 0);
        else
            _cfg.ApplyMapping((int)_nudX.Value, (int)_nudY.Value, (int)_nudW.Value, (int)_nudH.Value);

        _setDebug(_cfg.Debug);
        ConfigStore.Save(_cfg);
        RefreshInfo();
        AppLog.Info($"[配置] 端口={_cfg.ListenPort} 注入={_cfg.Mode} 比例={_cfg.Aspect} " +
                    $"区域={_cfg.MappingX},{_cfg.MappingY},{_cfg.MappingW},{_cfg.MappingH} " +
                    $"工作模式={_cfg.WorkMode} 鼠标速度={_cfg.MouseSpeed:0.##} " +
                    $"范围框={(_cfg.ShowRegion ? "显示" : "隐藏")} 浮窗={_cfg.Debug}");

        MessageBox.Show("已保存。\n· 比例 / 映射区域 / 工作模式 / 范围框显示：立即生效\n· 端口与注入模式：需重启程序（可用「保存并重启」）",
                        "SpenTablet", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
