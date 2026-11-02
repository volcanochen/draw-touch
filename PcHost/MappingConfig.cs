using System;
using System.Runtime.InteropServices;

namespace DrawTouchHost;

/// <summary>
/// 运行配置（来自 appsettings.json），并负责把手机传来的归一化坐标(0..1)映射到 Windows 虚拟屏幕像素。
///
/// 比例问题：手机屏（竖屏约 0.49）与桌面（如 3072×1920 = 1.6）宽高比不同。
/// 若直接把 0..1 拉伸到整个目标区域，圆形笔迹会被拉成椭圆，手感位移也会失真。
/// 支持三种模式：
///   Keep    —— 在目标区域内取「与手机同比例的最大矩形」并居中，两侧留边（笔迹不变形）
///   Stretch —— 拉伸铺满整个目标区域（覆盖大但会变形）
///   Follow  —— 跟随手机端设置面板的选择（默认）
///
/// 另外支持「操作范围框」：在桌面上画出笔实际能覆盖的区域，
/// 并可用手指在手机上滑动平移它、双指捏合缩放它（CenterX/CenterY + Scale）。
/// </summary>
public sealed class MappingConfig
{
    public int ListenPort { get; set; } = 6000;
    /// <summary>Pen = 合成笔注入（带压感）；Mouse = SendInput 回退（无压感）。</summary>
    public string Mode { get; set; } = "Pen";

    /// <summary>true 时弹出调试浮窗，实时显示收到的笔坐标/压感/倾斜/按键。</summary>
    public bool Debug { get; set; } = false;

    /// <summary>比例模式：Follow（跟随手机端） / Keep（保持等比居中） / Stretch（拉伸铺满）。</summary>
    public string Aspect { get; set; } = "Follow";

    /// <summary>true 时在桌面上画一个边框，标出 S Pen 实际能操作的区域。</summary>
    public bool ShowRegion { get; set; } = true;

    /// <summary>
    /// 工作模式（由手机端设置面板切换，PC 端配置界面也可改）：
    ///   Tablet —— 数位板：显示操作范围框，S Pen 在框内作画，手指只负责拖动框；
    ///   Mouse  —— 鼠标：取消操作范围框，手指当触摸板，S Pen 当绝对定位的鼠标笔。
    /// 两种模式互斥，手指不会同时既拖框又控鼠标。
    /// </summary>
    public string WorkMode { get; set; } = "Tablet";

    /// <summary>鼠标模式下手指位移的增益（1.0 = 划过整个手机宽 ≈ 光标走过整个虚拟屏宽）。</summary>
    public double MouseSpeed { get; set; } = 1.0;

    /// <summary>
    /// 双指捏合缩放方式（只有两种）：
    ///   Touch —— 触摸板缩放 API：模拟双指捏合的触摸注入，由系统翻译成前台应用的缩放手势，
    ///            浏览器/地图/Office/图片查看器等原生支持，手感与真实触摸板一致；
    ///   Wheel —— Ctrl + 滚轮：通用方案，OneNote / Office / 浏览器 / PDF / 看图 都认，
    ///            且不会和文本排版快捷键撞车。
    /// 系统若不支持触摸注入（实测部分 Win11 版本 InjectTouchInput 返回 err=87），
    /// 选 Touch 也会自动降级到 Wheel。
    ///
    /// 历史值 "Keys"（Ctrl+加号/减号）已废弃：在 OneNote/Word 里那是「下标 / 删除线」，
    /// 不是缩放，会误改文档格式 —— 现在按 "Wheel" 处理。
    /// </summary>
    public string PinchMode { get; set; } = "Touch";

