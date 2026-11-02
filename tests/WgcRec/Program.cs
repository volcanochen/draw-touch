using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WgcRec
{
    /// <summary>
    /// 全屏连续抓帧 + 与基线逐帧差分定位。
    ///
    /// 目的：抓着「pan 时跟着触点走的那个图元」。GDI BitBlt 拍不到叠加层，
    /// 这里用 Windows.Graphics.Capture（IsCursorCaptureEnabled=true，实测能拍到硬件光标）。
    ///
    /// 用法: WgcRec --dur 40 --fps 8
    /// 输出: shots/base.bmp（基线，1/3 缩放）+ shots/dXY_NN.bmp（每帧变化区域的原分辨率裁剪）
    /// </summary>
    internal static class Program
    {
        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        private static int Main(string[] args)
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            int durSec = 40, fps = 8;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--dur": durSec = int.Parse(args[++i]); break;
                    case "--fps": fps = int.Parse(args[++i]); break;
                }
            }
            int gapMs = Math.Max(60, 1000 / Math.Max(1, fps));

            string outDir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);
            foreach (var f in Directory.GetFiles(outDir, "*.bmp")) File.Delete(f);

            var d3d = D3D11.D3D11CreateDevice(
                DriverType.Hardware, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
            using var dxgiDevice = d3d.QueryInterface<IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr rtDevPtr);
            if (hr != 0) { Console.WriteLine($"CreateDirect3D11DeviceFromDXGIDevice hr=0x{hr:X}"); return 3; }
            var rtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(rtDevPtr);
            Marshal.Release(rtDevPtr);

            var hmon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
            IntPtr itemPtr = CreateItemForMonitor(hmon);
            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            Console.WriteLine($"捕获项 {item.Size.Width}x{item.Size.Height}  时长 {durSec}s  {fps}fps");

            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = true;
            session.StartCapture();

            RawBmp? baseline = null;
            int idx = 0, saved = 0;
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed.TotalSeconds < durSec)
            {
                var frame = WaitFrame(pool, 2000);
                if (frame == null) { Console.WriteLine("帧超时"); break; }
                using (frame)
                {
                    var bmp = ReadTexture(d3d, frame);
                    GetCursorPos(out var cp);
                    string ts = DateTime.Now.ToString("HH:mm:ss.fff");

                    if (baseline == null)
                    {
                        baseline = bmp;
                        SaveBmpScaled(Path.Combine(outDir, "base.bmp"), bmp, 3);
                        Console.WriteLine($"{ts} 基线已存 ({bmp.Width}x{bmp.Height}) 光标({cp.X},{cp.Y})");
                    }
                    else
                    {
                        int cnt; int bx0, by0, bx1, by1;
                        Diff(baseline.Value, bmp, out cnt, out bx0, out by0, out bx1, out by1);
                        if (cnt > 150)
                        {
                            // 扩边 80px，至少 320x320
                            int mx = (bx1 - bx0), my = (by1 - by0);
                            int ex = Math.Max(80, (320 - mx) / 2), ey = Math.Max(80, (320 - my) / 2);
                            int x0 = Math.Max(0, bx0 - ex), y0 = Math.Max(0, by0 - ey);
                            int x1 = Math.Min(bmp.Width - 1, bx1 + ex), y1 = Math.Min(bmp.Height - 1, by1 + ey);
                            string path = Path.Combine(outDir, $"d{idx:00}_{saved:00}.bmp");
                            SaveBmpCrop(path, bmp, x0, y0, x1 - x0 + 1, y1 - y0 + 1);
                            Console.WriteLine($"{ts} 帧{idx} 差异={cnt,7} bbox=({bx0},{by0})-({bx1},{by1}) 光标({cp.X},{cp.Y}) → {Path.GetFileName(path)} ⊕({x0},{y0})");
                            saved++;
                        }
                        else
                        {
                            Console.WriteLine($"{ts} 帧{idx} 差异={cnt,7} 光标({cp.X},{cp.Y})");
                        }
                    }
                }
                idx++;
                Thread.Sleep(gapMs);
                if (saved >= 40) { Console.WriteLine("已存 40 张裁剪，提前结束"); break; }
            }

            session.Dispose();
            Console.WriteLine($"完成：{idx} 帧，存了 {saved} 张变化裁剪 → {outDir}");
            return 0;
        }

        /// <summary>逐像素差分：单通道差 &gt; 12 记为变化；给出变化总数与包围盒。</summary>
        private static void Diff(RawBmp a, RawBmp b, out int count, out int x0, out int y0, out int x1, out int y1)
        {
            count = 0; x0 = int.MaxValue; y0 = int.MaxValue; x1 = -1; y1 = -1;
            int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
            for (int y = 0; y < h; y++)
            {
                int row = y * a.Width * 4;
                int rowB = y * b.Width * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4, j = rowB + x * 4;
                    int db = Math.Abs(a.Data[i] - b.Data[j]);
                    int dg = Math.Abs(a.Data[i + 1] - b.Data[j + 1]);
                    int dr = Math.Abs(a.Data[i + 2] - b.Data[j + 2]);
                    if (db > 12 || dg > 12 || dr > 12)
                    {
                        count++;
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
                }
            }
            if (count == 0) { x0 = y0 = 0; x1 = y1 = 0; }
        }

        private static IntPtr QIPtr(object o, Guid iid)
        {
            IntPtr unk;
            if (o is IWinRTObject iwr) unk = iwr.NativeObject.ThisPtr;
            else if (o is IObjectReference ior) unk = ior.ThisPtr;
            else unk = Marshal.GetIUnknownForObject(o);
            int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr p);
            if (hr != 0) throw new COMException($"QI {iid} 失败 hr=0x{hr:X}");
            return p;
        }

        private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

        private static unsafe IntPtr CreateItemForMonitor(IntPtr hmon)
        {
            var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            IntPtr pInterop = QIPtr(factory, IID_IGraphicsCaptureItemInterop);
            IntPtr vtbl = *(IntPtr*)pInterop;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)
                     (*(IntPtr*)(vtbl + 4 * IntPtr.Size));
            Guid iid = IID_IGraphicsCaptureItem;
            IntPtr res;
            int hr = fn(pInterop, hmon, &iid, &res);
            if (hr != 0) throw new COMException($"CreateForMonitor hr=0x{hr:X}");
            return res;
        }

        private static unsafe IntPtr GetDxgiInterface(IntPtr pAccess)
        {
            IntPtr vtbl = *(IntPtr*)pAccess;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                     (*(IntPtr*)(vtbl + 3 * IntPtr.Size));
            Guid iid = typeof(ID3D11Texture2D).GUID;
            IntPtr res;
            int hr = fn(pAccess, &iid, &res);
            if (hr != 0) throw new COMException($"GetInterface hr=0x{hr:X}");
            return res;
        }

        private static Direct3D11CaptureFrame WaitFrame(Direct3D11CaptureFramePool pool, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var f = pool.TryGetNextFrame();
                if (f != null) return f;
                Thread.Sleep(10);
            }
            return null;
        }

        private struct RawBmp { public int Width, Height; public byte[] Data; }

        private static RawBmp ReadTexture(ID3D11Device d3d, Direct3D11CaptureFrame frame)
        {
            IntPtr pAccess = QIPtr(frame.Surface, IID_IDirect3DDxgiInterfaceAccess);
            IntPtr texPtr = GetDxgiInterface(pAccess);
            using var tex = new ID3D11Texture2D(texPtr);

            var desc = tex.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.BindFlags = BindFlags.None;
            desc.MiscFlags = ResourceOptionFlags.None;
            desc.ArraySize = 1;
            desc.MipLevels = 1;

            using var staging = d3d.CreateTexture2D(desc);
            var ctx = d3d.ImmediateContext;
            ctx.CopyResource(staging, tex);
            var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int w = (int)desc.Width, h = (int)desc.Height;
                var data = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch), data, y * w * 4, w * 4);
                return new RawBmp { Width = w, Height = h, Data = data };
            }
            finally { ctx.Unmap(staging, 0); }
        }

        private static void SaveBmpCrop(string path, RawBmp bmp, int x0, int y0, int w, int h)
        {
            int stride = w * 4, imgSize = stride * h;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            WriteBmpHeader(bw, w, h, imgSize);
            for (int y = 0; y < h; y++)
            {
                int src = ((y + y0) * bmp.Width + x0) * 4;
                bw.Write(bmp.Data, src, stride);
            }
        }

        private static void SaveBmpScaled(string path, RawBmp bmp, int factor)
        {
            int w = bmp.Width / factor, h = bmp.Height / factor;
            int stride = w * 4, imgSize = stride * h;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            WriteBmpHeader(bw, w, h, imgSize);
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                int sy = y * factor;
                for (int x = 0; x < w; x++)
                {
                    int s = (sy * bmp.Width + x * factor) * 4;
                    row[x * 4] = bmp.Data[s]; row[x * 4 + 1] = bmp.Data[s + 1];
                    row[x * 4 + 2] = bmp.Data[s + 2]; row[x * 4 + 3] = bmp.Data[s + 3];
                }
                bw.Write(row);
            }
        }

        private static void WriteBmpHeader(BinaryWriter bw, int w, int h, int imgSize)
        {
            bw.Write((ushort)0x4D42);
            bw.Write(14 + 40 + imgSize);
            bw.Write(0);
            bw.Write(14 + 40);
            bw.Write(40);
            bw.Write(w);
            bw.Write(-h);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(0);
            bw.Write(imgSize);
            bw.Write(2835);
            bw.Write(2835);
            bw.Write(0);
            bw.Write(0);
        }
    }
}
