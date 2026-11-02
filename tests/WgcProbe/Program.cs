using System;
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

namespace WgcProbe
{
    /// <summary>
    /// 用 Windows.Graphics.Capture 抓屏（IsCursorCaptureEnabled = true），
    /// 目的是拍到 GDI BitBlt 拍不到的叠加层：硬件光标 + 系统触摸视觉。
    ///
    /// 能力自检：把光标摆到指定点，抓一帧，看该点附近是否出现光标图形 ——
    /// 这一步决定「拍不到」到底是真没有，还是能力不足。
    ///
    /// 用法:
    ///   WgcProbe --selftest                     光标放到主屏中心后抓 1 帧
    ///   WgcProbe --frames 12 --gap 120          连续抓帧（配合外部触摸注入）
    /// </summary>
    internal static class Program
    {
        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
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
            int frames = 1, gap = 150, pad = 320, delayMs = 0;
            bool selfTest = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--gap": gap = int.Parse(args[++i]); break;
                    case "--pad": pad = int.Parse(args[++i]); break;
                    case "--delay": delayMs = int.Parse(args[++i]); break;
                    case "--selftest": selfTest = true; break;
                }
            }

            string outDir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);

            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);

            if (selfTest)
            {
                SetCursorPos(sw / 2, sh / 2);
                Thread.Sleep(200);
            }
            GetCursorPos(out var cp);
            Console.WriteLine($"屏幕 {sw}x{sh}  光标 ({cp.X},{cp.Y})");

            var d3d = D3D11.D3D11CreateDevice(
                DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                FeatureLevel.Level_11_0);
            using var dxgiDevice = d3d.QueryInterface<IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr rtDevPtr);
            if (hr != 0) { Console.WriteLine($"CreateDirect3D11DeviceFromDXGIDevice hr=0x{hr:X}"); return 3; }
            var rtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(rtDevPtr);
            Marshal.Release(rtDevPtr);
            Console.WriteLine("D3D11 / WinRT 设备就绪");

            var hmon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
            IntPtr itemPtr = CreateItemForMonitor(hmon);
            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            Console.WriteLine($"捕获项尺寸 {item.Size.Width}x{item.Size.Height}");

            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            using var session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = true; Console.WriteLine("IsCursorCaptureEnabled = true"); }
            catch (Exception ex) { Console.WriteLine("设置光标捕获失败: " + ex.Message); }
            session.StartCapture();

            if (delayMs > 0) Thread.Sleep(delayMs);

            int saved = 0;
            int cx0 = Math.Max(0, Math.Min(sw - pad * 2, cp.X - pad));
            int cy0 = Math.Max(0, Math.Min(sh - pad * 2, cp.Y - pad));
            for (int i = 0; i < frames; i++)
            {
                var frame = WaitFrame(pool, 3000);
                if (frame == null) { Console.WriteLine($"帧{i}: 超时"); continue; }
                using (frame)
                {
                    var bmp = ReadTexture(d3d, frame);
                    int x0 = Math.Max(0, Math.Min(bmp.Width - pad * 2, cp.X - pad));
                    int y0 = Math.Max(0, Math.Min(bmp.Height - pad * 2, cp.Y - pad));
                    int w = Math.Min(pad * 2, bmp.Width - x0);
                    int h = Math.Min(pad * 2, bmp.Height - y0);
                    string path = Path.Combine(outDir, $"wgc_{i:00}.bmp");
                    SaveBmpCrop(path, bmp, x0, y0, w, h);
                    Console.WriteLine($"帧{i}: {path}  裁剪({x0},{y0}) {w}x{h}  光标在图内 ({cp.X - x0},{cp.Y - y0})");
                    saved++;
                }
                if (i + 1 < frames) Thread.Sleep(gap);
            }

            session.Dispose();
            Console.WriteLine($"完成，共 {saved} 帧 → {outDir}");
            return 0;
        }

        /// <summary>把 WinRT 投影对象 QI 成原始接口指针（RCW 走 ComImport 会 InvalidCast，所以直接调 vtable）。</summary>
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
            // IUnknown: 0 QI, 1 AddRef, 2 Release ; interop: 3 CreateForWindow, 4 CreateForMonitor
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
            if (hr != 0) throw new COMException($"GetInterface(ID3D11Texture2D) hr=0x{hr:X}");
            return res;
        }

        private static Direct3D11CaptureFrame WaitFrame(Direct3D11CaptureFramePool pool, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var f = pool.TryGetNextFrame();
                if (f != null) return f;
                Thread.Sleep(16);
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
            finally
            {
                ctx.Unmap(staging, 0);
            }
        }

        private static void SaveBmpCrop(string path, RawBmp bmp, int x0, int y0, int w, int h)
        {
            int stride = w * 4;
            int imgSize = stride * h;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
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
            for (int y = 0; y < h; y++)
            {
                int src = ((y + y0) * bmp.Width + x0) * 4;
                bw.Write(bmp.Data, src, stride);
            }
        }
    }
}
