using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SpenTabletHost;

/// <summary>
/// 荧光笔光标：把 Windows 的箭头指针换成**马克笔造型**。
///
/// 做法：用 GDI+ 画一支荧光笔（笔身 + 笔帽 + 高光条 + 笔尖），
/// 再用 CreateIconIndirect（fIcon=false）做成 32 位**彩色**系统光标，
/// 之后 SetCursor 就能把桌面指针换成它。热点（hotspot）落在笔尖上 ——
/// 和真荧光笔一样，指哪涂哪。
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
    /// 画一支荧光笔：笔身斜指左下，笔尖在左下角，热点就在笔尖。
    /// </summary>
    private static Bitmap Draw(Color color, int size, out int hotX, out int hotY)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float bw = size * 0.34f;      // 笔身宽
        float bl = size * 0.70f;      // 笔身长
        float tipLen = size * 0.22f;  // 笔尖长度

        var body = new SolidBrush(color);
        var bodyDark = new SolidBrush(Darken(color, 0.72f));
        var cap = new SolidBrush(Color.FromArgb(235, 60, 60, 70));
        var tip = new SolidBrush(Color.FromArgb(235, 45, 45, 55));
        var sheen = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
        var outline = new Pen(Color.FromArgb(200, 20, 20, 25), Math.Max(1f, size * 0.035f));

        var m = new Matrix();
        m.Translate(size * 0.56f, size * 0.46f);
        m.Rotate(45f);               // 笔尖朝左下（和常规笔/箭头光标一致）
        float tipLocalY = bl / 2 + tipLen;   // 笔尖在局部坐标里的位置

        var st = g.Save();
        g.Transform = m;

        // 笔身（圆头）
        g.FillEllipse(body, -bw / 2, -bl / 2, bw, bl);
        g.DrawEllipse(outline, -bw / 2, -bl / 2, bw, bl);
        // 笔帽
        g.FillRectangle(cap, -bw / 2, -bl / 2, bw, size * 0.16f);
        // 高光条
        g.FillRectangle(sheen, -bw * 0.20f, -bl / 2 + size * 0.10f, bw * 0.20f, bl * 0.62f);
        // 笔尖（三角形，尖端在下方）
        var tipPts = new[]
        {
            new PointF(-bw / 2, bl / 2 - 1),
            new PointF(bw / 2, bl / 2 - 1),
            new PointF(0, bl / 2 + tipLen)
        };
        g.FillPolygon(tip, tipPts);
        g.DrawPolygon(outline, tipPts);
        g.Restore(st);

        // 热点 = 笔尖在画布上的位置。
        // 不靠矩阵手算（平移/旋转的合成顺序容易搞反，取到对角就废了），
        // 而是拿同一套变换画一个品红探针点，再扫描出它的位置 —— 准。
        {
            using var probe = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var pg = Graphics.FromImage(probe))
            {
                pg.SmoothingMode = SmoothingMode.AntiAlias;
                pg.Clear(Color.Transparent);
                pg.Transform = m;
                pg.FillEllipse(new SolidBrush(Color.Magenta), -1.2f, tipLocalY - 1.2f, 2.4f, 2.4f);
            }
            hotX = size / 2;
            hotY = size / 2;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var c = probe.GetPixel(x, y);
                    if (c.R > 200 && c.B > 200 && c.G < 60) { hotX = x; hotY = y; break; }
                }
            }
        }

        body.Dispose(); bodyDark.Dispose(); cap.Dispose(); tip.Dispose(); sheen.Dispose(); outline.Dispose();
        return bmp;
    }

    private static Color Darken(Color c, float f)
    {
        return Color.FromArgb(c.A,
            (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
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