    /// <summary>
    /// 双指平移方式（与 PinchMode 对称）。**默认 Touch（触摸注入）**：用户 2026-11-01 明确要求
    /// 「默认设置为鼠标注入」——原生平移手势手感是第一位，指针问题已按实测证据修掉。
    ///
    ///   Touch  —— 触摸注入平移（两个接触点一起平移 → 系统翻译成原生平移手势）。
    ///            手感与真实触摸板一致（Office / 浏览器 / 图片 / PDF / 地图都原生支持）。
    ///            已知副作用 & 处理（2026-11-01 受控探针 tests/PanProbe 实测）：
    ///              · 接触期间系统会隐藏光标 → 抬手后尾巴期「只点亮、不定位」把它叫回来；
    ///              · 系统**不会**把光标拽到触点（保持接触 1s 偏移恒 0px）→ 所以平移全程
    ///                一个字都不碰光标，物理鼠标可以同时移动（真触摸板的效果）。
    ///    Wheel  —— 高分辨率滚轮（WM_MOUSEWHEEL / WM_MOUSEHWHEEL 像素增量）。
    ///            全程不碰光标、连隐藏都不会发生；代价是「滚」而不是「拖」，某些应用里增益不一致。
    ///
    /// 两种方式都要保留、由用户在手机端设置页（或 PC 配置窗体）随时切换 —— 握手帧
    /// HELLO 的第 9 个 float 携带该开关（0=Wheel / 1=Touch），见 [SetPanMode]。
    ///
    /// 注：与「必须用注入触摸，不能 Ctrl+滚轮」那条硬规则一致 —— 那条针对**捏合缩放**
    /// （PinchMode 仍为 Touch），双指平移现在也默认走触摸注入。
    /// </summary>
    public string PanMode { get; set; } = "Touch";

    /// <summary>
    /// 【诊断用】强制平移实现方式，覆盖手机端设置：""（默认，听手机）/ "Touch" / "Wheel"。
    ///
    /// 为什么要有它：2026-11-01 排查「pan 时有个东西跟着手指走」，需要做 A/B
    ///（触摸注入 ↔ 滚轮）来判定现象来自哪条路径。但手机端那个单选项藏在
    /// 「设置 → 指针与滚动」卡片里，用户两次 pan 都仍是 Touch，A/B 一直做不成。
    /// 有了这个键，改 appsettings.json 重启宿主就能强制切过去，无需碰手机。
    /// 平时保持空字符串。
    /// </summary>
    public string PanModeForce { get; set; } = "";

    /// <summary>
    /// 双指平移（MOUSE_PAN 帧）的方向翻转，只作用于平移帧、**不影响单指滚动**：
    ///   ""（默认）/ "Normal" —— 默认方向（触摸注入 = 内容跟手；滚轮路径维持原调校）
    ///   "InvertY"  —— 纵向翻反（手指下滑 → 内容上移，即传统滚轮方向）
    ///   "InvertX"  —— 横向翻反
    ///   "Invert"   —— 两轴都翻
    /// 大小写不敏感；写别的值等同 ""。改 appsettings.json 后重启宿主生效。
    ///
    /// 为什么需要它：符号链是按滚轮路径手工调校的（手机端取反一次 + PC 端 pxY 再取反），
    /// 触摸注入路径当年又多套了一次取反（`UpdateTouchPan(-pxX, -pxY)`），
    /// 纵向凑成奇数次取反 → 内容与手指反向（2026-11-01 用户实测「滚动方向是反的」）。
    /// 代码已把触摸注入的纵向修正为「内容跟手」；若用户偏好反过来，用这个键翻。
    /// </summary>
    public string PanDirection { get; set; } = "";

