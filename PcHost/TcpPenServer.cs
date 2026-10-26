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

    private readonly TcpListener _listener;
    private readonly MappingConfig _cfg;
    private readonly IInjector _injector;
    private CancellationTokenSource? _cts;

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

                    if (header[3] >= TYPE_PEN_DOWN && header[3] <= TYPE_PEN_HOVER)
                        Dispatch(header[3], payload);
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

    private void Dispatch(byte type, byte[] p)
    {
        if (p.Length < 24) return;
        float x = BitConverter.ToSingle(p, 0);
        float y = BitConverter.ToSingle(p, 4);
        float pressure = BitConverter.ToSingle(p, 8);
        float tiltX = BitConverter.ToSingle(p, 12);
        float tiltY = BitConverter.ToSingle(p, 16);
        ushort buttons = BitConverter.ToUInt16(p, 20);

        var (px, py) = _cfg.ToPixels(x, y);
        var state = type switch
        {
            TYPE_PEN_DOWN => PenState.Down,
            TYPE_PEN_UP => PenState.Up,
            TYPE_PEN_HOVER => PenState.Hover,
            _ => PenState.Move,
        };
        var sample = new PenSample(px, py, pressure, tiltX, tiltY, 0f,
                                   barrel: (buttons & 0x1) != 0,
                                   eraser: (buttons & 0x2) != 0,
                                   state);
        if (type == TYPE_PEN_DOWN || type == TYPE_PEN_UP)
            Console.WriteLine($"[Pen] {(type == TYPE_PEN_DOWN ? "DOWN" : "UP")} x={px} y={py} pressure={pressure:F2} tilt=({tiltX:F0},{tiltY:F0})");
        OnSample?.Invoke(sample);
        _injector.Inject(in sample);
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
