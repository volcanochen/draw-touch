using System;
using System.Linq;
using System.Windows.Forms;

namespace SpenTabletHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 托盘程序没有控制台，任何未捕获异常都会让进程**静默消失**（表现为"主机自己关了"）。
        // 这里统一兜住并写日志，方便定位（mDNS / 网络线程最容易出这种问题）。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            AppLog.Error($"未捕获异常（IsTerminating={e.IsTerminating}）：{ex?.GetType().Name} {ex?.Message}");
            if (ex != null) AppLog.Error(ex.StackTrace ?? "");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error($"未观察到的任务异常：{e.Exception.GetType().Name} {e.Exception.Message}");
            e.SetObserved();
        };

        // --config：启动后直接打开设置窗口（方便双击排障，也方便截图核对界面）
        bool openConfig = args != null && args.Any(a =>
            a.Equals("--config", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/config", StringComparison.OrdinalIgnoreCase));
        // ---- DPI 感知：必须让 WinForms 自己接管 ----
        // 合成笔注入用的是物理像素，进程一定要 DPI 感知，否则 GetSystemMetrics
        // 返回的是虚拟化尺寸，坐标映射会整体错位一个缩放倍率。
        //
        // 但**不能**自己用 P/Invoke 先设进程感知：那样 WinForms 仍会按 SystemAware
        // 做布局，窗口被系统按 96/168 缩小，而文字仍按 168DPI 渲染，
        // 结果就是「窗口很小、内容全被裁掉」（用户截图就是这个问题）。
        // 让 Application.SetHighDpiMode 统一处理，WinForms 才知道自己是 PerMonitorV2。
        if (!Application.SetHighDpiMode(HighDpiMode.PerMonitorV2))
        {
            if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                SetProcessDPIAware();
        }

        // 必须在创建任何控件之前调用，否则桌面范围框的视觉样式不对
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---- WinForms UI 线程异常：必须接住，否则就是「进程悄悄消失」----
        // 托盘程序没有控制台，NoteOverlay / RegionFrame / TrayApp 这些 UI 线程上的异常
        // 默认会弹一个「未处理的异常」对话框（后台线程上甚至直接退出），
        // 而且这里若不注册 Application.ThreadException，日志里**一个字都不会有**，
        // 事后既没有 WER 报告也没有转储，根本没法查。注册后：记日志 + 继续跑。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            var ex = e.Exception;
            AppLog.Error($"[UI线程异常] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            if (ex.InnerException != null)
                AppLog.Error($"[UI线程异常·内层] {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");
        };
        // 进程退出轨迹：留下「是正常退出还是被外力终止」的证据（崩溃排查时能省掉一大圈猜测）
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.Info("[Host] 进程退出（ProcessExit，属正常/托管退出）");

        var cfg = ConfigStore.Load();
        cfg.ResolveVirtualScreen();

        AppLog.Info("=== SpenTablet Host（把三星 Note + S Pen 变成 PC 数位板）===");
        AppLog.Info($"虚拟屏幕: X={cfg.VirtualX} Y={cfg.VirtualY} W={cfg.VirtualW} H={cfg.VirtualH}");
        AppLog.Info($"注入模式: {cfg.Mode}｜比例: {cfg.Aspect}｜工作模式: {cfg.WorkMode}｜端口: {cfg.ListenPort}");
        AppLog.Info($"配置文件: {ConfigStore.Path}");
        AppLog.Info($"日志文件: {AppLog.FilePath}");
        InputGuard.Init();

        // ---- 提前补平权限 ----
        // WorkBuddy 这类程序常以管理员身份运行。前台是它们时，普通权限的本进程
        // SendInput / SetCursorPos 会被 Windows 的 UIPI **静默丢弃** ——
        // 表现就是「指针在某些窗口上完全动不了，切到别的窗口又正常」。
        // 这里在启动时一次性解决（只会弹一次 UAC），之后全程可用。
        // 不想提权可以加参数 --no-elevate（此时靠 InputGuard 的气泡提示定向排障）。
        bool noElevate = args != null && args.Any(a =>
            a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase) ||
            a.Equals(InputGuard.RestartMarker, StringComparison.OrdinalIgnoreCase));   // 已提权重启过 → 不再尝试
        if (!InputGuard.IsElevated && !noElevate)
        {
            if (InputGuard.RelaunchElevated())
            {
                AppLog.Info("[Host] 已请求以管理员身份重启，本实例退出（把端口让给新实例）");
                return;
            }
            AppLog.Warn("[Host] 未取得管理员权限，继续以普通权限运行：" +
                        "当前台是管理员权限程序（如 WorkBuddy）时指针会控制不了");
        }

        IInjector injector = cfg.Mode.Equals("Mouse", StringComparison.OrdinalIgnoreCase)
            ? new MouseInjector(cfg.VirtualX, cfg.VirtualY, cfg.VirtualW, cfg.VirtualH)
            : new PenInjector();

        if (!injector.Initialize())
        {
            AppLog.Warn("合成笔设备创建失败，自动回退到鼠标模式（无压感）。");
            injector.Dispose();
            injector = new MouseInjector(cfg.VirtualX, cfg.VirtualY, cfg.VirtualW, cfg.VirtualH);
            injector.Initialize();
        }

        using var server = new TcpPenServer(cfg, injector);
        try
        {
            server.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            // 端口被占用 = 大概率已经有一个 SpenTabletHost 在跑（托盘里没退出干净）
            MessageBox.Show(
                $"端口 {cfg.ListenPort} 已被占用（{ex.SocketErrorCode}）。\n\n" +
                "多半是已经有一个 SpenTabletHost 在运行：\n" +
                "· 看看右下角托盘图标，双击打开的可能是旧版本界面；\n" +
                "· 请在托盘图标上右键 →「退出」，再重新启动本程序。\n\n" +
                "（也可以用 --config 参数启动，确认打开的是哪个版本）",
                "SpenTablet 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        void SetDebug(bool on)
        {
            if (on)
            {
                var overlay = DebugOverlay.Start();
                server.OnSample = s => overlay.UpdateSample(in s);
                AppLog.Info("[Host] 调试浮窗已开启");
            }
            else
            {
                DebugOverlay.HideOverlay();
                server.OnSample = null;
                AppLog.Info("[Host] 调试浮窗已关闭");
            }
        }

        if (cfg.Debug) SetDebug(true);

        // 桌面上的「操作范围框」：标出笔能覆盖的区域；映射变化或模式切换时自动跟随
        cfg.Changed += () => RegionFrame.Refresh(cfg);
        RegionFrame.Create(cfg);
        AppLog.Info($"操作范围框: {(cfg.ShowRegion && !cfg.IsMouseMode ? "显示" : "隐藏")}");

        AppLog.Info("提示：USB 连接时在 PC 上执行  adb reverse tcp:6000 tcp:6000");
        AppLog.Info("数位板模式：手指在手机屏上滑动平移操作范围框，S Pen 才用于绘制。");
        AppLog.Info("鼠标模式：取消范围框，手指=触摸板，单击=左键，双击=右键。");

        Application.Run(new TrayApp(cfg, server, SetDebug, openConfig));
    }

    private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(int value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}
