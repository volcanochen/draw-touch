namespace DrawTouchHost;

/// <summary>
/// 主机（PC 端）版本号。手机端拓扑图里展示的「客户端版本号」就是它。
/// 升级时改这里即可，手机会随 mDNS/INFO 帧自动拿到新值。
/// 构建时间戳由 csproj 的 GenBuildTime Target 编译期生成（BuildInfo.BuildTime），
/// 托盘菜单第一行显示「版本 + 构建于」，用户用来确认跑的是哪个包。
/// </summary>
internal static class AppVersion
{
    public const string Version = "1.4.9";
}
