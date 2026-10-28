using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 桌面笔迹叠加层（屏幕备注）：一个透明、置顶、鼠标穿透的窗口，
/// 把手机 S Pen 的笔迹画在桌面上。
///
/// 坐标处理：笔坐标是相对操作框归一的，AddPoint 时按**当时的范围框**换算成
/// 绝对屏幕坐标并固定存下来 —— 之后框再缩放/移动，已有笔迹都留在原处、不被拉伸。
///
/// 颜色：**每一笔**各自记录落笔时的颜色（换色只影响之后的新笔画，已画的笔不变色）。
/// 颜色索引由手机端左侧色板选择后下发。
///
/// 覆盖整块虚拟屏幕（多屏也能画）。在自己的 STA 线程上跑消息循环，
/// 外部通过 Post 异步调用（AddPoint 高频写入不阻塞 TCP 线程）。
/// 笔迹层本身是鼠标穿透的（不接收点击，也不打扰当前应用）。
/// </summary>
public sealed class NoteOverlay : IDisposable
{
    /// <summary>
    /// GDI+ 全局锁。GDI+（System.Drawing）**不是线程安全的**：
    /// 截图线程（TcpPenServer：CopyFromScreen / PNG 编码）与这里的绘制线程并发调用 GDI+，
    /// 偶发会把 GDI+ 内部状态写坏 → AccessViolation / 参数无效，进程直接消失且不留 WER 报告。
    /// 截图与画笔迹这两条最重的 GDI+ 路径都拿这把锁，串行化之后就稳了。
    /// </summary>
    internal static readonly object GdiLock = new();

    private Thread? _thread;
    private OverlayForm? _form;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _gate = new();

    /// <summary>预设笔迹颜色（顺序与手机端色板一致；颜色索引由手机端下发）</summary>
    private static readonly Color[] Presets =
    {
        Color.FromArgb(255, 60, 60),    // 0 红
        Color.FromArgb(255, 190, 40),   // 1 橙
        Color.FromArgb(250, 240, 60),   // 2 黄
        Color.FromArgb(60, 220, 110),   // 3 绿
        Color.FromArgb(80, 190, 255),   // 4 蓝
        Color.FromArgb(200, 110, 255),  // 5 紫
        Color.FromArgb(250, 250, 250),  // 6 白
    };

    /// <summary>一笔笔迹：落笔时的颜色 + 一串绝对屏幕坐标点</summary>
    private sealed class Stroke
    {
        public Color Color;
        public readonly List<PointF> Points = new();
        public Stroke(Color c) => Color = c;
    }

    private sealed class OverlayForm : Form
    {
        // 每一笔 = 一串**绝对屏幕坐标**（落笔时按当时的范围框换算并固定下来，
        // 之后框缩放/移动都不再影响它 —— 笔迹留在桌面原处）
        public readonly List<Stroke> Strokes = new();
        public readonly Pen Pen = new(Presets[0], 3.5f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        /// <summary>单点笔迹的实心圆（不直接用 Pen.Brush：那个属性每次取都会新建 GDI 笔刷，画多了会句柄泄漏）</summary>
        private readonly SolidBrush DotBrush = new(Presets[0]);
        /// <summary>当前笔迹颜色（新起一笔时采用；已有笔画的颜色不变）</summary>
        public Color CurrentColor = Presets[0];

        /// <summary>当前桌面范围框（虚拟屏幕坐标），仅用于把归一化笔坐标换算成绝对坐标。</summary>
        public int Rx, Ry, Rw = 1920, Rh = 1080;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            // 覆盖整块虚拟屏幕：范围框可能被拖到第二块显示器上
            var v = SystemInformation.VirtualScreen;
            Bounds = new Rectangle(v.Left, v.Top, v.Width, v.Height);
            BackColor = Color.Black;
            TransparencyKey = Color.Black;      // 全透明，只显示笔迹
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
        }

        /// <summary>鼠标穿透 + 不抢焦点（点桌面时窗口本身无响应）</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TRANSPARENT = 0x20;
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TOOLWINDOW = 0x80;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void VisibleOn() { TopMost = true; Show(); }
        public void VisibleOff() => Hide();

        public void SetRect(int x, int y, int w, int h)
        {
            Rx = x; Ry = y; Rw = Math.Max(1, w); Rh = Math.Max(1, h);
            // 已有笔迹存的是绝对坐标，框变化不需要重绘（笔迹留在原处）
        }

        /// <summary>
        /// 跟随虚拟屏幕尺寸（插拔显示器 / 改分辨率后调用）。
        /// 笔迹存的是绝对坐标，窗口挪动或变大小都不影响已画的笔迹。
        /// </summary>
        public void ResizeToVirtualScreen()
        {
            var v = SystemInformation.VirtualScreen;
            if (v.Width <= 0 || v.Height <= 0) return;
            if (Bounds == new Rectangle(v.Left, v.Top, v.Width, v.Height)) return;
            Bounds = new Rectangle(v.Left, v.Top, v.Width, v.Height);
        }

