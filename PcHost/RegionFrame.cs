using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 桌面上的「操作范围框」：用橙色边框标出 S Pen 实际能覆盖的区域。
///
/// 关键点：窗口带 WS_EX_TRANSPARENT，鼠标/笔事件会穿透到下面的窗口，
/// 因此不会挡住画图软件的输入；它只是一条可见的边界提示。
/// 位置由手指在手机上滑动平移（见 MappingConfig.Pan）。
/// </summary>
internal sealed class RegionFrame : Form
{
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;

    private readonly MappingConfig _cfg;
    private readonly Label _lbl;

    public RegionFrame(MappingConfig cfg)
    {
        _cfg = cfg;

        FormBorderStyle = FormBorderStyle.None;   // 无边框，Text 只作为可检索的窗口标识，不会显示出来
        Text = "SpenTabletRegionFrame";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Fuchsia;
        TransparencyKey = Color.Fuchsia;   // 内部完全透明，只留边框
        Opacity = 0.95;

        var border = Color.Orange;
        const int t = 3;
        var top = new Panel { Dock = DockStyle.Top, Height = t, BackColor = border };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = t, BackColor = border };
        var left = new Panel { Dock = DockStyle.Left, Width = t, BackColor = border };
        var right = new Panel { Dock = DockStyle.Right, Width = t, BackColor = border };

        _lbl = new Label
        {
            AutoSize = true,
            BackColor = border,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9f),
            Text = "S Pen 操作范围",
            Top = t,
            Left = t
        };

        Controls.Add(_lbl);
        Controls.Add(top);
        Controls.Add(bottom);
        Controls.Add(left);
        Controls.Add(right);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            return cp;
        }
    }

    /// <summary>
    /// 按配置刷新：位置跟随映射区域；可见性 = ShowRegion 且「非鼠标模式」。
    /// 用 BeginInvoke 投递，允许从网络线程安全调用（鼠标模式切换由手机端触发）。
    /// </summary>
    public void Sync(MappingConfig cfg)
    {
        if (IsDisposed) return;
        var r = cfg.GetActiveRect();
        bool show = cfg.ShowRegion && !cfg.IsMouseMode;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!show)
                {
                    if (Visible) Visible = false;
                    return;
                }
                if (r.w > 0 && r.h > 0)
                {
                    Bounds = new Rectangle(r.x, r.y, r.w, r.h);
                    _lbl.Text = $"S Pen 操作范围 {r.w}×{r.h}  ×{cfg.Scale:0.##}" +
                                "（单指滑动=移动，双指捏合=缩放）";
                }
                if (!Visible) Visible = true;
            }));
        }
        catch { }
    }

    // ---------- 静态管理 ----------
    private static RegionFrame? _current;

    /// <summary>只能在 UI 线程调用一次（放在 Application.Run 之前）。之后一律用 Sync。</summary>
    public static void Create(MappingConfig cfg)
    {
        if (_current == null || _current.IsDisposed)
        {
            _current = new RegionFrame(cfg);
            _current.Show();
        }
        _current.Sync(cfg);
    }

    /// <summary>配置变化时刷新（可从任意线程调用）。</summary>
    public static void Refresh(MappingConfig cfg) => _current?.Sync(cfg);
}
