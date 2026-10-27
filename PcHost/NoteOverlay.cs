using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 桌面笔迹叠加层（屏幕备注）：一个透明、置顶、鼠标穿透的窗口，
/// 把手机 S Pen 的笔迹直接画在桌面的**固定位置**（不随操作范围框移动）。
/// 在自己的 STA 线程上跑消息循环，外部通过 Post 异步调用（AddPoint 高频写入不阻塞 TCP 线程）。
/// </summary>
public sealed class NoteOverlay : IDisposable
{
    private Thread? _thread;
    private OverlayForm? _form;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _gate = new();

    private sealed class OverlayForm : Form
    {
        // 每一笔 = 一串点（屏幕绝对坐标）
        public readonly List<List<Point>> Strokes = new();
        private readonly Pen _pen = new(Color.FromArgb(230, 255, 60, 60), 3.5f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            var b = Screen.PrimaryScreen.Bounds;
            StartPosition = FormStartPosition.Manual;
            Bounds = b;
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

        public void AddPoint(float nx, float ny, PenState st)
        {
            var b = Bounds;
            var pt = new Point(b.X + (int)(nx * b.Width), b.Y + (int)(ny * b.Height));
            switch (st)
            {
                case PenState.Down:
                    Strokes.Add(new List<Point> { pt });
                    break;
                case PenState.Move:
                    if (Strokes.Count > 0) { Strokes[^1].Add(pt); }
                    break;
                case PenState.Up:
                    if (Strokes.Count > 0 && Strokes[^1].Count == 1) Strokes[^1].Add(pt); // 单点笔迹也可见
                    return; // Up 不需要重绘
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
            foreach (var s in Strokes)
            {
                if (s.Count == 1) e.Graphics.FillEllipse(_pen.Brush, s[0].X - 2, s[0].Y - 2, 5, 5);
                else if (s.Count >= 2) e.Graphics.DrawLines(_pen, s.ToArray());
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _pen.Dispose();
            base.OnFormClosed(e);
        }
    }

    public NoteOverlay()
    {
        _thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            _form = new OverlayForm();
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

    public void Dispose()
    {
        try
        {
            lock (_gate)
            {
                var f = _form;
                if (f != null && !f.IsDisposed)
                {
                    if (f.IsHandleCreated) f.BeginInvoke(() => { f.Close(); });
                    else f.Close();
                }
                _form = null;
            }
        }
        catch { }
    }
}