        /// <summary>切换「之后新笔画」的颜色；已画好的笔画保持各自的颜色。</summary>
        public void SetColor(Color c)
        {
            CurrentColor = c;
            Pen.Color = c;
        }

        public void AddPoint(float nx, float ny, PenState st)
        {
            // 归一化笔坐标 → 绝对屏幕坐标（按落笔时刻的范围框换算一次，之后固定不变）
            var pt = new PointF(Rx + nx * Rw, Ry + ny * Rh);
            switch (st)
            {
                case PenState.Down:
                    Strokes.Add(new Stroke(CurrentColor) { Points = { pt } });
                    break;
                case PenState.Move:
                    if (Strokes.Count > 0) Strokes[^1].Points.Add(pt);
                    break;
                case PenState.Up:
                    if (Strokes.Count > 0 && Strokes[^1].Points.Count == 1) Strokes[^1].Points.Add(pt); // 单点笔迹也可见
                    Invalidate();
                    return;
                default:
                    return; // Hover 忽略
            }
            Invalidate();
        }

        public void UndoLast()
        {
            if (Strokes.Count > 0) { Strokes.RemoveAt(Strokes.Count - 1); Invalidate(); }
        }

        public void ClearAll()
        {
            Strokes.Clear();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // 绘制异常绝不能抛出去：这里是消息循环线程，抛出去就是整个进程消失
            try
            {
                lock (GdiLock)
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    // 窗口原点可能是负数（虚拟屏幕左/上边界），绘制前换算到客户区坐标
                    float ox = -Left;
                    float oy = -Top;
                    // 每笔用**自己的颜色**画（换色不会把已画的笔一起改掉）
                    foreach (var s in Strokes)
                    {
                        if (s.Points.Count == 0) continue;
                        Pen.Color = s.Color;
                        if (s.Points.Count == 1)
                        {
                            var p = s.Points[0];
                            DotBrush.Color = s.Color;
                            e.Graphics.FillEllipse(DotBrush, ox + p.X - 2.5f, oy + p.Y - 2.5f, 5, 5);
                        }
                        else
                        {
                            var pts = new PointF[s.Points.Count];
                            for (int i = 0; i < s.Points.Count; i++)
                                pts[i] = new PointF(ox + s.Points[i].X, oy + s.Points[i].Y);
                            e.Graphics.DrawLines(Pen, pts);
                        }
                    }
                    Pen.Color = CurrentColor;
                }
            }
            catch (Exception ex)
            {
                // 偶发一次就记一次，但不让它炸进程
                AppLog.Error($"[NoteOverlay] 绘制异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Pen.Dispose();
            DotBrush.Dispose();
            base.OnFormClosed(e);
        }
    }

    public NoteOverlay()
    {
        _thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            _form = new OverlayForm();
            // Form 构造时并不创建窗口句柄，而外部 Post 依赖句柄存在才能 BeginInvoke；
            // 这里在自己线程内先把句柄建出来（跨线程建句柄会让窗口挂错线程）。
            _ = _form.Handle;
            _ready.Set();
            Application.Run(_form); // 消息循环驱动窗口（窗口平时 Hidden）
        })
        { IsBackground = true, Name = "NoteOverlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    private void Post(Action<OverlayForm> a)
    {
        lock (_gate)
        {
            var f = _form;
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
            try
            {
                // 回调在消息循环线程上执行：里面再抛异常就是「进程悄悄消失」，必须兜住
                f.BeginInvoke(() =>
                {
                    try { a(f); }
                    catch (Exception ex)
                    {
                        AppLog.Error($"[NoteOverlay] 回调异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
                    }
                });
            }
            catch { }
        }
    }

    public void Show() => Post(f => f.VisibleOn());

    public void Hide() => Post(f => f.VisibleOff());

    public void Undo() => Post(f => f.UndoLast());
    public void Clear() => Post(f => f.ClearAll());
    public void AddPoint(float nx, float ny, PenState st) => Post(f => f.AddPoint(nx, ny, st));
    public void SetRect(int x, int y, int w, int h) => Post(f => f.SetRect(x, y, w, h));

    /// <summary>虚拟屏幕尺寸变了 → 叠加层跟着重建（否则笔迹层会盖不满新屏）。</summary>
    public void ResizeToVirtualScreen() => Post(f => f.ResizeToVirtualScreen());

    /// <summary>按索引切换笔迹颜色（索引由手机端色板下发）</summary>
    public void SetColorIndex(int idx)
    {
        if (idx < 0 || idx >= Presets.Length) return;
        Post(f => f.SetColor(Presets[idx]));
    }

    public void Dispose()
    {
        try
        {
            lock (_gate)
            {
                var f = _form;
                if (f != null && !f.IsDisposed)
                {
                    if (f.IsHandleCreated) f.BeginInvoke(() => f.Close());
                    else f.Close();
                }
                _form = null;
            }
        }
        catch { }
    }
}
