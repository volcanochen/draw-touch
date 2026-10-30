using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DrawTouchHost;

/// <summary>
/// 桌面笔迹叠加层（屏幕备注）：一个**真透明**、置顶、鼠标穿透的窗口，
/// 把手机 S Pen 的笔迹画在桌面上。
///
/// 坐标处理：笔坐标是相对操作框归一的，AddPoint 时按**当时的范围框**换算成
/// 绝对屏幕坐标并固定存下来 —— 之后框再缩放/移动，已有笔迹都留在原处、不被拉伸。
///
/// 颜色：**每一笔**各自记录落笔时的颜色（换色只影响之后的新笔画，已画的笔不变色）。
///
/// ── 为什么改成分层窗口（WS_EX_LAYERED）──────────────────────────
/// 以前是「黑色背景 + TransparencyKey=黑色」。问题：笔画用**抗锯齿**画，
/// 边缘像素是「笔色和黑色」的混合（比如半透明红 = 暗红），只有纯黑才会被抠掉，
/// 于是每一笔外面都糊着一圈**黑边** —— 在浅色桌面上非常扎眼。
/// 现在改成 32bpp 预乘 alpha 的分层窗口：边缘是真正的半透明，没有黑边。
///
/// 性能：整块虚拟屏（3072×1920）每帧全量推送太慢，所以维护一张持久化画布，
/// 只把**脏区那几行**拷进 DIB 再 UpdateLayeredWindow（与激光笔同一套做法）。
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

    /// <summary>笔宽（与手机端 strokeWidth=4 观感对齐）</summary>
    private const float PenWidth = 3.5f;

    /// <summary>一笔笔迹：落笔时的颜色 + 一串绝对屏幕坐标点</summary>
    private sealed class Stroke
    {
        public Color Color;
        public readonly List<PointF> Points = new();
        public Stroke(Color c) => Color = c;
    }

    private sealed class OverlayForm : Form
    {
        // ---- 分层窗口 API ----
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] bmiColors;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObj);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        // ---- 物理像素坐标（绕开 WinForms 的 DPI 缩放层）----
        // host 是 PMv2 感知，WinForms 却会对 Form.Bounds/Width/Height 做自己的缩放换算，
        // 导致「窗口实际尺寸（如 4992×1920）≠ 物理虚拟屏（如 6432×2662）」；
        // 而 UpdateLayeredWindow 的目标位置是物理屏幕坐标 —— 两个空间一混，
        // 笔迹就被推到屏幕外（混合 DPI 多屏下尤其必现）。所以窗口位置用 MoveWindow
        // 直接设物理矩形，画布尺寸与坐标换算全部以 GetWindowRect 的物理值为准。
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWRECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RAWRECT rect);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        /// <summary>窗口的物理像素矩形（每帧同步，WinForms 的 Left/Top 不可信）。</summary>
        private RAWRECT _phys = new() { left = 0, top = 0, right = 4992, bottom = 1920 };

        private int PhysW => Math.Max(1, _phys.right - _phys.left);
        private int PhysH => Math.Max(1, _phys.bottom - _phys.top);

        /// <summary>
        /// 把窗口设为整块虚拟屏幕的**物理**矩形，并同步画布。
        /// 句柄未创建时只记录目标（OnHandleCreated 会再调一次）。
        /// </summary>
        private void SyncPhysicalBounds()
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
            int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
            if (IsHandleCreated)
                MoveWindow(Handle, vx, vy, vw, vh, false);
            if (GetWindowRect(Handle, out var r) && r.right > r.left)
                _phys = r;
            else
                _phys = new RAWRECT { left = vx, top = vy, right = vx + vw, bottom = vy + vh };
            EnsureCanvas(PhysW, PhysH);
        }

        private static IntPtr GetWindowLongPtr(IntPtr h, int i) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(h, i) : GetWindowLongPtr64(h, i);

        private static IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v) =>
            IntPtr.Size == 8 ? SetWindowLongPtr64(h, i, v) : SetWindowLongPtr64(h, i, v);

        // ---- 笔迹数据 ----
        public readonly List<Stroke> Strokes = new();
        public readonly Pen Pen = new(Presets[0], PenWidth)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        private readonly SolidBrush DotBrush = new(Presets[0]);
        public Color CurrentColor = Presets[0];

        /// <summary>当前桌面范围框（虚拟屏幕坐标），仅用于把归一化笔坐标换算成绝对坐标。</summary>
        public int Rx, Ry, Rw = 1920, Rh = 1080;

        // ---- 分层画布 ----
        private Bitmap? _canvas;
        private Graphics? _cg;
        private IntPtr _dibHandle, _dibBits, _memDc;
        private int _dibW, _dibH;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            // 覆盖整块虚拟屏幕：范围框可能被拖到第二块显示器上。
            // 实际的物理矩形由 OnHandleCreated → SyncPhysicalBounds() 用 MoveWindow 设置
            //（WinForms 的 Bounds 在 PMv2 下会被它自己的 DPI 缩放改写，不可信）。
            ShowInTaskbar = false;
            TopMost = true;
            // 分层窗口不靠 BackColor / TransparencyKey 抠图，内容全由 UpdateLayeredWindow 推送
            BackColor = Color.Black;
            EnsureCanvas(PhysW, PhysH);
            // 画笔高频帧（60Hz+）合并到 20ms 一推：重画包围盒远比每帧推省
            _flush.Tick += (_, _) => { if (_dirty) PushBounds(); };
            _flush.Start();
        }

        /// <summary>鼠标穿透 + 不抢焦点 + 分层（点桌面时窗口本身无响应）</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TRANSPARENT = 0x20;
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TOOLWINDOW = 0x80;
                const int WS_EX_LAYERED = 0x80000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // WinForms 有时会重置扩展样式，这里再确认一次（激光笔踩过同一个坑）
            const int GWL_EXSTYLE = -20;
            const int WS_EX_LAYERED = 0x80000;
            var ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_LAYERED) == 0)
                SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
            // 先把窗口摆到物理虚拟屏矩形（WinForms 缩放过的 Bounds 在这里被纠正），
            // 再推送内容：分层窗口隐藏期间推送的内容会被丢掉
            SyncPhysicalBounds();
            PushBounds();
        }

        /// <summary>什么都不画：分层窗口的内容全部来自 UpdateLayeredWindow。</summary>
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        private void EnsureCanvas(int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (_canvas != null && _canvas.Width == w && _canvas.Height == h) return;
            _cg?.Dispose();
            _canvas?.Dispose();
            _canvas = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            _cg = Graphics.FromImage(_canvas);
            _cg.SmoothingMode = SmoothingMode.AntiAlias;
            _cg.CompositingMode = CompositingMode.SourceOver;
            ReleaseDib();
        }

        public void VisibleOn()
        {
            TopMost = true;
            Show();
            // Show 过程 WinForms 可能按自己的 DPI 逻辑改写窗口尺寸，再纠正一次
            SyncPhysicalBounds();
            PushBounds();
        }
        public void VisibleOff() => Hide();

        public void SetRect(int x, int y, int w, int h)
        {
            Rx = x; Ry = y; Rw = Math.Max(1, w); Rh = Math.Max(1, h);
            // 已有笔迹存的是绝对坐标，框变化不需要重绘（笔迹留在原处）
        }

        /// <summary>跟随虚拟屏幕物理尺寸（插拔显示器 / 改分辨率后调用）。</summary>
        public void ResizeToVirtualScreen()
        {
            int oldW = PhysW, oldH = PhysH;
            SyncPhysicalBounds();
            if (PhysW == oldW && PhysH == oldH) return;
            RedrawAll();
        }

        public void SetColor(Color c) { CurrentColor = c; Pen.Color = c; }

        /// <summary>屏幕物理坐标 → 画布坐标：减掉窗口物理原点（虚拟屏左/上边界可能为负）。</summary>
        private PointF ToCanvas(PointF scr) => new(scr.X - _phys.left, scr.Y - _phys.top);

        // UpdateLayeredWindow 的 pptDst/psize 语义是「**重设**窗口的位置与尺寸」——
        // 不能拿它做「整屏窗口 + 脏区局部推送」（每次都会把窗口挪到脏区、缩成脏区大小，
        // 之前的笔画全部丢失 —— 实测窗口被推到 (-1428,859)，笔迹全部落在屏幕外）。
        // 正确姿势：窗口 = 所有笔迹的**包围盒**。窗口位置/尺寸每次由 ULW 设为包围盒的
        // 屏幕矩形，内容 = 画布上包围盒区域。包围盒通常远小于整屏，拷贝与合成都便宜。
        private Rectangle _bb = Rectangle.Empty;      // 包围盒（画布坐标，含笔宽留白）
        private bool _dirty;                          // 画布有新内容待推送
        private readonly System.Windows.Forms.Timer _flush = new() { Interval = 20 };

        /// <summary>把画布坐标点纳入包围盒（带笔宽留白，笔帽是圆的要外扩一点）。</summary>
        private void ExtendBounds(PointF cp)
        {
            int pad = (int)Math.Ceiling(PenWidth) + 4;
            var r = new Rectangle((int)cp.X - pad, (int)cp.Y - pad, pad * 2 + 1, pad * 2 + 1);
            _bb = _bb.IsEmpty ? r : Rectangle.Union(_bb, r);
        }

        /// <summary>重算全部笔迹的包围盒（撤销 / 清空 / 换屏后调用）。</summary>
        private void RecalcBounds()
        {
            _bb = Rectangle.Empty;
            foreach (var s in Strokes)
            {
                foreach (var p in s.Points)
                    ExtendBounds(ToCanvas(p));
            }
        }

        public void AddPoint(float nx, float ny, PenState st)
        {
            if (_cg == null || _canvas == null) return;
            var pt = new PointF(Rx + nx * Rw, Ry + ny * Rh);
            var cp = ToCanvas(pt);
            try
            {
                switch (st)
                {
                    case PenState.Down:
                        Strokes.Add(new Stroke(CurrentColor) { Points = { pt } });
                        DotBrush.Color = CurrentColor;
                        _cg.FillEllipse(DotBrush, cp.X - PenWidth / 2 - 1, cp.Y - PenWidth / 2 - 1,
                            PenWidth + 2, PenWidth + 2);
                        break;
                    case PenState.Move:
                        if (Strokes.Count > 0)
                        {
                            var last = Strokes[^1];
                            var prev = last.Points.Count > 0 ? ToCanvas(last.Points[^1]) : cp;
                            last.Points.Add(pt);
                            Pen.Color = last.Color;
                            _cg.DrawLine(Pen, prev, cp);
                        }
                        break;
                    case PenState.Up:
                        if (Strokes.Count > 0 && Strokes[^1].Points.Count == 1)
                        {
                            // 单点笔迹也要看得见
                            var s = Strokes[^1];
                            var p = ToCanvas(s.Points[0]);
                            DotBrush.Color = s.Color;
                            _cg.FillEllipse(DotBrush, p.X - PenWidth / 2 - 1, p.Y - PenWidth / 2 - 1,
                                PenWidth + 2, PenWidth + 2);
                        }
                        break;
                }
                // 扩大包围盒并标记待推：DOWN/MOVE 由 20ms 定时器合并推送，
                // 抬笔立即推一次（笔停在哪里就要立刻看到哪里）
                ExtendBounds(cp);
                _dirty = true;
                if (st == PenState.Up) PushBounds();
            }
            catch (Exception ex)
            {
                AppLog.Error($"[NoteOverlay] 画笔迹异常：{ex.GetType().Name} {ex.Message}");
            }
        }

        public void UndoLast()
        {
            if (Strokes.Count > 0) { Strokes.RemoveAt(Strokes.Count - 1); RedrawAll(); }
        }

        public void ClearAll()
        {
            Strokes.Clear();
            RedrawAll();
        }

        /// <summary>清空画布并按当前笔画重画一遍（撤销 / 清空 / 换屏后调用）。</summary>
        private void RedrawAll()
        {
            if (_cg == null || _canvas == null) { PushBounds(); return; }
            _cg.Clear(Color.Transparent);
            foreach (var s in Strokes)
            {
                if (s.Points.Count == 0) continue;
                if (s.Points.Count == 1)
                {
                    var p = ToCanvas(s.Points[0]);
                    DotBrush.Color = s.Color;
                    _cg.FillEllipse(DotBrush, p.X - PenWidth / 2 - 1, p.Y - PenWidth / 2 - 1,
                        PenWidth + 2, PenWidth + 2);
                }
                else
                {
                    var pts = new PointF[s.Points.Count];
                    for (int i = 0; i < pts.Length; i++) pts[i] = ToCanvas(s.Points[i]);
                    Pen.Color = s.Color;
                    _cg.DrawLines(Pen, pts);
                }
            }
            Pen.Color = CurrentColor;
            var oldBb = _bb;   // 撤销/清空后被擦掉的区域也要重推（推成透明）
            RecalcBounds();
            if (!oldBb.IsEmpty)
                _bb = _bb.IsEmpty ? oldBb : Rectangle.Union(_bb, oldBb);
            PushBounds();
        }

        /// <summary>
        /// 把「笔迹包围盒」区域的内容经 DIB 用 UpdateLayeredWindow 推上屏幕
        /// （窗口始终 = 虚拟屏矩形不变；与 Highlighter 同一套脏区推送）。
        /// </summary>
        private void PushBounds()
        {
            _dirty = false;
            var canvas = _canvas;
            if (canvas == null || !IsHandleCreated || !Visible) return;
            var bb = Rectangle.Intersect(_bb, new Rectangle(0, 0, canvas.Width, canvas.Height));
            if (bb.Width <= 0 || bb.Height <= 0) return;   // 没有笔迹：不推（窗口内容为空）
            try
            {
                // 与 HighlighterOverlay.PushRegion 完全同一套做法（激光笔实测稳定）：
                // DIB = 整画布大小，脏区按绝对偏移拷入，ULW 从 DIB 的脏区偏移处取内容。
                // （之前「窗口=包围盒重定位」的写法实测不显示：位置对了尺寸却不对，
                //   ULW 推的内容整个落空 —— 别再绕回去。）
                EnsureDib(canvas.Width, canvas.Height);

                var data = canvas.LockBits(new Rectangle(0, 0, canvas.Width, canvas.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                int rowBytes = bb.Width * 4;
                var row = new byte[rowBytes];
                for (int y = bb.Y; y < bb.Y + bb.Height; y++)
                {
                    IntPtr src = (IntPtr)((long)data.Scan0 + (long)y * data.Stride + (long)bb.X * 4);
                    IntPtr dst = (IntPtr)((long)_dibBits + (long)y * canvas.Width * 4 + (long)bb.X * 4);
                    Marshal.Copy(src, row, 0, rowBytes);
                    Marshal.Copy(row, 0, dst, rowBytes);
                }
                canvas.UnlockBits(data);

                IntPtr screenDc = GetDC(IntPtr.Zero);
                // 目标位置 = 窗口物理原点 + 画布坐标（画布=虚拟屏大小，窗口=虚拟屏矩形）
                var ptDst = new POINT { x = _phys.left + bb.X, y = _phys.top + bb.Y };
                var ptSrc = new POINT { x = bb.X, y = bb.Y };
                var sz = new SIZE { cx = bb.Width, cy = bb.Height };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                if (!UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref sz, _memDc, ref ptSrc, 0, ref blend, ULW_ALPHA))
                {
                    int err = Marshal.GetLastWin32Error();
                    AppLog.Warn($"[NoteOverlay] UpdateLayeredWindow 失败 err={err} 包围盒={bb}");
                }
                ReleaseDC(IntPtr.Zero, screenDc);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[NoteOverlay] 推送画面失败：{ex.GetType().Name} {ex.Message}");
            }
        }

        /// <summary>
        /// 建立（并复用）与画布同尺寸的 32bpp DIB，作为分层窗口的源位图
        /// （与 Highlighter 相同：脏区按绝对偏移拷入，ULW 从偏移处取内容）。
        /// </summary>
        private void EnsureDib(int w, int h)
        {
            if (_dibHandle != IntPtr.Zero && _dibW == w && _dibH == h) return;
            ReleaseDib();
            IntPtr screenDc = GetDC(IntPtr.Zero);
            var bi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,          // 负数 = 自上而下，和画布一致
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0      // BI_RGB
                },
                bmiColors = new uint[256]
            };
            _dibHandle = CreateDIBSection(screenDc, ref bi, 0, out _dibBits, IntPtr.Zero, 0);
            ReleaseDC(IntPtr.Zero, screenDc);
            if (_dibHandle == IntPtr.Zero)
            {
                AppLog.Error("[NoteOverlay] CreateDIBSection 失败，桌面笔迹无法显示");
                return;
            }
            _memDc = CreateCompatibleDC(IntPtr.Zero);
            SelectObject(_memDc, _dibHandle);
            _dibW = w;
            _dibH = h;
            if (_dibBits != IntPtr.Zero)
            {
                var zero = new byte[Math.Min(w * 4 * 64, 1 << 20)];
                for (int off = 0; off < w * h * 4; off += zero.Length)
                {
                    int n = Math.Min(zero.Length, w * h * 4 - off);
                    Marshal.Copy(zero, 0, (IntPtr)((long)_dibBits + off), n);
                }
            }
            AppLog.Info($"[NoteOverlay] 分层画布就绪 {w}×{h}（真透明，无黑边）");
        }

        private void ReleaseDib()
        {
            if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_dibHandle != IntPtr.Zero) { DeleteObject(_dibHandle); _dibHandle = IntPtr.Zero; }
            _dibBits = IntPtr.Zero;
            _dibW = _dibH = 0;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Pen.Dispose();
            DotBrush.Dispose();
            _cg?.Dispose();
            _canvas?.Dispose();
            ReleaseDib();
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
