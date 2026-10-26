using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SpenTabletHost;

/// <summary>
/// TCP 服务器：接收手机端发来的二进制笔事件帧，解析后交给 IInjector 注入到 Windows。
/// 帧格式（小端）：magic('S','P') + version(1) + type(1) + length(2) + payload(length)
/// 笔事件 payload(24B)：x(float) y(float) pressure(float) tiltX(float) tiltY(float) buttons(uint16) reserved(uint16)
/// </summary>
public sealed class TcpPenServer : IDisposable
{
    private const byte MAGIC1 = 0x53; // 'S'
    private const byte MAGIC2 = 0x50; // 'P'
    private const byte TYPE_PEN_DOWN = 0x01;
    private const byte TYPE_PEN_MOVE = 0x02;
    private const byte TYPE_PEN_UP = 0x03;
    private const byte TYPE_PEN_HOVER = 0x04;
    private const byte TYPE_HELLO = 0x05;
    private const byte TYPE_PAN = 0x06;
    private const byte TYPE_MOUSE = 0x07;
    private const byte TYPE_MODE = 0x08;
    private const byte TYPE_ZOOM = 0x09;
    private const byte TYPE_RESET = 0x0A;
    private const byte TYPE_STATUS = 0x0B;

    // 鼠标帧 kind（与手机端 Protocol.MOUSE_* 一致）
    private const int MOUSE_MOVE = 0;
    private const int MOUSE_LEFT_CLICK = 3;
    private const int MOUSE_RIGHT_CLICK = 4;

    private readonly TcpListener _listener;
    private readonly MappingConfig _cfg;
    private readonly IInjector _injector;
    private CancellationTokenSource? _cts;

    /// <summary>各类型帧计数，用于控制台周期性汇报（确认链路是否真的在跑）。</summary>
    public long CountDown, CountMove, CountUp, CountHover, CountNoop;
    public double LastPressure;
    public int LastX, LastY;

    /// <summary>每收到一帧并映射成 PenSample 后回调（用于调试浮窗）。</summary>
    public Action<PenSample>? OnSample { get; set; }

    public TcpPenServer(MappingConfig cfg, IInjector injector)
    {
        _cfg = cfg;
        _injector = injector;
        _listener = new TcpListener(IPAddress.Any, cfg.ListenPort);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        Console.WriteLine($"[Server] 监听端口 {_cfg.ListenPort}，等待手机连接（adb reverse tcp:{0} tcp:{0} 或填 PC IP）", _cfg.ListenPort);
        _ = AcceptLoop(_cts.Token);
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                Console.WriteLine($"[Server] 手机已连接：{client.Client.RemoteEndPoint}");
                _ = Task.Run(() => HandleClient(client, token), token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[Server] Accept 异常：{ex.Message}"); }
        }
    }

