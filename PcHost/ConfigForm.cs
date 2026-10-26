using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 设置窗口：端口、注入模式、比例映射方式、映射区域、调试浮窗。
/// 比例与映射区域可即时生效；端口与注入模式需要重启程序（提供“保存并重启”按钮）。
/// </summary>
internal sealed class ConfigForm : Form
{
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
    private readonly ComboBox _cmbAspect;
    private readonly CheckBox _chkFull;
    private readonly NumericUpDown _nudX, _nudY, _nudW, _nudH;
    private readonly CheckBox _chkDebug;
    private readonly Label _info;
    private readonly System.Windows.Forms.Timer _timer;

    public ConfigForm(MappingConfig cfg, TcpPenServer server, Action<bool> setDebug)
    {
        _cfg = cfg;
        _server = server;
        _setDebug = setDebug;

        Text = "SpenTablet 设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Top,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));

        int row = 0;
        void AddRow(string label, Control ctl)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lb = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
            table.Controls.Add(lb, 0, row);
            table.Controls.Add(ctl, 1, row);
            row++;
        }

        _nudPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = cfg.ListenPort, Width = 120 };
        AddRow("监听端口", _nudPort);

        _cmbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        _cmbMode.Items.AddRange(new object[] { "Pen（合成笔·带压感）", "Mouse（SendInput 回退）" });
        _cmbMode.SelectedIndex = cfg.Mode.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AddRow("注入模式", _cmbMode);

        _cmbWork = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        _cmbWork.Items.AddRange(new object[]
        {
            "数位板：范围框 + S Pen 作画 + 手指拖框",
            "鼠标：取消范围框 + 手指当触摸板",
        });
        _cmbWork.SelectedIndex = cfg.IsMouseMode ? 1 : 0;
        AddRow("工作模式", _cmbWork);

        _nudMouseSpeed = new NumericUpDown
        {
            Minimum = 0.2m, Maximum = 4m, DecimalPlaces = 1, Increment = 0.1m,
            Value = (decimal)Math.Clamp(cfg.MouseSpeed, 0.2, 4.0), Width = 90
        };
        AddRow("鼠标速度", _nudMouseSpeed);

        _cmbAspect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        foreach (var (text, _) in AspectOptions) _cmbAspect.Items.Add(text);
        _cmbAspect.SelectedIndex = Math.Max(0, Array.FindIndex(AspectOptions, a =>
            a.Key.Equals(cfg.Aspect, StringComparison.OrdinalIgnoreCase)));
        AddRow("比例映射", _cmbAspect);

        bool isFull = cfg.MappingX == 0 && cfg.MappingY == 0 && cfg.MappingW == 0 && cfg.MappingH == 0;
        _chkFull = new CheckBox { Text = "自动铺满整个虚拟屏幕", Checked = isFull };
        table.Controls.Add(_chkFull, 1, row++);

        _nudX = new NumericUpDown { Minimum = -10000, Maximum = 20000, Value = cfg.MappingX, Width = 90 };
        _nudY = new NumericUpDown { Minimum = -10000, Maximum = 20000, Value = cfg.MappingY, Width = 90 };
        _nudW = new NumericUpDown { Minimum = 0, Maximum = 20000, Value = cfg.MappingW, Width = 90 };
        _nudH = new NumericUpDown { Minimum = 0, Maximum = 20000, Value = cfg.MappingH, Width = 90 };

        var region = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        region.Controls.Add(_nudX);
        region.Controls.Add(_nudY);
        region.Controls.Add(_nudW);
        region.Controls.Add(_nudH);
        AddRow("区域 X/Y/W/H", region);

        _chkDebug = new CheckBox { Text = "显示调试浮窗", Checked = cfg.Debug };
        table.Controls.Add(_chkDebug, 1, row++);

        _chkFull.CheckedChanged += (_, _) =>
        {
            bool off = _chkFull.Checked;
            _nudX.Enabled = _nudY.Enabled = _nudW.Enabled = _nudH.Enabled = !off;
        };
        _nudX.Enabled = _nudY.Enabled = _nudW.Enabled = _nudH.Enabled = !isFull;

        _info = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 10, 0, 6),
            Font = new Font("Consolas", 9f),
            ForeColor = Color.DimGray,
            Text = ""
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var btnClose = new Button { Text = "关闭", DialogResult = DialogResult.Cancel };
        var btnRestart = new Button { Text = "保存并重启", Width = 110 };
        var btnSave = new Button { Text = "保存并应用", Width = 110 };
        btnSave.Click += (_, _) => Save();
        btnRestart.Click += (_, _) => { Save(); Application.Restart(); };
        buttons.Controls.Add(btnClose);
        buttons.Controls.Add(btnRestart);
        buttons.Controls.Add(btnSave);
        AcceptButton = btnSave;
        CancelButton = btnClose;

        Controls.Add(buttons);
        Controls.Add(_info);
        Controls.Add(table);

        _timer = new System.Windows.Forms.Timer { Interval = 400 };
        _timer.Tick += (_, _) => RefreshInfo();
        _timer.Start();
        RefreshInfo();
    }

    private void RefreshInfo()
    {
        var r = _cfg.GetActiveRect();
        string phone = _cfg.PhoneAspect > 0
            ? $"{_cfg.PhoneAspect:0.###}（偏好 {(_cfg.PhonePrefersKeep ? "保持等比" : "拉伸")}）"
            : "未知（等待手机连接）";
        _info.Text =
            $"虚拟屏幕 : {_cfg.VirtualX},{_cfg.VirtualY} {_cfg.VirtualW}×{_cfg.VirtualH}\n" +
            $"手机比例 : {phone}\n" +
            $"实映射区 : {r.x},{r.y} {r.w}×{r.h}  （{(r.w > 0 && r.h > 0 ? ((double)r.w / r.h).ToString("0.###") : "-")}）\n" +
            $"工作模式 : {(_cfg.IsMouseMode ? "鼠标（范围框已隐藏）" : "数位板（范围框显示）")}  速度×{_cfg.MouseSpeed:0.##}\n" +
            $"帧统计   : DOWN={_server.CountDown} MOVE={_server.CountMove} UP={_server.CountUp} HOVER={_server.CountHover}\n" +
            $"最新     : ({_server.LastX},{_server.LastY}) 压感 {_server.LastPressure:0.00}\n" +
            "注：端口与注入模式需重启程序后生效";
    }

    private void Save()
    {
        _cfg.ListenPort = (int)_nudPort.Value;
        _cfg.Mode = _cmbMode.SelectedIndex == 1 ? "Mouse" : "Pen";
        _cfg.Aspect = AspectOptions[Math.Max(0, _cmbAspect.SelectedIndex)].Key;
        _cfg.Debug = _chkDebug.Checked;
        _cfg.MouseSpeed = (double)_nudMouseSpeed.Value;
        _cfg.SetWorkMode(_cmbWork.SelectedIndex == 1);   // 立即生效并刷新范围框
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
                    $"工作模式={_cfg.WorkMode} 鼠标速度={_cfg.MouseSpeed:0.##} 浮窗={_cfg.Debug}");

        MessageBox.Show("已保存。比例与映射区域立即生效；\n端口 / 注入模式需重启程序（可用“保存并重启”）。",
                        "SpenTablet", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
