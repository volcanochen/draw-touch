using System;
using System.Runtime.InteropServices;

namespace SpenTabletHost;

/// <summary>
/// 运行配置（来自 appsettings.json），并负责把手机传来的归一化坐标(0..1)映射到 Windows 虚拟屏幕像素。
/// </summary>
public sealed class MappingConfig
{
    public int ListenPort { get; set; } = 6000;
    /// <summary>Pen = 合成笔注入（带压感）；Mouse = SendInput 回退（无压感）。</summary>
    public string Mode { get; set; } = "Pen";

    /// <summary>true 时弹出调试浮窗，实时显示收到的笔坐标/压感/倾斜/按键。</summary>
    public bool Debug { get; set; } = false;

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

    /// <summary>把归一化坐标(0..1)映射到像素坐标，并夹在虚拟屏幕内。</summary>
    public (int px, int py) ToPixels(float nx, float ny)
    {
        float cx = Math.Clamp(nx, 0f, 1f);
        float cy = Math.Clamp(ny, 0f, 1f);
        int px = _mapX + (int)(cx * _mapW);
        int py = _mapY + (int)(cy * _mapH);
        px = Math.Clamp(px, VirtualX, VirtualX + VirtualW - 1);
        py = Math.Clamp(py, VirtualY, VirtualY + VirtualH - 1);
        return (px, py);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
