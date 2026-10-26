using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 可选调试浮窗：实时显示收到的笔样（坐标/压感/倾斜/按键），
/// 让用户在“不装任何绘画软件”的情况下也能确认笔数据已到达 PC。
/// 在独立 STA 线程上运行 WinForms 消息循环，不阻塞接收/注入线程。
/// </summary>
internal sealed class DebugOverlay : Form
{
    private readonly Label _lbl;

    public DebugOverlay()
    {
        _lbl = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 11f),
            ForeColor = Color.Lime,
            BackColor = Color.FromArgb(20, 20, 20),
            Padding = new Padding(8)
        };
        Controls.Add(_lbl);
        Text = "SpenTablet 调试";
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Width = 300;
        Height = 170;
        _lbl.Text = "等待笔数据…";
    }

    public void UpdateSample(in PenSample s)
    {
        if (IsDisposed || !IsHandleCreated) return;
        string keys = (s.Barrel ? "侧键 " : "") + (s.Eraser ? "橡皮" : "");
        if (keys.Length == 0) keys = "-";
        string txt = $"状态 : {s.State}\n" +
                     $"坐标 : ({s.Px}, {s.Py})\n" +
                     $"压感 : {s.Pressure:F2}\n" +
                     $"倾斜 : ({s.TiltX:F0}, {s.TiltY:F0})\n" +
                     $"按键 : {keys}";
        BeginInvoke((Action)(() => { _lbl.Text = txt; }));
    }

    /// <summary>当前浮窗实例（由 Start 创建，Hide 关闭）。</summary>
    public static DebugOverlay? Current { get; private set; }

    public static DebugOverlay Start()
    {
        if (Current != null && !Current.IsDisposed) return Current;
        DebugOverlay? form = null;
        var t = new Thread(() =>
        {
            form = new DebugOverlay();
            Current = form;
            Application.Run(form);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        // 等待实例与窗口句柄就绪，确保后续 BeginInvoke 安全
        while (form == null || !form.IsHandleCreated) Thread.Sleep(10);
        return form;
    }

    /// <summary>关闭浮窗（可在设置里反复开关）。</summary>
    public static void HideOverlay()
    {
        var f = Current;
        Current = null;
        if (f == null || f.IsDisposed) return;
        try
        {
            if (f.IsHandleCreated)
                f.BeginInvoke((Action)(() => { f.Close(); f.Dispose(); }));
        }
        catch { /* 窗口已销毁则忽略 */ }
    }
}
