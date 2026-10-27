using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SpenTabletHost;

/// <summary>一块网卡的 IPv4 信息。</summary>
public sealed class NicInfo
{
    public string Name = "";
    public string Description = "";
    public string IPv4 = "";
    public string Prefix = "";       // 掩码长度
    public bool IsUp = false;
    public bool IsLoopback = false;
    /// <summary>看起来像 USB 网络共享 / 热点网段（192.168.42.x / 192.168.43.x）</summary>
    public bool LooksLikeTether => IPv4.StartsWith("192.168.42.") || IPv4.StartsWith("192.168.43.");

    /// <summary>
    /// 是否是「手机 USB 网络共享」那张网卡。
    /// 手机做 USB 共享时，PC 上会多出一张 RNDIS 网卡，描述里带
    /// "Remote NDIS" / "NDIS" / "USB ... Network Device" 之类字样。
    /// 本机实测：SAMSUNG Mobile USB Remote NDIS Network Device #2 → 10.136.80.112。
    /// </summary>
    public bool IsUsbTether
    {
        get
        {
            string d = (Description ?? "") + " " + (Name ?? "");
            if (d.IndexOf("RNDIS", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (d.IndexOf("Remote NDIS", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (d.IndexOf("NDIS", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // 有些手机用 "USB Ethernet" 之类的名字
            if (d.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (d.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 d.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return false;
        }
    }

    /// <summary>虚拟网卡 / VPN / 隧道 / 蓝牙 —— 手机端不能用这些 IP。</summary>
    public bool IsVirtual
    {
        get
        {
            string d = (Description ?? "") + " " + (Name ?? "");
            string[] junk = { "Tunnel", "VPN", "OpenVPN", "TAP-Windows", "Wintun", "Hyper-V",
                              "VirtualBox", "Host-Only", "vEthernet", "Bluetooth", "Loopback" };
            foreach (var k in junk)
                if (d.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}

/// <summary>
/// 网络信息助手：解决「USB 网络共享到底该填哪个 IP」和「防火墙拦了」这两个高频问题。
/// </summary>
internal static class NetInfo
{
    /// <summary>列出本机所有启用网卡的 IPv4。</summary>
    public static List<NicInfo> ListIps()
    {
        var list = new List<NicInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.Address.ToString().StartsWith("127.")) continue;
                    list.Add(new NicInfo
                    {
                        Name = ni.Name,
                        Description = ni.Description ?? "",
                        IPv4 = ua.Address.ToString(),
                        Prefix = ua.PrefixLength.ToString(),
                        IsUp = props.GatewayAddresses.Count > 0 || ni.OperationalStatus == OperationalStatus.Up,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"枚举网卡失败：{ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// 手机端该填的 PC IP。
    /// 优先级：① 真正的 RNDIS/USB 网络共享网卡 ② 192.168.42/43 段 ③ 其它在用的、非虚拟的网卡。
    /// 之前只按 192.168.42/43 判断，会把 VPN 虚拟网卡（vgate0）当成答案，必须修。
    /// </summary>
    public static string GuessTetheringIp()
    {
        var ips = ListIps();
        var rndis = ips.FirstOrDefault(i => i.IsUsbTether && i.IsUp)
                    ?? ips.FirstOrDefault(i => i.IsUsbTether);
        if (rndis != null) return rndis.IPv4;

        var tether = ips.FirstOrDefault(i => i.LooksLikeTether && i.IsUp)
                    ?? ips.FirstOrDefault(i => i.LooksLikeTether);
        if (tether != null) return tether.IPv4;

        var any = ips.FirstOrDefault(i => i.IsUp && !i.IsVirtual);
        if (any != null) return any.IPv4;

        return ips.FirstOrDefault(i => !i.IsVirtual)?.IPv4 ?? "";
    }

    /// <summary>当前推荐的手机端 IP 是不是来自 RNDIS 网卡（UI 上可以据此给提示）。</summary>
    public static string TetherHint()
    {
        var ips = ListIps();
        var rndis = ips.FirstOrDefault(i => i.IsUsbTether && i.IsUp);
        if (rndis != null) return $"来自「{rndis.Name}」（{rndis.Description}）—— 这就是 USB 网络共享的网卡";
        var any = ips.FirstOrDefault(i => i.IsUp && !i.IsVirtual);
        if (any != null) return $"来自「{any.Name}」—— 若用 USB 网络共享，请确认手机已开启共享并拔插一次 USB";
        return "未检测到在用的网卡";
    }

    /// <summary>所有 IP 的一行文本，方便在气泡提示里展示与复制。</summary>
    public static string DescribeAll()
    {
        var ips = ListIps();
        if (ips.Count == 0) return "（没有找到可用的 IPv4 地址）";
        return string.Join("  |  ", ips.Select(i =>
            $"{i.IPv4} ← {i.Name}{(i.IsUsbTether ? "（USB 网络共享）" : i.LooksLikeTether ? "（热点网段）" : "")}"));
    }

    /// <summary>每块网卡一行（诊断框里用，避免一行太长被横向截断）。</summary>
    public static List<string> DescribeAllLines()
    {
        var ips = ListIps();
        if (ips.Count == 0) return new List<string> { "（没有找到可用的 IPv4 地址）" };
        return ips.Select(i =>
            $"{i.IPv4,-16} {i.Name}" +
            (i.IsUsbTether ? "  ← USB 网络共享，手机端填这个" :
             i.LooksLikeTether ? "  ← 热点网段，手机端填这个" :
             i.IsVirtual ? "  （虚拟网卡/隧道，手机端不能用）" : "")).ToList();
    }

    public const string FirewallRuleName = "SpenTablet 数位板";

    /// <summary>检查 Windows 防火墙里是否已有放行规则。</summary>
    public static bool HasFirewallRule(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + FirewallRuleName + "\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return outp.Contains("LocalPort") && outp.Contains(port.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Error($"检查防火墙规则失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 放行入站端口。
    /// 先直接跑 netsh（进程本身已提权时一次成功）；失败再用 runas 触发 UAC 提权框。
    /// 返回 (是否成功, 提示信息)。
    /// </summary>
    public static (bool ok, string msg) AddFirewallRule(int port)
    {
        string args = "advfirewall firewall add rule name=\"" + FirewallRuleName + "\" " +
                      $"dir=in action=allow protocol=TCP localport={port}";

        // 1) 直接执行（已提权时可用）
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit(8000);
                if (output.IndexOf("添加的规则", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    output.IndexOf("Ok.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    HasFirewallRule(port))
                {
                    return (true, $"防火墙已放行入站端口 {port}，手机端现在可以连上了。");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"直接添加防火墙规则失败：{ex.Message}");
        }

        // 2) 走 UAC 提权
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                Verb = "runas",           // 触发 UAC
                UseShellExecute = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "无法启动 netsh");
            p.WaitForExit(20000);
            return (true, HasFirewallRule(port)
                ? $"防火墙已放行入站端口 {port}，手机端现在可以连上了。"
                : $"已提交防火墙放行规则（端口 {port}）。若弹出 UAC 请点「是」，然后重试连接。");
        }
        catch (Exception ex)
        {
            return (false, $"添加防火墙规则失败：{ex.Message}\n" +
                           "请右键 EXE →「以管理员身份运行」，或在 Windows 防火墙里放行本程序。");
        }
    }

    /// <summary>把某个 IP/端口的可连性快速测一下（TCP 探测）。</summary>
    public static bool CanReach(string ip, int port, int timeoutMs = 800)
    {
        try
        {
            using var c = new TcpClient();
            var ar = c.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
            c.EndConnect(ar);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