    /// <summary>PanDirection 是否要求横向翻转。</summary>
    public bool PanInvertX
    {
        get
        {
            var v = (PanDirection ?? "").Trim();
            return v.Equals("Invert", StringComparison.OrdinalIgnoreCase)
                || v.Equals("InvertX", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>PanDirection 是否要求纵向翻转。</summary>
    public bool PanInvertY
    {
        get
        {
            var v = (PanDirection ?? "").Trim();
            return v.Equals("Invert", StringComparison.OrdinalIgnoreCase)
                || v.Equals("InvertY", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>当前是否为「触摸注入」平移（false = 高分辨率滚轮）。</summary>
    public bool IsTouchPan
    {
        get
        {
            var f = (PanModeForce ?? "").Trim();
            if (f.Equals("Touch", StringComparison.OrdinalIgnoreCase)) return true;
            if (f.Equals("Wheel", StringComparison.OrdinalIgnoreCase)) return false;
            return PanMode.Equals("Touch", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>平移方式的一句人话说明（日志/界面用）。</summary>
    public string PanModeText => IsTouchPan
        ? "触摸注入（原生触摸板手感；接触期间光标会隐藏，鼠标一动即显示）"
        : "滚轮（指针不动、不消失；与纵向滚动一致）";

    /// <summary>
    /// 切换双指平移方式（手机端设置页开关 → 握手帧下发）。
    /// code：0=滚轮 Wheel / 1=触摸注入 Touch；其余值忽略（保持现状）。
    /// </summary>
    /// <returns>true = 真的变了（调用方需要清理另一条路径的残留状态）。</returns>
    public bool SetPanMode(int code)
    {
        var v = code >= 1 ? "Touch" : "Wheel";
        if (v.Equals(PanMode, StringComparison.OrdinalIgnoreCase)) return false;
        var old = PanModeText;
        PanMode = v;
        AppLog.Info($"[Pan] 平移方式切换：{old} → {PanModeText}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 截屏保存目录（手机点「截屏」时 PNG 存到哪）。
    /// 留空 = 默认「图片库\DrawTouch」；目录不存在会自动创建。
    /// </summary>
    public string ShotSaveDir { get; set; } = "";

    /// <summary>当前是否为鼠标模式。</summary>
    public bool IsMouseMode => WorkMode.Equals("Mouse", StringComparison.OrdinalIgnoreCase);

    /// <summary>切换工作模式（会触发 Changed，桌面范围框随之显示/隐藏）。</summary>
    public void SetWorkMode(bool mouse)
    {
        var v = mouse ? "Mouse" : "Tablet";
        if (v.Equals(WorkMode, StringComparison.OrdinalIgnoreCase)) return;
        WorkMode = v;
        Changed?.Invoke();
    }

    /// <summary>
    /// 操作范围框相对「基准矩形」的位置与大小（都用基准矩形的相对量表示，0..1）。
    ///   Scale  —— 缩放倍数。1.0 = 基准（等比模式下就是能放下的最大等比矩形）
    ///   FrameL/FrameT —— 操作框左上角在基准矩形里的相对位置
    ///
    /// 手机端用完全相同的三个量（相对书写区）画出自己的操作框，
    /// 因此「手机上的框」与「桌面上的框」永远是同一个形状、同一个位置。
    /// </summary>
    public double Scale { get; set; } = 1.0;
    public double FrameL { get; set; } = 0.0;
    public double FrameT { get; set; } = 0.0;

    /// <summary>映射区域（虚拟屏幕像素）。任一为 0 表示使用整块虚拟屏幕。</summary>
    public int MappingX { get; set; } = 0;
    public int MappingY { get; set; } = 0;
    public int MappingW { get; set; } = 0;
    public int MappingH { get; set; } = 0;

    // 虚拟屏幕范围（多显示器时为所有显示器合并范围）
    public int VirtualX { get; private set; }
    public int VirtualY { get; private set; }
    public int VirtualW { get; private set; }
    public int VirtualH { get; private set; }

    /// <summary>映射区域发生变化（比例/区域/平移）时触发，用于刷新桌面上的范围框。</summary>
    public event Action? Changed;

    private double _phoneAspect;      // 手机绘制面 宽/高，0 表示未知
    private double _phoneSurfaceW;    // 手机书写区像素宽（用于精确还原手机端的框形状）
    private double _phoneSurfaceH;    // 手机书写区像素高
    private bool _phonePrefersKeep;   // 手机端设置面板的选择
    public double PhoneAspect => _phoneAspect;
    public bool PhonePrefersKeep => _phonePrefersKeep;

    private int _mapX, _mapY, _mapW, _mapH;

    public void ResolveVirtualScreen()
    {
        VirtualX = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        VirtualY = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        VirtualW = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        VirtualH = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (VirtualW <= 0) VirtualW = GetSystemMetrics(0); // SM_CXSCREEN
        if (VirtualH <= 0) VirtualH = GetSystemMetrics(1); // SM_CYSCREEN

        if (MappingX == 0 && MappingY == 0 && MappingW == 0 && MappingH == 0)
        {
            _mapX = VirtualX; _mapY = VirtualY; _mapW = VirtualW; _mapH = VirtualH;
        }
        else
        {
            _mapX = MappingX; _mapY = MappingY;
            _mapW = MappingW <= 0 ? VirtualW : MappingW;
            _mapH = MappingH <= 0 ? VirtualH : MappingH;
        }
    }

    /// <summary>上次刷新虚拟屏幕的时刻（毫秒），用于限流。</summary>
    private long _lastResolveAt;

    /// <summary>
    /// 虚拟屏幕**变化时**才重新解析并通知外部刷新。
    ///
    /// 以前只在启动时取一次虚拟屏幕尺寸：插拔第二块显示器、改分辨率、接远程桌面之后，
    /// 映射区域就成了过期的旧值 —— 光标被夹在过期范围里（完全控制不了），
    /// 桌面框和笔的落点也跟着错（笔在框框之外）。这里按需复查，变了就更新。
    ///
    /// 最多每 500ms 查一次（GetSystemMetrics 很便宜，但没必要每帧都调）。
    /// </summary>
    public bool RefreshVirtualScreenIfChanged(long nowMs)
    {
        if (nowMs - _lastResolveAt < 500) return false;
        _lastResolveAt = nowMs;

        int x = GetSystemMetrics(76);
        int y = GetSystemMetrics(77);
        int w = GetSystemMetrics(78);
        int h = GetSystemMetrics(79);
        if (w <= 0) w = GetSystemMetrics(0);
        if (h <= 0) h = GetSystemMetrics(1);
        if (x == VirtualX && y == VirtualY && w == VirtualW && h == VirtualH) return false;

        int oldX = VirtualX, oldY = VirtualY, oldW = VirtualW, oldH = VirtualH;
        ResolveVirtualScreen();
        // 框要重新夹回合法区间（虚拟屏幕变小后，原来的位置可能越界）
        ClampFrame();
        AppLog.Info($"[Display] 虚拟屏幕变化：{oldX},{oldY} {oldW}×{oldH} → {VirtualX},{VirtualY} {VirtualW}×{VirtualH}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>手机握手：上报绘制面像素宽高、比例偏好、工作模式，以及当前操作框状态。</summary>
    public void SetPhoneHello(float w, float h, float aspectMode, float workMode = 0f,
                              float frameL = -1f, float frameT = -1f, float frameScale = -1f)
    {
        if (w > 0 && h > 0)
        {
            _phoneAspect = (double)w / h;
            _phoneSurfaceW = w;
            _phoneSurfaceH = h;
        }
        _phonePrefersKeep = aspectMode >= 1f;
        SetWorkMode(workMode >= 1f);
        // 手机端上报了操作框状态就采纳它，保证重连后两端框仍然是同一个形状/位置
        if (frameScale > 0f) SetFrame(frameL, frameT, frameScale);
        Changed?.Invoke();
    }

    /// <summary>最终生效的比例模式。</summary>
    public bool IsKeepAspect()
    {
        if (Aspect.Equals("Keep", StringComparison.OrdinalIgnoreCase)) return true;
        if (Aspect.Equals("Stretch", StringComparison.OrdinalIgnoreCase)) return false;
        return _phonePrefersKeep; // Follow
    }

    /// <summary>未加平移的基准矩形（目标区域内的等比矩形）。</summary>
    private (int x, int y, int w, int h) BaseRect()
    {
        if (!IsKeepAspect() || _phoneAspect <= 0 || _mapH <= 0 || _mapW <= 0)
            return (_mapX, _mapY, _mapW, _mapH);

        double regionAspect = (double)_mapW / _mapH;
        if (_phoneAspect > regionAspect)
        {
            // 手机更宽 → 贴合宽度，上下留边
            int h = (int)(_mapW / _phoneAspect);
            return (_mapX, _mapY + (_mapH - h) / 2, _mapW, h);
        }
        // 手机更高 → 贴合高度，左右留边
        int w = (int)(_mapH * _phoneAspect);
        return (_mapX + (_mapW - w) / 2, _mapY, w, _mapH);
    }

    /// <summary>
    /// 操作框在桌面上的尺寸。
    /// 关键：尺寸由**手机书写区尺寸**换算而来（而不是直接缩放基准矩形），
    /// 这样「桌面框」就是「手机框」等比放大后取整的结果 —— 两端形状严格一致，
    /// 不会出现捏合后桌面框变形的现象。
    /// </summary>
    private (int w, int h) BoxSize(int baseW, double s)
    {
        double sw = _phoneSurfaceW > 0 ? _phoneSurfaceW : baseW;
        double sh = _phoneSurfaceH > 0 ? _phoneSurfaceH : baseW;
        double k = baseW / sw;                      // 桌面像素 / 手机像素
        int w = Math.Max(16, (int)Math.Round(sw * s * k));
        // 高由宽推出来（而不是各自取整），这样桌面框的宽高比严格等于手机框的宽高比，
        // 捏合后再也不会出现「手机框和桌面框形状不一样」。
        int h = Math.Max(16, (int)Math.Round(w * sh / sw));
        return (w, h);
    }

    /// <summary>
    /// 实际生效的映射矩形（操作框）。
    /// 不再夹在「基准矩形」内 —— 操作框可移动到**整个虚拟屏幕**的任意位置，
    /// 包括把框拖到第二块显示器上（多屏场景）。
    /// 仅做最后一道兜底：把整块框夹在虚拟屏幕范围内，避免框被拖到完全看不见的虚空里。
    /// </summary>
    public (int x, int y, int w, int h) GetActiveRect()
    {
        var (bx, by, bw, bh) = BaseRect();
        double s = Math.Clamp(Scale, MIN_SCALE, 1.0);
        var (w, h) = BoxSize(bw, s);
        int x = (int)Math.Round(bx + FrameL * bw);
        int y = (int)Math.Round(by + FrameT * bh);
        // 夹在虚拟屏幕（多显示器合并范围）内：框可落到任意显示器，但不会被拖到屏幕外完全消失
        x = Math.Clamp(x, VirtualX, Math.Max(VirtualX, VirtualX + VirtualW - w));
        y = Math.Clamp(y, VirtualY, Math.Max(VirtualY, VirtualY + VirtualH - h));
        return (x, y, w, h);
    }

    /// <summary>
    /// 把 FrameL/FrameT 夹回合法区间。
    /// 规则：操作框的**整块**必须留在虚拟屏幕内（可落在任意显示器，包括第二块屏），
    /// 因此 FrameL/FrameT 的上下限由「框尺寸」与「虚拟屏幕」推导，
    /// 而不是手机端那种 [0, 1-Scale] 的书写区相对量。
    /// 这样手指滑动平移时不会有「撞到隐形墙」的死区。
    /// </summary>
    private void ClampFrame()
    {
        var (bx, by, bw, bh) = BaseRect();
        double s = Math.Clamp(Scale, MIN_SCALE, 1.0);
        var (w, h) = BoxSize(bw, s);
        if (bw > 0 && VirtualW > 0)
        {
            double minL = (double)(VirtualX - bx) / bw;
            double maxL = (double)(VirtualX + VirtualW - w - bx) / bw;
            FrameL = Math.Clamp(FrameL, Math.Min(minL, maxL), Math.Max(minL, maxL));
        }
        if (bh > 0 && VirtualH > 0)
        {
            double minT = (double)(VirtualY - by) / bh;
            double maxT = (double)(VirtualY + VirtualH - h - by) / bh;
            FrameT = Math.Clamp(FrameT, Math.Min(minT, maxT), Math.Max(minT, maxT));
        }
    }

    /// <summary>手指单指滑动平移：dx/dy 是「操作框宽度/高度」的相对位移。</summary>
    public void Pan(double dx, double dy)
    {
        FrameL += dx;
        FrameT += dy;
        ClampFrame();
        Changed?.Invoke();
    }

    /// <summary>
    /// 双指捏合缩放。focusX/focusY 是捏合焦点在**操作框内**的相对位置(0..1)：
    /// 缩放后该点仍停在原来的屏幕位置上，也就是「以捏合中心为锚点」缩放。
    /// 手机端用同一套公式算自己的框，两端结果逐位一致。
    /// </summary>
    public void Zoom(double factor, float focusX, float focusY)
    {
        var (bx, by, bw, bh) = BaseRect();
        double fx = Math.Clamp(focusX, 0f, 1f);
        double fy = Math.Clamp(focusY, 0f, 1f);
        double s = Math.Clamp(Scale, MIN_SCALE, 1.0);
        var (w, h) = BoxSize(bw, s);
        // 焦点所在的屏幕位置（桌面像素）
        double px = bx + FrameL * bw + fx * w;
        double py = by + FrameT * bh + fy * h;

        double s2 = Math.Clamp(s * factor, MIN_SCALE, 1.0);
        Scale = s2;
        var (w2, h2) = BoxSize(bw, s2);
        // 焦点不动 → 反推框的新位置
        FrameL = bw > 0 ? (px - fx * w2 - bx) / bw : 0.0;
        FrameT = bh > 0 ? (py - fy * h2 - by) / bh : 0.0;
        ClampFrame();
        Changed?.Invoke();
    }

    /// <summary>复位：操作框恢复为整个基准矩形。</summary>
    public void ResetView()
    {
        Scale = 1.0;
        FrameL = 0.0;
        FrameT = 0.0;
        Changed?.Invoke();
    }

    /// <summary>直接采纳手机端上报的操作框状态（重连时用，保证两端一致）。</summary>
    public void SetFrame(double frameL, double frameT, double scale)
    {
        Scale = Math.Clamp(scale, MIN_SCALE, 1.0);
        FrameL = frameL;
        FrameT = frameT;
        ClampFrame();
        Changed?.Invoke();
    }

    public const double MIN_SCALE = 0.05;

    /// <summary>把归一化坐标(0..1)映射到像素坐标，并夹在虚拟屏幕内。</summary>
    public (int px, int py) ToPixels(float nx, float ny)
    {
        float cx = Math.Clamp(nx, 0f, 1f);
        float cy = Math.Clamp(ny, 0f, 1f);

        var (rx, ry, rw, rh) = GetActiveRect();
        int px = rx + (int)Math.Round(cx * rw);
        int py = ry + (int)Math.Round(cy * rh);

        px = Math.Clamp(px, VirtualX, VirtualX + VirtualW - 1);
        py = Math.Clamp(py, VirtualY, VirtualY + VirtualH - 1);
        return (px, py);
    }

    /// <summary>
    /// 鼠标模式下把归一化坐标映射到「整块虚拟屏幕」（不做等比留边、不加平移）。
    /// 因为鼠标模式已经取消了操作范围框，笔/光标就该覆盖全屏。
    /// </summary>
    public (int px, int py) ToPixelsFull(float nx, float ny)
    {
        float cx = Math.Clamp(nx, 0f, 1f);
        float cy = Math.Clamp(ny, 0f, 1f);
        int px = VirtualX + (int)Math.Round(cx * (VirtualW - 1));
        int py = VirtualY + (int)Math.Round(cy * (VirtualH - 1));
        return (px, py);
    }

    /// <summary>界面上改完映射区域后即时生效。</summary>
    public void ApplyMapping(int x, int y, int w, int h)
    {
        MappingX = x; MappingY = y; MappingW = w; MappingH = h;
        ResolveVirtualScreen();
        Changed?.Invoke();
    }

    /// <summary>通知外部（范围框）刷新。</summary>
    public void NotifyChanged() => Changed?.Invoke();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
