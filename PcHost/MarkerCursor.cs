using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SpenTabletHost;

/// <summary>
/// 激光笔光标：把 Windows 的箭头指针换成一个**醒目的激光光斑**。
///
/// 做法：用 GDI+ 画「白热核心 + 当前颜色光晕 + 颜色亮环」的圆斑，
/// 再用 CreateIconIndirect（fIcon=false）做成 32 位**彩色**系统光标，
/// 之后 SetCursor 就能把桌面指针换成它。热点（hotspot）落在光斑中心 ——
/// 和真激光笔一样，指哪打哪。
///
/// 光标尺寸随系统 DPI 变（96DPI=32px，144DPI=48px，192DPI=64px）。
/// </summary>
internal sealed class MarkerCursor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;          // 0 = 光标
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;     // 1bpp 单色掩码：1 = 透明
        public IntPtr hbmColor;    // 32bpp 彩色位图
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>光标句柄（可直接传给 SetCursor）</summary>
    public IntPtr Handle { get; private set; }

    /// <summary>光标图形（调试时存成 PNG 预览用）</summary>
    public Bitmap Image { get; private set; }

    /// <summary>热点（笔尖）在光标位图里的位置</summary>
    public int HotspotX { get; private set; }
    public int HotspotY { get; private set; }

    public MarkerCursor(Color color)
    {
        // 固定 32×32：系统游标方案里 CursorBaseSize=32，
        // 尺寸不一致的 .cur 会被游标加载器直接忽略（改了也白改）。
        // 系统会按当前 DPI 自动缩放，不用我们自己做多尺寸。
        int size = 32;
        Image = Draw(color, size, out int hotX, out int hotY);
        HotspotX = hotX;
        HotspotY = hotY;

        // 单色掩码：alpha 低的地方算透明（1 = 透明）
        // 注意：1bpp 是索引格式 —— Graphics.FromImage / SetPixel 都不支持，
        // 只能 LockBits 手工按位写（DIB 的位序是 MSB 在左）。
        var mask = new Bitmap(size, size, PixelFormat.Format1bppIndexed);
        var md = mask.LockBits(new Rectangle(0, 0, size, size),
            ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        int stride = md.Stride;
        var row = new byte[stride];
        for (int y = 0; y < size; y++)
        {
            Array.Clear(row, 0, stride);
            for (int x = 0; x < size; x++)
            {
                var c = Image.GetPixel(x, y);
                if (c.A < 128) row[x >> 3] |= (byte)(0x80 >> (x & 7));   // 1 = 透明
            }
            Marshal.Copy(row, 0, (IntPtr)((long)md.Scan0 + (long)y * stride), stride);
        }
        mask.UnlockBits(md);

        var info = new ICONINFO
        {
            fIcon = 0,
            xHotspot = hotX,
            yHotspot = hotY,
            hbmMask = mask.GetHbitmap(Color.Black),
            hbmColor = Image.GetHbitmap(Color.FromArgb(0))
        };
        Handle = CreateIconIndirect(ref info);
        if (Handle == IntPtr.Zero)
        {
            AppLog.Warn($"[Cursor] CreateIconIndirect 失败 err={Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// 画一个激光光斑：外层淡光晕 → 中层光晕 → 颜色亮环 → 白热核心。
    /// 中心就是热点（激光指哪打哪），不再需要探针扫描。
    /// </summary>
    private static Bitmap Draw(Color color, int size, out int hotX, out int hotY)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float c = size / 2f;
        // 外层大光晕（淡）——远看也知道这里有个激光点
        using (var halo = new SolidBrush(Color.FromArgb(55, color)))
            g.FillEllipse(halo, c - size * 0.46f, c - size * 0.46f, size * 0.92f, size * 0.92f);
        // 中层光晕
        using (var mid = new SolidBrush(Color.FromArgb(130, color)))
            g.FillEllipse(mid, c - size * 0.28f, c - size * 0.28f, size * 0.56f, size * 0.56f);
        // 颜色亮环：勾出光斑边界，任何背景下都醒目
        using (var ring = new Pen(Color.FromArgb(235, color), Math.Max(1.5f, size * 0.055f)))
            g.DrawEllipse(ring, c - size * 0.21f, c - size * 0.21f, size * 0.42f, size * 0.42f);
        // 白热核心（激光中心过曝发白）
        using (var core = new SolidBrush(Color.White))
            g.FillEllipse(core, c - size * 0.11f, c - size * 0.11f, size * 0.22f, size * 0.22f);
        // 核心上再罩一点颜色，避免纯白看不出当前是什么颜色
        using (var tint = new SolidBrush(Color.FromArgb(80, color)))
            g.FillEllipse(tint, c - size * 0.11f, c - size * 0.11f, size * 0.22f, size * 0.22f);

        hotX = (int)c;
        hotY = (int)c;
        return bmp;
    }

        /// <summary>存一份 PNG 预览（方便肉眼确认光标长什么样）</summary>
    public void SavePreview(string path)
    {
        try { Image.Save(path, ImageFormat.Png); }
        catch { }
    }

    // ---- .cur 文件：SetSystemCursor 只认「用 LoadImage 加载的」光标，
    //      CreateIconIndirect 造出来的它不收，所以先写一个 .cur 再 LoadImage 加载 ----

    /// <summary>
    /// 把光标位图写成 .cur 文件（ICO 容器，type=2 表示光标，热点写在目录项里）。
    /// </summary>
    private static void WriteCurFile(string path, Bitmap bmp, int hotX, int hotY)
    {
        int w = bmp.Width, h = bmp.Height;
        int xorStride = w * 4;
        int maskStride = ((w + 31) / 32) * 4;      // 1bpp 每行按 4 字节对齐
        int xorSize = xorStride * h;
        int maskSize = maskStride * h;
        int imageSize = 40 + xorSize + maskSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var wtr = new BinaryWriter(fs);

        // ICONDIR：type=2 → 光标
        wtr.Write((ushort)0);
        wtr.Write((ushort)2);
        wtr.Write((ushort)1);
        // ICONDIRENTRY
        wtr.Write((byte)(w >= 256 ? 0 : w));
        wtr.Write((byte)(h >= 256 ? 0 : h));
        wtr.Write((byte)0);            // 调色板数
        wtr.Write((byte)0);            // 保留
        wtr.Write((ushort)1);          // planes
        wtr.Write((ushort)32);         // bpp
        wtr.Write((uint)imageSize);
        wtr.Write((uint)30);           // 偏移：6(目录) + 16(目录项) + 8(热点)
        // .cur 特有：目录项之后紧跟两个 DWORD 的热点坐标
        wtr.Write((uint)hotX);
        wtr.Write((uint)hotY);

        // BITMAPINFOHEADER：biHeight = 2*h（XOR 图 + AND 掩码）
        wtr.Write((uint)40);
        wtr.Write(w);
        wtr.Write(h * 2);
        wtr.Write((ushort)1);
        wtr.Write((ushort)32);
        wtr.Write((uint)0);
        wtr.Write((uint)(uint)(xorSize + maskSize));
        wtr.Write((uint)0);
        wtr.Write((uint)0);
        wtr.Write((uint)0);
        wtr.Write((uint)0);

        // XOR 图（BGRA，自下而上）
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                wtr.Write(c.B); wtr.Write(c.G); wtr.Write(c.R); wtr.Write(c.A);
            }
        }
        // AND 掩码（1bpp，自下而上；alpha 低于阈值 = 透明 = 置 1）
        for (int y = h - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < w; x++)
            {
                if (bmp.GetPixel(x, y).A < 128) row[x >> 3] |= (byte)(0x80 >> (x & 7));
            }
            wtr.Write(row);
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType,
        int cxDesired, int cyDesired, uint fuFlags);

    private const uint IMAGE_CURSOR = 2;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    /// <summary>
    /// 生成一个系统「认得」的 .cur 文件并加载它（SetSystemCursor 要求这种）。
    /// 返回 0 表示失败。
    /// </summary>
    public static IntPtr LoadAsSystemCursor(Bitmap bmp, int hotX, int hotY, out string curPath)
    {
        curPath = Path.Combine(Path.GetTempPath(),
            $"SpenTablet-marker-{(uint)bmp.Width}x{(uint)bmp.Height}.cur");
        try
        {
            WriteCurFile(curPath, bmp, hotX, hotY);
            IntPtr h = LoadImage(IntPtr.Zero, curPath, IMAGE_CURSOR, 0, 0,
                LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (h == IntPtr.Zero)
                AppLog.Warn($"[Cursor] LoadImage 失败 err={Marshal.GetLastWin32Error()} 文件={curPath}");
            return h;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[Cursor] 生成 .cur 失败：{ex.Message}");
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyIcon(Handle);
            Handle = IntPtr.Zero;
        }
        Image?.Dispose();
    }

    /// <summary>
    /// 生成 .cur 文件并用 LoadImage 加载 —— 只有这种光标系统才认，
    /// SetSystemCursor 会拒绝 CreateIconIndirect 造出来的光标。
    /// </summary>
    public IntPtr LoadAsSystemCursor()
    {
        IntPtr h = LoadAsSystemCursor(Image, HotspotX, HotspotY, out _);
        return h;
    }
}
