using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SpenTabletHost;

/// <summary>
/// 激光笔叠加层：把桌面指针变成激光笔。
///
/// 做法：一个覆盖整块虚拟屏幕的**分层窗口**（每像素 alpha），
/// 定时（40Hz）读取系统光标位置，光标走到哪就在哪画一段半透明粗线 ——
/// 就是激光笔划过纸面的效果。系统箭头同时被隐藏（换成一枚几乎看不见的空白光标），
/// 所以看起来就是「指针变成了激光笔」。
///
/// 与桌面备注（NoteOverlay）一样：鼠标穿透、不抢焦点、自己的 STA 线程跑消息循环。
/// 画布用 Format32bppPArgb（**预乘 alpha**）：UpdateLayeredWindow 要求预乘格式，
/// 用直 alpha 会被 Windows 再乘一次，颜色偏暗。
/// </summary>
public sealed class HighlighterOverlay : IDisposable
{
    private Thread? _thread;
    private HlForm? _form;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _gate = new();

    /// <summary>开启/关闭激光笔。</summary>
    public void SetEnabled(bool on) => Post(f => f.SetEnabled(on));

    /// <summary>切换激光笔颜色（索引见 HlForm.Presets）。</summary>
    public void SetColorIndex(int idx) => Post(f => f.SetColorIndex(idx));

    /// <summary>清掉已画的激光笔痕迹。</summary>
    public void Clear() => Post(f => f.ClearTrail());

    /// <summary>虚拟屏幕尺寸变了（插拔显示器）→ 画布跟着重建。</summary>
    public void ResizeToVirtualScreen() => Post(f => f.SyncBounds());

    /// <summary>启动时清理上次异常退出留下的激光笔游标。</summary>
    public void CleanupLeftoverCursorScheme() => Post(f => f.CleanupLeftoverCursorScheme());

