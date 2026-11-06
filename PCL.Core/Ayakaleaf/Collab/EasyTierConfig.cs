using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PCL.Core.Ayakaleaf.Collab;

/// <summary>
/// 单个虚拟网络实例的配置（写入 TOML 文件，由 easytier-core -c 加载）。
/// 字段名为 EasyTier 官方配置格式。
/// </summary>
public sealed class EasyTierConfig
{
    /// <summary>本机在网络中的主机名（发现协议载体：ayaka-host-… / ayaka-guest-…）。</summary>
    public required string Hostname { get; init; }

    /// <summary>网络名（同网络的成员必须一致）。</summary>
    public required string NetworkName { get; init; }

    /// <summary>网络密钥（同网络的成员必须一致）。</summary>
    public required string NetworkSecret { get; init; }

    /// <summary>初始连接的节点列表（公共节点/自定义节点）。</summary>
    public IReadOnlyList<string> Peers { get; init; } = [];

    /// <summary>允许其他成员访问的本机 TCP 端口白名单；["0"] 表示全部拒绝。</summary>
    public IReadOnlyList<string> TcpWhitelist { get; init; } = [];

    /// <summary>允许其他成员访问的本机 UDP 端口白名单。</summary>
    public IReadOnlyList<string> UdpWhitelist { get; init; } = [];

    /// <summary>隐藏真实 IP（全部流量经节点中继，不直连、不监听）。</summary>
    public bool HideIp { get; init; }

    /// <summary>仅允许直连（无中继兜底）。</summary>
    public bool DirectOnly { get; init; }

    /// <summary>序列化为 TOML 配置文本。</summary>
    public string ToToml()
    {
        var toml = new StringBuilder();
        toml.AppendLine("hostname = " + Quote(Hostname));
        toml.AppendLine("dhcp = true");

        // 隐藏 IP 时不监听任何端口；否则在全部接口随机端口监听（IPv6 自动附加）
        toml.AppendLine(HideIp
            ? "listeners = []"
            : "listeners = [\"tcp://0.0.0.0:0\", \"udp://0.0.0.0:0\"]");
        toml.AppendLine("tcp_whitelist = " + Array(TcpWhitelist));
        toml.AppendLine("udp_whitelist = " + Array(UdpWhitelist));
        toml.AppendLine();

        toml.AppendLine("[network_identity]");
        toml.AppendLine("network_name = " + Quote(NetworkName));
        toml.AppendLine("network_secret = " + Quote(NetworkSecret));
        toml.AppendLine();

        foreach (string peer in Peers)
        {
            toml.AppendLine("[[peer]]");
            toml.AppendLine("uri = " + Quote(peer));
            toml.AppendLine();
        }

        toml.AppendLine("[flags]");
        // 无虚拟网卡、无管理员权限、无驱动
        toml.AppendLine("no_tun = true");
        toml.AppendLine("latency_first = true");
        toml.AppendLine("enable_kcp_proxy = true");
        toml.AppendLine("enable_quic_proxy = true");
        // Zstandard 压缩
        toml.AppendLine("data_compress_algo = 2");
        // 不为陌生网络担任中继
        toml.AppendLine("private_mode = true");
        toml.AppendLine("p2p_only = " + (DirectOnly && !HideIp ? "true" : "false"));
        if (HideIp)
        {
            toml.AppendLine("disable_p2p = true");
            toml.AppendLine("disable_udp_hole_punching = true");
            toml.AppendLine("disable_tcp_hole_punching = true");
            toml.AppendLine("disable_sym_hole_punching = true");
            toml.AppendLine("disable_upnp = true");
        }
        return toml.ToString();
    }

    private static string Array(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";

    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in value ?? "")
        {
            if (c is '"' or '\\') quoted.Append('\\').Append(c);
            else if (c < 0x20) quoted.Append("\\u").Append(((int)c).ToString("X4"));
            else quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }
}
