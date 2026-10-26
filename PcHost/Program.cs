using System;
using System.Text.Json;
using System.Threading;

namespace SpenTabletHost;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("=== SpenTablet Host (把三星 Note + S Pen 变成 PC 数位板) ===");

        // 必须先开启 DPI 感知：合成笔注入用的是物理像素，
        // 若进程处于 DPI 无感知状态，GetSystemMetrics 会返回虚拟化后的尺寸（如 200% 缩放下只有一半），
        // 坐标映射就会整体错位一个缩放倍率。
        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            SetProcessDPIAware();

        var cfg = new MappingConfig();
        if (File.Exists("appsettings.json"))
        {
            try { cfg = JsonSerializer.Deserialize<MappingConfig>(File.ReadAllText("appsettings.json")) ?? cfg; }
            catch (Exception ex) { Console.Error.WriteLine($"读取 appsettings.json 失败，使用默认配置：{ex.Message}"); }
        }
        cfg.ResolveVirtualScreen();

        Console.WriteLine($"虚拟屏幕范围: X={cfg.VirtualX} Y={cfg.VirtualY} W={cfg.VirtualW} H={cfg.VirtualH}");
        Console.WriteLine($"映射模式: {cfg.Mode}");

        IInjector injector = cfg.Mode.Equals("Mouse", StringComparison.OrdinalIgnoreCase)
            ? new MouseInjector(cfg.VirtualX, cfg.VirtualY, cfg.VirtualW, cfg.VirtualH)
            : new PenInjector();

        if (!injector.Initialize())
        {
            Console.WriteLine("[Host] 合成笔设备创建失败，自动回退到鼠标模式（无压感）。");
            injector.Dispose();
            injector = new MouseInjector(cfg.VirtualX, cfg.VirtualY, cfg.VirtualW, cfg.VirtualH);
            injector.Initialize();
        }

        using var server = new TcpPenServer(cfg, injector);
        if (cfg.Debug)
        {
            var overlay = DebugOverlay.Start();
            server.OnSample = s => overlay.UpdateSample(in s);
            Console.WriteLine("[Host] 调试浮窗已开启");
        }
        server.Start();

        Console.WriteLine("提示：PC 端先执行  adb reverse tcp:6000 tcp:6000  （USB 连接时）。Ctrl+C 退出。");
        Console.WriteLine("手机端打开 SpenTablet，默认连 127.0.0.1:6000；USB 共享/WiFi 时改成 PC 的 IP。");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        int tick = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            Thread.Sleep(200);
            if (++tick % 10 == 0)   // 每 2 秒汇报一次，便于确认链路在跑
                Console.WriteLine($"[统计] DOWN={server.CountDown} MOVE={server.CountMove} UP={server.CountUp} " +
                                  $"HOVER={server.CountHover} | 最新 ({server.LastX},{server.LastY}) p={server.LastPressure:F2}");
        }

        Console.WriteLine("\n[Host] 正在退出…");
    }

    private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(int value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}
