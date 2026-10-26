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
/// 并可用手指在手机上滑动来平移它（OffsetX / OffsetY）。
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

    /// <summary>操作范围框相对“基准位置”的平移量（像素）。手指滑动时改变。</summary>
    public int OffsetX { get; set; } = 0;
    public int OffsetY { get; set; } = 0;

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

    /// <summary>手机握手：上报绘制面像素宽高、比例偏好，以及手机端当前的工作模式。</summary>
    public void SetPhoneHello(float w, float h, float aspectMode, float workMode = 0f)
    {
        if (w > 0 && h > 0) _phoneAspect = (double)w / h;
        _phonePrefersKeep = aspectMode >= 1f;
        SetWorkMode(workMode >= 1f);
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

    /// <summary>实际生效的映射矩形（含平移），保证不越出虚拟屏幕。</summary>
    public (int x, int y, int w, int h) GetActiveRect()
    {
        var (bx, by, bw, bh) = BaseRect();
        int x = Math.Clamp(bx + OffsetX, VirtualX, Math.Max(VirtualX, VirtualX + VirtualW - bw));
        int y = Math.Clamp(by + OffsetY, VirtualY, Math.Max(VirtualY, VirtualY + VirtualH - bh));
        return (x, y, bw, bh);
    }

    /// <summary>手指滑动平移：位移量按“框自身尺寸”的比例换算，手感 1:1。</summary>
    public void Pan(float dxNorm, float dyNorm)
    {
        var r = GetActiveRect();
        OffsetX += (int)Math.Round(dxNorm * r.w);
        OffsetY += (int)Math.Round(dyNorm * r.h);
        var b = BaseRect();
        OffsetX = Math.Clamp(OffsetX, VirtualX - b.x, Math.Max(VirtualX - b.x, VirtualX + VirtualW - b.x - b.w));
        OffsetY = Math.Clamp(OffsetY, VirtualY - b.y, Math.Max(VirtualY - b.y, VirtualY + VirtualH - b.y - b.h));
        Changed?.Invoke();
    }

    /// <summary>回到基准位置（等比模式下即居中）。</summary>
    public void CenterOffset()
    {
        OffsetX = 0; OffsetY = 0;
        Changed?.Invoke();
    }

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
