using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DrawTouchHost;

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
    /// <summary>INFO 帧：连接握手后主机主动下发给手机，携带版本号/计算机名/候选 IP。</summary>
    private const byte TYPE_INFO = 0x0C;
    /// <summary>备注帧：控制桌面笔迹叠加层。payload[0]=action: 0=关 1=开 2=撤销 3=清空</summary>
    private const byte TYPE_NOTE = 0x0D;
    /// <summary>截图请求：手机要 PC 截取当前范围框画面</summary>
    private const byte TYPE_SHOT_REQ = 0x0E;
    /// <summary>截图数据（下行分片）：payload = totalLen(u32) + seq(u16) + data</summary>
    private const byte TYPE_SHOT = 0x0F;
    /// <summary>激光笔帧：payload[0]=开关(0/1)，payload[1]=颜色索引</summary>
    private const byte TYPE_HL = 0x10;
    /// <summary>截屏保存请求（上行）：payload[0]=范围(0=范围框 1=整块虚拟屏幕)，[1]=存 PC，[2]=传手机存相册</summary>
    private const byte TYPE_SAVE_REQ = 0x11;
    /// <summary>截屏数据（下行分片，格式同 TYPE_SHOT）</summary>
    private const byte TYPE_SAVE = 0x12;

    /// <summary>
    /// 协议兼容版本：握手（HELLO 的第 8 个 float / INFO 的 pv=）互相交换，
    /// 不一致就拒绝对接（手机黄灯警告、PC 拒收）。改协议时两端同步 +1。
    /// </summary>
    internal const int ProtoVer = 2;

    // 鼠标帧 kind（与手机端 Protocol.MOUSE_* 一致）
    private const int MOUSE_MOVE = 0;
    private const int MOUSE_LEFT_DOWN = 1;
    private const int MOUSE_LEFT_UP = 2;
    private const int MOUSE_LEFT_CLICK = 3;
    private const int MOUSE_RIGHT_CLICK = 4;
    private const int MOUSE_SCROLL = 5;
    private const int MOUSE_ZOOM = 6;
    private const int MOUSE_MIDDLE_CLICK = 7;
    private const int MOUSE_BACK = 8;
    private const int MOUSE_FORWARD = 9;
    private const int MOUSE_VOL_UP = 10;      // 音量 +
    private const int MOUSE_VOL_DOWN = 11;     // 音量 -
    private const int MOUSE_VOL_MUTE = 12;    // 静音
    private const int MOUSE_TASK_NEXT = 13;   // 任务切换 Alt+Tab
    private const int MOUSE_TASK_PREV = 14;   // 任务反向切换 Alt+Shift+Tab
    /// <summary>手指是否正落在触摸板上（1=在，0=离开）。用来让笔在手指操作期间让位。</summary>
    private const int MOUSE_FINGER = 15;
    /// <summary>左键双击：一次 SendInput 内发 下-上-下-上，避免被拆成两次单击。</summary>
    private const int MOUSE_LEFT_DOUBLE = 16;

    /// <summary>
    /// 手指划过整个书写区高度 = 滚动多少**像素**。
    /// 用像素而不是「格」，是因为 Windows 对「非 120 倍数」的滚轮增量会按
    /// 高分辨率滚动处理（和真实触摸板发出的 pan 一样），应用会平滑地按像素滚动，
    /// 而不会像鼠标滚轮那样一格一格跳。
    /// </summary>
    private const double SCROLL_PIXELS_PER_SCREEN = 360.0;
    /// <summary>捏合放大 100% ≈ 多少像素的 Ctrl+滚轮（绘图软件里即缩放画布）。</summary>
    private const double ZOOM_PIXELS_PER_FULL = 220.0;

    private readonly TcpListener _listener;
    private readonly MappingConfig _cfg;
    private readonly IInjector _injector;
    private CancellationTokenSource? _cts;
    private MdnsAdvertiser? _mdns;
    /// <summary>当前连接手机的流（握手后下发 INFO 帧用）。</summary>
    private NetworkStream? _clientStream;
    /// <summary>桌面笔迹叠加层（屏幕备注）</summary>
    private readonly NoteOverlay _note = new();
    /// <summary>激光笔叠加层（触摸板模式：把桌面指针变成激光笔）</summary>
    private readonly HighlighterOverlay _hl = new();
    /// <summary>备注开启中：S Pen 笔迹画到桌面固定位置，不注入系统笔</summary>
    private bool _noteOn;
    /// <summary>
    /// 手指是否正压在触摸板上。
    /// 触摸板模式下 **S Pen 的悬停流会不停把光标拽回笔的位置**：
    /// 笔只要在平板上（哪怕只是放着），桌面光标就会一帧一帧地被重定位，
    /// 手指的相对移动被反复覆盖 —— 表现就是「手指往右下微动，光标却往左跑」。
    /// 所以手指一按上来，笔就完全让位（光标只听手指的）。
    /// </summary>
    private volatile bool _fingerOnPad;
    /// <summary>上一次检测到的 Windows 虚拟桌面（切换桌面时坐标空间会变）</summary>
    private IntPtr _lastDesktopId = IntPtr.Zero;

    /// <summary>各类型帧计数，用于控制台周期性汇报（确认链路是否真的在跑）。</summary>
    public long CountDown, CountMove, CountUp, CountHover, CountNoop;
    /// <summary>按帧类型统计实收数量（诊断：手机上发了什么帧、这里收到没有）。</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<byte, long> FrameTypes =
        new System.Collections.Concurrent.ConcurrentDictionary<byte, long>();
    /// <summary>手机自报「已发出的鼠标帧数」（心跳帧里带过来的），用于发现链路丢帧。</summary>
    private long _phoneMouseSent;
    /// <summary>最近一次收到任意帧的时间（Environment.TickCount64）。</summary>
    private long _lastFrameAt;
    /// <summary>最近一次收到输入帧（鼠标/笔/手势）的时间。</summary>
    private long _lastInputAt;
    /// <summary>最近一次收到 MOUSE 帧的时间（判「手指是否真的还压在板上」）。</summary>
    private long _lastMouseAt;
    public double LastPressure;
    public int LastX, LastY;

    /// <summary>当前是否有手机连接（托盘图标据此显示连接状态）。</summary>
    private volatile bool _clientConnected;
    public bool ClientConnected => _clientConnected;

    /// <summary>每收到一帧并映射成 PenSample 后回调（用于调试浮窗）。</summary>
    public Action<PenSample>? OnSample { get; set; }

    /// <summary>mDNS 广播状态（配置窗口展示：手机端能否自动发现本机）。</summary>
    public string MdnsStatus => _mdns?.Status ?? "未启动";

    public TcpPenServer(MappingConfig cfg, IInjector injector)
    {
        _cfg = cfg;
        _injector = injector;
        _listener = new TcpListener(IPAddress.Any, cfg.ListenPort);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        // 提权重启时，新实例往往比旧实例先启动完成，端口要等一会儿才释放 ——
        // 这里重试绑定最多约 6 秒，避免「以管理员身份重启」后弹一个「端口被占用」的假故障框。
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _listener.Start();
                break;
            }
            catch (System.Net.Sockets.SocketException ex)
                when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse && attempt < 20)
            {
                if (attempt == 0)
                    AppLog.Warn($"[Server] 端口 {_cfg.ListenPort} 暂时被占用（大概率是刚被替换的旧实例），等待释放…");
                System.Threading.Thread.Sleep(300);
            }
        }
        Console.WriteLine($"[Server] 监听端口 {_cfg.ListenPort}，等待手机连接（adb reverse tcp:{0} tcp:{0} 或填 PC IP）", _cfg.ListenPort);
        _ = AcceptLoop(_cts.Token);

        // 每秒检查一次显示器布局 / Windows 虚拟桌面是否变化
        _watchTimer = new System.Threading.Timer(_ => WatchDisplay(), null, 1000, 1000);

        // mDNS 广播：手机端自动发现本机，免手填 IP
        try
        {
            _mdns = new MdnsAdvertiser(_cfg.ListenPort);
            _mdns.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Server] mDNS 广播启动失败（不影响手动填 IP 连接）：{ex.Message}");
        }
    }

    /// <summary>显示器布局 / 虚拟桌面变化的监视器。</summary>
    private System.Threading.Timer? _watchTimer;

    /// <summary>
    /// 每秒检查两件事：
    ///   ① 虚拟屏幕尺寸有没有变（插拔显示器 / 改分辨率 / 远程桌面）——
    ///      变了就重新解析映射区域，并把备注层、激光笔层的画布一起重建；
    ///   ② Windows 虚拟桌面有没有切换 —— 切换后光标的绝对坐标空间变了，
    ///      累积的残量必须作废，否则会出现「突然跳一大段」。
    /// </summary>
    private void WatchDisplay()
    {
        try
        {
            if (_cfg.RefreshVirtualScreenIfChanged(Environment.TickCount64))
            {
                _hl.ResizeToVirtualScreen();
                _note.ResizeToVirtualScreen();
            }

            var did = DesktopInfo.CurrentDesktopId();
            if (_lastDesktopId == IntPtr.Zero)
            {
                _lastDesktopId = did;
            }
            else if (did != _lastDesktopId)
            {
                _lastDesktopId = did;
                _moveRemX = 0;
                _moveRemY = 0;
                AppLog.Info($"[Desktop] 已切换 Windows 虚拟桌面（{DesktopInfo.CurrentDesktopName()}）→ 重新锚定光标");
            }

            // 兜底：手机的「手指离开触摸板」信号万一没发出来（事件被系统吞掉、
            // 手势状态卡死、切到别的应用……），_fingerOnPad 会永远停在 true，
            // 之后笔的任何事件在这里都被丢掉 —— 表现就是「手机彻底动不了，
            // 但心跳正常、手机界面也还有反应」。
            // 手指真压在板上时必然有 MOUSE 帧持续过来，静默 4 秒就认为手指已离开。
            var nowT = Environment.TickCount64;
            var lastMouse = System.Threading.Interlocked.Read(ref _lastMouseAt);
            if (_fingerOnPad && lastMouse > 0 && nowT - lastMouse > 4000)
            {
                _fingerOnPad = false;
                AppLog.Warn("[Mouse] 4s 内没有手指事件 → 自动解除「笔让位」，把光标交还给笔" +
                            "（兜底：手机端可能漏发了手指离开信号）");
            }

            // 看门狗：连接还在、心跳也在收，但很久没有输入帧 → 提醒一次（不自动断，
            // 因为「用户没碰手机」和「帧丢了」在这里长得一样；真正的判定靠心跳里的帧数比对）
            var now = Environment.TickCount64;
            var lastFrame = System.Threading.Interlocked.Read(ref _lastFrameAt);
            var lastInput = System.Threading.Interlocked.Read(ref _lastInputAt);
            if (_clientConnected && lastFrame > 0 && now - lastFrame < 5000 && lastInput > 0
                && now - lastInput > 120000)
            {
                AppLog.Warn($"[Watchdog] 心跳正常但已 {(now - lastInput) / 1000}s 没有输入帧" +
                            "（若手机在滑动却无反应，等手机心跳里的帧数比对日志出现即可自动重连）");
                System.Threading.Interlocked.Exchange(ref _lastInputAt, now); // 只提醒一次，避免刷屏
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[Display] 监视异常：{ex.GetType().Name} {ex.Message}");
        }
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
        _clientConnected = true;
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        AppLog.Info($"[Server] 手机接入 {remote}");
        try
        {
            using (client)
            using (var ns = client.GetStream())
            {
                // 关键：不能无限等待读。半死的连接（手机写线程写进死链路）会让读线程
                // 永远卡在一帧上，表现为「手机在发帧、但 PC 一帧都收不到」。
                // 设一个读超时：超时抛 IOException → 连接被丢弃 → 手机自动重连 → 自愈。
                ns.ReadTimeout = 20000;
                ns.WriteTimeout = 5000;
                _clientStream = ns;
                var header = new byte[6];
                // 本连接上的「帧丢失」检测：手机心跳里会报它已发多少鼠标帧，
                // 这里记录基线；差值过大就说明帧在链路上丢了（半死连接）→ 主动断开重连。
                //
                // 基线必须取**本连接第一条心跳**里的计数，不能用全局 _phoneMouseSent：
                // 全局值是上一条连接遗留的，而 recvMouse 是每条连接从 0 开始数的，
                // 两个基准不同源，差值就没有意义（还会被上一次的残留值污染）。
                long basePhone = -1;
                long recvMouse = 0;
                // 协议版本不匹配 → 拒绝**本条**连接（按连接局部，别串台到别的客户端）
                bool rejectConn = false;
                while (!token.IsCancellationRequested && !rejectConn)
                {
                    if (!ReadExactly(ns, header, 6)) break;
                    if (header[0] != MAGIC1 || header[1] != MAGIC2)
                    {
                        AppLog.Warn($"[Server] 帧头错误，丢弃连接 {remote}");
                        break;
                    }
                    int len = header[4] | (header[5] << 8);
                    var payload = new byte[len];
                    if (!ReadExactly(ns, payload, len)) break;
                    FrameTypes.AddOrUpdate(header[3], 1L, (_, v) => v + 1);
                    var now = Environment.TickCount64;
                    System.Threading.Interlocked.Exchange(ref _lastFrameAt, now);
                    // 输入帧（鼠标/笔/手势）：用于「心跳正常但输入帧不来」的看门狗
                    if (header[3] == TYPE_MOUSE || (header[3] >= TYPE_PEN_DOWN && header[3] <= TYPE_PEN_HOVER)
                        || header[3] == TYPE_PAN || header[3] == TYPE_ZOOM)
                        System.Threading.Interlocked.Exchange(ref _lastInputAt, now);

                    switch (header[3])
                    {
                        case TYPE_HELLO:
                            if (HandleHello(payload)) rejectConn = true;
                            break;
                        case TYPE_PAN:
                            HandlePan(payload);
                            break;
                        case TYPE_MOUSE:
                            HandleMouse(payload);
                            recvMouse++;
                            System.Threading.Interlocked.Exchange(ref _lastMouseAt, now);
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
                        case TYPE_INFO:
                            // 手机端主动查询主机信息（一般不需要，握手时主机会主动下发）
                            SendInfo(ns);
                            break;
                        case TYPE_NOTE:
                            HandleNote(payload);
                            break;
                        case TYPE_HL:
                            HandleHighlighter(payload);
                            break;
                        case TYPE_SHOT_REQ:
                            HandleShotReq(ns);
                            break;
                        case TYPE_SAVE_REQ:
                            HandleSaveReq(payload, ns);
                            break;
                        case TYPE_PEN_DOWN:
                        case TYPE_PEN_MOVE:
                        case TYPE_PEN_UP:
                        case TYPE_PEN_HOVER:
                            Dispatch(header[3], payload);
                            break;
                        default:
                            CountNoop++;
                            // 心跳帧（type=0）：payload[0..3] 是手机自报的「已发鼠标帧数」
                            if (header[3] == 0x00 && payload.Length >= 4)
                            {
                                long said = BitConverter.ToInt32(payload, 0);
                                if (said > 0) _phoneMouseSent = said;
                                if (said <= 0) break;           // 手机没报数 → 不参与比对
                                if (basePhone < 0)
                                {
                                    basePhone = said;           // 本连接第一条心跳 → 定基线
                                    break;
                                }
                                long delta = said - basePhone;
                                if (delta > recvMouse + 100)
                                {
                                    AppLog.Warn($"[Server] 帧丢失：手机称已发 {delta} 个鼠标帧，PC 只收到 {recvMouse} 个 " +
                                                $"→ 链路半死，断开重连（手机会自动重连）");
                                    // 注意：这里必须 **退出读循环**，不能 break ——
                                    // break 在 switch 里只跳出 switch，读循环会继续空转，
                                    // 自愈就等于没写（这个坑之前一直存在）。
                                    // return 会走 using 释放 socket、走 finally 清连接标志，
                                    // 手机端随即重连，状态两边一起重来。
                                    return;
                                }
                            }
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
            _clientConnected = false;
            Console.WriteLine("[Server] 手机断开");
        }
    }

    /// <summary>握手帧：手机上报绘制面像素尺寸与比例偏好，用于做正确的等比映射。</summary>
    /// <summary>握手帧。返回 true = 手机协议版本不匹配，调用方读完这帧就断开。</summary>
    private bool HandleHello(byte[] p)
    {
        if (p.Length < 12) return false;
        float w = BitConverter.ToSingle(p, 0);
        float h = BitConverter.ToSingle(p, 4);
        float mode = BitConverter.ToSingle(p, 8);
        float work = p.Length >= 16 ? BitConverter.ToSingle(p, 12) : 0f;
        float frameL = p.Length >= 20 ? BitConverter.ToSingle(p, 16) : -1f;
        float frameT = p.Length >= 24 ? BitConverter.ToSingle(p, 20) : -1f;
        float frameScale = p.Length >= 28 ? BitConverter.ToSingle(p, 24) : -1f;
        // 手机上报的协议兼容版本（老手机 HELLO 只有 28 字节 → 视为 v0，不兼容）
        int phonePv = p.Length >= 32 ? (int)BitConverter.ToSingle(p, 28) : 0;
        bool reject = phonePv != ProtoVer;
        if (reject)
        {
            // 被拒的连接**不得触碰共享配置**：手机端旧包会每 1~2 秒重连一次，
            // 若每次都把模式/书写区写进 _cfg，会把正在正常工作的其它连接搅乱。
            AppLog.Warn($"[Ver] 手机协议版本 v{phonePv} ≠ 本机 v{ProtoVer} → 下发 INFO 后断开，停止对接。" +
                        "请把两端都更新到同一版本。");
            SendInfo(_clientStream!);
            return true;
        }
        _cfg.SetPhoneHello(w, h, mode, work, frameL, frameT, frameScale);
        // 握手成功后立刻把主机信息（版本号/计算机名/候选 IP）下发给手机，供拓扑图展示
        SendInfo(_clientStream!);
        _zoomLog = 0;
        var r = _cfg.GetActiveRect();
        AppLog.Info($"[Hello] 手机书写区 {w:0}×{h:0} 宽高比={(_cfg.PhoneAspect > 0 ? _cfg.PhoneAspect : 0):0.###} " +
                    $"偏好={(mode >= 1f ? "保持等比" : "拉伸")}");
        AppLog.Info($"[Hello] 工作模式={(_cfg.IsMouseMode ? "触摸板" : "数位板")} " +
                    $"操作框 缩放×{_cfg.Scale:0.###} 位置({_cfg.FrameL:0.###},{_cfg.FrameT:0.###}) " +
                    $"→ 桌面映射区域 {r.x},{r.y} {r.w}×{r.h}");
        return reject;
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
                AppLog.Warn("[Pan] 收到平移帧，但当前是触摸板模式，已忽略（手指应只控光标）");
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
        MouseControl.EndTouchZoomIfActive(); // 捏合注入还挂着就抬起，防触摸接触点残留锁死左键
        MouseControl.ReleaseAll();     // 避免左键卡在按下状态
        AppLog.Info(mouse
            ? "[Mode] 切换到【触摸板模式】：取消操作范围框，手指=触摸板，右侧滚轮条，底部左/中/右键，S Pen=绝对定位鼠标笔"
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

    /// <summary>
    /// 把主机信息（版本号 / 计算机名 / 候选 IP）封装成 INFO 帧下发给手机。
    /// 手机端据此在拓扑图里展示「客户端版本号」「桌面 PC IP」「网络连接方式」等。
    /// </summary>
    private void SendInfo(Stream ns)
    {
        try
        {
            var ips = string.Join(",", NetInfo.ConnectionCandidates());
            var text = $"ver={AppVersion.Version}&pv={ProtoVer}&name={Uri.EscapeDataString(Environment.MachineName)}&ips={Uri.EscapeDataString(ips)}";
            var body = System.Text.Encoding.UTF8.GetBytes(text);
            var buf = new byte[6 + body.Length];
            buf[0] = MAGIC1;
            buf[1] = MAGIC2;
            buf[2] = 0x01;            // protocol version
            buf[3] = TYPE_INFO;
            buf[4] = (byte)(body.Length & 0xFF);
            buf[5] = (byte)((body.Length >> 8) & 0xFF);
            Array.Copy(body, 0, buf, 6, body.Length);
            lock (ns) { ns.Write(buf, 0, buf.Length); }
            AppLog.Info($"[Info] 下发主机信息：ver={AppVersion.Version} pv={ProtoVer} 候选IP={ips}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[Info] 下发失败：{ex.Message}");
        }
    }

    /// <summary>复位帧：手机端把操作框恢复到整个书写区。</summary>
    private void HandleReset(byte[] p)
    {
        _cfg.ResetView();
        _zoomLog = 0;          // 复位后重新计数，日志从第 1 帧开始打
        var r = _cfg.GetActiveRect();
        AppLog.Info($"[Reset] 操作框已复位 → 桌面范围 {r.x},{r.y} {r.w}×{r.h}");
    }


    /// <summary>备注帧：开关桌面笔迹叠加层 / 撤销一笔 / 清空。</summary>
    private void HandleNote(byte[] p)
    {
        if (p.Length < 4) return;
        int action = (int)BitConverter.ToSingle(p, 0);
        switch (action)
        {
            case 0: // 关
                _noteOn = false;
                _note.Hide();
                break;
            case 1: // 开
                _noteOn = true;
                _note.Show();
                break;
            case 4: // 切换笔迹颜色（arg = 手机端色板的预设颜色索引）
                if (p.Length >= 8)
                {
                    int cidx = (int)BitConverter.ToSingle(p, 4);
                    _note.SetColorIndex(cidx);
                    AppLog.Info($"[Note] 笔迹颜色 → 预设 #{cidx}");
                }
                break;
            case 2: // 撤销一笔
                _note.Undo();
                break;
            case 3: // 清空
                _note.Clear();
                break;
        }
        // 开/关/撤销/清空都是低频用户操作，始终记日志（换色有自己的日志）；
        // 高频帧（若有）才需要节流。
        if (action != 4)
            AppLog.Info($"[Note] action={action}（{(action == 0 ? "关" : action == 1 ? "开" : action == 2 ? "撤销" : action == 3 ? "清空" : "换色")}）");
    }

    /// <summary>截图请求：截取当前范围框画面，PNG 分片下发到手机。</summary>
    private void HandleShotReq(Stream ns)
    {
        try
        {
            var r = _cfg.GetActiveRect();
            int w = Math.Max(1, r.w), h = Math.Max(1, r.h);
            byte[] png;
            // GDI+ 非线程安全：截图（CopyFromScreen / PNG 编码）必须与备注叠加层的绘制串行化，
            // 否则两条线程并发用 GDI+ 偶发写坏内部状态 → 进程无提示消失（曾经的「捕获后崩溃」）
            lock (NoteOverlay.GdiLock)
            {
                using var bmp = new System.Drawing.Bitmap(w, h);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.x, r.y, 0, 0, new System.Drawing.Size(w, h));
                }
                // 缩到宽 ≤720 再编码，减小传输量
                System.Drawing.Bitmap send = bmp;
                if (w > 720)
                {
                    int nh = Math.Max(1, h * 720 / w);
                    send = new System.Drawing.Bitmap(bmp, 720, nh);
                }
                using var ms = new MemoryStream();
                send.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                png = ms.ToArray();
                if (!ReferenceEquals(send, bmp)) send.Dispose();
            }
            SendShot(ns, png);
            AppLog.Info($"[Shot] 截取范围框 ({r.x},{r.y}) {w}×{h} → PNG {png.Length}B 已下发");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[Shot] 截图失败：{ex.Message}");
        }
    }

    /// <summary>分片下发截图：每帧 payload = totalLen(u32 LE) + seq(u16 LE) + data(≤60000B)。</summary>
    private void SendShot(Stream ns, byte[] png)
    {
        const int CHUNK = 60000;
        int total = png.Length;
        int seq = 0;
        lock (ns)
        {
            for (int off = 0; ; off += CHUNK, seq++)
            {
                int n = Math.Min(CHUNK, total - off);
                if (n < 0) n = 0;
                var payload = new byte[6 + n];
                BitConverter.GetBytes(total).CopyTo(payload, 0);
                BitConverter.GetBytes((ushort)seq).CopyTo(payload, 4);
                if (n > 0) Array.Copy(png, off, payload, 6, n);
                var frame = new byte[6 + payload.Length];
                frame[0] = MAGIC1; frame[1] = MAGIC2; frame[2] = 0x01; frame[3] = TYPE_SHOT;
                frame[4] = (byte)(payload.Length & 0xFF);
                frame[5] = (byte)((payload.Length >> 8) & 0xFF);
                Array.Copy(payload, 0, frame, 6, payload.Length);
                ns.Write(frame, 0, frame.Length);
                if (n == 0) break;
            }
        }
    }

    /// <summary>
    /// 截屏保存：抓「桌面范围框」或「整块虚拟屏幕」，PNG 存到 PC 和/或下发手机存相册。
    ///
    /// 为什么不再自己把笔迹合成一遍：CopyFromScreen 抓的是**已经合成好的屏幕图像**，
    /// 桌面笔迹层本来就在屏幕上，抓下来就是"所见即所得"，再画一遍只会让笔画重叠变粗。
    /// </summary>
    private void HandleSaveReq(byte[] p, Stream ns)
    {
        bool full = p.Length >= 4 && BitConverter.ToSingle(p, 0) >= 1f;
        bool toPc = p.Length >= 8 && BitConverter.ToSingle(p, 4) >= 1f;
        bool toPhone = p.Length >= 12 && BitConverter.ToSingle(p, 8) >= 1f;
        if (!toPc && !toPhone) { AppLog.Info("[Save] 两个保存位置都没选 → 忽略"); return; }

        try
        {
            int x, y, w, h;
            if (full)
            {
                var v = System.Windows.Forms.SystemInformation.VirtualScreen;
                x = v.Left; y = v.Top; w = Math.Max(1, v.Width); h = Math.Max(1, v.Height);
            }
            else
            {
                var r = _cfg.GetActiveRect();
                x = r.x; y = r.y; w = Math.Max(1, r.w); h = Math.Max(1, r.h);
            }

            byte[] png;
            // 与 HandleShotReq 同理：GDI+ 必须和叠加层的绘制串行化
            lock (NoteOverlay.GdiLock)
            {
                using var bmp = new System.Drawing.Bitmap(w, h);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                png = ms.ToArray();
            }

            string name = "DrawTouch_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            if (toPc)
            {
                // 保存目录按优先级逐个试。真实环境里「图片库」可能根本建不了目录：
                // OneDrive 重定向未落地、公司策略锁死、已知文件夹指向失效路径等，
                // 都会让 Directory.CreateDirectory 直接抛「未能找到文件」。只认死一个
                // 默认位置的话，用户点了截屏却哪儿都没有文件，等于功能没做。
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(_cfg.ShotSaveDir))
                    candidates.Add(_cfg.ShotSaveDir!);
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "draw&touch"));
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "draw&touch"));
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "draw&touch"));
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "shots"));

                string? saved = null;
                var errs = new List<string>();
                int idx = 0;
                foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string file = Path.Combine(dir, name);
                        File.WriteAllBytes(file, png);
                        saved = file;
                        AppLog.Info($"[Save] 已存到电脑：{file}（{png.Length}B）");
                        // 落到备用位置要明说，否则用户会去图片库里翻半天
                        if (idx > 0)
                            AppLog.Warn($"[Save] 注意：默认位置不可用，本次改存到 {file}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        errs.Add($"{dir} → {ex.Message}");
                    }
                    idx++;
                }
                if (saved == null)
                    AppLog.Warn("[Save] 存到电脑失败，已依次试过：" + string.Join(" | ", errs));
            }
            if (toPhone)
            {
                SendSave(ns, png);
                AppLog.Info($"[Save] 已下发手机存相册：{name} ({w}×{h}, {png.Length}B)");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[Save] 截屏失败：{ex.Message}");
        }
    }

    /// <summary>分片下发截屏（与 SendShot 同格式，只是帧类型不同，手机端据此存相册而不是当背景）。</summary>
    private void SendSave(Stream ns, byte[] png)
    {
        const int CHUNK = 60000;
        int total = png.Length;
        int seq = 0;
        lock (ns)
        {
            for (int off = 0; ; off += CHUNK, seq++)
            {
                int n = Math.Min(CHUNK, total - off);
                if (n < 0) n = 0;
                var payload = new byte[6 + n];
                BitConverter.GetBytes(total).CopyTo(payload, 0);
                BitConverter.GetBytes((ushort)seq).CopyTo(payload, 4);
                if (n > 0) Array.Copy(png, off, payload, 6, n);
                var frame = new byte[6 + payload.Length];
                frame[0] = MAGIC1; frame[1] = MAGIC2; frame[2] = 0x01; frame[3] = TYPE_SAVE;
                frame[4] = (byte)(payload.Length & 0xFF);
                frame[5] = (byte)((payload.Length >> 8) & 0xFF);
                Array.Copy(payload, 0, frame, 6, payload.Length);
                ns.Write(frame, 0, frame.Length);
                if (n == 0) break;
            }
        }
    }

    private int _zoomIgnored;
    private int _mouseLog;

    /// <summary>
    /// 把像素量转成「高分辨率滚轮」增量。
    /// Windows 的规则：滚轮增量是 120 的整数倍 = 普通鼠标滚轮（一格一格跳）；
    /// 不是整数倍 = 高分辨率设备（触摸板），应用会按像素平滑滚动。
    /// 所以这里保证结果永远不是 120 的倍数。
    /// </summary>
    private static int PreciseDelta(double pixels)
    {
        int v = (int)Math.Round(pixels);
        if (v == 0 && Math.Abs(pixels) >= 0.5) v = pixels > 0 ? 1 : -1;
        if (v != 0 && v % 120 == 0) v += (v > 0 ? 1 : -1);
        return v;
    }

    // 触摸板相对移动：不足 1 像素的位移残量（跨帧累积，避免慢速移动"不动/跳"）
    private double _moveRemX, _moveRemY;

    /// <summary>残量上限：系统在虚拟桌面/异构多屏下截断位移时，残量不能无限堆积。</summary>
    private const double MoveRemainLimit = 600;

    // （缩放余量与换算已移入 MouseControl 的触摸板缩放会话：CtrlZoomFactor/CtrlZoomEnd）

    /// <summary>激光笔帧：开关 + 颜色索引。</summary>
    private void HandleHighlighter(byte[] p)
    {
        if (p.Length < 4) return;
        bool on = BitConverter.ToSingle(p, 0) >= 1f;
        int cidx = p.Length >= 8 ? (int)BitConverter.ToSingle(p, 4) : 0;
        if (cidx >= 0) _hl.SetColorIndex(cidx);
        _hl.SetEnabled(on);
    }

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
                // 横纵**必须乘同一个系数**：手机端 dx/dy 已用同一个除数（书写区高度）归一化，
                // 这里再各自乘 VirtualW / VirtualH 的话，横竖速度会差 VirtualW/VirtualH 倍。
                // 统一用 VirtualH：手指划过手机书写区全高 = 光标走满虚拟屏幕高度，横竖一致。
                //
                // 另外：慢速移动时单帧位移常常不足 1 像素，直接取整会被吃掉（表现为"不动"，
                // 攒够一次又突然跳一下 = "飘"）。这里累积残量，攒够整像素就走、余量留到下一帧。
                _moveRemX += dx * _cfg.VirtualH * _cfg.MouseSpeed;
                _moveRemY += dy * _cfg.VirtualH * _cfg.MouseSpeed;
                int mx = (int)_moveRemX;
                int my = (int)_moveRemY;
                _moveRemX -= mx;
                _moveRemY -= my;
                if (mx != 0 || my != 0)
                {
                    // 绝对定位可能被系统截断（虚拟桌面 / 异构多屏）→ 没走完的部分下帧继续
                    var (remX, remY) = MouseControl.MoveBy(_cfg, mx, my);
                    if (remX != 0 || remY != 0)
                    {
                        _moveRemX = Math.Clamp(_moveRemX + remX, -MoveRemainLimit, MoveRemainLimit);
                        _moveRemY = Math.Clamp(_moveRemY + remY, -MoveRemainLimit, MoveRemainLimit);
                    }
                }
                if (_mouseLog++ % 20 == 0)
                    AppLog.Info($"[Mouse] 移动 dx={dx:F3} dy={dy:F3} → 光标位移 ({mx},{my}) 残量({_moveRemX:F2},{_moveRemY:F2}) 增益={_cfg.MouseSpeed:0.##}");
                break;
            case MOUSE_FINGER:
            {
                // 手指在触摸板上的状态：手指一按上来，S Pen 就把光标完全让给手指
                bool on = dx >= 1f;
                if (on != _fingerOnPad)
                {
                    _fingerOnPad = on;
                    if (on)
                    {
                        // 笔可能正按着（笔在平板上压到会误触）→ 先松开，别把左键卡住
                        MouseControl.Up();
                        AppLog.Info("[Mouse] 手指已落在触摸板上 → S Pen 让位，光标只跟手指");
                    }
                    else
                    {
                        // 手指真正离开触摸板 → 结束一切缩放会话：
                        // 触摸注入的抬接触点 / Ctrl 缩放会话松开 Ctrl——应用此刻定稿刷新。
                        MouseControl.EndTouchZoomIfActive();
                        MouseControl.CtrlZoomEnd();
                        AppLog.Info("[Mouse] 手指离开触摸板 → S Pen 恢复控制光标");
                    }
                }
                break;
            }
            case MOUSE_LEFT_CLICK:
                MouseControl.LeftClick();
                AppLog.Info("[Mouse] 单击 → 左键");
                break;
            case MOUSE_LEFT_DOUBLE:
                // 单指双击 → 双击（手机上「双击」不再映射成右键，右键走双指点击）
                MouseControl.DoubleClick();
                AppLog.Info("[Mouse] 双击 → 左键 ×2");
                break;
            case MOUSE_LEFT_DOWN:
                // 按住不放（触摸板模式下：按住「左」键按钮 + 移动手指 = 拖拽 / 涂荧光）
                MouseControl.Down();
                break;
            case MOUSE_LEFT_UP:
                MouseControl.Up();
                break;
            case MOUSE_RIGHT_CLICK:
                MouseControl.RightClick();
                AppLog.Info("[Mouse] 右键");
                break;
            case MOUSE_SCROLL:
            {
                // 手指上滑(dy<0) → 向上滚动（内容跟着手指走）
                // 单位是**像素**：Windows 收到非 120 倍数的滚轮增量时，
                // 会当成高分辨率滚动（触摸板那样平滑），而不是一格一格跳。
                int v = PreciseDelta(-dy * SCROLL_PIXELS_PER_SCREEN);
                int h = PreciseDelta(dx * SCROLL_PIXELS_PER_SCREEN);
                if (v != 0) MouseControl.WheelPixels(v);
                if (h != 0) MouseControl.HWheelPixels(h);
                if (v != 0 || h != 0) AppLog.Info($"[Mouse] 滚动 v={v}px h={h}px");
                break;
            }
            case MOUSE_ZOOM:
            {
                // 捏合张开(factor>1) → 放大；捏拢(<1) → 缩小。
                // 手机端把缩放系数放在 dx 字段里（相对量，>1 张开 / <1 捏拢）。
                double factor = dx;
                if (factor <= 0) break;

                // ① 触摸注入（合成双指触摸 → 系统翻译成原生缩放手势）
                //    本机可用（2026-10-30 实测日志确认走的是这条路径）。
                //    但 MouseControl 内部有安全闸门：光标或前台窗口不在【主显示器】上时
                //    一律不注入（副屏注入会让 Windows 把触点按主屏坐标解释 → 光标飞到
                //    主屏右边缘、且触摸期间被系统藏起来 = 用户说的「指针消失、飞走」），
                //    返回 false 后自动落到 ② 的 Ctrl+滚轮。
                if (_cfg.PinchMode != "Wheel" && MouseControl.TouchZoomAvailable() &&
                    MouseControl.TryGetCursorPos(out var zx, out var zy) &&
                    MouseControl.UpdateTouchZoom(factor, zx, zy))
                {
                    if (_mouseLog++ % 20 == 0)
                        AppLog.Info($"[Mouse] 捏合 ×{factor:F3} → 触摸注入缩放");
                    break;
                }

                // ② 触摸板缩放会话（照抄真实触摸板抓包 2026-10-29）：
                //    Ctrl 按住跨整轮手势 + 逐格滚轮；停手时无消息无刷新；
                //    手指离开触摸板（MOUSE_FINGER off）松开 Ctrl → 应用定稿。
                MouseControl.CtrlZoomFactor(factor);
                break;
            }
            case MOUSE_MIDDLE_CLICK:
                MouseControl.MiddleClick();
                AppLog.Info("[Mouse] 中键");
                break;
            case MOUSE_BACK:
                MouseControl.Back();
                AppLog.Info("[Mouse] 后退");
                break;
            case MOUSE_FORWARD:
                MouseControl.Forward();
                AppLog.Info("[Mouse] 前进");
                break;
            case MOUSE_VOL_UP:
                MouseControl.VolumeUp();
                AppLog.Info("[Mouse] 音量 +");
                break;
            case MOUSE_VOL_DOWN:
                MouseControl.VolumeDown();
                AppLog.Info("[Mouse] 音量 -");
                break;
            case MOUSE_VOL_MUTE:
                MouseControl.VolumeMute();
                AppLog.Info("[Mouse] 静音");
                break;
            case MOUSE_TASK_NEXT:
                MouseControl.TaskSwitch(back: false);
                AppLog.Info("[Mouse] 任务切换 Alt+Tab");
                break;
            case MOUSE_TASK_PREV:
                MouseControl.TaskSwitch(back: true);
                AppLog.Info("[Mouse] 任务切换 Alt+Shift+Tab");
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

        // 桌面备注开启中：笔迹画在**桌面范围框内**（与数位板绘制逻辑一致），
        // 坐标按范围框映射；框缩放/移动时笔迹跟着走。不注入系统笔（不打扰当前应用）。
        if (_noteOn)
        {
            if (state == PenState.Hover) return;
            var nr = _cfg.GetActiveRect();
            _note.SetRect(nr.x, nr.y, nr.w, nr.h);
            _note.AddPoint(x, y, state);
            if (state == PenState.Down) { CountDown++; }
            else if (state == PenState.Up) { CountUp++; }
            else CountMove++;
            return;
        }

        // 鼠标模式：S Pen 当绝对定位的鼠标笔（覆盖整块虚拟屏幕），不再走合成笔注入
        if (_cfg.IsMouseMode)
        {
            // 手指正压在触摸板上时，光标只听手指的：笔的悬停流不能再来抢光标
            // （笔只要在平板上就会一直发悬停，把光标一帧一帧拽回笔的位置 ——
            //   表现出来就是「手指往右下微动，光标却往左跑」）。
            if (_fingerOnPad) return;

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

    /// <summary>
    /// 释放资源。**必须幂等**：托盘「退出」会先 `_server.Dispose()`，回到 Program.Main 时
    /// `using var server` 还会再释放一次；以前第二次会在已释放的 CTS 上 `Cancel()`，
    /// 抛 ObjectDisposedException 把退出流程搞成「未处理异常」（日志里一片红）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _watchTimer?.Dispose(); } catch { }
        try { _note.Dispose(); } catch { }
        try { _hl.Dispose(); } catch { }
        try { _mdns?.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    private bool _disposed;
}
