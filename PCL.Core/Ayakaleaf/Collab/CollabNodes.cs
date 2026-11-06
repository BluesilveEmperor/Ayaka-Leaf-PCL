using System;
using System.Collections.Generic;

namespace PCL.Core.Ayakaleaf.Collab;

/// <summary>
/// EasyTier 公共节点表：内置社区节点 + 用户自定义节点（追加）。
/// 节点仅用于成员之间的 rendezvous 与中继兜底，流量默认优先 P2P 直连。
/// </summary>
public static class CollabNodes
{
    /// <summary>
    /// 内置社区公共节点（EasyTier 社区提供，公开信息）。
    /// </summary>
    public static readonly string[] BuiltIn =
    [
        "tcp://225284.xyz:11010",
        "tcp://easytier.weiai.org.cn:11010",
        "udp://et.basd1.de:11010",
        "wss://et.chinokou.cn",
        "https://et-public.lctn.top",
    ];

    /// <summary>
    /// 实际使用的节点列表：内置节点 + 自定义节点（追加，不去重）。
    /// </summary>
    /// <param name="custom">自定义节点配置（逗号/分号/空白分隔的节点 URL）。</param>
    public static IReadOnlyList<string> Effective(string? custom)
    {
        var result = new List<string>(BuiltIn);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            foreach (var raw in custom.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var uri = raw.Trim();
                if (uri.Contains("://")) result.Add(uri);
            }
        }
        return result;
    }
}
