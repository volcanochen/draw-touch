using Makaretu.Dns;
using System;

namespace SpenTabletHost;

/// <summary>
/// mDNS 服务广播：向局域网宣布 <c>_spentablet._tcp</c>，手机端用 Android NSD 自动发现本机，
/// 彻底告别「手填 PC IP」。手机开热点 / 同一路由器 / USB 网络共享 同网段均可被发现。
///
/// 实现基于 Makaretu.Dns.Multicast（纯托管 mDNS / DNS-SD 实现，无需安装 Bonjour 服务）。
/// 关键 API 要点：
///   - ServiceProfile 的 serviceName 传 "_spentablet._tcp"（**不要**带 .local，库内会自动补全）
///   - Advertise() 把服务登记进应答目录（被动应答手机端的查询）
///   - Announce() 主动发一次宣告（手机端晚启动也能立刻发现）
///   - Unadvertise() 退出时发 goodbye，避免手机端缓存旧地址
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    /// <summary>手机端 NsdManager 搜索的服务类型。</summary>
    public const string ServiceType = "_spentablet._tcp";

    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _sd;
    private readonly ServiceProfile _profile;
    private readonly int _port;
    private bool _disposed;

    /// <summary>广播状态（给配置窗口展示）。</summary>
    public string Status { get; private set; } = "未启动";

    public MdnsAdvertiser(int port)
    {
        _port = port;
        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);

        // 只广告"真实网卡"的地址：默认行为会把 VPN/隧道虚拟网卡也报上去，
        // 手机解析到虚拟地址（如 172.30.x.x 的 Wintun 隧道）必然连不上。
        var candidates = NetInfo.ConnectionCandidates();
        var primary = candidates.Count > 0 ? System.Net.IPAddress.Parse(candidates[0]) : null;

        // 实例名用计算机名；多机同时开也不怕，mDNS 会按主机名区分
        _profile = new ServiceProfile(
            new DomainName(InstanceName),
            new DomainName(ServiceType),
            (ushort)port,
            primary == null ? null : new[] { primary });
        _profile.AddProperty("ver", "1");
        _profile.AddProperty("app", "SpenTablet");
        // 把全部候选 IP 放进 TXT：手机端逐个试，第一个能连上的就是对的
        // （PC 可能同时开着 USB 共享和 WiFi，手机只能走其中一条路）
        if (candidates.Count > 0)
        {
            _profile.AddProperty("ips", string.Join(",", candidates));
            AppLog.Info("mDNS 广告地址：" + string.Join(", ", candidates));
        }

        // 网络变化（换 WiFi / 拔网线 / 热点重连）时重新宣告，避免手机发现的是旧地址
        _mdns.NetworkInterfaceDiscovered += (_, __) =>
        {
            try
            {
                _sd.Announce(_profile);
                Status = $"广播中（网络变化已刷新）：{InstanceName}";
                AppLog.Info($"mDNS 网络变化，重新宣告 {InstanceName}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"mDNS 网络变化刷新失败：{ex.Message}");
            }
        };
    }

    public static string InstanceName => Environment.MachineName;

    public void Start()
    {
        if (_disposed) return;
        try
        {
            _mdns.Start();
            _sd.Advertise(_profile);   // 登记服务，后续被动应答查询
            _sd.Announce(_profile);    // 主动宣告一次
            Status = $"广播中：{InstanceName}（{ServiceType}）";
            AppLog.Info($"mDNS 已广播 {InstanceName} → {ServiceType} 端口 {_port}");
        }
        catch (Exception ex)
        {
            Status = $"广播失败：{ex.Message}";
            AppLog.Error($"mDNS 启动失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // 发 goodbye，让手机端立刻把这条服务标记为离线，而不是等 TTL 过期
            _sd.Unadvertise(_profile);
        }
        catch (Exception ex)
        {
            AppLog.Error($"mDNS 撤销宣告异常：{ex.Message}");
        }
        try
        {
            _mdns.Stop();
            _mdns.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Error($"mDNS 停止异常：{ex.Message}");
        }
        Status = "已停止";
    }
}
