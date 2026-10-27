namespace SpenTabletHost;

/// <summary>
/// 主机（PC 端）版本号。手机端拓扑图里展示的「客户端版本号」就是它。
/// 升级时改这里即可，手机会随 mDNS/INFO 帧自动拿到新值。
/// </summary>
internal static class AppVersion
{
    public const string Version = "1.1.0";
}
