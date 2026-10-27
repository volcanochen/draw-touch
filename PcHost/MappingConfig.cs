using System;
using System.Runtime.InteropServices;

namespace SpenTabletHost;

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
