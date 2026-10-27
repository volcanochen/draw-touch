using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 桌面笔迹叠加层（屏幕备注）：一个透明、置顶、鼠标穿透的窗口，
/// 把手机 S Pen 的笔迹画在**桌面操作范围框内**（与数位板绘制逻辑一致：
/// 笔坐标本来就是相对操作框归一的，框缩放/移动时笔迹跟着走）。
///
/// 覆盖整块虚拟屏幕（多屏也能画）。在自己的 STA 线程上跑消息循环，
/// 外部通过 Post 异步调用（AddPoint 高频写入不阻塞 TCP 线程）。
///
/// 笔迹颜色由**手机端**左侧的浮出色板选择（索引随 NOTE 帧下发），PC 只负责按索引换色。
/// 笔迹层本身是鼠标穿透的（不接收点击，也不打扰当前应用）。
/// </summary>
public sealed class NoteOverlay : IDisposable
{
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

    private sealed class OverlayForm : Form
    {
        // 每一笔 = 一串点，存**范围框内的归一化坐标**（绘制时才乘当前框矩形 → 笔迹永远跟着框）
        public readonly List<List<PointF>> Strokes = new();
        public readonly Pen Pen = new(Presets[0], 3.5f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        /// <summary>当前桌面范围框（虚拟屏幕坐标）。由 TCP 线程每次派发前同步。</summary>
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
            if (x == Rx && y == Ry && w == Rw && h == Rh) return;
            Rx = x; Ry = y; Rw = Math.Max(1, w); Rh = Math.Max(1, h);
            if (Strokes.Count > 0) Invalidate();  // 框动了 → 已有笔迹跟着重绘
        }

        public void SetColor(Color c)
        {
            Pen.Color = c;
            Invalidate();
        }

        public void AddPoint(float nx, float ny, PenState st)
        {
            switch (st)
            {
                case PenState.Down:
                    Strokes.Add(new List<PointF> { new(nx, ny) });
                    break;
                case PenState.Move:
                    if (Strokes.Count > 0) Strokes[^1].Add(new PointF(nx, ny));
                    break;
                case PenState.Up:
                    if (Strokes.Count > 0 && Strokes[^1].Count == 1) Strokes[^1].Add(new PointF(nx, ny)); // 单点笔迹也可见
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
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // 窗口原点可能是负数（虚拟屏幕左/上边界），绘制前换算到客户区坐标
            float ox = Rx - Left;
            float oy = Ry - Top;
            foreach (var s in Strokes)
            {
                if (s.Count == 0) continue;
                if (s.Count == 1)
                {
                    var p = s[0];
                    e.Graphics.FillEllipse(Pen.Brush, ox + p.X * Rw - 2.5f, oy + p.Y * Rh - 2.5f, 5, 5);
                }
                else
                {
                    var pts = new PointF[s.Count];
                    for (int i = 0; i < s.Count; i++)
                        pts[i] = new PointF(ox + s[i].X * Rw, oy + s[i].Y * Rh);
                    e.Graphics.DrawLines(Pen, pts);
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Pen.Dispose();
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
            try { f.BeginInvoke(() => a(f)); } catch { }
        }
    }

    public void Show() => Post(f => f.VisibleOn());

    public void Hide() => Post(f => f.VisibleOff());

    public void Undo() => Post(f => f.UndoLast());
    public void Clear() => Post(f => f.ClearAll());
    public void AddPoint(float nx, float ny, PenState st) => Post(f => f.AddPoint(nx, ny, st));
    public void SetRect(int x, int y, int w, int h) => Post(f => f.SetRect(x, y, w, h));

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