    public HighlighterOverlay()
    {
        _thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            _form = new HlForm();
            _ = _form.Handle;          // 先建出句柄，跨线程 BeginInvoke 才安全
            // 分层窗口必须**可见**，UpdateLayeredWindow 推的内容才会显示出来；
            // 显示后立刻按物理像素对齐虚拟屏幕（WinForms 的 Show() 会把窗口摆回默认位置）
            _form.Show();
            _form.SyncBounds();
            _form.CleanupLeftoverCursorScheme();
            _ready.Set();
            Application.Run(_form);
        }) { IsBackground = true, Name = "HighlighterOverlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    private void Post(Action<HlForm> a)
    {
        lock (_gate)
        {
            var f = _form;
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
            try
            {
                f.BeginInvoke(() =>
                {
                    try { a(f); }
                    catch (Exception ex)
                    {
                        AppLog.Error($"[Highlighter] 回调异常：{ex.GetType().Name} {ex.Message}");
                    }
                });
            }
            catch { }
        }
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

    // ================= 窗口本体 =================
    private sealed class HlForm : Form
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
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool SetCursor(IntPtr hCursor);

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CURSORINFO pci);

        // ---- 低级鼠标钩子：系统每次鼠标移动都会把光标重置成窗口自己的，
        // 所以要在鼠标事件里**重新贴上**激光笔光标（这是替换系统光标的标准做法）----
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MOUSEWHEEL = 0x020A;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static HookProc? _hookProc;      // 必须持有委托引用，否则会被 GC 回收
        private IntPtr _hook;

        // ---- 「有意义」的系统光标：文本 I 型、手型、等待、调整大小……
        // 【已废弃】2026-10-29 按用户要求，激光笔开启后**所有位置都保持激光笔图标**
        // （包括编辑框的 I 型），这套「保留光标」机制随之移除。
        // 唯一保留的例外：光标被应用隐藏时不去强拉出来。

        /// <summary>
        /// 只要激光笔开着，就把当前光标换成激光笔 —— **所有位置都换**，包括编辑框的 I 型。
        /// 用户要求：激光笔演示时走到哪儿都该是激光笔，不能一进输入框就变回文字光标。
        /// 唯一例外：光标被应用隐藏（全屏游戏/隐藏光标的场景）时保持隐藏，不去强拉出来。
        /// （2026-10-29 之前这里是「只换普通箭头」，编辑框/链接上会变回 I 型/手型。）
        /// </summary>
        private void ApplyLaserIfArrow()
        {
            var laser = _cursor?.Handle ?? IntPtr.Zero;
            if (laser == IntPtr.Zero) return;
            var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci)) return;
            if (ci.hCursor == IntPtr.Zero) return;          // 光标被隐藏（全屏游戏/隐藏光标的应用）
            if (ci.hCursor == laser) return;                // 已经是激光笔，不用重复设
            SetCursor(laser);
            // 自证：只有**真正替换成功**那一次才校验（在钩子线程里 SetCursor 才生效；
            // 叠加层线程调用通常是 no-op，在那里校验只会刷出误导日志）
            if (!_cursorVerified)
            {
                _cursorVerified = true;
                var ci2 = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(ref ci2))
                {
                    bool ok = ci2.hCursor == laser;
                    AppLog.Info($"[Highlighter] 系统光标 → 激光笔 " +
                                $"{(ok ? "已生效 ✓" : "尚未替换（当前句柄不同）")}" +
                                "｜所有位置（含编辑框）都保持激光笔图标");
                }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        // ---- 替换系统箭头光标 ----
        // 光标句柄一旦交给 SetSystemCursor，系统就会一直用它；
        // 关掉激光笔时用事先复制下来的原件还原，不留痕迹。
        private const int IDC_ARROW = 32512;
        private const int OCR_CURS_FROM_TYPE = 2;
        private const int SPI_SETCURSORS = 0x0057;
        private const int SPIF_SENDCHANGE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetSystemCursor(IntPtr hcur, int iType, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        private bool _sysCursorReplaced;
        /// <summary>原来的箭头方案（注册表里 "Arrow" 的值），关掉时原样写回</summary>
        private string? _origCursorScheme;
        private string? _curFilePath;

        /// <summary>
        /// 启动时的清理：上次如果异常退出（被强杀/崩溃），注册表里可能还留着我们的激光笔 .cur，
        /// 那会让用户的箭头一直是激光笔。这里发现残留就还原掉。
        /// </summary>
        public void CleanupLeftoverCursorScheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
                if (key == null) return;
                var v = key.GetValue("Arrow") as string;
                if (string.IsNullOrEmpty(v)) return;
                if (!v.Contains("SpenTablet-marker", StringComparison.OrdinalIgnoreCase)) return;
                // 还原成系统自带的那支箭头
                var aero = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Cursors", "arrow_eoa.cur");
                using var wkey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", true);
                if (wkey != null)
                {
                    wkey.SetValue("Arrow", File.Exists(aero) ? aero : v,
                        Microsoft.Win32.RegistryValueKind.String);
                    SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE);
                    AppLog.Info("[Highlighter] 检测到上次遗留的激光笔游标，已还原系统箭头");
                }
                try { if (File.Exists(v)) File.Delete(v); } catch { }
            }
            catch { }
        }

        /// <summary>把系统箭头换成激光笔。
        ///
        /// 走**游标方案注册表**（HKCU\Control Panel\Cursors\Arrow + SPI_SETCURSORS）——
        /// 这是系统真正支持的换箭头机制。
        /// （SetSystemCursor 在 Win11 上对 IDC_ARROW 已经失效：连拿系统箭头替换自己都返回 false。）
        /// </summary>
        private void ReplaceSystemCursor()
        {
            if (_sysCursorReplaced || _cursor == null) return;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", true);
                if (key == null)
                {
                    AppLog.Warn("[Highlighter] 打不开游标方案注册表键");
                    return;
                }
                _origCursorScheme = key.GetValue("Arrow") as string;

                // 把光标写成 .cur 文件（Windows 的游标方案只认 .cur）
                IntPtr loaded = MarkerCursor.LoadAsSystemCursor(_cursor.Image, _cursor.HotspotX, _cursor.HotspotY, out _curFilePath);
                if (loaded == IntPtr.Zero) return;

                key.SetValue("Arrow", _curFilePath!, Microsoft.Win32.RegistryValueKind.String);
                SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE);
                _sysCursorReplaced = true;
                AppLog.Info("[Highlighter] 系统箭头 → 激光笔（游标方案已生效）");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[Highlighter] 替换系统光标失败：{ex.Message}");
            }
        }

        /// <summary>还原系统箭头光标。</summary>
        private void RestoreSystemCursor()
        {
            if (!_sysCursorReplaced) return;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", true);
                if (key != null)
                {
                    if (!string.IsNullOrEmpty(_origCursorScheme))
                        key.SetValue("Arrow", _origCursorScheme, Microsoft.Win32.RegistryValueKind.String);
                    else
                        key.DeleteValue("Arrow", false);
                }
                SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE);
                _sysCursorReplaced = false;
                AppLog.Info("[Highlighter] 系统箭头已还原");
                try { if (_curFilePath != null && File.Exists(_curFilePath)) File.Delete(_curFilePath); } catch { }
                _curFilePath = null;
            }
            catch { }
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
            uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int l; public int t; public int r; public int b; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] bmiColors;
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

        // 分层窗口的源位图：**自己建 32bpp DIB**，
        // 再把画布（Format32bppPArgb = BGRA 预乘）逐行拷进去。
        // 不用 Bitmap.GetHbitmap：那条路经常把 alpha 丢掉，
        // 结果 UpdateLayeredWindow 推上去的是一张全透明的图 —— 屏幕上一个像素都不变。
        private IntPtr _dibBits;
        private IntPtr _dibHandle;
        private IntPtr _memDc;
        private int _dibW, _dibH;

        /// <summary>激光笔颜色（索引由手机端选择）。</summary>
        private static readonly Color[] Presets =
        {
            Color.FromArgb(255, 235, 60),   // 0 黄
            Color.FromArgb(80, 240, 120),   // 1 绿
            Color.FromArgb(255, 110, 190),  // 2 粉
            Color.FromArgb(90, 190, 255),   // 3 蓝
        };

        /// <summary>笔迹不透明度：真激光笔大约 30%~40% 覆盖。</summary>
        private const int TrailAlpha = 92;
        /// <summary>笔迹粗细（桌面像素）</summary>
        private const float TrailWidth = 26f;

        private Bitmap? _canvas;
        private Graphics? _cg;
        private readonly Pen _pen;
        private readonly SolidBrush _dot;
        private int _colorIdx;
        private bool _enabled;
        private bool _hasLast;
        private PointF _last;
        /// <summary>轮询光标位置的定时器（WinForms 定时器：跑在窗口自己的 UI 线程上）</summary>
        private System.Windows.Forms.Timer? _timer;
        /// <summary>激光笔光标（把系统箭头换成马克笔造型）</summary>
        private MarkerCursor? _cursor;
        /// <summary>调试计数：每 10 段笔迹打一条日志</summary>
        private int _dbg;
        /// <summary>是否已确认过系统光标已换成激光笔</summary>
        private bool _cursorVerified;

        public HlForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            // 关键：**关掉 WinForms 的自动缩放**。
            // 进程是 PerMonitorV2 DPI 感知，Form.Bounds 用的是逻辑像素，
            // 而画布和光标坐标都是物理像素 —— 两者不一致时窗口位置会被缩放偏，
            // 笔迹就画到画布外面（等于没画），清除也清不掉。
            AutoScaleMode = AutoScaleMode.None;
            // 分层窗口的内容全部来自 UpdateLayeredWindow，窗口本身不画任何东西
            SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque, true);
            BackColor = Color.Black;

            _pen = new Pen(Color.FromArgb(TrailAlpha, Presets[0]), TrailWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            _dot = new SolidBrush(Color.FromArgb(TrailAlpha, Presets[0]));
            RebuildCanvas();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TRANSPARENT = 0x20;
                const int WS_EX_LAYERED = 0x80000;
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TOOLWINDOW = 0x80;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        /// <summary>什么都不画：内容由 UpdateLayeredWindow 逐像素推送。</summary>
        protected override void OnPaint(PaintEventArgs e) { }

        private const int WM_DPICHANGED = 0x02E0;

        /// <summary>
        /// 忽略 DPI 变化消息。
        /// 这个窗口覆盖整块虚拟屏幕，一旦鼠标移到另一块不同缩放的显示器上，
        /// Windows 会发 WM_DPICHANGED，WinForms 默认按建议值**改变窗口大小和位置** ——
        /// 窗口就被拽到屏幕中间，画布坐标系和窗口坐标系对不上，
        /// 笔迹画到画布外（等于没画）、清除也清不掉。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED) return;
            base.WndProc(ref m);
        }

        /// <summary>按当前虚拟屏幕重建画布（插拔显示器后调用）。</summary>
        public void RebuildCanvas()
        {
            var v = SystemInformation.VirtualScreen;
            int vw = Math.Max(1, v.Width);
            int vh = Math.Max(1, v.Height);
            ApplyWindowBounds(v.Left, v.Top, vw, vh);
            lock (NoteOverlay.GdiLock)
            {
                _cg?.Dispose();
                _canvas?.Dispose();
                // 画布按**物理像素**的虚拟屏幕尺寸来，和光标坐标同一套坐标系
                _canvas = new Bitmap(vw, vh, PixelFormat.Format32bppPArgb);
                _canvas.SetResolution(96, 96);
                _cg = Graphics.FromImage(_canvas);
                _cg.SmoothingMode = SmoothingMode.AntiAlias;
                _cg.Clear(Color.Transparent);
                _hasLast = false;
            }
            PushRegion(new Rectangle(0, 0, vw, vh));
        }

        /// <summary>
        /// 把窗口按**物理像素**对齐到虚拟屏幕，并重建画布。
        /// 必须在窗口**显示之后**调用：WinForms 在 Show() 时会按自己的布局把窗口摆回默认位置，
        /// 之前在这里定位会被覆盖（窗口跑到屏幕中间去，笔迹就画到画布外面、也清不掉）。
        /// </summary>
        public void SyncBounds()
        {
            if (!IsHandleCreated) return;
            var v = SystemInformation.VirtualScreen;
            ApplyWindowBounds(v.Left, v.Top, Math.Max(1, v.Width), Math.Max(1, v.Height));
            RebuildCanvas();
            GetWindowRect(Handle, out var rc);
            AppLog.Info($"[Highlighter] 窗口定位 {rc.l},{rc.t} {rc.r - rc.l}×{rc.b - rc.t}" +
                        $"（虚拟屏 {v.Left},{v.Top} {v.Width}×{v.Height}）");
        }

        /// <summary>用 Win32 按**物理像素**把窗口摆到虚拟屏幕上（绕开 WinForms 的逻辑像素缩放）。</summary>
        private void ApplyWindowBounds(int x, int y, int w, int h)
        {
            if (!IsHandleCreated) return;
            SetWindowPos(Handle, new IntPtr(-1) /*HWND_TOPMOST*/, x, y, w, h,
                0x0010 /*SWP_NOACTIVATE*/ | 0x0040 /*SWP_SHOWWINDOW*/);
        }

        public void SetEnabled(bool on)
        {
            if (_enabled == on) return;
            _enabled = on;
            _cursorVerified = false;
            if (on)
            {
                // 开启前先重新对齐窗口（期间显示器可能变过），否则笔迹会画到画布外
                SyncBounds();
                EnsureCursor();
                InstallHook();
                ReplaceSystemCursor();
                ClearTrail();
                _timer ??= new System.Windows.Forms.Timer { Interval = 10 };
                _timer.Tick -= OnTick;
                _timer.Tick += OnTick;
                _timer.Start();
                AppLog.Info("[Highlighter] 激光笔已开启");
            }
            else
            {
                _timer?.Stop();
                RemoveHook();
                RestoreSystemCursor();
                ClearTrail();
                AppLog.Info("[Highlighter] 激光笔已关闭");
            }
        }

        /// <summary>装上低级鼠标钩子：鼠标一动就把光标贴回激光笔。</summary>
        private void InstallHook()
        {
            if (_hook != IntPtr.Zero) return;
            try
            {
                _hookProc = MouseHook;
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero)
                    AppLog.Warn($"[Highlighter] 安装鼠标钩子失败 err={Marshal.GetLastWin32Error()}");
                else
                    AppLog.Info("[Highlighter] 鼠标钩子已安装（光标将随鼠标实时替换为激光笔）");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[Highlighter] 安装鼠标钩子异常：{ex.Message}");
            }
        }

        private void RemoveHook()
        {
            try
            {
                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
                _hookProc = null;
            }
            catch { }
        }

        private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && _enabled && _cursor?.Handle != IntPtr.Zero)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ||
                        msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_MBUTTONDOWN ||
                        msg == WM_MOUSEWHEEL)
                    {
                        // 只在箭头位置贴激光笔；I 型/手型/调整大小等保持原样
                        ApplyLaserIfArrow();
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void SetColorIndex(int idx)
        {
            if (idx < 0 || idx >= Presets.Length) return;
            _colorIdx = idx;
            var c = Presets[idx];
            _pen.Color = Color.FromArgb(TrailAlpha, c);
            _dot.Color = Color.FromArgb(TrailAlpha, c);
            // 光标也跟着换颜色
            try
            {
                _cursor?.Dispose();
                _cursor = new MarkerCursor(c);
                SaveCursorPreview();
                AppLog.Info($"[Highlighter] 激光笔光标已创建（色 #{idx}）");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[Highlighter] 创建激光笔光标失败：{ex.Message}");
            }
        }

        /// <summary>存一张光标 PNG 预览（肉眼确认造型用）。</summary>
        private void SaveCursorPreview()
        {
            try
            {
                _cursor?.SavePreview(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "SpenTablet-highlighter-cursor.png"));
            }
            catch { }
        }

        /// <summary>确保激光笔光标已创建（第一次开启时用）。</summary>
        private void EnsureCursor()
        {
            if (_cursor != null) return;
            try
            {
                _cursor = new MarkerCursor(Presets[_colorIdx]);
                SaveCursorPreview();
                AppLog.Info("[Highlighter] 激光笔光标已创建");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[Highlighter] 创建激光笔光标失败：{ex.Message}");
            }
        }

        /// <summary>清空痕迹（并把清空后的画布推上去，否则旧痕迹会留在屏幕上）。</summary>
        public void ClearTrail()
        {
            lock (NoteOverlay.GdiLock)
            {
                _cg?.Clear(Color.Transparent);
                _hasLast = false;
            }
            var c = _canvas;
            if (c != null) PushRegion(new Rectangle(0, 0, c.Width, c.Height));
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                if (!_enabled) return;
                // 把系统指针换成激光笔（每帧重申一次：别的程序改光标后要抢回来）。
                // 注意只在「当前是普通箭头」时替换 —— 否则文本 I 型、链接手型、
                // 等待、调整大小等光标都会被踩成激光图标。
                if (_cursor is { } cur && cur.Handle != IntPtr.Zero)
                {
                    ApplyLaserIfArrow();
                }
                TrackCursor();
            }
            catch (Exception ex)
            {
                AppLog.Error($"[Highlighter] 定时器异常：{ex.GetType().Name} {ex.Message}");
            }
        }

        /// <summary>读取当前光标位置并补一段笔迹。</summary>
        private void TrackCursor()
        {
            if (!MouseControl.TryGetCursorPos(out var cx, out var cy)) return;
            var canvas = _canvas;
            if (canvas == null || !IsHandleCreated) return;
            // 用 Win32 取窗口的**物理**矩形（Form.Left/Top 是逻辑像素，DPI 下会偏）
            if (!GetWindowRect(Handle, out var rc)) return;
            // 自愈：窗口被别的机制挪走/改尺寸了 → 拉回虚拟屏幕原点
            var vs = SystemInformation.VirtualScreen;
            if (rc.l != vs.Left || rc.t != vs.Top || rc.r - rc.l != vs.Width || rc.b - rc.t != vs.Height)
            {
                ApplyWindowBounds(vs.Left, vs.Top, Math.Max(1, vs.Width), Math.Max(1, vs.Height));
                GetWindowRect(Handle, out rc);
            }
            float x = cx - rc.l;
            float y = cy - rc.t;
            if (x < -40 || y < -40 || x > canvas.Width + 40 || y > canvas.Height + 40)
            {
                _hasLast = false;
                return;
            }
            // **只有按住左键时才留下荧光痕迹**（就像真的激光笔：笔尖按在纸上才出墨）
            if (!MouseControl.LeftDown)
            {
                _hasLast = false;
                return;
            }
            var pt = new PointF(x, y);
            if (!_hasLast)
            {
                _last = pt;
                _hasLast = true;
                lock (NoteOverlay.GdiLock)
                {
                    _cg?.FillEllipse(_dot, x - TrailWidth / 2f, y - TrailWidth / 2f, TrailWidth, TrailWidth);
                }
                PushRegion(new Rectangle((int)(x - TrailWidth), (int)(y - TrailWidth),
                    (int)TrailWidth + 2, (int)TrailWidth + 2));
                return;
            }
            float dx = pt.X - _last.X, dy = pt.Y - _last.Y;
            if (dx * dx + dy * dy < 1f) return;   // 没动就不画
            lock (NoteOverlay.GdiLock)
            {
                _cg?.DrawLine(_pen, _last, pt);
            }
            _last = pt;
            if (_dbg++ % 20 == 0)
            {
                var px = _canvas?.GetPixel(Math.Max(0, Math.Min(_canvas.Width - 1, (int)pt.X)),
                                           Math.Max(0, Math.Min(_canvas.Height - 1, (int)pt.Y))) ?? Color.Transparent;
                AppLog.Info($"[Highlighter] 画线 {_last} 画布像素={px} 画布={_canvas?.Width}×{_canvas?.Height} 窗口={rc.l},{rc.t} 可见={Visible}");
            }
            // 只推送变化的那一小块，避免每次都传整屏
            var dirty = Rectangle.FromLTRB(
                (int)Math.Floor(Math.Min(_last.X, pt.X) - TrailWidth),
                (int)Math.Floor(Math.Min(_last.Y, pt.Y) - TrailWidth),
                (int)Math.Ceiling(Math.Max(_last.X, pt.X) + TrailWidth),
                (int)Math.Ceiling(Math.Max(_last.Y, pt.Y) + TrailWidth));
            PushRegion(dirty);
        }

        /// <summary>把画布的指定区域推送到分层窗口（带 alpha 混合）。</summary>
        private void PushRegion(Rectangle dirty)
        {
            var canvas = _canvas;
            if (canvas == null || !IsHandleCreated) return;
            dirty = Rectangle.Intersect(dirty, new Rectangle(0, 0, canvas.Width, canvas.Height));
            if (dirty.Width <= 0 || dirty.Height <= 0) return;
            try
            {
                EnsureDib(canvas.Width, canvas.Height);

                // 只把「脏区」这几行从画布拷进 DIB（整屏 5000×1920 全拷太费带宽）
                var data = canvas.LockBits(new Rectangle(0, 0, canvas.Width, canvas.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                int rowBytes = dirty.Width * 4;
                var row = new byte[rowBytes];
                for (int y = dirty.Y; y < dirty.Y + dirty.Height; y++)
                {
                    IntPtr src = (IntPtr)((long)data.Scan0 + (long)y * data.Stride + (long)dirty.X * 4);
                    IntPtr dst = (IntPtr)((long)_dibBits + (long)y * canvas.Width * 4 + (long)dirty.X * 4);
                    Marshal.Copy(src, row, 0, rowBytes);
                    Marshal.Copy(row, 0, dst, rowBytes);
                }
                canvas.UnlockBits(data);

                IntPtr screenDc = GetDC(IntPtr.Zero);
                // 目标位置用**虚拟屏原点 + 画布坐标**（画布就是虚拟屏大小），
                // 不用窗口矩形：窗口被挪动时，内容仍落在正确的屏幕区域
                var vs = SystemInformation.VirtualScreen;
                var ptSrc = new POINT { x = dirty.X, y = dirty.Y };
                var ptDst = new POINT { x = vs.Left + dirty.X, y = vs.Top + dirty.Y };
                var sz = new SIZE { cx = dirty.Width, cy = dirty.Height };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                bool ok = UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref sz, _memDc, ref ptSrc, 0, ref blend, ULW_ALPHA);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    AppLog.Warn($"[Highlighter] UpdateLayeredWindow 失败 err={err} 区域={dirty}");
                }
                ReleaseDC(IntPtr.Zero, screenDc);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[Highlighter] 推送画面失败：{ex.GetType().Name} {ex.Message}");
            }
        }

        /// <summary>建立（并复用）一块与画布同尺寸的 32bpp DIB，作为分层窗口的源位图。</summary>
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
                AppLog.Error("[Highlighter] CreateDIBSection 失败，激光笔无法显示");
                return;
            }
            _memDc = CreateCompatibleDC(IntPtr.Zero);
            SelectObject(_memDc, _dibHandle);
            _dibW = w;
            _dibH = h;
            // DIB 初始为全 0（透明）
            if (_dibBits != IntPtr.Zero)
            {
                var zero = new byte[Math.Min(w * 4 * 64, 1 << 20)];
                for (int off = 0; off < w * h * 4; off += zero.Length)
                {
                    int n = Math.Min(zero.Length, w * h * 4 - off);
                    System.Runtime.InteropServices.Marshal.Copy(zero, 0, (IntPtr)((long)_dibBits + off), n);
                }
            }
            AppLog.Info($"[Highlighter] 分层源位图就绪 {w}×{h}");
        }

        private void ReleaseDib()
        {
            if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_dibHandle != IntPtr.Zero) { DeleteObject(_dibHandle); _dibHandle = IntPtr.Zero; }
            _dibBits = IntPtr.Zero;
            _dibW = _dibH = 0;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 分层窗口（WS_EX_LAYERED）是 UpdateLayeredWindow 生效的前提。
            // WinForms 有可能把扩展样式重置掉，这里再强制设一次并记日志确认。
            const int GWL_EXSTYLE = -20;
            const int WS_EX_LAYERED = 0x80000;
            var ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_LAYERED) == 0)
            {
                SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
                var now = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
                AppLog.Info($"[Highlighter] 强制加上 WS_EX_LAYERED：{ex:X} → {now:X}");
            }
            else
            {
                AppLog.Info($"[Highlighter] 扩展样式 {ex:X}（已含 WS_EX_LAYERED）");
            }

            // 句柄建好后按物理像素摆到虚拟屏幕（构造时还没句柄）
            var v = SystemInformation.VirtualScreen;
            ApplyWindowBounds(v.Left, v.Top, Math.Max(1, v.Width), Math.Max(1, v.Height));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer?.Stop();
            _timer?.Dispose();
            RemoveHook();
            RestoreSystemCursor();
            _cg?.Dispose();
            _canvas?.Dispose();
            ReleaseDib();
            _pen.Dispose();
            _dot.Dispose();
            _cursor?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