    private void HandleClient(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            using (var ns = client.GetStream())
            {
                ns.ReadTimeout = Timeout.Infinite;
                var header = new byte[6];
                while (!token.IsCancellationRequested)
                {
                    if (!ReadExactly(ns, header, 6)) break;
                    if (header[0] != MAGIC1 || header[1] != MAGIC2)
                    {
                        Console.Error.WriteLine("[Server] 帧头错误，丢弃连接");
                        break;
                    }
                    int len = header[4] | (header[5] << 8);
                    var payload = new byte[len];
                    if (!ReadExactly(ns, payload, len)) break;

                    switch (header[3])
                    {
                        case TYPE_HELLO:
                            HandleHello(payload);
                            break;
                        case TYPE_PAN:
                            HandlePan(payload);
                            break;
                        case TYPE_MOUSE:
                            HandleMouse(payload);
                            break;
                        case TYPE_MODE:
                            HandleMode(payload);
                            break;
                        case TYPE_ZOOM:
                            HandleZoom(payload);
                            break;
                        case TYPE_RESET:
                            HandleReset(payload);
                            break;
                        case TYPE_STATUS:
                            HandleStatus(payload);
                            break;
                        case TYPE_PEN_DOWN:
                        case TYPE_PEN_MOVE:
                        case TYPE_PEN_UP:
                        case TYPE_PEN_HOVER:
                            Dispatch(header[3], payload);
                            break;
                        default:
                            CountNoop++;
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Server] 客户端处理异常：{ex.Message}");
        }
        finally
        {
            Console.WriteLine("[Server] 手机断开");
        }
    }

    /// <summary>握手帧：手机上报绘制面像素尺寸与比例偏好，用于做正确的等比映射。</summary>
    private void HandleHello(byte[] p)
    {
        if (p.Length < 12) return;
        float w = BitConverter.ToSingle(p, 0);
        float h = BitConverter.ToSingle(p, 4);
        float mode = BitConverter.ToSingle(p, 8);
        float work = p.Length >= 16 ? BitConverter.ToSingle(p, 12) : 0f;
        float frameL = p.Length >= 20 ? BitConverter.ToSingle(p, 16) : -1f;
        float frameT = p.Length >= 24 ? BitConverter.ToSingle(p, 20) : -1f;
        float frameScale = p.Length >= 28 ? BitConverter.ToSingle(p, 24) : -1f;
        _cfg.SetPhoneHello(w, h, mode, work, frameL, frameT, frameScale);
        _zoomLog = 0;
        var r = _cfg.GetActiveRect();
        AppLog.Info($"[Hello] 手机书写区 {w:0}×{h:0} 宽高比={(_cfg.PhoneAspect > 0 ? _cfg.PhoneAspect : 0):0.###} " +
                    $"偏好={(mode >= 1f ? "保持等比" : "拉伸")}");
        AppLog.Info($"[Hello] 工作模式={(_cfg.IsMouseMode ? "鼠标" : "数位板")} " +
                    $"操作框 缩放×{_cfg.Scale:0.###} 位置({_cfg.FrameL:0.###},{_cfg.FrameT:0.###}) " +
                    $"→ 桌面映射区域 {r.x},{r.y} {r.w}×{r.h}");
    }

    private int _panLog;
    private int _panIgnored;

    /// <summary>手指滑动平移：移动桌面上的“操作范围框”。</summary>
    private void HandlePan(byte[] p)
    {
        if (p.Length < 8) return;
        float dx = BitConverter.ToSingle(p, 0);
        float dy = BitConverter.ToSingle(p, 4);
        // 互斥保险：鼠标模式下手指是触摸板，绝不再去拖动范围框
        if (_cfg.IsMouseMode)
        {
            if (_panIgnored++ % 20 == 0)
                AppLog.Warn("[Pan] 收到平移帧，但当前是鼠标模式，已忽略（手指应只控鼠标）");
            return;
        }
        _cfg.Pan(dx, dy);
        // 滑动会产生大量帧，抽样打日志避免刷屏（第一条一定打，方便确认链路）
        if (_panLog++ % 12 == 0)
        {
            var r = _cfg.GetActiveRect();
            AppLog.Info($"[Pan] 平移 dx={dx:F3} dy={dy:F3} → 位置=({_cfg.FrameL:F3},{_cfg.FrameT:F3}) " +
                        $"桌面范围 {r.x},{r.y} {r.w}×{r.h}");
        }
    }

    /// <summary>模式帧：手机在数位板 / 鼠标之间切换。</summary>
    private void HandleMode(byte[] p)
    {
        if (p.Length < 4) return;
        bool mouse = BitConverter.ToSingle(p, 0) >= 1f;
        if (mouse == _cfg.IsMouseMode) return;
        _cfg.SetWorkMode(mouse);
        MouseControl.ReleaseAll();     // 避免左键卡在按下状态
        AppLog.Info(mouse
            ? "[Mode] 切换到【鼠标模式】：取消操作范围框，手指=触摸板，单击=左键，双击=右键，S Pen=绝对定位鼠标笔"
            : "[Mode] 切换到【数位板模式】：显示操作范围框，S Pen 在框内作画，手指滑动=移动范围框");
    }

    private int _zoomLog;

    /// <summary>缩放帧：手指双指捏合 → 缩放桌面上的操作范围框（以捏合中心为锚点）。</summary>
    private void HandleZoom(byte[] p)
    {
        if (p.Length < 12) return;
        float factor = BitConverter.ToSingle(p, 0);
        float fx = BitConverter.ToSingle(p, 4);
        float fy = BitConverter.ToSingle(p, 8);
        // 互斥保险：鼠标模式下没有范围框，缩放无意义
        if (_cfg.IsMouseMode)
        {
            if (_zoomIgnored++ % 20 == 0)
                AppLog.Warn("[Zoom] 收到缩放帧，但当前是鼠标模式（无范围框），已忽略");
            return;
        }
        _cfg.Zoom(factor, fx, fy);
        if (++_zoomLog % 6 == 1)
        {
            var r = _cfg.GetActiveRect();
            AppLog.Info($"[Zoom] ×{factor:F3} 焦点({fx:F2},{fy:F2}) → 缩放={_cfg.Scale:F3} " +
                        $"位置=({_cfg.FrameL:F3},{_cfg.FrameT:F3}) 桌面范围 {r.x},{r.y} {r.w}×{r.h}");
        }
    }

    /// <summary>状态查询帧：把当前操作框状态打进日志（调试/自测用）。</summary>
    private void HandleStatus(byte[] p)
    {
        _zoomLog = 0;
        var r = _cfg.GetActiveRect();
        AppLog.Info($"[Status] 缩放={_cfg.Scale:F6} 位置=({_cfg.FrameL:F6},{_cfg.FrameT:F6}) " +
                    $"桌面范围 {r.x},{r.y} {r.w}×{r.h} 宽高比={(r.h > 0 ? (double)r.w / r.h : 0):F6}");
    }

    /// <summary>复位帧：手机端把操作框恢复到整个书写区。</summary>
    private void HandleReset(byte[] p)
    {
        _cfg.ResetView();
        _zoomLog = 0;          // 复位后重新计数，日志从第 1 帧开始打
        var r = _cfg.GetActiveRect();
        AppLog.Info($"[Reset] 操作框已复位 → 桌面范围 {r.x},{r.y} {r.w}×{r.h}");
    }

    private int _zoomIgnored;
    private int _mouseLog;

    /// <summary>鼠标帧：手指当触摸板（相对移动 / 左键 / 右键）。</summary>
    private void HandleMouse(byte[] p)
    {
        if (p.Length < 12) return;
        int kind = (int)BitConverter.ToSingle(p, 0);
        float dx = BitConverter.ToSingle(p, 4);
        float dy = BitConverter.ToSingle(p, 8);

        switch (kind)
        {
            case MOUSE_MOVE:
                int mx = (int)Math.Round(dx * _cfg.VirtualW * _cfg.MouseSpeed);
                int my = (int)Math.Round(dy * _cfg.VirtualH * _cfg.MouseSpeed);
                MouseControl.MoveBy(_cfg, mx, my);
                if (_mouseLog++ % 20 == 0)
                    AppLog.Info($"[Mouse] 移动 dx={dx:F3} dy={dy:F3} → 光标位移 ({mx},{my}) 增益={_cfg.MouseSpeed:0.##}");
                break;
            case MOUSE_LEFT_CLICK:
                MouseControl.LeftClick();
                AppLog.Info("[Mouse] 单击 → 左键");
                break;
            case MOUSE_RIGHT_CLICK:
                MouseControl.RightClick();
                AppLog.Info("[Mouse] 双击 → 右键");
                break;
            default:
                break;
        }
    }

    private void Dispatch(byte type, byte[] p)
    {
        if (p.Length < 24) return;
        float x = BitConverter.ToSingle(p, 0);
        float y = BitConverter.ToSingle(p, 4);
        float pressure = BitConverter.ToSingle(p, 8);
        float tiltX = BitConverter.ToSingle(p, 12);
        float tiltY = BitConverter.ToSingle(p, 16);
        ushort buttons = BitConverter.ToUInt16(p, 20);

        var state = type switch
        {
            TYPE_PEN_DOWN => PenState.Down,
            TYPE_PEN_UP => PenState.Up,
            TYPE_PEN_HOVER => PenState.Hover,
            _ => PenState.Move,
        };

        // 鼠标模式：S Pen 当绝对定位的鼠标笔（覆盖整块虚拟屏幕），不再走合成笔注入
        if (_cfg.IsMouseMode)
        {
            var (mx, my) = _cfg.ToPixelsFull(x, y);
            LastX = mx; LastY = my; LastPressure = pressure;
            CountMousePen(state);
            MouseControl.MoveTo(_cfg, mx, my);
            if (state == PenState.Down) MouseControl.Down();
            else if (state == PenState.Up) MouseControl.Up();
            if (state == PenState.Down || state == PenState.Up)
                AppLog.Info($"[MousePen] {(state == PenState.Down ? "按下" : "抬起")} ({mx},{my}) p={pressure:F2}");
            return;
        }

        var (px, py) = _cfg.ToPixels(x, y);
        switch (type)
        {
            case TYPE_PEN_DOWN: CountDown++; break;
            case TYPE_PEN_UP: CountUp++; break;
            case TYPE_PEN_HOVER: CountHover++; break;
            default: CountMove++; break;
        }
        LastX = px; LastY = py; LastPressure = pressure;
        var sample = new PenSample(px, py, pressure, tiltX, tiltY, 0f,
                                   barrel: (buttons & 0x1) != 0,
                                   eraser: (buttons & 0x2) != 0,
                                   state);
        if (type == TYPE_PEN_DOWN || type == TYPE_PEN_UP)
            Console.WriteLine($"[Pen] {(type == TYPE_PEN_DOWN ? "DOWN" : "UP")} x={px} y={py} pressure={pressure:F2} tilt=({tiltX:F0},{tiltY:F0})");
        OnSample?.Invoke(sample);
        _injector.Inject(in sample);
    }

    private void CountMousePen(PenState s)
    {
        switch (s)
        {
            case PenState.Down: CountDown++; break;
            case PenState.Up: CountUp++; break;
            case PenState.Hover: CountHover++; break;
            default: CountMove++; break;
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read == 0) return false; // 连接关闭
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        _cts?.Dispose();
    }
}
